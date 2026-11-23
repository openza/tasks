using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Core.Data;

public sealed partial class SqliteTaskStore
{
    public Func<PersonalCloudConnectionProfile?>? PersonalCloudConnection { get; set; }

    public async Task<int> ApplyPendingMicrosoftCategoriesAfterPullAsync(PersonalCloudConnectionProfile cloud, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var read = connection.CreateCommand();
        read.CommandText = """
            SELECT p.id,p.adopted_task_id FROM provider_source_items p JOIN tasks t ON t.id=p.adopted_task_id
            WHERE p.integration_id=@integration AND p.adoption_state='adopted'
              AND COALESCE(json_extract(t.source_metadata,'$.msToDoCategoriesApplied'),0)=0
              AND EXISTS(SELECT 1 FROM personal_cloud_entity_versions v WHERE v.instance_id=@i AND v.product='tasks' AND v.entity_id=t.id)
              AND NOT EXISTS(SELECT 1 FROM personal_cloud_task_conflicts c WHERE c.instance_id=@i AND c.entity_id=t.id)
            """;
        read.Parameters.AddWithValue("@i", cloud.InstanceId);
        read.Parameters.AddWithValue("@integration", IntegrationIds.MicrosoftToDo);
        var candidates = new List<(string Source, string Task)>();
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) candidates.Add((reader.GetString(0), reader.GetString(1)));
        if (candidates.Count == 0) return 0;

        using var lease = PersonalCloudConnectionLease.AcquireAuthoring(DatabasePath);
        var current = PersonalCloudConnection?.Invoke();
        if (PersonalCloudConnection is not null && (current is null || current.InstanceId != cloud.InstanceId || current.DeviceId != cloud.DeviceId)) return 0;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var applied = 0;
        foreach (var candidate in candidates)
        {
            var source = await ReadProviderSourceItemByIdAsync(connection, candidate.Source, cancellationToken).ConfigureAwait(false);
            if (source is null || source.AdoptionState != ProviderSourceAdoptionStates.Adopted || source.AdoptedTaskId != candidate.Task) continue;
            var task = (await SqliteTaskReadQueries.ReadTasksAsync(connection, cancellationToken, candidate.Task).ConfigureAwait(false)).FirstOrDefault();
            if (task is null) continue;
            var updated = await ApplyMicrosoftCategoriesAsync(connection, source, task, cancellationToken, publishingCloud: cloud).ConfigureAwait(false);
            if (updated.Revision != task.Revision) applied++;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return applied;
    }

    private async Task<TaskItem> ApplyMicrosoftCategoriesAsync(SqliteConnection connection, ProviderSourceItem source,
        TaskItem task, CancellationToken cancellationToken, bool creatingCloudTask = false, PersonalCloudConnectionProfile? publishingCloud = null)
    {
        if (source.IntegrationId != IntegrationIds.MicrosoftToDo) return task;
        var metadata = JsonNode.Parse(task.SourceMetadataJson ?? "{}")!.AsObject();
        if (metadata["msToDoCategoriesApplied"]?.GetValue<bool>() == true) return task;

        using var snapshot = JsonDocument.Parse(source.SnapshotJson ?? "{}");
        if (!snapshot.RootElement.TryGetProperty("msToDo", out var microsoft) ||
            !microsoft.TryGetProperty("categories", out var categories) || categories.ValueKind != JsonValueKind.Array)
            return task;

        if (!creatingCloudTask && publishingCloud is null)
        {
            var published = connection.CreateCommand();
            published.CommandText = """
                SELECT EXISTS(SELECT 1 FROM personal_cloud_entity_versions WHERE product='tasks' AND entity_id=@t)
                    OR EXISTS(SELECT 1 FROM personal_cloud_replica_entities WHERE product='tasks' AND entity_id=@t)
                    OR EXISTS(SELECT 1 FROM personal_cloud_outbox WHERE product='tasks' AND entity_id=@t AND operation IN('task.create','task.createRich'))
                """;
            published.Parameters.AddWithValue("@t", task.Id);
            if (Convert.ToInt64(await published.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0) return task;
        }

        var labels = task.Labels.ToList();
        PersonalCloudConnectionProfile? cloud = null;
        long? version = null;
        var cloudResolved = false;
        var localLabels = new List<LabelItem>();
        var read = connection.CreateCommand();
        read.CommandText = "SELECT id,external_id,integration_id,provider_connection_id,name,color,description,sort_order,is_favorite,provider_metadata,created_at FROM labels WHERE integration_id='openza_tasks'";
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) localLabels.Add(ReadLabel(reader));

        foreach (var category in categories.EnumerateArray())
        {
            var name = category.GetProperty("name").GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (labels.Any(item => string.Equals(item.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))) continue;
            var label = localLabels.FirstOrDefault(item => string.Equals(item.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
            var isNew = label is null;
            if (label is null)
            {
                label = new LabelItem { Id = $"label_{Guid.NewGuid():N}", Name = name,
                    Color = category.TryGetProperty("color", out var color) ? color.GetString() ?? "#0078D4" : "#0078D4" };
            }
            if (labels.Any(item => item.Id == label.Id)) continue;
            if (labels.Count >= 32) break;
            if (!cloudResolved)
            {
                cloud = publishingCloud;
                version = cloud is null ? null : await ReadPersonalCloudEntityVersionAsync(connection, cloud.InstanceId, task.Id, cancellationToken).ConfigureAwait(false);
                cloudResolved = true;
                if (cloud is not null && version is null) return task;
                if (cloud is not null && version is not null)
                {
                    var pending = connection.CreateCommand();
                    pending.CommandText = "SELECT (SELECT COUNT(*) FROM personal_cloud_outbox WHERE instance_id=@i AND entity_id=@t AND acknowledged_at IS NULL) + (SELECT COUNT(*) FROM personal_cloud_task_conflicts WHERE instance_id=@i AND entity_id=@t)";
                    pending.Parameters.AddWithValue("@i", cloud.InstanceId);
                    pending.Parameters.AddWithValue("@t", task.Id);
                    if (Convert.ToInt64(await pending.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0)
                        return task;
                }
            }
            if (isNew)
            {
                await UpsertLabelCoreAsync(connection, label, cancellationToken).ConfigureAwait(false);
                localLabels.Add(label);
            }
            labels.Add(label);
            if (cloud is not null && version is not null)
            {
                await MutateTaskRelationshipWithPersonalCloudIntentCoreAsync(connection, task.Id, "task.addLabel", label.Id,
                    label.Name, version.Value, cloud.InstanceId, cloud.DeviceId, cancellationToken).ConfigureAwait(false);
                version = await ReadPersonalCloudEntityVersionAsync(connection, cloud.InstanceId, task.Id, cancellationToken).ConfigureAwait(false);
            }
            else
                await WorkspaceExecuteAsync(connection, "INSERT OR IGNORE INTO task_labels(task_id,label_id) VALUES(@t,@l)",
                    cancellationToken, ("@t", task.Id), ("@l", label.Id)).ConfigureAwait(false);
        }

        metadata["msToDoCategoriesApplied"] = true;
        var json = metadata.ToJsonString();
        await WorkspaceExecuteAsync(connection, "UPDATE tasks SET source_metadata=@m,revision=revision+1 WHERE id=@t",
            cancellationToken, ("@m", json), ("@t", task.Id)).ConfigureAwait(false);
        return task with { Labels = labels, SourceMetadataJson = json, Revision = task.Revision + 1 };
    }

}
