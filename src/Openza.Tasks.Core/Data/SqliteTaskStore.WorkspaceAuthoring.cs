using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Core.Data;

public sealed partial class SqliteTaskStore
{
    private static long WorkspaceMutationVersion(string json)
    {
        using var document = JsonDocument.Parse(json);
        var version = document.RootElement.GetProperty("baseEntityVersion");
        return version.ValueKind == JsonValueKind.Null ? 1 : checked(version.GetInt64() + 1);
    }

    private static async Task<long?> WorkspaceVersionAsync(SqliteConnection connection, string instanceId, string kind, string id, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT entity_version FROM personal_cloud_workspace_records WHERE instance_id=@i AND kind=@k AND entity_id=@e";
        command.Parameters.AddWithValue("@i", instanceId); command.Parameters.AddWithValue("@k", kind); command.Parameters.AddWithValue("@e", id);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        long? version = value is null or DBNull ? null : Convert.ToInt64(value);
        command = connection.CreateCommand();
        command.CommandText = "SELECT canonical_json FROM personal_cloud_outbox WHERE instance_id=@i AND entity_id=@e ORDER BY device_sequence";
        command.Parameters.AddWithValue("@i", instanceId); command.Parameters.AddWithValue("@e", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var candidate = WorkspaceMutationVersion(reader.GetString(0));
            version = version is null ? candidate : Math.Max(version.Value, candidate);
        }
        return await WithIndirectWorkspaceVersionAsync(connection, instanceId, kind, id, version, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long?> WithIndirectWorkspaceVersionAsync(SqliteConnection connection, string instanceId, string kind,
        string id, long? version, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(entity_version) FROM personal_cloud_workspace_indirect_versions WHERE instance_id=@i AND kind=@k AND entity_id=@e";
        command.Parameters.AddWithValue("@i", instanceId); command.Parameters.AddWithValue("@k", kind); command.Parameters.AddWithValue("@e", id);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? version : Math.Max(version ?? 0, Convert.ToInt64(value));
    }

    private static Task SaveIndirectWorkspaceVersionAsync(SqliteConnection connection, string instanceId, string kind, string id,
        string mutationId, long version, CancellationToken cancellationToken) => WorkspaceExecuteAsync(connection,
        "INSERT INTO personal_cloud_workspace_indirect_versions(instance_id,kind,entity_id,mutation_id,entity_version) VALUES(@i,@k,@e,@m,@v)",
        cancellationToken, ("@i", instanceId), ("@k", kind), ("@e", id), ("@m", mutationId), ("@v", checked(version + 1)));

    private static async Task<PersonalCloudK1OutboxEntry> AppendWorkspaceMutationAsync(SqliteConnection connection, string instanceId, string deviceId,
        string operation, string entityId, long? baseVersion, object payload, CancellationToken cancellationToken)
    {
        await WorkspaceExecuteAsync(connection, "INSERT OR IGNORE INTO personal_cloud_device_state(instance_id,device_id,next_device_sequence) VALUES(@i,@d,1)", cancellationToken, ("@i", instanceId), ("@d", deviceId)).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT next_device_sequence FROM personal_cloud_device_state WHERE instance_id=@i AND device_id=@d";
        command.Parameters.AddWithValue("@i", instanceId); command.Parameters.AddWithValue("@d", deviceId);
        var sequence = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        var mutationId = $"mutation_{Guid.NewGuid():N}"; var now = DateTimeOffset.UtcNow;
        var json = PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize(new
        {
            protocolVersion = 1, product = "tasks", instanceId, deviceId, mutationId, deviceSequence = sequence,
            operation, entityId, baseEntityVersion = baseVersion, payload, clientCreatedAt = PersonalCloudK0Mapper.FormatTimestamp(now),
        }));
        var hash = PersonalCloudK0Codec.ComputeCanonicalHash(json);
        command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO personal_cloud_outbox(mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at)
            VALUES(@m,'tasks',@i,@d,@q,@o,@e,@j,@h,@a) RETURNING local_sequence
            """;
        foreach (var (name, value) in new (string, object)[] { ("@m", mutationId), ("@i", instanceId), ("@d", deviceId), ("@q", sequence), ("@o", operation), ("@e", entityId), ("@j", json), ("@h", hash), ("@a", now.ToUnixTimeMilliseconds()) }) command.Parameters.AddWithValue(name, value);
        var localSequence = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        await WorkspaceExecuteAsync(connection, "UPDATE personal_cloud_device_state SET next_device_sequence=@q WHERE instance_id=@i AND device_id=@d", cancellationToken, ("@q", checked(sequence + 1)), ("@i", instanceId), ("@d", deviceId)).ConfigureAwait(false);
        return new() { LocalSequence = localSequence, MutationId = mutationId, Product = "tasks", InstanceId = instanceId, DeviceId = deviceId,
            DeviceSequence = sequence, Operation = operation, EntityId = entityId, CanonicalJson = json, CanonicalHash = hash, CreatedAt = now };
    }

    private static object SpacePayload(SpaceItem space, string? createdAt = null, bool preserveNullColor = false) => new { space = new { name = space.Name, color = preserveNullColor && space.Color == "#808080" ? null : space.Color, icon = space.Icon, sortOrder = space.SortOrder, isArchived = space.IsArchived, createdAt = createdAt ?? PersonalCloudK0Mapper.FormatTimestamp(space.CreatedAt) } };
    private static object ProjectPayload(ProjectItem project, string? createdAt = null, bool preserveNullColor = false) => new { project = new { spaceId = project.SpaceId, name = project.Name, description = project.Description, color = preserveNullColor && project.Color == "#808080" ? null : project.Color, icon = project.Icon, parentId = project.ParentId,
        sortOrder = project.SortOrder, status = project.IsArchived ? "archived" : project.Status, isFavorite = project.IsFavorite, createdAt = createdAt ?? PersonalCloudK0Mapper.FormatTimestamp(project.CreatedAt) } };

    private static async Task<JsonElement?> WorkspaceReferenceFactsAsync(SqliteConnection connection, string instanceId, string kind, string id, CancellationToken cancellationToken)
    {
        var confirmed = await ReadWorkspaceRecordAsync(connection, instanceId, kind, id, cancellationToken).ConfigureAwait(false);
        if (confirmed is { } record)
        {
            if (WText(record, "deletedAt") is not null) throw new PersonalCloudK0ContractException("invalid_transition", "A deleted reference cannot be restored.");
            return record;
        }
        var read = connection.CreateCommand(); read.CommandText = "SELECT canonical_json FROM personal_cloud_outbox WHERE instance_id=@i AND entity_id=@id AND operation=@op ORDER BY device_sequence LIMIT 1";
        read.Parameters.AddWithValue("@i", instanceId); read.Parameters.AddWithValue("@id", id); read.Parameters.AddWithValue("@op", kind == "spaces" ? "space.put" : "project.put");
        if ((string?)await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not { } json) return null;
        using var document = JsonDocument.Parse(json); return document.RootElement.GetProperty("payload").GetProperty(kind == "spaces" ? "space" : "project").Clone();
    }
    private static object RichTaskPayload(TaskItem task) => new
    {
        task = new { id = task.Id, spaceId = task.SpaceId, title = task.Title, workflowStatus = task.WorkflowStatus.ToStorageValue(), priority = task.Priority,
            createdAt = PersonalCloudK0Mapper.FormatTimestamp(task.CreatedAt), notes = task.Notes,
            plannedOn = task.PlannedAt is null ? DayString(task.PlannedOn) : null, plannedAt = MomentString(task.PlannedAt),
            deadlineOn = task.DeadlineAt is null ? DayString(task.DeadlineOn) : null, deadlineAt = MomentString(task.DeadlineAt),
            scheduledStart = MomentString(task.ScheduledStart), scheduledEnd = MomentString(task.ScheduledEnd), durationMinutes = task.DurationMinutes,
            recurrenceRule = task.RecurrenceRule, projectId = task.ProjectId, labels = task.Labels.Select(l => new { id = l.Id, name = l.Name }).ToArray() },
    };
    private static string? DayString(DateOnly? value) => value?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    private static string? MomentString(DateTimeOffset? value) => value is null ? null : PersonalCloudK0Mapper.FormatTimestamp(value.Value);

    public async Task UpsertSpaceWithPersonalCloudIntentAsync(SpaceItem space, string instanceId, string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await WorkspaceEnabledAsync(connection, instanceId, cancellationToken).ConfigureAwait(false)) throw new PersonalCloudK0ContractException("upgrade_required", "Upgrade Personal Cloud before synchronizing Space metadata.");
        var version = await WorkspaceVersionAsync(connection, instanceId, "spaces", space.Id, cancellationToken).ConfigureAwait(false);
        var facts = await WorkspaceReferenceFactsAsync(connection, instanceId, "spaces", space.Id, cancellationToken).ConfigureAwait(false);
        await UpsertSpaceCoreAsync(connection, space, cancellationToken).ConfigureAwait(false);
        await AppendWorkspaceMutationAsync(connection, instanceId, deviceId, "space.put", space.Id, version, SpacePayload(space, facts is { } known ? WText(known, "createdAt") : null, facts is { } color && WText(color, "color") is null), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertProjectWithPersonalCloudIntentAsync(ProjectItem project, string instanceId, string deviceId, CancellationToken cancellationToken = default)
    {
        if (project.IntegrationId != IntegrationIds.Local) throw new PersonalCloudK0ContractException("invalid_request", "Provider projects remain owned by their local integration.");
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await WorkspaceEnabledAsync(connection, instanceId, cancellationToken).ConfigureAwait(false)) throw new PersonalCloudK0ContractException("upgrade_required", "Upgrade Personal Cloud before synchronizing project metadata.");
        var version = await WorkspaceVersionAsync(connection, instanceId, "projects", project.Id, cancellationToken).ConfigureAwait(false);
        var facts = await WorkspaceReferenceFactsAsync(connection, instanceId, "projects", project.Id, cancellationToken).ConfigureAwait(false);
        if (facts is { } existing && WText(existing, "spaceId") != project.SpaceId) throw new PersonalCloudK0ContractException("invalid_transition", "A project's Space is immutable.");
        await UpsertProjectCoreAsync(connection, project, cancellationToken).ConfigureAwait(false);
        await AppendWorkspaceMutationAsync(connection, instanceId, deviceId, "project.put", project.Id, version, ProjectPayload(project, facts is { } known ? WText(known, "createdAt") : null, facts is { } color && WText(color, "color") is null), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteProjectWithPersonalCloudIntentAsync(string projectId, string instanceId, string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var version = await WorkspaceVersionAsync(connection, instanceId, "projects", projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new PersonalCloudK0ContractException("entity_not_found", "Publish this project before deleting it from Personal Cloud.");
        var affected = new List<(string Kind, string Id, long Version)>();
        foreach (var task in (await ReadTasksAsync(connection, cancellationToken).ConfigureAwait(false)).Where(t => t.ProjectId == projectId))
            if (await ReadPersonalCloudEntityVersionAsync(connection, instanceId, task.Id, cancellationToken).ConfigureAwait(false) is { } taskVersion)
                affected.Add(("tasks", task.Id, taskVersion));
        var children = connection.CreateCommand(); children.CommandText = "SELECT id FROM projects WHERE parent_id=@id";
        children.Parameters.AddWithValue("@id", projectId);
        var childIds = new List<string>();
        await using (var reader = await children.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) childIds.Add(reader.GetString(0));
        foreach (var id in childIds)
            if (await WorkspaceVersionAsync(connection, instanceId, "projects", id, cancellationToken).ConfigureAwait(false) is { } childVersion)
                affected.Add(("projects", id, childVersion));
        await WorkspaceExecuteAsync(connection, """
            UPDATE tasks SET project_id=NULL,workflow_status='inbox',revision=revision+1 WHERE project_id=@id;
            UPDATE projects SET parent_id=NULL WHERE parent_id=@id;
            DELETE FROM projects WHERE id=@id;
            """, cancellationToken, ("@id", projectId)).ConfigureAwait(false);
        var entry = await AppendWorkspaceMutationAsync(connection, instanceId, deviceId, "project.delete", projectId, version, new { }, cancellationToken).ConfigureAwait(false);
        foreach (var item in affected)
            await SaveIndirectWorkspaceVersionAsync(connection, instanceId, item.Kind, item.Id, entry.MutationId, item.Version, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateRichTaskWithPersonalCloudIntentAsync(TaskItem task, string instanceId, string deviceId, K0SafeSourceDescriptor? source = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await WorkspaceEnabledAsync(connection, instanceId, cancellationToken).ConfigureAwait(false)) throw new PersonalCloudK0ContractException("upgrade_required", "Rich task creation requires schema 23.");
        var check = connection.CreateCommand(); check.CommandText = "SELECT EXISTS(SELECT 1 FROM tasks WHERE id=@id)"; check.Parameters.AddWithValue("@id", task.Id);
        if (Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0 || await ReadPersonalCloudEntityVersionAsync(connection, instanceId, task.Id, cancellationToken).ConfigureAwait(false) is not null)
            throw new PersonalCloudK0ContractException("entity_exists", "This task identity already exists.");
        if (task.IntegrationId != IntegrationIds.Local) throw PersonalCloudWorkspaceCodec.Invalid();
        await UpsertTaskCoreAsync(connection, task, cancellationToken).ConfigureAwait(false);
        await SetTaskLabelsCoreAsync(connection, task.Id, task.Labels, cancellationToken).ConfigureAwait(false);
        await AppendRichTaskAsync(connection, task, instanceId, deviceId, source, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PersonalCloudK1OutboxEntry> PatchTaskWithPersonalCloudIntentAsync(string taskId, IReadOnlyDictionary<string, object?> fields,
        long expectedRevision, string instanceId, string deviceId, CancellationToken cancellationToken = default)
    {
        var allowed = new HashSet<string>("title notes workflowStatus priority plannedOn plannedAt deadlineOn deadlineAt scheduledStart scheduledEnd durationMinutes recurrenceRule projectId".Split(' '), StringComparer.Ordinal);
        if (fields.Count == 0 || fields.Keys.Any(k => !allowed.Contains(k))) throw PersonalCloudWorkspaceCodec.Invalid();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await WorkspaceEnabledAsync(connection, instanceId, cancellationToken).ConfigureAwait(false)) throw new PersonalCloudK0ContractException("upgrade_required", "Workspace field edits require schema 23.");
        var original = (await ReadTasksAsync(connection, cancellationToken).ConfigureAwait(false)).SingleOrDefault(t => t.Id == taskId)
            ?? throw new PersonalCloudK0ContractException("entity_not_found", "The task does not exist.");
        if (original.Revision != expectedRevision) throw new PersonalCloudK0ContractException("version_conflict", "The task changed after it was loaded.");
        if (fields.ContainsKey("title") && original.SourceIntegrationId is not null)
            throw new PersonalCloudK0ContractException("invalid_transition", "Provider titles are owned by their source.");
        var version = await ReadPersonalCloudEntityVersionAsync(connection, instanceId, taskId, cancellationToken).ConfigureAwait(false)
            ?? throw new PersonalCloudK0ContractException("entity_not_found", "Publish the task before editing its workspace fields.");
        using var document = JsonDocument.Parse(PersonalCloudK0Codec.Serialize(fields)); var json = document.RootElement;
        var updated = original;
        foreach (var field in json.EnumerateObject())
        {
            updated = field.Name switch
            {
                "title" => updated with { Title = WText(json, field.Name)! },
                "notes" => updated with { Notes = WText(json, field.Name) },
                "workflowStatus" => updated with { WorkflowStatus = TaskStatusExtensions.WorkflowFromStorageValue(WText(json, field.Name)) },
                "priority" => updated with { Priority = field.Value.GetInt32() },
                "plannedOn" => updated with { PlannedOn = WDay(json, field.Name), PlannedAt = fields.ContainsKey("plannedAt") ? WMoment(json, "plannedAt") : null },
                "plannedAt" => updated with { PlannedAt = WMoment(json, field.Name), PlannedOn = fields.ContainsKey("plannedOn") ? WDay(json, "plannedOn") : null },
                "deadlineOn" => updated with { DeadlineOn = WDay(json, field.Name), DeadlineAt = fields.ContainsKey("deadlineAt") ? WMoment(json, "deadlineAt") : null },
                "deadlineAt" => updated with { DeadlineAt = WMoment(json, field.Name), DeadlineOn = fields.ContainsKey("deadlineOn") ? WDay(json, "deadlineOn") : null },
                "scheduledStart" => updated with { ScheduledStart = WMoment(json, field.Name) },
                "scheduledEnd" => updated with { ScheduledEnd = WMoment(json, field.Name) },
                "durationMinutes" => updated with { DurationMinutes = field.Value.ValueKind == JsonValueKind.Null ? null : field.Value.GetInt32() },
                "recurrenceRule" => updated with { RecurrenceRule = WText(json, field.Name) },
                "projectId" => updated with { ProjectId = WText(json, field.Name) },
                _ => throw PersonalCloudWorkspaceCodec.Invalid(),
            };
        }
        if (fields.ContainsKey("plannedOn") && fields.ContainsKey("plannedAt") && WText(json, "plannedOn") is not null && WText(json, "plannedAt") is not null ||
            fields.ContainsKey("deadlineOn") && fields.ContainsKey("deadlineAt") && WText(json, "deadlineOn") is not null && WText(json, "deadlineAt") is not null)
            throw PersonalCloudWorkspaceCodec.Invalid();
        using var snapshot = JsonDocument.Parse(PersonalCloudK0Codec.Serialize(new
        {
            id = updated.Id, spaceId = updated.SpaceId, projectId = updated.ProjectId, parentId = updated.ParentId, title = updated.Title,
            completionState = updated.CompletionState.ToStorageValue(), workflowStatus = updated.WorkflowStatus.ToStorageValue(), priority = updated.Priority,
            plannedOn = updated.PlannedAt is null ? DayString(updated.PlannedOn) : null, plannedAt = MomentString(updated.PlannedAt), deadlineOn = updated.DeadlineAt is null ? DayString(updated.DeadlineOn) : null, deadlineAt = MomentString(updated.DeadlineAt),
            scheduledStart = MomentString(updated.ScheduledStart), scheduledEnd = MomentString(updated.ScheduledEnd), durationMinutes = updated.DurationMinutes,
            recurrenceRule = updated.RecurrenceRule, notes = updated.Notes, completedAt = MomentString(updated.CompletedAt), createdAt = MomentString(updated.CreatedAt),
            entityVersion = version + 1, deletedAt = (string?)null, labels = updated.Labels.Select(l => new { id = l.Id, name = l.Name }), source = (object?)null, sourceOwnerDeviceId = (string?)null,
        }));
        PersonalCloudWorkspaceCodec.ValidateRecord("tasks", snapshot.RootElement);
        if (updated.ProjectId is { } projectId)
        {
            var project = connection.CreateCommand();
            project.CommandText = "SELECT EXISTS(SELECT 1 FROM projects WHERE id=@id AND space_id=@s AND integration_id='openza_tasks' AND is_archived=0 AND status!='archived')";
            project.Parameters.AddWithValue("@id", projectId); project.Parameters.AddWithValue("@s", updated.SpaceId);
            if (Convert.ToInt64(await project.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0)
                throw new PersonalCloudK0ContractException("parent_not_found", "The project is not available in this task's Space.");
        }
        if (fields.TryGetValue("workflowStatus", out var requestedStatus) && requestedStatus is not ("none" or "inbox" or "next" or "waiting" or "someday")) throw PersonalCloudWorkspaceCodec.Invalid();
        await UpsertTaskCoreAsync(connection, updated with { UpdatedAt = DateTimeOffset.UtcNow, Revision = updated.Revision + 1 }, cancellationToken).ConfigureAwait(false);
        var entry = await AppendWorkspaceMutationAsync(connection, instanceId, deviceId, "task.patch", taskId, version, new { fields }, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return entry;
    }

    private static async Task AppendRichTaskAsync(SqliteConnection connection, TaskItem task, string instanceId, string deviceId, K0SafeSourceDescriptor? source, CancellationToken cancellationToken)
    {
        using var validation = JsonDocument.Parse(PersonalCloudK0Codec.Serialize(new {
            id = task.Id, spaceId = task.SpaceId, projectId = task.ProjectId, parentId = task.ParentId, title = task.Title,
            completionState = task.CompletionState.ToStorageValue(), workflowStatus = task.WorkflowStatus.ToStorageValue(), priority = task.Priority,
            plannedOn = task.PlannedAt is null ? DayString(task.PlannedOn) : null, plannedAt = MomentString(task.PlannedAt),
            deadlineOn = task.DeadlineAt is null ? DayString(task.DeadlineOn) : null, deadlineAt = MomentString(task.DeadlineAt),
            scheduledStart = MomentString(task.ScheduledStart), scheduledEnd = MomentString(task.ScheduledEnd), durationMinutes = task.DurationMinutes,
            recurrenceRule = task.RecurrenceRule, notes = task.Notes, completedAt = MomentString(task.CompletedAt), createdAt = MomentString(task.CreatedAt),
            entityVersion = 1, deletedAt = (string?)null, labels = task.Labels.Select(l => new { id = l.Id, name = l.Name }), source, sourceOwnerDeviceId = source is null ? null : deviceId }));
        PersonalCloudWorkspaceCodec.ValidateRecord("tasks", validation.RootElement);
        var payload = RichTaskPayload(task);
        if (source is not null)
        {
            using var document = JsonDocument.Parse(PersonalCloudK0Codec.Serialize(payload));
            payload = new { task = document.RootElement.GetProperty("task").Clone(), source };
        }
        await AppendWorkspaceMutationAsync(connection, instanceId, deviceId, "task.createRich", task.Id, null, payload, cancellationToken).ConfigureAwait(false);
        var version = 1L;
        if (task.ParentId is not null)
            await AppendWorkspaceMutationAsync(connection, instanceId, deviceId, "task.setParent", task.Id, version++, new { parentId = task.ParentId }, cancellationToken).ConfigureAwait(false);
        if (task.IsCompleted)
            await AppendWorkspaceMutationAsync(connection, instanceId, deviceId, "task.complete", task.Id, version, new { completedAt = PersonalCloudK0Mapper.FormatTimestamp(task.CompletedAt ?? DateTimeOffset.UtcNow) }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> StagePersonalCloudWorkspaceSeedAsync(string instanceId, string deviceId, CancellationToken cancellationToken)
    {
        var spaces = await GetSpacesAsync(includeArchived: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        var projects = (await GetProjectsAsync(includeArchived: true, cancellationToken: cancellationToken).ConfigureAwait(false)).Where(p => p.IntegrationId == IntegrationIds.Local).ToList();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var count = 0;
        var newSpaces = new List<SpaceItem>(); var newProjects = new List<ProjectItem>();
        foreach (var space in spaces)
        {
            if (await WorkspaceVersionAsync(connection, instanceId, "spaces", space.Id, cancellationToken).ConfigureAwait(false) is not null) continue;
            await AppendWorkspaceMutationAsync(connection, instanceId, deviceId, "space.put", space.Id, null, SpacePayload(space with { IsArchived = false }), cancellationToken).ConfigureAwait(false);
            newSpaces.Add(space); count++;
        }
        var stagedProjects = new HashSet<string>(StringComparer.Ordinal);
        while (projects.Count > 0)
        {
            var ready = projects.Where(p => p.ParentId is null || stagedProjects.Contains(p.ParentId) || !projects.Any(q => q.Id == p.ParentId)).ToList();
            if (ready.Count == 0) throw new PersonalCloudK0ContractException("invalid_request", "The local project hierarchy contains a cycle.");
            foreach (var project in ready)
            {
                if (await WorkspaceVersionAsync(connection, instanceId, "projects", project.Id, cancellationToken).ConfigureAwait(false) is null)
                {
                    await AppendWorkspaceMutationAsync(connection, instanceId, deviceId, "project.put", project.Id, null, ProjectPayload(project with { Status = "active", IsArchived = false }), cancellationToken).ConfigureAwait(false);
                    newProjects.Add(project); count++;
                }
                stagedProjects.Add(project.Id); projects.Remove(project);
            }
        }
        var tasks = (await ReadTasksAsync(connection, cancellationToken).ConfigureAwait(false)).Where(t => t.IntegrationId == IntegrationIds.Local).ToList();
        var published = connection.CreateCommand(); published.CommandText = """
            SELECT entity_id FROM personal_cloud_outbox WHERE instance_id=@i AND operation IN('task.create','task.createRich')
            UNION SELECT entity_id FROM personal_cloud_replica_entities WHERE instance_id=@i AND product='tasks'
            """; published.Parameters.AddWithValue("@i", instanceId);
        var known = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await published.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) known.Add(reader.GetString(0));
        foreach (var task in tasks.Where(t => !known.Contains(t.Id)))
        {
            await AppendRichTaskAsync(connection, task with { ParentId = null }, instanceId, deviceId, PersonalCloudK0Mapper.CreateSafeSourceDescriptor(task), cancellationToken).ConfigureAwait(false);
            count += task.IsCompleted ? 2 : 1;
        }
        foreach (var task in tasks.Where(t => !known.Contains(t.Id) && t.ParentId is not null))
        {
            await AppendWorkspaceMutationAsync(connection, instanceId, deviceId, "task.setParent", task.Id, task.IsCompleted ? 2 : 1, new { parentId = task.ParentId }, cancellationToken).ConfigureAwait(false); count++;
        }
        foreach (var project in newProjects.Where(p => p.IsArchived || p.Status != "active"))
        {
            await AppendWorkspaceMutationAsync(connection, instanceId, deviceId, "project.put", project.Id, 1, ProjectPayload(project), cancellationToken).ConfigureAwait(false); count++;
        }
        foreach (var space in newSpaces.Where(s => s.IsArchived))
        {
            await AppendWorkspaceMutationAsync(connection, instanceId, deviceId, "space.put", space.Id, 1, SpacePayload(space), cancellationToken).ConfigureAwait(false); count++;
        }
        await SealInitialSyncSeedAsync(connection, instanceId, deviceId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }
}
