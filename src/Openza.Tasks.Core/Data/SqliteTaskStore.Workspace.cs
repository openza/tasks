using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Core.Data;

public sealed partial class SqliteTaskStore
{
    public async Task SavePersonalCloudWorkspaceCapabilitiesAsync(string instanceId, bool workspace, bool providerCatalog, CancellationToken cancellationToken = default, int providerCatalogProtocolVersion = 1)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await WorkspaceExecuteAsync(connection, """
            INSERT INTO personal_cloud_workspace_capabilities(instance_id,workspace,provider_catalog) VALUES(@i,@w,@p)
            ON CONFLICT(instance_id) DO UPDATE SET workspace=excluded.workspace,provider_catalog=excluded.provider_catalog
            """, cancellationToken, ("@i", instanceId), ("@w", workspace ? 1 : 0), ("@p", providerCatalog ? providerCatalogProtocolVersion : 0)).ConfigureAwait(false);
    }

    public async Task<bool> SupportsPersonalCloudWorkspaceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await WorkspaceEnabledAsync(connection, instanceId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasPersonalCloudWorkspaceBaselineAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand(); command.CommandText = "SELECT hydrated FROM personal_cloud_workspace_capabilities WHERE instance_id=@i";
        command.Parameters.AddWithValue("@i", instanceId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    private static async Task<bool> WorkspaceEnabledAsync(SqliteConnection connection, string instanceId, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT workspace FROM personal_cloud_workspace_capabilities WHERE instance_id=@i";
        command.Parameters.AddWithValue("@i", instanceId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    public async Task<bool> SupportsPersonalCloudProviderScopesAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand(); command.CommandText = "SELECT provider_catalog FROM personal_cloud_workspace_capabilities WHERE instance_id=@i";
        command.Parameters.AddWithValue("@i", instanceId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 2;
    }

    public async Task<IReadOnlyList<JsonElement>> GetPersonalCloudWorkspaceRecordsAsync(string instanceId, string kind, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT canonical_json FROM personal_cloud_workspace_records WHERE instance_id=@i AND kind=@k ORDER BY entity_id";
        command.Parameters.AddWithValue("@i", instanceId); command.Parameters.AddWithValue("@k", kind);
        var records = new List<JsonElement>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            using var document = JsonDocument.Parse(reader.GetString(0)); records.Add(document.RootElement.Clone());
        }
        return records;
    }

    private static async Task<bool> SaveWorkspaceRecordAsync(SqliteConnection connection, string instanceId, string kind, JsonElement item, CancellationToken cancellationToken)
    {
        if (kind == "tasks")
        {
            var existing = connection.CreateCommand(); existing.CommandText = "SELECT entity_version FROM personal_cloud_entity_versions WHERE instance_id=@i AND product='tasks' AND entity_id=@id";
            existing.Parameters.AddWithValue("@i", instanceId); existing.Parameters.AddWithValue("@id", WText(item, "id")!);
            var version = await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (version is not null and not DBNull && Convert.ToInt64(version) > item.GetProperty("entityVersion").GetInt64()) return false;
        }
        if (kind is "spaces" or "projects" or "tasks" && WText(item, "deletedAt") is null)
        {
            var previous = await ReadWorkspaceRecordAsync(connection, instanceId, kind, WText(item, "id")!, cancellationToken).ConfigureAwait(false);
            if (previous is { } record && WText(record, "deletedAt") is not null) return false;
        }
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO personal_cloud_workspace_records(instance_id,kind,entity_id,entity_version,canonical_json)
            VALUES(@i,@k,@e,@v,@j) ON CONFLICT(instance_id,kind,entity_id) DO UPDATE SET
              entity_version=excluded.entity_version,canonical_json=excluded.canonical_json
            WHERE personal_cloud_workspace_records.entity_version<excluded.entity_version
            """;
        command.Parameters.AddWithValue("@i", instanceId); command.Parameters.AddWithValue("@k", kind);
        command.Parameters.AddWithValue("@e", WText(item, "id")!); command.Parameters.AddWithValue("@v", item.GetProperty("entityVersion").GetInt64());
        command.Parameters.AddWithValue("@j", PersonalCloudK0Codec.Canonicalize(item.GetRawText()));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async Task<bool> HasPendingWorkspaceOverlayAsync(SqliteConnection connection, string instanceId, string id, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM personal_cloud_outbox WHERE instance_id=@i AND entity_id=@e
              AND acknowledged_at IS NULL AND operation NOT IN('source.refreshSnapshot','externalCommand.confirm','externalCommand.reject'))
              OR EXISTS(SELECT 1 FROM personal_cloud_workspace_indirect_versions v JOIN personal_cloud_outbox o ON o.mutation_id=v.mutation_id
                WHERE v.instance_id=@i AND v.entity_id=@e AND o.acknowledged_at IS NULL)
            """;
        command.Parameters.AddWithValue("@i", instanceId); command.Parameters.AddWithValue("@e", id);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    private async Task ApplyWorkspacePayloadAsync(SqliteConnection connection, string instanceId, JsonElement payload, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, TaskItem>? priorTasks = null)
    {
        // Store every complete server record atomically. While delivery is pending, domain
        // rows retain their optimistic overlay; its exact outbox envelope remains authoritative.
        foreach (var item in payload.GetProperty("spaces").EnumerateArray())
        {
            if (!await SaveWorkspaceRecordAsync(connection, instanceId, "spaces", item, cancellationToken).ConfigureAwait(false)) continue;
            if (await HasPendingWorkspaceOverlayAsync(connection, instanceId, WText(item, "id")!, cancellationToken).ConfigureAwait(false)) continue;
            await WorkspaceExecuteAsync(connection, """
                INSERT INTO spaces(id,name,color,icon,sort_order,is_archived,created_at,updated_at) VALUES(@id,@n,@c,@ic,@o,@a,@cr,@u)
                ON CONFLICT(id) DO UPDATE SET name=excluded.name,color=excluded.color,icon=excluded.icon,
                  sort_order=excluded.sort_order,is_archived=excluded.is_archived,created_at=excluded.created_at,updated_at=excluded.updated_at
                """, cancellationToken, ("@id", WText(item, "id")), ("@n", WText(item, "name")), ("@c", WText(item, "color")),
                ("@ic", WText(item, "icon")), ("@o", item.GetProperty("sortOrder").GetInt32()),
                ("@a", WText(item, "deletedAt") is not null || item.GetProperty("isArchived").GetBoolean() ? 1 : 0),
                ("@cr", WDate(item, "createdAt")), ("@u", WDate(item, "updatedAt"))).ConfigureAwait(false);
        }
        foreach (var item in payload.GetProperty("projects").EnumerateArray())
        {
            if (!await SaveWorkspaceRecordAsync(connection, instanceId, "projects", item, cancellationToken).ConfigureAwait(false)) continue;
            if (await HasPendingWorkspaceOverlayAsync(connection, instanceId, WText(item, "id")!, cancellationToken).ConfigureAwait(false)) continue;
            if (WText(item, "deletedAt") is not null)
            {
                // The complete task array clears assignments later in this same transaction.
                // Retain a hidden archived FK target while a local optimistic task still refers to it.
                await WorkspaceExecuteAsync(connection, "UPDATE projects SET is_archived=1,status='archived' WHERE id=@id", cancellationToken, ("@id", WText(item, "id"))).ConfigureAwait(false);
                continue;
            }
            await WorkspaceExecuteAsync(connection, """
                INSERT INTO projects(id,space_id,integration_id,name,description,color,icon,parent_id,sort_order,status,is_archived,is_favorite,created_at,updated_at)
                VALUES(@id,@s,'openza_tasks',@n,@d,@c,@ic,@p,@o,@st,@a,@f,@cr,@u)
                ON CONFLICT(id) DO UPDATE SET name=excluded.name,description=excluded.description,color=excluded.color,
                  icon=excluded.icon,parent_id=excluded.parent_id,sort_order=excluded.sort_order,status=excluded.status,
                  is_archived=excluded.is_archived,is_favorite=excluded.is_favorite,created_at=excluded.created_at,updated_at=excluded.updated_at
                """, cancellationToken, ("@id", WText(item, "id")), ("@s", WText(item, "spaceId")), ("@n", WText(item, "name")),
                ("@d", WText(item, "description")), ("@c", WText(item, "color")), ("@ic", WText(item, "icon")), ("@p", WText(item, "parentId")),
                ("@o", item.GetProperty("sortOrder").GetInt32()), ("@st", WText(item, "status")), ("@a", WText(item, "status") == "archived" ? 1 : 0),
                ("@f", item.GetProperty("isFavorite").GetBoolean() ? 1 : 0), ("@cr", WDate(item, "createdAt")), ("@u", WDate(item, "updatedAt"))).ConfigureAwait(false);
        }
        var existing = priorTasks ?? (payload.GetProperty("tasks").GetArrayLength() == 0 ? new Dictionary<string, TaskItem>() :
            (await ReadTasksAsync(connection, cancellationToken).ConfigureAwait(false)).ToDictionary(t => t.Id, StringComparer.Ordinal));
        foreach (var item in payload.GetProperty("tasks").EnumerateArray())
        {
            var id = WText(item, "id")!;
            if (!await SaveWorkspaceRecordAsync(connection, instanceId, "tasks", item, cancellationToken).ConfigureAwait(false)) continue;
            var version = item.GetProperty("entityVersion").GetInt64();
            await UpsertPersonalCloudEntityVersionAsync(connection, instanceId, id, version, cancellationToken).ConfigureAwait(false);
            if (await HasPendingWorkspaceOverlayAsync(connection, instanceId, id, cancellationToken).ConfigureAwait(false)) continue;
            if (WText(item, "deletedAt") is not null)
            {
                await WorkspaceExecuteAsync(connection, """
                    INSERT INTO personal_cloud_tombstones(instance_id,product,entity_id,entity_version,deleted_at,purge_after)
                    VALUES(@i,'tasks',@e,@v,@d,NULL) ON CONFLICT(instance_id,product,entity_id) DO UPDATE SET
                      entity_version=excluded.entity_version,deleted_at=excluded.deleted_at
                    """, cancellationToken, ("@i", instanceId), ("@e", id), ("@v", version), ("@d", WDate(item, "deletedAt"))).ConfigureAwait(false);
                continue;
            }
            var sourceJson = item.GetProperty("source");
            var source = sourceJson.ValueKind == JsonValueKind.Null ? null : PersonalCloudK0Codec.Deserialize<K0SafeSourceDescriptor>(sourceJson.GetRawText());
            existing.TryGetValue(id, out var prior);
            var priorSource = prior is null ? null : PersonalCloudK0Mapper.CreateSafeSourceDescriptor(prior);
            var sameSource = source is not null && priorSource is not null && source.IntegrationKind == priorSource.IntegrationKind && source.ExternalIdHash == priorSource.ExternalIdHash;
            var task = (prior ?? new TaskItem { Id = id }) with
            {
                SpaceId = WText(item, "spaceId")!, ProjectId = WText(item, "projectId"), ParentId = WText(item, "parentId"),
                Title = WText(item, "title")!, CompletionState = TaskStatusExtensions.CompletionFromStorageValue(WText(item, "completionState")),
                WorkflowStatus = TaskStatusExtensions.WorkflowFromStorageValue(WText(item, "workflowStatus")), Priority = item.GetProperty("priority").GetInt32(),
                PlannedOn = WDay(item, "plannedOn"), PlannedAt = WMoment(item, "plannedAt"), DeadlineOn = WDay(item, "deadlineOn"), DeadlineAt = WMoment(item, "deadlineAt"),
                ScheduledStart = WMoment(item, "scheduledStart"), ScheduledEnd = WMoment(item, "scheduledEnd"),
                DurationMinutes = item.GetProperty("durationMinutes").ValueKind == JsonValueKind.Null ? null : item.GetProperty("durationMinutes").GetInt32(),
                RecurrenceRule = WText(item, "recurrenceRule"), Notes = WText(item, "notes"), CompletedAt = WMoment(item, "completedAt"),
                CreatedAt = WMoment(item, "createdAt")!.Value,
                UpdatedAt = item.TryGetProperty("updatedAt", out _) ? WMoment(item, "updatedAt") : prior?.UpdatedAt,
                Revision = (prior?.Revision ?? 0) + 1,
                SourceIntegrationId = source?.IntegrationKind, SourceExternalId = sameSource ? prior?.SourceExternalId : null,
                SourceConnectionId = sameSource ? prior?.SourceConnectionId : null, SourceProviderTaskId = sameSource ? prior?.SourceProviderTaskId : null,
                SourceUrl = source?.SourceUrl, SourceTitle = source?.Title, SourceDescription = source?.Description, SourceProjectName = source?.ProjectName,
                SourcePriority = source?.Priority, SourcePlannedOn = source?.PlannedOn is null ? null : DateOnly.ParseExact(source.PlannedOn, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                SourcePlannedAt = source?.PlannedAt is null ? null : ParseProtocolTimestamp(source.PlannedAt),
                SourceDeadlineOn = source?.DeadlineOn is null ? null : DateOnly.ParseExact(source.DeadlineOn, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                SourceDeadlineAt = source?.DeadlineAt is null ? null : ParseProtocolTimestamp(source.DeadlineAt),
                SourceMetadataJson = source is null ? null : PersonalCloudK0Codec.Serialize(source),
            };
            await UpsertTaskCoreAsync(connection, task, cancellationToken).ConfigureAwait(false);
            await SetTaskLabelsCoreAsync(connection, id, item.GetProperty("labels").EnumerateArray().Select(l => new LabelItem { Id = WText(l, "id")!, Name = WText(l, "name")!, IntegrationId = IntegrationIds.Local }).ToArray(), cancellationToken).ConfigureAwait(false);
            await WorkspaceExecuteAsync(connection, "INSERT OR IGNORE INTO personal_cloud_replica_entities(instance_id,product,entity_id) VALUES(@i,'tasks',@e)", cancellationToken, ("@i", instanceId), ("@e", id)).ConfigureAwait(false);
            if (source is not null) await SaveBridgeSourceAsync(connection, instanceId, id, source, cancellationToken).ConfigureAwait(false);
            else await WorkspaceExecuteAsync(connection, "DELETE FROM personal_cloud_provider_sources WHERE instance_id=@i AND task_id=@e", cancellationToken, ("@i", instanceId), ("@e", id)).ConfigureAwait(false);
        }
        foreach (var kind in new[] { "scopes", "catalog", "intents", "routes" })
        foreach (var item in payload.TryGetProperty(kind, out var array) ? array.EnumerateArray() : Enumerable.Empty<JsonElement>())
        {
            await SaveWorkspaceRecordAsync(connection, instanceId, kind, item, cancellationToken).ConfigureAwait(false);
            if (kind == "intents" && WText(item, "state") is "confirmed" or "rejected")
                await WorkspaceExecuteAsync(connection, "UPDATE personal_cloud_provider_intent_actions SET phase='settled' WHERE instance_id=@i AND intent_id=@id", cancellationToken,
                    ("@i", instanceId), ("@id", WText(item, "id"))).ConfigureAwait(false);
        }
        await WorkspaceExecuteAsync(connection, """
            DELETE FROM projects WHERE id IN(SELECT entity_id FROM personal_cloud_workspace_records
              WHERE instance_id=@i AND kind='projects' AND json_extract(canonical_json,'$.deletedAt') IS NOT NULL)
              AND NOT EXISTS(SELECT 1 FROM tasks WHERE project_id=projects.id)
            """, cancellationToken, ("@i", instanceId)).ConfigureAwait(false);
    }

    public async Task<long> ReplacePersonalCloudWorkspaceFromBootstrapAsync(string instanceId, string deviceId, string json, CancellationToken cancellationToken = default)
    {
        var (checkpoint, snapshot) = PersonalCloudWorkspaceCodec.ParseBootstrap(json);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureNoPendingPersonalCloudIntentsAsync(connection, cancellationToken).ConfigureAwait(false);
        await CreateIncomingWorkspaceTasksAsync(connection, cancellationToken).ConfigureAwait(false);
        await AddIncomingWorkspaceTasksAsync(connection, snapshot, cancellationToken).ConfigureAwait(false);
        await PrepareWorkspaceBootstrapReplacementAsync(connection, instanceId, cancellationToken).ConfigureAwait(false);
        await ApplyWorkspaceBootstrapSnapshotAsync(connection, instanceId, snapshot, cancellationToken).ConfigureAwait(false);
        await FinishWorkspaceBootstrapAsync(connection, instanceId, deviceId, checkpoint, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return checkpoint;
    }

    private static Task CreateIncomingWorkspaceTasksAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        WorkspaceExecuteAsync(connection, "DROP TABLE IF EXISTS temp.incoming_workspace_tasks; CREATE TEMP TABLE incoming_workspace_tasks(id TEXT PRIMARY KEY NOT NULL)", cancellationToken);

    private static Task AddIncomingWorkspaceTasksAsync(SqliteConnection connection, JsonElement snapshot, CancellationToken cancellationToken) =>
        WorkspaceExecuteAsync(connection, """
            INSERT INTO incoming_workspace_tasks(id) SELECT json_extract(value,'$.id') FROM json_each(@tasks)
              WHERE json_extract(value,'$.deletedAt') IS NULL
            """, cancellationToken, ("@tasks", snapshot.GetProperty("tasks").GetRawText()));

    private static async Task PrepareWorkspaceBootstrapReplacementAsync(SqliteConnection connection, string instanceId, CancellationToken cancellationToken)
    {
        // Keep retained task rows and their device-only metadata/links. Only absent or
        // deleted replica tasks are removed; unpublished local data remains for staging.
        await WorkspaceExecuteAsync(connection, """
            DELETE FROM task_labels WHERE task_id IN(SELECT entity_id FROM personal_cloud_replica_entities WHERE instance_id=@i AND product='tasks')
              AND task_id NOT IN(SELECT id FROM incoming_workspace_tasks);
            DELETE FROM tasks WHERE id IN(SELECT entity_id FROM personal_cloud_replica_entities WHERE instance_id=@i AND product='tasks')
              AND id NOT IN(SELECT id FROM incoming_workspace_tasks);
            DELETE FROM personal_cloud_replica_entities WHERE instance_id=@i AND product='tasks';
            DELETE FROM personal_cloud_applied_changes WHERE instance_id=@i AND product='tasks';
            DELETE FROM personal_cloud_checkpoints WHERE instance_id=@i AND product='tasks';
            DELETE FROM personal_cloud_entity_versions WHERE instance_id=@i AND product='tasks';
            DELETE FROM personal_cloud_tombstones WHERE instance_id=@i AND product='tasks';
            DELETE FROM personal_cloud_provider_sources WHERE instance_id=@i;
            DELETE FROM personal_cloud_provider_commands WHERE instance_id=@i;
            DELETE FROM personal_cloud_workspace_records WHERE instance_id=@i;
            DELETE FROM personal_cloud_workspace_indirect_versions WHERE instance_id=@i;
            """, cancellationToken, ("@i", instanceId)).ConfigureAwait(false);
    }

    private async Task ApplyWorkspaceBootstrapSnapshotAsync(SqliteConnection connection, string instanceId, JsonElement snapshot, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, TaskItem>? priorTasks = null)
    {
        // Embedded complete tasks own labels/source. Compatibility mirrors cannot undo clears.
        var payloadJson = "{" + string.Join(',', new[] { "spaces", "projects", "tasks", "scopes", "catalog", "intents", "routes" }.Select(k => $"\"{k}\":{(snapshot.TryGetProperty(k, out var a) ? a.GetRawText() : "[]")}")) + "}";
        using var payload = JsonDocument.Parse(payloadJson);
        await ApplyWorkspacePayloadAsync(connection, instanceId, payload.RootElement, cancellationToken, priorTasks).ConfigureAwait(false);
        foreach (var element in snapshot.GetProperty("tombstones").EnumerateArray())
        {
            var tombstone = PersonalCloudK0Codec.Deserialize<PersonalCloudBootstrapTombstone>(element.GetRawText());
            if (tombstone.EntityKind != "task" || tombstone.EntityVersion <= 0) throw PersonalCloudWorkspaceCodec.Invalid();
            PersonalCloudK0Codec.ValidateIdentifier(tombstone.EntityId, "tombstone.entityId");
            PersonalCloudK0Codec.ValidateTimestamp(tombstone.DeletedAt, "tombstone.deletedAt");
            await WorkspaceExecuteAsync(connection, """
                INSERT INTO personal_cloud_tombstones(instance_id,product,entity_id,entity_version,deleted_at,purge_after)
                VALUES(@i,'tasks',@e,@v,@d,@p) ON CONFLICT(instance_id,product,entity_id) DO UPDATE SET
                  entity_version=excluded.entity_version,deleted_at=excluded.deleted_at,purge_after=excluded.purge_after
                """, cancellationToken, ("@i", instanceId), ("@e", tombstone.EntityId), ("@v", tombstone.EntityVersion),
                ("@d", ParseProtocolTimestamp(tombstone.DeletedAt).ToUnixTimeMilliseconds()),
                ("@p", tombstone.PurgeAfter is null ? null : ParseProtocolTimestamp(tombstone.PurgeAfter).ToUnixTimeMilliseconds())).ConfigureAwait(false);
            await UpsertPersonalCloudEntityVersionAsync(connection, instanceId, tombstone.EntityId, tombstone.EntityVersion, cancellationToken).ConfigureAwait(false);
        }
        foreach (var element in snapshot.GetProperty("labels").EnumerateArray())
        {
            var label = PersonalCloudK0Codec.Deserialize<PersonalCloudBootstrapLabel>(element.GetRawText());
            if (label.DeletedAt is null)
                await WorkspaceExecuteAsync(connection, "INSERT INTO labels(id,integration_id,name,created_at) VALUES(@id,'openza_tasks',@n,@a) ON CONFLICT(id) DO UPDATE SET name=excluded.name", cancellationToken,
                    ("@id", label.Id), ("@n", label.Name), ("@a", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).ConfigureAwait(false);
        }
        // Legacy external commands are still bridge requests, not workspace intents.
        foreach (var element in snapshot.GetProperty("externalCommands").EnumerateArray())
        {
            var command = PersonalCloudK0Codec.Deserialize<PersonalCloudBootstrapExternalCommand>(element.GetRawText());
            await SaveBridgeCommandAsync(connection, instanceId, command.TaskId, command.RequestedChangeSeq, command.Command, command.State, command.ErrorCode, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task FinishWorkspaceBootstrapAsync(SqliteConnection connection, string instanceId, string deviceId, long checkpoint, CancellationToken cancellationToken)
    {
        await WorkspaceExecuteAsync(connection, "INSERT INTO personal_cloud_checkpoints(instance_id,device_id,product,last_change_seq,updated_at) VALUES(@i,@d,'tasks',@q,@now)", cancellationToken,
            ("@i", instanceId), ("@d", deviceId), ("@q", checkpoint), ("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).ConfigureAwait(false);
        await WorkspaceExecuteAsync(connection, "UPDATE personal_cloud_workspace_capabilities SET hydrated=1 WHERE instance_id=@i", cancellationToken, ("@i", instanceId)).ConfigureAwait(false);
    }

    private static string? WText(JsonElement item, string key) => PersonalCloudWorkspaceCodec.Text(item, key);
    private static DateTimeOffset? WMoment(JsonElement item, string key) => WText(item, key) is { } stamp ? ParseProtocolTimestamp(stamp) : null;
    private static object? WDate(JsonElement item, string key) => WMoment(item, key)?.ToUnixTimeMilliseconds();
    private static DateOnly? WDay(JsonElement item, string key) => WText(item, key) is { } day ? DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    private static async Task WorkspaceExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
