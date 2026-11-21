using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Core.Data;

public sealed record PersonalCloudConflictField(string Name, string DesktopValue, string CloudValue);
public sealed record PersonalCloudTaskConflict(string TaskId, string Title, IReadOnlyList<PersonalCloudConflictField> Fields);

public sealed partial class SqliteTaskStore
{
    public async Task<bool> ParkPersonalCloudTaskConflictAsync(PersonalCloudK1OutboxEntry rejected, CancellationToken cancellationToken = default)
    {
        if (rejected.Operation is not ("task.patch" or "task.updateOpenzaFields")) return false;
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await WorkspaceEnabledAsync(connection, rejected.InstanceId, cancellationToken).ConfigureAwait(false)) return false;
        await ParkTaskConflictAsync(connection, rejected.InstanceId, rejected.DeviceId, rejected.EntityId, rejected.DeviceSequence, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task ParkTaskConflictAsync(SqliteConnection connection, string instanceId, string deviceId, string entityId, long sequence, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT local_sequence,mutation_id,entity_id,device_sequence,canonical_json,created_at FROM personal_cloud_outbox WHERE instance_id=@i AND device_id=@d AND acknowledged_at IS NULL AND device_sequence>=@q ORDER BY device_sequence";
        command.Parameters.AddWithValue("@i", instanceId); command.Parameters.AddWithValue("@d", deviceId); command.Parameters.AddWithValue("@q", sequence);
        var tail = new List<(long Local, string Id, string Entity, long Sequence, string Json, long CreatedAt)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) tail.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.GetInt64(5)));
        if (tail.Count == 0 || tail[0].Entity != entityId || tail[0].Sequence != sequence)
            throw new PersonalCloudK0ContractException("version_conflict", "The pending queue changed; sync again before resolving conflicts.");
        var cloud = await ReadWorkspaceRecordAsync(connection, instanceId, "tasks", entityId, cancellationToken).ConfigureAwait(false);
        using var first = JsonDocument.Parse(tail[0].Json);
        var baseVersion = first.RootElement.GetProperty("baseEntityVersion");
        var baseJson = cloud is { } record && baseVersion.ValueKind == JsonValueKind.Number && record.GetProperty("entityVersion").GetInt64() == baseVersion.GetInt64() ? record.GetRawText() : null;
        foreach (var row in tail.Where(r => r.Entity == entityId))
        {
            await WorkspaceExecuteAsync(connection, "INSERT INTO personal_cloud_task_conflicts(mutation_id,instance_id,device_id,entity_id,device_sequence,canonical_json,base_record_json) VALUES(@m,@i,@d,@e,@q,@j,@b)", cancellationToken,
                ("@m", row.Id), ("@i", instanceId), ("@d", deviceId), ("@e", entityId), ("@q", row.Sequence), ("@j", row.Json), ("@b", row.Id == tail[0].Id ? baseJson : null)).ConfigureAwait(false);
        }
        await WorkspaceExecuteAsync(connection, "UPDATE personal_cloud_initial_sync SET seed_end_sequence=seed_end_sequence-(SELECT COUNT(*) FROM personal_cloud_outbox WHERE instance_id=@i AND device_id=@d AND entity_id=@e AND acknowledged_at IS NULL AND device_sequence BETWEEN @q AND seed_end_sequence) WHERE instance_id=@i AND device_id=@d AND seed_end_sequence>=@q", cancellationToken,
            ("@q", sequence), ("@i", instanceId), ("@d", deviceId), ("@e", entityId)).ConfigureAwait(false);
        await WorkspaceExecuteAsync(connection, "DELETE FROM personal_cloud_outbox WHERE instance_id=@i AND device_id=@d AND acknowledged_at IS NULL AND device_sequence>=@q", cancellationToken, ("@i", instanceId), ("@d", deviceId), ("@q", sequence)).ConfigureAwait(false);
        var next = sequence;
        foreach (var row in tail.Where(r => r.Entity != entityId))
        {
            var node = JsonNode.Parse(row.Json)!.AsObject(); var id = $"mutation_{Guid.NewGuid():N}";
            node["mutationId"] = id; node["deviceSequence"] = next++;
            var json = PersonalCloudK0Codec.Canonicalize(node.ToJsonString());
            await WorkspaceExecuteAsync(connection, "INSERT INTO personal_cloud_outbox(local_sequence,mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at) VALUES(@l,@m,'tasks',@i,@d,@q,@o,@e,@j,@h,@a)", cancellationToken,
                ("@l", row.Local), ("@m", id), ("@i", instanceId), ("@d", deviceId), ("@q", next - 1), ("@o", node["operation"]!.GetValue<string>()), ("@e", row.Entity), ("@j", json), ("@h", PersonalCloudK0Codec.ComputeCanonicalHash(json)), ("@a", row.CreatedAt)).ConfigureAwait(false);
            await WorkspaceExecuteAsync(connection, "UPDATE personal_cloud_workspace_indirect_versions SET mutation_id=@n WHERE mutation_id=@m", cancellationToken, ("@n", id), ("@m", row.Id)).ConfigureAwait(false);
            await WorkspaceExecuteAsync(connection, "UPDATE personal_cloud_provider_attempts SET result_mutation_id=@n WHERE result_mutation_id=@m", cancellationToken, ("@n", id), ("@m", row.Id)).ConfigureAwait(false);
        }
        await WorkspaceExecuteAsync(connection, "UPDATE personal_cloud_device_state SET next_device_sequence=@q WHERE instance_id=@i AND device_id=@d", cancellationToken, ("@q", next), ("@i", instanceId), ("@d", deviceId)).ConfigureAwait(false);
    }

    private sealed record ConflictState(string Id, string DeviceId, JsonObject? Base, JsonObject? Cloud, JsonObject Fields, bool Supported);

    private static async Task<IReadOnlyList<ConflictState>> ReadTaskConflictsAsync(SqliteConnection connection, string instanceId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "personal_cloud_task_conflicts", cancellationToken).ConfigureAwait(false)) return [];
        var command = connection.CreateCommand();
        command.CommandText = "SELECT entity_id,device_id,canonical_json,base_record_json FROM personal_cloud_task_conflicts WHERE instance_id=@i ORDER BY rowid";
        command.Parameters.AddWithValue("@i", instanceId);
        var rows = new List<(string Id, string Device, string Json, string? Base)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        var conflicts = new List<ConflictState>();
        foreach (var group in rows.GroupBy(r => r.Id))
        {
            var fields = new JsonObject(); var supported = true;
            foreach (var row in group)
            {
                var mutation = JsonNode.Parse(row.Json)!;
                var operation = mutation["operation"]!.GetValue<string>();
                if (operation is not ("task.patch" or "task.updateOpenzaFields")) { supported = false; continue; }
                var patch = (operation == "task.patch" ? mutation["payload"]!["fields"]! : mutation["payload"]!).AsObject();
                foreach (var field in patch) fields[field.Key] = field.Value?.DeepClone();
                foreach (var (day, exact) in new[] { ("plannedOn", "plannedAt"), ("deadlineOn", "deadlineAt") })
                {
                    if (patch.ContainsKey(day) && !patch.ContainsKey(exact)) fields[exact] = null;
                    if (patch.ContainsKey(exact) && !patch.ContainsKey(day)) fields[day] = null;
                }
            }
            var first = group.First();
            var cloud = await ReadWorkspaceRecordAsync(connection, instanceId, "tasks", group.Key, cancellationToken).ConfigureAwait(false);
            conflicts.Add(new(group.Key, first.Device, first.Base is null ? null : JsonNode.Parse(first.Base)!.AsObject(), cloud is { } value ? JsonNode.Parse(value.GetRawText())!.AsObject() : null, fields, supported));
        }
        return conflicts;
    }

    public async Task<IReadOnlyList<PersonalCloudTaskConflict>> GetPersonalCloudTaskConflictsAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await ReadTaskConflictsAsync(connection, instanceId, cancellationToken).ConfigureAwait(false)).Select(c => new PersonalCloudTaskConflict(c.Id,
            c.Fields["title"]?.GetValue<string>() ?? c.Cloud?["title"]?.GetValue<string>() ?? c.Id,
            c.Fields.Where(f => c.Cloud is null || !JsonNode.DeepEquals(f.Value, c.Cloud[f.Key])).Select(f => new PersonalCloudConflictField(f.Key, DisplayConflictValue(f.Value), DisplayConflictValue(c.Cloud?[f.Key]))).ToArray())).ToArray();
    }

    private static string DisplayConflictValue(JsonNode? value) => value is null ? "(empty)" : value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value.ToJsonString();

    public async Task ParkAdditionalPersonalCloudTaskEditsAsync(string instanceId, string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var conflict in await ReadTaskConflictsAsync(connection, instanceId, cancellationToken).ConfigureAwait(false))
        {
            var command = connection.CreateCommand(); command.CommandText = "SELECT MIN(device_sequence) FROM personal_cloud_outbox WHERE instance_id=@i AND device_id=@d AND entity_id=@e AND acknowledged_at IS NULL";
            command.Parameters.AddWithValue("@i", instanceId); command.Parameters.AddWithValue("@d", deviceId); command.Parameters.AddWithValue("@e", conflict.Id);
            var first = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (first is not null and not DBNull) await ParkTaskConflictAsync(connection, instanceId, deviceId, conflict.Id, Convert.ToInt64(first), cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> AutoResolvePersonalCloudTaskConflictsAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var resolved = 0;
        foreach (var conflict in await ReadTaskConflictsAsync(connection, instanceId, cancellationToken).ConfigureAwait(false))
        {
            if (!conflict.Supported || conflict.Cloud is null || conflict.Cloud["deletedAt"] is not null ||
                conflict.Cloud["source"] is not null && conflict.Fields.ContainsKey("title") && !JsonNode.DeepEquals(conflict.Fields["title"], conflict.Cloud["title"]) ||
                !conflict.Fields.All(f => JsonNode.DeepEquals(f.Value, conflict.Cloud[f.Key]) || conflict.Base is not null && JsonNode.DeepEquals(conflict.Base[f.Key], conflict.Cloud[f.Key]))) continue;
            await ResolveTaskConflictAsync(connection, instanceId, conflict, true, cancellationToken).ConfigureAwait(false); resolved++;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return resolved;
    }

    public async Task ResolvePersonalCloudTaskConflictAsync(string instanceId, string taskId, bool keepDesktop, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var conflict = (await ReadTaskConflictsAsync(connection, instanceId, cancellationToken).ConfigureAwait(false)).SingleOrDefault(c => c.Id == taskId)
            ?? throw new PersonalCloudK0ContractException("entity_not_found", "This conflict has already been resolved.");
        await ResolveTaskConflictAsync(connection, instanceId, conflict, keepDesktop, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ResolveTaskConflictAsync(SqliteConnection connection, string instanceId, ConflictState conflict, bool keepDesktop, CancellationToken cancellationToken)
    {
        if (conflict.Cloud is null || keepDesktop && (!conflict.Supported || conflict.Cloud["deletedAt"] is not null))
            throw new PersonalCloudK0ContractException("invalid_transition", "Sync the latest cloud record before resolving this task. This edit cannot be safely replayed.");
        var pending = connection.CreateCommand(); pending.CommandText = "SELECT COUNT(*) FROM personal_cloud_outbox WHERE instance_id=@i AND entity_id=@e AND acknowledged_at IS NULL";
        pending.Parameters.AddWithValue("@i", instanceId); pending.Parameters.AddWithValue("@e", conflict.Id);
        if (Convert.ToInt64(await pending.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0)
            throw new PersonalCloudK0ContractException("pending_intents", "Sync the latest desktop edits before choosing a version.");
        var related = connection.CreateCommand();
        related.CommandText = "SELECT DISTINCT w.canonical_json FROM personal_cloud_workspace_records w JOIN personal_cloud_workspace_indirect_versions v ON v.instance_id=w.instance_id AND v.kind=w.kind AND v.entity_id=w.entity_id JOIN personal_cloud_task_conflicts c ON c.mutation_id=v.mutation_id WHERE c.instance_id=@i AND c.entity_id=@e AND w.kind='tasks' AND w.entity_id<>@e";
        related.Parameters.AddWithValue("@i", instanceId); related.Parameters.AddWithValue("@e", conflict.Id);
        var records = new List<JsonObject>();
        await using (var reader = await related.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) records.Add(JsonNode.Parse(reader.GetString(0))!.AsObject());
        await WorkspaceExecuteAsync(connection, "DELETE FROM personal_cloud_workspace_indirect_versions WHERE mutation_id IN (SELECT mutation_id FROM personal_cloud_task_conflicts WHERE instance_id=@i AND entity_id=@e)", cancellationToken, ("@i", instanceId), ("@e", conflict.Id)).ConfigureAwait(false);
        await WorkspaceExecuteAsync(connection, "DELETE FROM personal_cloud_task_conflicts WHERE instance_id=@i AND entity_id=@e", cancellationToken, ("@i", instanceId), ("@e", conflict.Id)).ConfigureAwait(false);
        var record = conflict.Cloud.DeepClone().AsObject(); var differing = new Dictionary<string, object?>();
        if (keepDesktop)
            foreach (var field in conflict.Fields.Where(f => !JsonNode.DeepEquals(f.Value, record[f.Key])).ToArray())
            { record[field.Key] = field.Value?.DeepClone(); differing[field.Key] = field.Value?.DeepClone(); }
        records.Insert(0, record);
        using var payload = JsonDocument.Parse(PersonalCloudK0Codec.Serialize(new { spaces = Array.Empty<object>(), projects = Array.Empty<object>(), tasks = records }));
        await ApplyWorkspacePayloadAsync(connection, instanceId, payload.RootElement, cancellationToken, applyStoredTask: true).ConfigureAwait(false);
        if (differing.Count > 0)
            await AppendWorkspaceMutationAsync(connection, instanceId, conflict.DeviceId, "task.patch", conflict.Id, conflict.Cloud["entityVersion"]!.GetValue<long>(), new { fields = differing }, cancellationToken).ConfigureAwait(false);
    }
}
