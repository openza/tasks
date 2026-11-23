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
    public string RevisionToken { get; init; } = string.Empty;
    public bool CanKeepDesktop { get; init; } = true;
    public string ResolutionNotice { get; init; } = string.Empty;
}

public sealed partial class SqliteTaskStore
{
    public async Task<bool> ParkPersonalCloudTaskConflictAsync(PersonalCloudK1OutboxEntry rejected, CancellationToken cancellationToken = default)
        => await ParkRejectedPersonalCloudTaskAsync(rejected, "version_conflict", cancellationToken).ConfigureAwait(false);

    internal async Task<bool> ParkRejectedPersonalCloudTaskAsync(PersonalCloudK1OutboxEntry rejected, string code, CancellationToken cancellationToken)
    {
        // These are semantic rejections, not transport failures or indications that the
        // server's sequence/idempotency state differs from ours.
        if (!rejected.Operation.StartsWith("task.", StringComparison.Ordinal) ||
            code is not ("version_conflict" or "entity_not_found" or "entity_exists" or "invalid_transition" or "parent_not_found" or "source_identity_conflict"))
            return false;
        if (code == "parent_not_found" && rejected.Operation != "task.setParent") return false;
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await WorkspaceEnabledAsync(connection, rejected.InstanceId, cancellationToken).ConfigureAwait(false)) return false;
        await ParkTaskConflictAsync(connection, rejected.InstanceId, rejected.DeviceId, rejected.EntityId, rejected.DeviceSequence, cancellationToken,
            expectedMutationId: rejected.MutationId, rejectionCode: code == "version_conflict" ? null : code).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task ParkTaskConflictAsync(SqliteConnection connection, string instanceId, string deviceId, string entityId, long sequence,
        CancellationToken cancellationToken, string? expectedMutationId = null, string? rejectionCode = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT local_sequence,mutation_id,entity_id,device_sequence,canonical_json,created_at,operation FROM personal_cloud_outbox WHERE instance_id=@i AND device_id=@d AND acknowledged_at IS NULL AND device_sequence>=@q ORDER BY device_sequence";
        command.Parameters.AddWithValue("@i", instanceId); command.Parameters.AddWithValue("@d", deviceId); command.Parameters.AddWithValue("@q", sequence);
        var tail = new List<(long Local, string Id, string Entity, long Sequence, string Json, long CreatedAt, string Operation)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) tail.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.GetInt64(5), reader.GetString(6)));
        if (tail.Count == 0 || tail[0].Entity != entityId || tail[0].Sequence != sequence ||
            expectedMutationId is not null && tail[0].Id != expectedMutationId)
            throw new PersonalCloudK0ContractException("version_conflict", "The pending queue changed; sync again before resolving conflicts.");
        var indirect = await ReadConflictIndirectTasksAsync(connection, instanceId, cancellationToken).ConfigureAwait(false);
        var affected = new HashSet<string>(StringComparer.Ordinal) { entityId };
        var parkedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in tail.Where(r => r.Operation.StartsWith("task.", StringComparison.Ordinal)))
        {
            var targets = indirect.GetValueOrDefault(row.Id) ?? [];
            if (!affected.Contains(row.Entity) && !targets.Any(affected.Contains)) continue;
            parkedIds.Add(row.Id); affected.Add(row.Entity); affected.UnionWith(targets);
        }
        foreach (var row in tail.Where(r => parkedIds.Contains(r.Id)))
        {
            var cloud = await ReadWorkspaceRecordAsync(connection, instanceId, "tasks", row.Entity, cancellationToken).ConfigureAwait(false);
            using var mutation = JsonDocument.Parse(row.Json);
            var baseVersion = mutation.RootElement.GetProperty("baseEntityVersion");
            var baseJson = cloud is { } record && baseVersion.ValueKind == JsonValueKind.Number && record.GetProperty("entityVersion").GetInt64() == baseVersion.GetInt64() ? record.GetRawText() : null;
            await WorkspaceExecuteAsync(connection, "INSERT INTO personal_cloud_task_conflicts(mutation_id,instance_id,device_id,entity_id,device_sequence,canonical_json,base_record_json,rejection_code) VALUES(@m,@i,@d,@e,@q,@j,@b,@code)", cancellationToken,
                ("@m", row.Id), ("@i", instanceId), ("@d", deviceId), ("@e", row.Entity), ("@q", row.Sequence), ("@j", row.Json), ("@b", baseJson),
                ("@code", row.Sequence == sequence ? rejectionCode : null)).ConfigureAwait(false);
        }
        await WorkspaceExecuteAsync(connection, "UPDATE personal_cloud_initial_sync SET seed_end_sequence=seed_end_sequence-(SELECT COUNT(*) FROM personal_cloud_outbox WHERE mutation_id IN(SELECT value FROM json_each(@ids)) AND device_sequence<=seed_end_sequence) WHERE instance_id=@i AND device_id=@d AND seed_end_sequence>=@q", cancellationToken,
            ("@q", sequence), ("@i", instanceId), ("@d", deviceId), ("@ids", JsonSerializer.Serialize(parkedIds))).ConfigureAwait(false);
        await WorkspaceExecuteAsync(connection, "DELETE FROM personal_cloud_outbox WHERE instance_id=@i AND device_id=@d AND acknowledged_at IS NULL AND device_sequence>=@q", cancellationToken, ("@i", instanceId), ("@d", deviceId), ("@q", sequence)).ConfigureAwait(false);
        var next = sequence;
        foreach (var row in tail.Where(r => !parkedIds.Contains(r.Id)))
        {
            foreach (var parked in tail.Where(p => parkedIds.Contains(p.Id) && p.Sequence < row.Sequence))
            {
                var versionedTasks = new HashSet<string>(indirect.GetValueOrDefault(parked.Id) ?? [], StringComparer.Ordinal) { parked.Entity };
                await WorkspaceExecuteAsync(connection, """
                    UPDATE personal_cloud_workspace_indirect_versions AS v
                    SET entity_version=MAX(entity_version-1, COALESCE((SELECT r.entity_version+1
                        FROM personal_cloud_workspace_records r WHERE r.instance_id=@i AND r.kind='tasks' AND r.entity_id=v.entity_id),1))
                    WHERE instance_id=@i AND mutation_id=@m AND kind='tasks' AND entity_id IN(SELECT value FROM json_each(@ids))
                    """, cancellationToken, ("@i", instanceId), ("@m", row.Id), ("@ids", JsonSerializer.Serialize(versionedTasks))).ConfigureAwait(false);
            }
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

    private sealed record ConflictState(string Id, string DeviceId, JsonObject? Base, JsonObject? Cloud, JsonObject Fields, bool Supported, IReadOnlyList<string> Operations)
    {
        public bool RetainsRecoveryCopy => Cloud is null && Base is null && Operations.Any(operation => operation is "task.create" or "task.createRich");
        public string? RejectionCode { get; init; }
        public IReadOnlyList<string> TaskIds { get; init; } = [Id];
        public IReadOnlyList<ConflictState> Dependents { get; init; } = [];
        public string RevisionToken { get; init; } = string.Empty;
    }

    private static async Task<Dictionary<string, HashSet<string>>> ReadConflictIndirectTasksAsync(SqliteConnection connection, string instanceId, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT mutation_id,entity_id FROM personal_cloud_workspace_indirect_versions WHERE instance_id=@i AND kind='tasks'";
        command.Parameters.AddWithValue("@i", instanceId);
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            if (!result.TryGetValue(id, out var tasks)) result[id] = tasks = new(StringComparer.Ordinal);
            tasks.Add(reader.GetString(1));
        }
        return result;
    }

    private static async Task<IReadOnlyList<ConflictState>> ReadTaskConflictsAsync(SqliteConnection connection, string instanceId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "personal_cloud_task_conflicts", cancellationToken).ConfigureAwait(false)) return [];
        var command = connection.CreateCommand();
        command.CommandText = "SELECT entity_id,device_id,canonical_json,base_record_json,rejection_code FROM personal_cloud_task_conflicts WHERE instance_id=@i ORDER BY rowid";
        command.Parameters.AddWithValue("@i", instanceId);
        var rows = new List<(string Id, string Device, string Json, string? Base, string? Code)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), GetNullableString(reader, 3), GetNullableString(reader, 4)));
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
            conflicts.Add(new(group.Key, first.Device, first.Base is null ? null : JsonNode.Parse(first.Base)!.AsObject(), cloud is { } value ? JsonNode.Parse(value.GetRawText())!.AsObject() : null, fields, supported, operations)
            { RejectionCode = group.FirstOrDefault(r => r.Code is not null).Code });
        }
        var indirect = await ReadConflictIndirectTasksAsync(connection, instanceId, cancellationToken).ConfigureAwait(false);
        var groups = new List<ConflictState>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var conflict in conflicts)
        {
            if (visited.Contains(conflict.Id)) continue;
            var ids = new HashSet<string>(StringComparer.Ordinal) { conflict.Id };
            bool expanded;
            do
            {
                expanded = false;
                foreach (var row in rows)
                {
                    var mutationId = JsonNode.Parse(row.Json)!["mutationId"]!.GetValue<string>();
                    var targets = indirect.GetValueOrDefault(mutationId) ?? [];
                    if (!ids.Contains(row.Id) && !targets.Any(ids.Contains)) continue;
                    if (ids.Add(row.Id)) expanded = true;
                    foreach (var id in targets) if (ids.Add(id)) expanded = true;
                }
            } while (expanded);
            visited.UnionWith(ids);
            var records = new List<string?>();
            foreach (var id in ids.Order(StringComparer.Ordinal))
                records.Add((await ReadWorkspaceRecordAsync(connection, instanceId, "tasks", id, cancellationToken).ConfigureAwait(false))?.GetRawText());
            groups.Add(conflict with
            {
                TaskIds = ids.Order(StringComparer.Ordinal).ToArray(),
                Dependents = conflicts.Where(c => c.Id != conflict.Id && ids.Contains(c.Id)).ToArray(),
                RevisionToken = PersonalCloudK0Codec.ComputeCanonicalHash(PersonalCloudK0Codec.Serialize(new
                {
                    rows = rows.Where(r => ids.Contains(r.Id)).Select(r => new { r.Json, r.Base, r.Code }).ToArray(),
                    affected = ids.Order(StringComparer.Ordinal).ToArray(), records,
                })),
            });
        }
        return groups;
    }

    public async Task<IReadOnlyList<PersonalCloudTaskConflict>> GetPersonalCloudTaskConflictsAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<PersonalCloudTaskConflict>();
        foreach (var c in await ReadTaskConflictsAsync(connection, instanceId, cancellationToken).ConfigureAwait(false))
        {
            var fields = new List<PersonalCloudConflictField>();
            foreach (var field in c.Fields.Where(f => c.Cloud is null || !JsonNode.DeepEquals(f.Value, c.Cloud[f.Key])))
                fields.Add(new(field.Key,
                    await DisplayConflictFieldValueAsync(connection, instanceId, field.Key, field.Value, cancellationToken).ConfigureAwait(false),
                    await DisplayConflictFieldValueAsync(connection, instanceId, field.Key, c.Cloud?[field.Key], cancellationToken).ConfigureAwait(false)));
            var dependentOperations = new List<string>();
            foreach (var dependent in c.Dependents)
            {
                var title = dependent.Fields["title"]?.GetValue<string>() ?? dependent.Cloud?["title"]?.GetValue<string>() ?? dependent.Base?["title"]?.GetValue<string>() ?? dependent.Id;
                dependentOperations.AddRange(dependent.Operations.Select(operation => $"{OperationLabel(operation)}: {title}"));
                foreach (var field in dependent.Fields)
                {
                    var label = new PersonalCloudConflictField(field.Key, "", "").DisplayName;
                    fields.Add(new($"{title} — {label}",
                        await DisplayConflictFieldValueAsync(connection, instanceId, field.Key, field.Value, cancellationToken).ConfigureAwait(false),
                        await DisplayConflictFieldValueAsync(connection, instanceId, field.Key, dependent.Cloud?[field.Key], cancellationToken).ConfigureAwait(false)));
                }
            }
            var reason = await ConflictReplayRestrictionAsync(connection, instanceId, c, cancellationToken).ConfigureAwait(false);
            result.Add(new(c.Id, c.Fields["title"]?.GetValue<string>() ?? c.Cloud?["title"]?.GetValue<string>() ?? c.Id, fields)
            {
                RevisionToken = c.RevisionToken,
                CanKeepDesktop = reason is null,
                ResolutionNotice = (c.RetainsRecoveryCopy
                    ? "Use cloud version keeps this task as a local recovery copy without uploading it to this cloud."
                    : c.Cloud is null
                    ? "No cached cloud record is available. Use cloud version discards the parked changes and restores the last known base when available; otherwise the local task is kept as a recovery copy until the next cloud download."
                    : reason is not null ? reason + " Only Use cloud version is available; it discards all parked desktop changes for this task."
                    : fields.Count == 0 ? "These field values already match the cloud. Choose a version to clear the parked changes." : string.Empty)
                    + (dependentOperations.Count == 0 ? string.Empty : " Use cloud version also discards: " + string.Join("; ", dependentOperations) + "."),
            });
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<bool> IsPersonalCloudTaskRecoveryCopyAsync(string instanceId, string taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM personal_cloud_task_recovery WHERE instance_id=@i AND task_id=@e)
              AND NOT EXISTS(SELECT 1 FROM personal_cloud_replica_entities WHERE instance_id=@i AND entity_id=@e AND product='tasks')
            """;
        command.Parameters.AddWithValue("@i", instanceId);
        command.Parameters.AddWithValue("@e", taskId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
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
        command.CommandText = "SELECT entity_id,device_sequence FROM personal_cloud_outbox o WHERE mutation_id=@m AND operation NOT IN ('source.refreshSnapshot','externalCommand.confirm','externalCommand.reject') AND EXISTS(SELECT 1 FROM personal_cloud_task_conflicts c WHERE c.instance_id=o.instance_id)";
        command.Parameters.AddWithValue("@m", mutationId);
        string entityId; long sequence;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return;
            entityId = reader.GetString(0); sequence = reader.GetInt64(1);
        }
        var targets = (await ReadConflictIndirectTasksAsync(connection, instanceId, cancellationToken).ConfigureAwait(false)).GetValueOrDefault(mutationId) ?? [];
        var groups = await ReadTaskConflictsAsync(connection, instanceId, cancellationToken).ConfigureAwait(false);
        if (!groups.Any(c => c.TaskIds.Contains(entityId) || c.TaskIds.Any(targets.Contains))) return;
        await ParkTaskConflictAsync(connection, instanceId, deviceId, entityId, sequence, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> HasConflictUploadsAsync(SqliteConnection connection, string instanceId, IReadOnlyList<string> taskIds, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM personal_cloud_outbox o WHERE o.instance_id=@i AND o.acknowledged_at IS NULL
              AND (o.entity_id IN(SELECT value FROM json_each(@ids))
                OR EXISTS(SELECT 1 FROM personal_cloud_workspace_indirect_versions v WHERE v.mutation_id=o.mutation_id
                  AND v.instance_id=@i AND v.kind='tasks' AND v.entity_id IN(SELECT value FROM json_each(@ids)))))
            """;
        command.Parameters.AddWithValue("@i", instanceId); command.Parameters.AddWithValue("@ids", JsonSerializer.Serialize(taskIds));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    private static async Task<string?> ConflictReplayRestrictionAsync(SqliteConnection connection, string instanceId, ConflictState conflict, CancellationToken cancellationToken)
    {
        if (conflict.Dependents.Count > 0)
            return "Parked changes include dependent task edits. Choosing the cloud version also discards the dependent edits shown below.";
        if (conflict.RejectionCode is { } code)
            return $"Personal Cloud rejected this edit ({code}). It cannot be safely replayed.";
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
            if (conflict.Cloud is null || await HasConflictUploadsAsync(connection, instanceId, conflict.TaskIds, cancellationToken).ConfigureAwait(false) ||
                await ConflictReplayRestrictionAsync(connection, instanceId, conflict, cancellationToken).ConfigureAwait(false) is not null ||
                !conflict.Fields.All(f => JsonNode.DeepEquals(f.Value, conflict.Cloud[f.Key]) || conflict.Base is not null && JsonNode.DeepEquals(conflict.Base[f.Key], conflict.Cloud[f.Key]))) continue;
            await ResolveTaskConflictAsync(connection, instanceId, conflict, true, cancellationToken).ConfigureAwait(false); resolved++;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return resolved;
    }

    public async Task ResolvePersonalCloudTaskConflictAsync(string instanceId, string taskId, bool keepDesktop, CancellationToken cancellationToken = default, string? expectedRevision = null)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var conflict = (await ReadTaskConflictsAsync(connection, instanceId, cancellationToken).ConfigureAwait(false)).SingleOrDefault(c => c.Id == taskId)
            ?? throw new PersonalCloudK0ContractException(expectedRevision is null ? "entity_not_found" : "conflict_changed", "This conflict changed. Review the refreshed card and choose again.");
        if (expectedRevision is not null && !string.Equals(expectedRevision, conflict.RevisionToken, StringComparison.Ordinal))
            throw new PersonalCloudK0ContractException("conflict_changed", "This conflict changed. Review the refreshed card and choose again.");
        await ResolveTaskConflictAsync(connection, instanceId, conflict, keepDesktop, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ResolveTaskConflictAsync(SqliteConnection connection, string instanceId, ConflictState conflict, bool keepDesktop, CancellationToken cancellationToken)
    {
        if (keepDesktop && await ConflictReplayRestrictionAsync(connection, instanceId, conflict, cancellationToken).ConfigureAwait(false) is not null)
            throw new PersonalCloudK0ContractException("invalid_transition", "Sync the latest cloud record before resolving this task. This edit cannot be safely replayed.");
        if (await HasConflictUploadsAsync(connection, instanceId, conflict.TaskIds, cancellationToken).ConfigureAwait(false))
            throw new PersonalCloudK0ContractException("pending_intents", "Sync the latest desktop edits before choosing a version.");
        if (!keepDesktop)
        {
            var dateTasks = conflict.Dependents.Prepend(conflict)
                .Where(c => c.Fields.ContainsKey("plannedOn") || c.Fields.ContainsKey("plannedAt")).Select(c => c.Id);
            await WorkspaceExecuteAsync(connection, "DELETE FROM pending_task_date_updates WHERE task_id IN(SELECT value FROM json_each(@ids))", cancellationToken,
                ("@ids", JsonSerializer.Serialize(dateTasks))).ConfigureAwait(false);
            foreach (var recovery in conflict.Dependents.Prepend(conflict).Where(c => c.RetainsRecoveryCopy))
                await WorkspaceExecuteAsync(connection, """
                    INSERT OR IGNORE INTO personal_cloud_task_recovery(instance_id,task_id)
                    SELECT @i,id FROM tasks WHERE id=@e
                    """, cancellationToken, ("@i", instanceId), ("@e", recovery.Id)).ConfigureAwait(false);
        }
        var records = new List<JsonObject>();
        foreach (var id in conflict.TaskIds.Where(id => id != conflict.Id))
        {
            var cached = await ReadWorkspaceRecordAsync(connection, instanceId, "tasks", id, cancellationToken).ConfigureAwait(false);
            var restored = cached is { } cloud ? JsonNode.Parse(cloud.GetRawText())!.AsObject() : conflict.Dependents.FirstOrDefault(c => c.Id == id)?.Base?.DeepClone().AsObject();
            if (restored is not null) records.Add(restored);
        }
        var ids = JsonSerializer.Serialize(conflict.TaskIds);
        await WorkspaceExecuteAsync(connection, "DELETE FROM personal_cloud_workspace_indirect_versions WHERE mutation_id IN (SELECT mutation_id FROM personal_cloud_task_conflicts WHERE instance_id=@i AND entity_id IN(SELECT value FROM json_each(@ids)))", cancellationToken, ("@i", instanceId), ("@ids", ids)).ConfigureAwait(false);
        await WorkspaceExecuteAsync(connection, "DELETE FROM personal_cloud_task_conflicts WHERE instance_id=@i AND entity_id IN(SELECT value FROM json_each(@ids))", cancellationToken, ("@i", instanceId), ("@ids", ids)).ConfigureAwait(false);
        if (!keepDesktop)
            await WorkspaceExecuteAsync(connection, "DELETE FROM personal_cloud_workspace_indirect_versions WHERE instance_id=@i AND kind='tasks' AND entity_id IN(SELECT value FROM json_each(@ids))", cancellationToken,
                ("@i", instanceId), ("@ids", ids)).ConfigureAwait(false);
        await WorkspaceExecuteAsync(connection, "DELETE FROM personal_cloud_tombstones WHERE instance_id=@i AND product='tasks' AND entity_id IN(SELECT value FROM json_each(@ids))", cancellationToken, ("@i", instanceId),
            ("@ids", JsonSerializer.Serialize(records.Where(r => r["deletedAt"] is null).Select(r => r["id"]!.GetValue<string>())))).ConfigureAwait(false);
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
        await DetachPersonalCloudOrphansAsync(connection, instanceId, includeMissing: false, cancellationToken).ConfigureAwait(false);
        if (differing.Count > 0)
            await AppendWorkspaceMutationAsync(connection, instanceId, conflict.DeviceId, "task.patch", conflict.Id, conflict.Cloud!["entityVersion"]!.GetValue<long>(), new { fields = differing }, cancellationToken).ConfigureAwait(false);
    }
}
