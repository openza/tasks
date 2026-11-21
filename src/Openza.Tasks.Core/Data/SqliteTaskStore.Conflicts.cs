using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Core.Data;

public sealed record PersonalCloudConflictField(string Name, string DesktopValue, string CloudValue)
{
    public string DisplayName => Name switch
    {
        "title" => "Title", "notes" => "Notes", "workflowStatus" => "Workflow status", "priority" => "Priority",
        "plannedOn" => "Planned date", "plannedAt" => "Planned time", "deadlineOn" => "Deadline date",
        "deadlineAt" => "Deadline time", "scheduledStart" => "Scheduled start", "scheduledEnd" => "Scheduled end",
        "durationMinutes" => "Duration (minutes)", "recurrenceRule" => "Recurrence", "projectId" => "Project", _ => Name,
    };
}
public sealed record PersonalCloudTaskConflict(string TaskId, string Title, IReadOnlyList<PersonalCloudConflictField> Fields)
{
    public bool CanKeepDesktop { get; init; } = true;
    public string ResolutionNotice { get; init; } = string.Empty;
}

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
        command.CommandText = "SELECT local_sequence,mutation_id,entity_id,device_sequence,canonical_json,created_at,operation FROM personal_cloud_outbox WHERE instance_id=@i AND device_id=@d AND acknowledged_at IS NULL AND device_sequence>=@q ORDER BY device_sequence";
        command.Parameters.AddWithValue("@i", instanceId); command.Parameters.AddWithValue("@d", deviceId); command.Parameters.AddWithValue("@q", sequence);
        var tail = new List<(long Local, string Id, string Entity, long Sequence, string Json, long CreatedAt, string Operation)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) tail.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.GetInt64(5), reader.GetString(6)));
        if (tail.Count == 0 || tail[0].Entity != entityId || tail[0].Sequence != sequence)
            throw new PersonalCloudK0ContractException("version_conflict", "The pending queue changed; sync again before resolving conflicts.");
        var cloud = await ReadWorkspaceRecordAsync(connection, instanceId, "tasks", entityId, cancellationToken).ConfigureAwait(false);
        using var first = JsonDocument.Parse(tail[0].Json);
        var baseVersion = first.RootElement.GetProperty("baseEntityVersion");
        var baseJson = cloud is { } record && baseVersion.ValueKind == JsonValueKind.Number && record.GetProperty("entityVersion").GetInt64() == baseVersion.GetInt64() ? record.GetRawText() : null;
        var parkedIds = tail.Where(r => r.Entity == entityId && !IsUnversionedProviderOperation(r.Operation)).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var row in tail.Where(r => parkedIds.Contains(r.Id)))
        {
            await WorkspaceExecuteAsync(connection, "INSERT INTO personal_cloud_task_conflicts(mutation_id,instance_id,device_id,entity_id,device_sequence,canonical_json,base_record_json) VALUES(@m,@i,@d,@e,@q,@j,@b)", cancellationToken,
                ("@m", row.Id), ("@i", instanceId), ("@d", deviceId), ("@e", entityId), ("@q", row.Sequence), ("@j", row.Json), ("@b", row.Id == tail[0].Id ? baseJson : null)).ConfigureAwait(false);
        }
        await WorkspaceExecuteAsync(connection, "UPDATE personal_cloud_initial_sync SET seed_end_sequence=seed_end_sequence-(SELECT COUNT(*) FROM personal_cloud_outbox WHERE instance_id=@i AND device_id=@d AND entity_id=@e AND operation NOT IN ('source.refreshSnapshot','externalCommand.confirm','externalCommand.reject') AND acknowledged_at IS NULL AND device_sequence BETWEEN @q AND seed_end_sequence) WHERE instance_id=@i AND device_id=@d AND seed_end_sequence>=@q", cancellationToken,
            ("@q", sequence), ("@i", instanceId), ("@d", deviceId), ("@e", entityId)).ConfigureAwait(false);
        await WorkspaceExecuteAsync(connection, "DELETE FROM personal_cloud_outbox WHERE instance_id=@i AND device_id=@d AND acknowledged_at IS NULL AND device_sequence>=@q", cancellationToken, ("@i", instanceId), ("@d", deviceId), ("@q", sequence)).ConfigureAwait(false);
        var next = sequence;
        foreach (var row in tail.Where(r => !parkedIds.Contains(r.Id)))
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

    private static bool IsUnversionedProviderOperation(string operation) =>
        operation is "source.refreshSnapshot" or "externalCommand.confirm" or "externalCommand.reject";

    private sealed record ConflictState(string Id, string DeviceId, JsonObject? Base, JsonObject? Cloud, JsonObject Fields, bool Supported, IReadOnlyList<string> Operations);

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
            var fields = new JsonObject(); var supported = true; var operations = new List<string>();
            foreach (var row in group)
            {
                var mutation = JsonNode.Parse(row.Json)!;
                var operation = mutation["operation"]!.GetValue<string>();
                if (operation is not ("task.patch" or "task.updateOpenzaFields")) { supported = false; operations.Add(operation); continue; }
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
            conflicts.Add(new(group.Key, first.Device, first.Base is null ? null : JsonNode.Parse(first.Base)!.AsObject(), cloud is { } value ? JsonNode.Parse(value.GetRawText())!.AsObject() : null, fields, supported, operations));
        }
        return conflicts;
    }

    public async Task<IReadOnlyList<PersonalCloudTaskConflict>> GetPersonalCloudTaskConflictsAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<PersonalCloudTaskConflict>();
        foreach (var c in await ReadTaskConflictsAsync(connection, instanceId, cancellationToken).ConfigureAwait(false))
        {
            var fields = new List<PersonalCloudConflictField>();
            foreach (var field in c.Fields.Where(f => c.Cloud is null || !JsonNode.DeepEquals(f.Value, c.Cloud[f.Key])))
                fields.Add(new(field.Key,
                    await DisplayConflictFieldValueAsync(connection, instanceId, field.Key, field.Value, cancellationToken).ConfigureAwait(false),
                    await DisplayConflictFieldValueAsync(connection, instanceId, field.Key, c.Cloud?[field.Key], cancellationToken).ConfigureAwait(false)));
            var reason = await ConflictReplayRestrictionAsync(connection, instanceId, c, cancellationToken).ConfigureAwait(false);
            result.Add(new(c.Id, c.Fields["title"]?.GetValue<string>() ?? c.Cloud?["title"]?.GetValue<string>() ?? c.Id, fields)
            {
                CanKeepDesktop = reason is null,
                ResolutionNotice = c.Cloud is null
                    ? "No cached cloud record is available. Use cloud version discards the parked changes and restores the last known base when available; otherwise the local task is kept as a recovery copy until the next cloud download."
                    : reason is not null ? reason + " Only Use cloud version is available; it discards all parked desktop changes for this task."
                    : fields.Count == 0 ? "These field values already match the cloud. Choose a version to clear the parked changes." : string.Empty,
            });
        }
        return result;
    }

    private static string DisplayConflictValue(JsonNode? value) => value is null ? "(empty)" : value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value.ToJsonString();

    private static async Task<string> DisplayConflictFieldValueAsync(SqliteConnection connection, string instanceId, string field, JsonNode? value, CancellationToken cancellationToken)
    {
        if (field != "projectId" || value is null) return DisplayConflictValue(value);
        var id = value.GetValue<string>();
        var project = await ReadWorkspaceRecordAsync(connection, instanceId, "projects", id, cancellationToken).ConfigureAwait(false);
        if (project is { } record && WText(record, "name") is { } name) return name;
        var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM projects WHERE id=@id";
        command.Parameters.AddWithValue("@id", id);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string ?? "(unavailable project)";
    }

    private static async Task ParkNewTaskMutationAsync(SqliteConnection connection, string instanceId, string deviceId, string mutationId, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT entity_id,device_sequence FROM personal_cloud_outbox o WHERE mutation_id=@m AND operation NOT IN ('source.refreshSnapshot','externalCommand.confirm','externalCommand.reject') AND EXISTS(SELECT 1 FROM personal_cloud_task_conflicts c WHERE c.instance_id=o.instance_id AND c.entity_id=o.entity_id)";
        command.Parameters.AddWithValue("@m", mutationId);
        string entityId; long sequence;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return;
            entityId = reader.GetString(0); sequence = reader.GetInt64(1);
        }
        await ParkTaskConflictAsync(connection, instanceId, deviceId, entityId, sequence, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> HasConflictUploadsAsync(SqliteConnection connection, string instanceId, string taskId, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM personal_cloud_outbox WHERE instance_id=@i AND entity_id=@e AND acknowledged_at IS NULL)";
        command.Parameters.AddWithValue("@i", instanceId); command.Parameters.AddWithValue("@e", taskId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    private static async Task<string?> ConflictReplayRestrictionAsync(SqliteConnection connection, string instanceId, ConflictState conflict, CancellationToken cancellationToken)
    {
        if (!conflict.Supported)
            return "Parked desktop operations cannot be safely replayed: " + string.Join(", ", conflict.Operations.Distinct().Select(OperationLabel)) + ".";
        if (conflict.Cloud is null || conflict.Cloud["deletedAt"] is not null) return "The cloud task is unavailable or deleted.";
        if (conflict.Cloud["source"] is not null && conflict.Fields.ContainsKey("title") && !JsonNode.DeepEquals(conflict.Fields["title"], conflict.Cloud["title"]))
            return "Title is now owned by the source provider and cannot be changed here.";
        if (conflict.Fields.TryGetPropertyValue("projectId", out var project) && project is not null && !JsonNode.DeepEquals(project, conflict.Cloud["projectId"]))
        {
            var record = await ReadWorkspaceRecordAsync(connection, instanceId, "projects", project.GetValue<string>(), cancellationToken).ConfigureAwait(false);
            if (record is null || record.Value.GetProperty("deletedAt").ValueKind != JsonValueKind.Null ||
                record.Value.GetProperty("status").GetString() == "archived" ||
                record.Value.GetProperty("spaceId").GetString() != conflict.Cloud["spaceId"]?.GetValue<string>())
                return "The desktop project is deleted, archived, unavailable, or in another Space.";
        }
        return null;
    }

    private static string OperationLabel(string operation) => operation switch
    {
        "task.complete" => "Complete task", "task.reopen" => "Reopen task", "task.delete" => "Delete task",
        "task.move" or "task.moveTree" => "Move task", "task.setProject" => "Change project",
        "task.addLabel" => "Add label", "task.removeLabel" => "Remove label", "task.setParent" => "Change parent", _ => operation,
    };

    public async Task<int> AutoResolvePersonalCloudTaskConflictsAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var resolved = 0;
        foreach (var conflict in await ReadTaskConflictsAsync(connection, instanceId, cancellationToken).ConfigureAwait(false))
        {
            if (conflict.Cloud is null || await HasConflictUploadsAsync(connection, instanceId, conflict.Id, cancellationToken).ConfigureAwait(false) ||
                await ConflictReplayRestrictionAsync(connection, instanceId, conflict, cancellationToken).ConfigureAwait(false) is not null ||
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
        if (keepDesktop && await ConflictReplayRestrictionAsync(connection, instanceId, conflict, cancellationToken).ConfigureAwait(false) is not null)
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
        var record = (conflict.Cloud ?? conflict.Base)?.DeepClone().AsObject();
        if (record?["deletedAt"] is null)
            await WorkspaceExecuteAsync(connection, "DELETE FROM personal_cloud_tombstones WHERE instance_id=@i AND product='tasks' AND entity_id=@e", cancellationToken, ("@i", instanceId), ("@e", conflict.Id)).ConfigureAwait(false);
        var differing = new Dictionary<string, object?>();
        if (keepDesktop)
            foreach (var field in conflict.Fields.Where(f => !JsonNode.DeepEquals(f.Value, record![f.Key])).ToArray())
            { record![field.Key] = field.Value?.DeepClone(); differing[field.Key] = field.Value?.DeepClone(); }
        if (record is not null) records.Insert(0, record);
        using var payload = JsonDocument.Parse(PersonalCloudK0Codec.Serialize(new { spaces = Array.Empty<object>(), projects = Array.Empty<object>(), tasks = records }));
        await ApplyWorkspacePayloadAsync(connection, instanceId, payload.RootElement, cancellationToken, applyStoredTask: true).ConfigureAwait(false);
        if (differing.Count > 0)
            await AppendWorkspaceMutationAsync(connection, instanceId, conflict.DeviceId, "task.patch", conflict.Id, conflict.Cloud!["entityVersion"]!.GetValue<long>(), new { fields = differing }, cancellationToken).ConfigureAwait(false);
    }
}
