using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Core.Data;

public sealed partial class SqliteTaskStore
{
    private static bool HasMicrosoftRecurrenceObservation(ProviderSourceItem source)
    {
        if (source.IntegrationId != IntegrationIds.MicrosoftToDo || source.SnapshotJson is null) return false;
        using var snapshot = JsonDocument.Parse(source.SnapshotJson);
        return snapshot.RootElement.ValueKind == JsonValueKind.Object &&
            snapshot.RootElement.TryGetProperty("msToDo", out var microsoft) && microsoft.ValueKind == JsonValueKind.Object &&
            microsoft.TryGetProperty("recurrenceObserved", out var observed) && observed.ValueKind == JsonValueKind.True;
    }

    private async Task<bool> ApplyMicrosoftRecurrenceAsync(SqliteConnection connection, ProviderSourceItem source,
        string taskId, CancellationToken cancellationToken, bool afterCloudPull = false)
    {
        if (!HasMicrosoftRecurrenceObservation(source)) return false;
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tasks SET recurrence_rule=COALESCE(recurrence_rule,@rule),
                source_metadata=json_set(COALESCE(source_metadata,'{}'),'$.msToDoRecurrenceApplied',json('true')),
                revision=revision+1
            WHERE id=@id AND COALESCE(json_extract(source_metadata,'$.msToDoRecurrenceApplied'),0)=0
              AND (@cloud_instance IS NULL OR NOT (
                EXISTS(SELECT 1 FROM personal_cloud_entity_versions WHERE instance_id=@cloud_instance AND product='tasks' AND entity_id=@id)
                OR EXISTS(SELECT 1 FROM personal_cloud_replica_entities WHERE instance_id=@cloud_instance AND product='tasks' AND entity_id=@id)
                OR EXISTS(SELECT 1 FROM personal_cloud_outbox WHERE instance_id=@cloud_instance AND product='tasks' AND entity_id=@id
                    AND operation IN ('task.create','task.createRich'))))
            """;
        command.Parameters.AddWithValue("@id", taskId);
        command.Parameters.AddWithValue("@rule", ToDbValue(source.RecurrenceRule));
        command.Parameters.AddWithValue("@cloud_instance", ToDbValue(afterCloudPull ? null : PersonalCloudConnection?.Invoke()?.InstanceId));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    internal async Task<int> ApplyPendingMicrosoftRecurrenceAfterPullAsync(PersonalCloudConnectionProfile cloud, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var read = connection.CreateCommand();
        read.CommandText = """
            SELECT p.id,t.id
            FROM provider_source_items p JOIN tasks t ON t.id=p.adopted_task_id
            JOIN personal_cloud_entity_versions v ON v.entity_id=t.id AND v.product='tasks' AND v.instance_id=@instance
            WHERE p.integration_id=@integration AND p.adoption_state='adopted'
              AND json_extract(p.snapshot_json,'$.msToDo.recurrenceObserved')=1
              AND COALESCE(json_extract(t.source_metadata,'$.msToDoRecurrenceApplied'),0)=0
            """;
        read.Parameters.AddWithValue("@instance", cloud.InstanceId);
        read.Parameters.AddWithValue("@integration", IntegrationIds.MicrosoftToDo);
        var candidates = new List<(string SourceId, string TaskId)>();
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                candidates.Add((reader.GetString(0), reader.GetString(1)));
        if (candidates.Count == 0) return 0;

        using var lease = PersonalCloudConnectionLease.AcquireAuthoring(DatabasePath);
        var current = PersonalCloudConnection?.Invoke();
        if (PersonalCloudConnection is not null && (current is null || current.InstanceId != cloud.InstanceId || current.DeviceId != cloud.DeviceId)) return 0;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var staged = 0;
        var workspace = await WorkspaceEnabledAsync(connection, cloud.InstanceId, cancellationToken).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            if (await HasPendingWorkspaceOverlayAsync(connection, cloud.InstanceId, candidate.TaskId, cancellationToken).ConfigureAwait(false))
                continue;
            var source = await ReadProviderSourceItemByIdAsync(connection, candidate.SourceId, cancellationToken).ConfigureAwait(false);
            if (source is null || source.AdoptionState != ProviderSourceAdoptionStates.Adopted || source.AdoptedTaskId != candidate.TaskId)
                continue;
            var version = await ReadPersonalCloudEntityVersionAsync(connection, cloud.InstanceId, candidate.TaskId, cancellationToken).ConfigureAwait(false);
            if (version is null) continue;
            var task = (await SqliteTaskReadQueries.ReadTasksAsync(connection, cancellationToken, candidate.TaskId).ConfigureAwait(false)).SingleOrDefault();
            if (task is null || !await ApplyMicrosoftRecurrenceAsync(connection, source, task.Id, cancellationToken, afterCloudPull: true).ConfigureAwait(false))
                continue;
            if (!workspace || task.RecurrenceRule is not null || source.RecurrenceRule is null) continue;
            await AppendWorkspaceMutationAsync(connection, cloud.InstanceId, cloud.DeviceId, "task.patch", candidate.TaskId, version,
                new { fields = new { recurrenceRule = source.RecurrenceRule } }, cancellationToken).ConfigureAwait(false);
            staged++;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return staged;
    }

    private static Task CaptureLegacyMicrosoftRecurrenceAsync(SqliteConnection connection, string instanceId, CancellationToken cancellationToken) =>
        WorkspaceExecuteAsync(connection, """
            DROP TABLE IF EXISTS temp.legacy_microsoft_recurrence;
            CREATE TEMP TABLE legacy_microsoft_recurrence AS
            SELECT t.id,t.recurrence_rule FROM tasks t
            JOIN incoming_workspace_tasks incoming ON incoming.id=t.id
            JOIN personal_cloud_entity_versions v ON v.entity_id=t.id AND v.product='tasks' AND v.instance_id=@i
            WHERE t.recurrence_rule IS NOT NULL
              AND json_extract(t.source_metadata,'$.msToDoRecurrenceApplied')=1
              AND NOT EXISTS(SELECT 1 FROM personal_cloud_workspace_capabilities WHERE instance_id=@i AND hydrated=1)
              AND NOT EXISTS(SELECT 1 FROM personal_cloud_workspace_records WHERE instance_id=@i AND kind='tasks' AND entity_id=t.id)
            """, cancellationToken, ("@i", instanceId));

    private static async Task StageLegacyMicrosoftRecurrenceAsync(SqliteConnection connection, string instanceId, string deviceId,
        CancellationToken cancellationToken)
    {
        // K0 never stored the local rule, so a null in its first workspace record
        // does not supersede the device copy. Later workspace records are authoritative.
        var read = connection.CreateCommand();
        read.CommandText = """
            SELECT legacy.id,legacy.recurrence_rule,w.entity_version FROM legacy_microsoft_recurrence legacy
            JOIN personal_cloud_workspace_records w ON w.instance_id=@i AND w.kind='tasks' AND w.entity_id=legacy.id
            WHERE json_type(w.canonical_json,'$.recurrenceRule')='null' AND json_type(w.canonical_json,'$.deletedAt')='null'
            """;
        read.Parameters.AddWithValue("@i", instanceId);
        var pending = new List<(string Id, string Rule, long Version)>();
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                pending.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));

        foreach (var (id, rule, version) in pending)
        {
            await WorkspaceExecuteAsync(connection, "UPDATE tasks SET recurrence_rule=@rule WHERE id=@id", cancellationToken,
                ("@rule", rule), ("@id", id)).ConfigureAwait(false);
            await AppendWorkspaceMutationAsync(connection, instanceId, deviceId, "task.patch", id, version,
                new { fields = new { recurrenceRule = rule } }, cancellationToken).ConfigureAwait(false);
        }
    }
}
