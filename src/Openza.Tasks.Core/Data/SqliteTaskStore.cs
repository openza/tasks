using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Services;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Core.Data;

public sealed partial class SqliteTaskStore(string databasePath, bool readOnly = false, TimeProvider? timeProvider = null) : ITaskStore
{
    public const int CurrentSchemaVersion = 11;
    public const int MinimumReadOnlySchemaVersion = 5;

    private sealed record ParentTaskContext(string Id, string SpaceId, string? ProjectId, TaskWorkflowStatus WorkflowStatus);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string DatabasePath { get; } = databasePath;
    public bool IsReadOnly { get; } = readOnly;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsReadOnly)
        {
            if (!File.Exists(DatabasePath))
            {
                throw new FileNotFoundException("The Openza database does not exist.", DatabasePath);
            }

            await using var readOnlyConnection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var command = readOnlyConnection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            var schemaVersion = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (schemaVersion < MinimumReadOnlySchemaVersion || schemaVersion > CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"The Openza database schema is version {schemaVersion}; read-only CLI commands support versions " +
                    $"{MinimumReadOnlySchemaVersion} through {CurrentSchemaVersion}.");
            }
            return;
        }

        PrivateFilePermissions.EnsureDirectory(Path.GetDirectoryName(DatabasePath) ?? ".");
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureLegacySchemaCompatibilityAsync(connection, cancellationToken).ConfigureAwait(false);
        await CreateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await InsertDefaultDataAsync(connection, cancellationToken).ConfigureAwait(false);
        await connection.CloseAsync().ConfigureAwait(false);
        PrivateFilePermissions.EnsureFile(DatabasePath);
    }

    public async Task<IReadOnlyList<TaskItem>> GetTasksAsync(TaskQuery query, CancellationToken cancellationToken = default)
    {
        var localToday = DateOnly.FromDateTime(_timeProvider.GetUtcNow().LocalDateTime);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var tasks = await ReadTasksAsync(connection, cancellationToken).ConfigureAwait(false);
        return TaskListProjection.Build(tasks, query, localToday);
    }

    public async Task<TaskListRefreshSnapshot> GetTaskListRefreshSnapshotAsync(TaskQuery query, CancellationToken cancellationToken = default)
    {
        var localToday = DateOnly.FromDateTime(_timeProvider.GetUtcNow().LocalDateTime);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var tasks = await ReadTasksAsync(connection, cancellationToken).ConfigureAwait(false);
        return new TaskListRefreshSnapshot(
            TaskListProjection.Build(tasks, query, localToday),
            TaskListProjection.Build(tasks, new TaskQuery
            {
                SpaceId = query.SpaceId,
                Kind = TaskListKind.All,
                IncludeSubtasks = true,
            }, localToday),
            TaskListProjection.Counts(tasks, query.SpaceId, localToday));
    }

    public async Task<IReadOnlyList<GlobalSearchResult>> SearchAsync(GlobalSearchQuery query, CancellationToken cancellationToken = default)
    {
        var tokens = SplitSearchTokens(query.SearchText);
        if (tokens.Count == 0)
        {
            return [];
        }

        var spaceId = query.IncludeAllSpaces ? null : query.SpaceId;
        var activeSpaceIds = query.IncludeAllSpaces
            ? (await GetSpacesAsync(includeArchived: false, cancellationToken).ConfigureAwait(false))
                .Select(space => space.Id)
                .ToHashSet(StringComparer.Ordinal)
            : null;
        var taskKind = query.IncludeCompletedTasks ? TaskListKind.All : TaskListKind.Open;
        var tasks = await GetTasksAsync(new TaskQuery
        {
            Kind = taskKind,
            SpaceId = spaceId,
            IncludeSubtasks = true,
        }, cancellationToken).ConfigureAwait(false);
        var projects = await GetProjectsAsync(spaceId, includeArchived: false, cancellationToken).ConfigureAwait(false);
        var projectNames = projects.ToDictionary(project => project.Id, project => project.Name, StringComparer.Ordinal);
        var limit = query.Limit <= 0 ? 30 : query.Limit;

        var taskResults = tasks
            .Where(task => activeSpaceIds is null || activeSpaceIds.Contains(task.SpaceId))
            .Where(task => MatchesSearchTokens(tokens, task.Title, task.Notes, task.SourceDescription))
            .Select(task =>
            {
                projectNames.TryGetValue(task.ProjectId ?? string.Empty, out var projectName);
                return new GlobalSearchResult
                {
                    Kind = GlobalSearchResultKind.Task,
                    Id = task.Id,
                    SpaceId = task.SpaceId,
                    Title = task.Title,
                    Subtitle = BuildTaskSearchSubtitle(task, projectName),
                    Snippet = BuildSearchSnippet(tokens, task.Notes, task.SourceDescription),
                    Score = CalculateSearchScore(query.SearchText, tokens, task.Title, task.Notes, task.SourceDescription),
                    ProjectId = task.ProjectId,
                };
            })
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Title, StringComparer.CurrentCultureIgnoreCase);

        var projectResults = projects
            .Where(project => activeSpaceIds is null || activeSpaceIds.Contains(project.SpaceId))
            .Where(project => MatchesSearchTokens(tokens, project.Name, project.Description))
            .Select(project => new GlobalSearchResult
            {
                Kind = GlobalSearchResultKind.Project,
                Id = project.Id,
                SpaceId = project.SpaceId,
                Title = project.Name,
                Subtitle = project.IsCompleted ? "Completed project" : "Project",
                Snippet = BuildSearchSnippet(tokens, project.Description),
                Score = CalculateSearchScore(query.SearchText, tokens, project.Name, project.Description),
            })
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Title, StringComparer.CurrentCultureIgnoreCase);

        return taskResults
            .Concat(projectResults)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Kind)
            .ThenBy(result => result.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .ToList();
    }

    public async Task<TaskCountSummary> GetTaskCountsAsync(string? spaceId = null, CancellationToken cancellationToken = default)
    {
        var todayDate = DateOnly.FromDateTime(_timeProvider.GetUtcNow().LocalDateTime);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var bounds = TaskDateValues.LocalDayBounds(todayDate, TimeZoneInfo.Local);
        var todayStart = bounds.Start.ToUnixTimeSeconds();
        var tomorrowStart = bounds.End.ToUnixTimeSeconds();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              SUM(CASE WHEN completion_state = 'open' AND workflow_status = 'inbox' AND project_id IS NULL THEN 1 ELSE 0 END) AS inbox,
              SUM(CASE WHEN completion_state = 'open' AND workflow_status = 'next' THEN 1 ELSE 0 END) AS next_actions,
              SUM(CASE WHEN completion_state = 'open' AND workflow_status = 'waiting' THEN 1 ELSE 0 END) AS waiting,
              SUM(CASE WHEN completion_state = 'open' AND workflow_status = 'someday' THEN 1 ELSE 0 END) AS someday,
              SUM(CASE WHEN completion_state = 'open' AND (
                    COALESCE(planned_on, date(planned_at, 'unixepoch', 'localtime')) = @today_date OR
                    COALESCE(deadline_on, date(deadline_at, 'unixepoch', 'localtime')) = @today_date OR
                    (scheduled_start >= @today_start AND scheduled_start < @tomorrow_start)
                  ) THEN 1 ELSE 0 END) AS today,
              SUM(CASE WHEN completion_state = 'open' AND (
                    (COALESCE(planned_on, date(planned_at, 'unixepoch', 'localtime')) IS NOT NULL
                        AND COALESCE(planned_on, date(planned_at, 'unixepoch', 'localtime')) < @today_date) OR
                    (COALESCE(deadline_on, date(deadline_at, 'unixepoch', 'localtime')) IS NOT NULL
                        AND COALESCE(deadline_on, date(deadline_at, 'unixepoch', 'localtime')) < @today_date) OR
                    (scheduled_start IS NOT NULL AND scheduled_start < @today_start)
                  ) THEN 1 ELSE 0 END) AS overdue,
              SUM(CASE WHEN completion_state = 'open' AND (
                    planned_on IS NOT NULL OR
                    deadline_on IS NOT NULL OR
                    planned_at IS NOT NULL OR
                    deadline_at IS NOT NULL OR
                    scheduled_start IS NOT NULL
                  ) THEN 1 ELSE 0 END) AS calendar,
              SUM(CASE WHEN completion_state = 'open' THEN 1 ELSE 0 END) AS open_tasks,
              COUNT(*) AS all_tasks,
              SUM(CASE WHEN completion_state = 'completed' THEN 1 ELSE 0 END) AS completed
            FROM tasks
            WHERE integration_id = 'openza_tasks'
              AND (@space_id IS NULL OR space_id = @space_id)
              AND parent_id IS NULL
            """;
        command.Parameters.AddWithValue("@space_id", ToDbValue(spaceId));
        command.Parameters.AddWithValue("@today_date", TaskDateValues.ToStorageValue(todayDate));
        command.Parameters.AddWithValue("@today_start", todayStart);
        command.Parameters.AddWithValue("@tomorrow_start", tomorrowStart);

        var inbox = 0;
        var nextActions = 0;
        var waiting = 0;
        var someday = 0;
        var today = 0;
        var overdue = 0;
        var calendar = 0;
        var open = 0;
        var all = 0;
        var completed = 0;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                inbox = ReadNullableInt(reader, 0);
                nextActions = ReadNullableInt(reader, 1);
                waiting = ReadNullableInt(reader, 2);
                someday = ReadNullableInt(reader, 3);
                today = ReadNullableInt(reader, 4);
                overdue = ReadNullableInt(reader, 5);
                calendar = ReadNullableInt(reader, 6);
                open = ReadNullableInt(reader, 7);
                all = ReadNullableInt(reader, 8);
                completed = ReadNullableInt(reader, 9);
            }
        }

        var byProjectCommand = connection.CreateCommand();
        byProjectCommand.CommandText = """
            SELECT project_id,
                   COUNT(*) AS open_count,
                   SUM(CASE WHEN workflow_status = 'next' THEN 1 ELSE 0 END) AS next_count
            FROM tasks
            WHERE integration_id = 'openza_tasks' AND completion_state = 'open' AND project_id IS NOT NULL
              AND (@space_id IS NULL OR space_id = @space_id)
              AND parent_id IS NULL
            GROUP BY project_id
            """;
        byProjectCommand.Parameters.AddWithValue("@space_id", ToDbValue(spaceId));

        var byProject = new Dictionary<string, int>(StringComparer.Ordinal);
        var nextByProject = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var reader = await byProjectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0))
                {
                    var projectId = reader.GetString(0);
                    byProject[projectId] = reader.GetInt32(1);
                    nextByProject[projectId] = ReadNullableInt(reader, 2);
                }
            }
        }

        return new TaskCountSummary(inbox, nextActions, waiting, someday, today, calendar, overdue, open, all, completed, byProject, nextByProject);
    }

    public async Task<TaskItem?> GetTaskAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await ReadTasksAsync(connection, cancellationToken).ConfigureAwait(false)).FirstOrDefault(t => t.Id == id);
    }

    public async Task<IReadOnlyList<SpaceItem>> GetSpacesAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, color, icon, sort_order, is_archived, created_at, updated_at
            FROM spaces
            WHERE @include_archived = 1 OR is_archived = 0
            ORDER BY sort_order, name
            """;
        command.Parameters.AddWithValue("@include_archived", includeArchived ? 1 : 0);

        var spaces = new List<SpaceItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            spaces.Add(ReadSpace(reader));
        }

        return spaces;
    }

    public async Task UpsertSpaceAsync(SpaceItem space, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await UpsertSpaceCoreAsync(connection, space, cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertSpaceCoreAsync(SqliteConnection connection, SpaceItem space, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO spaces (id, name, color, icon, sort_order, is_archived, created_at, updated_at)
            VALUES (@id, @name, @color, @icon, @sort_order, @is_archived, @created_at, @updated_at)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                color = excluded.color,
                icon = excluded.icon,
                sort_order = excluded.sort_order,
                is_archived = excluded.is_archived,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("@id", space.Id);
        command.Parameters.AddWithValue("@name", space.Name);
        command.Parameters.AddWithValue("@color", space.Color);
        command.Parameters.AddWithValue("@icon", ToDbValue(space.Icon));
        command.Parameters.AddWithValue("@sort_order", space.SortOrder);
        command.Parameters.AddWithValue("@is_archived", space.IsArchived ? 1 : 0);
        command.Parameters.AddWithValue("@created_at", ToDbDate(space.CreatedAt));
        command.Parameters.AddWithValue("@updated_at", ToDbValue(space.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProjectItem>> GetProjectsAsync(string? spaceId = null, bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, external_id, space_id, integration_id, provider_connection_id, name, description, color, icon, parent_id,
                   sort_order, is_favorite, is_archived, status, provider_metadata, created_at, updated_at
            FROM projects
            WHERE integration_id = 'openza_tasks' AND (@include_archived = 1 OR (is_archived = 0 AND COALESCE(status, 'active') != 'archived'))
              AND (@space_id IS NULL OR space_id = @space_id)
            ORDER BY is_favorite DESC, sort_order, name
            """;
        command.Parameters.AddWithValue("@space_id", ToDbValue(spaceId));
        command.Parameters.AddWithValue("@include_archived", includeArchived ? 1 : 0);

        if (await TableExistsAsync(connection, "personal_cloud_workspace_records", cancellationToken).ConfigureAwait(false))
            command.CommandText = command.CommandText.Replace("ORDER BY is_favorite", "AND NOT EXISTS(SELECT 1 FROM personal_cloud_workspace_records w WHERE w.kind='projects' AND w.entity_id=projects.id AND json_extract(w.canonical_json,'$.deletedAt') IS NOT NULL) ORDER BY is_favorite", StringComparison.Ordinal);

        var projects = new List<ProjectItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            projects.Add(ReadProject(reader));
        }

        return projects;
    }

    public async Task<IReadOnlyList<LabelItem>> GetLabelsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, external_id, integration_id, provider_connection_id, name, color, description, sort_order,
                   is_favorite, provider_metadata, created_at
            FROM labels
            WHERE integration_id = 'openza_tasks'
            ORDER BY sort_order, name
            """;

        var labels = new List<LabelItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            labels.Add(ReadLabel(reader));
        }

        return labels;
    }

    public async Task<IReadOnlyList<ProjectItem>> GetProviderProjectsAsync(string integrationId, bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, external_id, space_id, integration_id, provider_connection_id, name, description, color, icon, parent_id,
                   sort_order, is_favorite, is_archived, status, provider_metadata, created_at, updated_at
            FROM projects
            WHERE integration_id = @integration_id
              AND (@include_archived = 1 OR (is_archived = 0 AND COALESCE(status, 'active') != 'archived'))
            ORDER BY is_favorite DESC, sort_order, name
            """;
        command.Parameters.AddWithValue("@integration_id", integrationId);
        command.Parameters.AddWithValue("@include_archived", includeArchived ? 1 : 0);

        var projects = new List<ProjectItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            projects.Add(ReadProject(reader));
        }

        return projects;
    }

    public async Task<IReadOnlyList<LabelItem>> GetProviderLabelsAsync(string integrationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, external_id, integration_id, provider_connection_id, name, color, description, sort_order,
                   is_favorite, provider_metadata, created_at
            FROM labels
            WHERE integration_id = @integration_id
            ORDER BY sort_order, name
            """;
        command.Parameters.AddWithValue("@integration_id", integrationId);

        var labels = new List<LabelItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            labels.Add(ReadLabel(reader));
        }

        return labels;
    }

    public async Task<IReadOnlyList<IntegrationInfo>> GetIntegrationsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, display_name, color, icon, logo_path, is_active, is_configured,
                   config, last_sync_at, sync_token, created_at
            FROM integrations
            ORDER BY id
            """;

        var integrations = new List<IntegrationInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            integrations.Add(new IntegrationInfo
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                DisplayName = reader.GetString(2),
                Color = reader.GetString(3),
                Icon = GetNullableString(reader, 4),
                LogoPath = GetNullableString(reader, 5),
                IsActive = reader.GetInt32(6) != 0,
                IsConfigured = reader.GetInt32(7) != 0,
                ConfigJson = GetNullableString(reader, 8),
                LastSyncAt = ReadDate(reader, 9),
                SyncToken = GetNullableString(reader, 10),
                CreatedAt = ReadDate(reader, 11) ?? DateTimeOffset.UtcNow,
            });
        }

        return integrations;
    }

    public async Task<IReadOnlyList<ProviderConnectionInfo>> GetProviderConnectionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, workspace_id, integration_id, display_name, account_key, status, settings,
                   last_sync_at, created_at, updated_at
            FROM provider_connections
            ORDER BY integration_id, display_name
            """;

        var connections = new List<ProviderConnectionInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            connections.Add(new ProviderConnectionInfo
            {
                Id = reader.GetString(0),
                WorkspaceId = reader.GetString(1),
                IntegrationId = reader.GetString(2),
                DisplayName = reader.GetString(3),
                AccountKey = GetNullableString(reader, 4),
                Status = reader.GetString(5),
                SettingsJson = GetNullableString(reader, 6),
                LastSyncAt = ReadDate(reader, 7),
                CreatedAt = ReadDate(reader, 8) ?? DateTimeOffset.UtcNow,
                UpdatedAt = ReadDate(reader, 9),
            });
        }

        return connections;
    }

    public async Task UpsertProviderConnectionAsync(ProviderConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO provider_connections (id, workspace_id, integration_id, display_name, account_key, status, settings,
                                              last_sync_at, created_at, updated_at)
            VALUES (@id, @workspace_id, @integration_id, @display_name, @account_key, @status, @settings,
                    @last_sync_at, @created_at, @updated_at)
            ON CONFLICT(id) DO UPDATE SET
                workspace_id = excluded.workspace_id,
                integration_id = excluded.integration_id,
                display_name = excluded.display_name,
                account_key = excluded.account_key,
                status = excluded.status,
                settings = excluded.settings,
                last_sync_at = excluded.last_sync_at,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("@id", connectionInfo.Id);
        command.Parameters.AddWithValue("@workspace_id", connectionInfo.WorkspaceId);
        command.Parameters.AddWithValue("@integration_id", connectionInfo.IntegrationId);
        command.Parameters.AddWithValue("@display_name", connectionInfo.DisplayName);
        command.Parameters.AddWithValue("@account_key", ToDbValue(connectionInfo.AccountKey));
        command.Parameters.AddWithValue("@status", connectionInfo.Status);
        command.Parameters.AddWithValue("@settings", ToDbValue(connectionInfo.SettingsJson));
        command.Parameters.AddWithValue("@last_sync_at", ToDbValue(connectionInfo.LastSyncAt));
        command.Parameters.AddWithValue("@created_at", ToDbDate(connectionInfo.CreatedAt));
        command.Parameters.AddWithValue("@updated_at", ToDbValue(connectionInfo.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TaskExternalLinkInfo>> GetTaskExternalLinksAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, task_id, integration_id, connection_id, external_id, kind, display_name, url,
                   metadata, created_at, updated_at
            FROM task_external_links
            WHERE task_id = @task_id
            ORDER BY created_at DESC, display_name
            """;
        command.Parameters.AddWithValue("@task_id", taskId);

        var links = new List<TaskExternalLinkInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            links.Add(ReadTaskExternalLink(reader));
        }

        return links;
    }

    public async Task UpsertTaskExternalLinkAsync(TaskExternalLinkInfo link, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO task_external_links (id, task_id, integration_id, connection_id, external_id, kind, display_name, url,
                                             metadata, created_at, updated_at)
            VALUES (@id, @task_id, @integration_id, @connection_id, @external_id, @kind, @display_name, @url,
                    @metadata, @created_at, @updated_at)
            ON CONFLICT(task_id, integration_id, external_id) DO UPDATE SET
                connection_id = excluded.connection_id,
                kind = excluded.kind,
                display_name = excluded.display_name,
                url = excluded.url,
                metadata = excluded.metadata,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("@id", string.IsNullOrWhiteSpace(link.Id) ? $"link_{Guid.NewGuid():N}" : link.Id);
        command.Parameters.AddWithValue("@task_id", link.TaskId);
        command.Parameters.AddWithValue("@integration_id", link.IntegrationId);
        command.Parameters.AddWithValue("@connection_id", ToDbValue(link.ConnectionId));
        command.Parameters.AddWithValue("@external_id", link.ExternalId);
        command.Parameters.AddWithValue("@kind", link.Kind);
        command.Parameters.AddWithValue("@display_name", link.DisplayName);
        command.Parameters.AddWithValue("@url", link.Url);
        command.Parameters.AddWithValue("@metadata", ToDbValue(link.MetadataJson));
        command.Parameters.AddWithValue("@created_at", ToDbDate(link.CreatedAt));
        command.Parameters.AddWithValue("@updated_at", ToDbValue(link.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceTaskExternalLinkAsync(TaskExternalLinkInfo link, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        // One statement is atomic; do not delete the old link before a replacement is saved.
        command.CommandText = """
            UPDATE task_external_links
            SET connection_id = @connection_id, external_id = @external_id, kind = @kind,
                display_name = @display_name, url = @url, metadata = @metadata, updated_at = @updated_at
            WHERE id = @id AND task_id = @task_id AND integration_id = @integration_id
            """;
        command.Parameters.AddWithValue("@id", link.Id);
        command.Parameters.AddWithValue("@task_id", link.TaskId);
        command.Parameters.AddWithValue("@integration_id", link.IntegrationId);
        command.Parameters.AddWithValue("@connection_id", ToDbValue(link.ConnectionId));
        command.Parameters.AddWithValue("@external_id", link.ExternalId);
        command.Parameters.AddWithValue("@kind", link.Kind);
        command.Parameters.AddWithValue("@display_name", link.DisplayName);
        command.Parameters.AddWithValue("@url", link.Url);
        command.Parameters.AddWithValue("@metadata", ToDbValue(link.MetadataJson));
        command.Parameters.AddWithValue("@updated_at", ToDbValue(link.UpdatedAt));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The issue link changed or was removed. Refresh the task before replacing it.");
    }

    public async Task DeleteTaskExternalLinkAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM task_external_links WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProviderSourceItem>> GetProviderSourceItemsAsync(string? integrationId = null, string? spaceId = null, bool includeAdopted = false, bool includeIgnored = false, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, integration_id, provider_connection_id, external_id, provider_task_id, title, description,
                   source_project_id, source_project_name, parent_external_id, suggested_space_id, priority, completion_state,
                   planned_on, planned_at, deadline_on, deadline_at, source_url,
                   recurrence_rule, snapshot_json, adoption_state, adopted_task_id, first_seen_at, last_seen_at, updated_at
            FROM provider_source_items
            WHERE (@integration_id IS NULL OR integration_id = @integration_id)
              AND (
                @space_id IS NULL
                OR suggested_space_id = @space_id
                OR suggested_space_id IS NULL
                OR NOT EXISTS (
                    SELECT 1
                    FROM spaces
                    WHERE spaces.id = provider_source_items.suggested_space_id
                      AND spaces.is_archived = 0
                )
              )
              AND (
                adoption_state = 'not_adopted'
                OR (@include_adopted = 1 AND adoption_state = 'adopted')
                OR (@include_ignored = 1 AND adoption_state = 'ignored')
              )
            ORDER BY last_seen_at DESC, title
            """;
        command.Parameters.AddWithValue("@integration_id", ToDbValue(integrationId));
        command.Parameters.AddWithValue("@space_id", ToDbValue(spaceId));
        command.Parameters.AddWithValue("@include_adopted", includeAdopted ? 1 : 0);
        command.Parameters.AddWithValue("@include_ignored", includeIgnored ? 1 : 0);

        var items = new List<ProviderSourceItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadProviderSourceItem(reader));
        }

        return items;
    }

    public async Task UpsertProviderSourceItemAsync(ProviderSourceItem item, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var existing = await ReadProviderSourceItemByExternalAsync(connection, item.ProviderConnectionId, item.ExternalId, cancellationToken).ConfigureAwait(false);
        var restoredAdoption = await ResolveProviderSourceAdoptionAsync(connection, existing, item, cancellationToken).ConfigureAwait(false);
        var source = item with
        {
            Id = string.IsNullOrWhiteSpace(item.Id) ? BuildProviderSourceItemId(item.ProviderConnectionId, item.ExternalId) : item.Id,
            AdoptionState = restoredAdoption.AdoptionState,
            AdoptedTaskId = restoredAdoption.AdoptedTaskId,
            SuggestedSpaceId = await ResolveProviderSourceSpaceIdAsync(connection, existing?.SuggestedSpaceId, item.SuggestedSpaceId, cancellationToken).ConfigureAwait(false),
            FirstSeenAt = existing?.FirstSeenAt ?? item.FirstSeenAt,
            LastSeenAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await UpsertProviderSourceItemCoreAsync(connection, source, cancellationToken).ConfigureAwait(false);
        var parentContext = await ResolveAdoptedParentTaskContextAsync(connection, source, cancellationToken).ConfigureAwait(false);
        if (source.AdoptionState == ProviderSourceAdoptionStates.NotAdopted &&
            (parentContext is not null || (string.IsNullOrWhiteSpace(source.ParentExternalId) && ShouldBypassInbox(source))))
        {
            var now = DateTimeOffset.UtcNow;
            var targetSpaceId = parentContext?.SpaceId ??
                await ResolveTaskCreationSpaceIdAsync(connection, source.SuggestedSpaceId, cancellationToken).ConfigureAwait(false);
            var task = CreateTaskFromSource(source, targetSpaceId, now, parentContext);
            await UpsertTaskCoreAsync(connection, task, cancellationToken).ConfigureAwait(false);
            await SetTaskLabelsCoreAsync(connection, task.Id, task.Labels, cancellationToken).ConfigureAwait(false);

            source = source with
            {
                AdoptionState = ProviderSourceAdoptionStates.Adopted,
                AdoptedTaskId = task.Id,
                UpdatedAt = now,
            };
            await UpsertProviderSourceItemCoreAsync(connection, source, cancellationToken).ConfigureAwait(false);
        }
        else if (source.AdoptionState == ProviderSourceAdoptionStates.Adopted && !string.IsNullOrWhiteSpace(source.AdoptedTaskId))
        {
            await RefreshAdoptedTaskFromSourceAsync(
                connection,
                source,
                parentContext,
                ShouldSyncPlannedDateFromSource(source, existing),
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DetachProviderSourceItemAsync(string sourceItemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var source = await ReadProviderSourceItemByIdAsync(connection, sourceItemId, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(source.AdoptedTaskId))
        {
            var taskCommand = connection.CreateCommand();
            taskCommand.CommandText = """
                UPDATE tasks
                SET source_integration_id = NULL,
                    source_connection_id = NULL,
                    source_external_id = NULL,
                    source_provider_task_id = NULL,
                    source_url = NULL,
                    source_metadata = NULL,
                    updated_at = @updated_at,
                    revision = revision + 1
                WHERE id = @task_id
                """;
            taskCommand.Parameters.AddWithValue("@task_id", source.AdoptedTaskId);
            taskCommand.Parameters.AddWithValue("@updated_at", ToDbDate(DateTimeOffset.UtcNow));
            await taskCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var sourceCommand = connection.CreateCommand();
        sourceCommand.CommandText = """
            UPDATE provider_source_items
            SET adoption_state = @adoption_state,
                updated_at = @updated_at
            WHERE id = @id
            """;
        sourceCommand.Parameters.AddWithValue("@id", sourceItemId);
        sourceCommand.Parameters.AddWithValue("@adoption_state", ProviderSourceAdoptionStates.Missing);
        sourceCommand.Parameters.AddWithValue("@updated_at", ToDbDate(DateTimeOffset.UtcNow));
        await sourceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteProviderSourceItemAsync(string sourceItemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM provider_source_items
            WHERE id = @id
              AND adoption_state = @adoption_state
            """;
        command.Parameters.AddWithValue("@id", sourceItemId);
        command.Parameters.AddWithValue("@adoption_state", ProviderSourceAdoptionStates.NotAdopted);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<TaskItem?> AdoptProviderSourceItemAsync(string sourceItemId, string spaceId = SpaceIds.Default, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var source = await ReadProviderSourceItemByIdAsync(connection, sourceItemId, cancellationToken).ConfigureAwait(false);
        if (source is null || source.AdoptionState == ProviderSourceAdoptionStates.Ignored)
        {
            return null;
        }

        if (source.AdoptionState == ProviderSourceAdoptionStates.Adopted && !string.IsNullOrWhiteSpace(source.AdoptedTaskId))
        {
            var existingTasks = await ReadTasksAsync(connection, cancellationToken).ConfigureAwait(false);
            var existing = existingTasks.FirstOrDefault(task => task.Id == source.AdoptedTaskId);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return existing;
            }
        }

        var now = DateTimeOffset.UtcNow;
        var parentContext = await ResolveAdoptedParentTaskContextAsync(connection, source, cancellationToken).ConfigureAwait(false);
        var requestedSpaceId = string.IsNullOrWhiteSpace(spaceId) ? source.SuggestedSpaceId : spaceId;
        var targetSpaceId = parentContext?.SpaceId ??
            await ResolveTaskCreationSpaceIdAsync(connection, requestedSpaceId, cancellationToken).ConfigureAwait(false);
        var task = CreateTaskFromSource(source, targetSpaceId, now, parentContext);

        await UpsertTaskCoreAsync(connection, task, cancellationToken).ConfigureAwait(false);
        await SetTaskLabelsCoreAsync(connection, task.Id, task.Labels, cancellationToken).ConfigureAwait(false);

        var adopted = source with
        {
            AdoptionState = ProviderSourceAdoptionStates.Adopted,
            AdoptedTaskId = task.Id,
            UpdatedAt = now,
        };
        await UpsertProviderSourceItemCoreAsync(connection, adopted, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return task;
    }

    private static TaskItem CreateTaskFromSource(ProviderSourceItem source, string spaceId, DateTimeOffset now, ParentTaskContext? parent = null)
    {
        var (plannedOn, plannedAt) = NormalizeDatePair(source.PlannedOn, source.PlannedAt);
        var (deadlineOn, deadlineAt) = NormalizeDatePair(source.DeadlineOn, source.DeadlineAt);
        return new TaskItem
        {
            Id = $"local_{Guid.NewGuid():N}",
            SpaceId = spaceId,
            IntegrationId = IntegrationIds.Local,
            Title = source.Title,
            SourceDescription = source.Description,
            ProjectId = parent?.ProjectId,
            ParentId = parent?.Id,
            Priority = source.Priority,
            CompletionState = source.CompletionState,
            WorkflowStatus = parent?.WorkflowStatus ?? InitialWorkflowStatusFor(source),
            PlannedOn = plannedOn,
            PlannedAt = plannedAt,
            DeadlineOn = deadlineOn,
            DeadlineAt = deadlineAt,
            RecurrenceRule = null,
            SourceIntegrationId = source.IntegrationId,
            SourceConnectionId = source.ProviderConnectionId,
            SourceExternalId = source.ExternalId,
            SourceProviderTaskId = source.ProviderTaskId,
            SourceUrl = source.SourceUrl,
            SourceMetadataJson = BuildSourceMetadataJson(source),
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = source.CompletionState == TaskCompletionState.Completed ? now : null,
        };
    }

    private static TaskWorkflowStatus InitialWorkflowStatusFor(ProviderSourceItem source)
    {
        if (source.CompletionState != TaskCompletionState.Open)
        {
            return TaskWorkflowStatus.None;
        }

        return ShouldBypassInbox(source) ? TaskWorkflowStatus.Someday : TaskWorkflowStatus.Inbox;
    }

    private static bool ShouldBypassInbox(ProviderSourceItem source) =>
        !string.IsNullOrWhiteSpace(source.RecurrenceRule) &&
        (source.PlannedOn is not null ||
            source.PlannedAt is not null ||
            source.DeadlineOn is not null ||
            source.DeadlineAt is not null);

    private static async Task<ParentTaskContext?> ResolveAdoptedParentTaskContextAsync(SqliteConnection connection, ProviderSourceItem source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.ParentExternalId))
        {
            return null;
        }

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id, t.space_id, t.project_id, t.workflow_status
            FROM provider_source_items parent_source
            INNER JOIN tasks t ON t.id = parent_source.adopted_task_id
            WHERE parent_source.provider_connection_id = @provider_connection_id
              AND parent_source.external_id = @parent_external_id
              AND parent_source.adopted_task_id IS NOT NULL
              AND (parent_source.parent_external_id IS NULL OR TRIM(parent_source.parent_external_id) = '')
              AND (t.parent_id IS NULL OR TRIM(t.parent_id) = '')
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@provider_connection_id", source.ProviderConnectionId);
        command.Parameters.AddWithValue("@parent_external_id", source.ParentExternalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var parent = new ParentTaskContext(
            reader.GetString(0),
            reader.GetString(1),
            GetNullableString(reader, 2),
            TaskStatusExtensions.WorkflowFromStorageValue(reader.GetString(3)));
        return await ShouldPreserveLocalParentDetachAsync(connection, source, parent, cancellationToken).ConfigureAwait(false)
            ? null
            : parent;
    }

    private static async Task<bool> ShouldPreserveLocalParentDetachAsync(
        SqliteConnection connection,
        ProviderSourceItem source,
        ParentTaskContext parent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.AdoptedTaskId) ||
            string.IsNullOrWhiteSpace(source.SuggestedSpaceId) ||
            string.Equals(source.SuggestedSpaceId, parent.SpaceId, StringComparison.Ordinal))
        {
            return false;
        }

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT space_id, parent_id
            FROM tasks
            WHERE id = @task_id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@task_id", source.AdoptedTaskId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var taskSpaceId = reader.GetString(0);
        var taskParentId = GetNullableString(reader, 1);
        return string.IsNullOrWhiteSpace(taskParentId) &&
            string.Equals(taskSpaceId, source.SuggestedSpaceId, StringComparison.Ordinal);
    }

    public async Task<bool> SkipProviderSourceItemAsync(string sourceItemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var source = await ReadProviderSourceItemByIdAsync(connection, sourceItemId, cancellationToken).ConfigureAwait(false);
        if (source is null || source.AdoptionState == ProviderSourceAdoptionStates.Adopted)
        {
            return false;
        }

        var skipped = source with
        {
            AdoptionState = ProviderSourceAdoptionStates.Ignored,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await UpsertProviderSourceItemCoreAsync(connection, skipped, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UnskipProviderSourceItemAsync(string sourceItemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var source = await ReadProviderSourceItemByIdAsync(connection, sourceItemId, cancellationToken).ConfigureAwait(false);
        if (source is null || source.AdoptionState != ProviderSourceAdoptionStates.Ignored)
        {
            return false;
        }

        var unskipped = source with
        {
            AdoptionState = ProviderSourceAdoptionStates.NotAdopted,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await UpsertProviderSourceItemCoreAsync(connection, unskipped, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<SyncRouteInfo>> GetSyncRoutesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadSyncRoutesCoreAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<SyncRouteInfo>> ReadSyncRoutesCoreAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, workspace_id, name, source_connection_id, target_connection_id, mode, visibility,
                   schedule, is_enabled, settings, created_at, updated_at
            FROM sync_routes
            ORDER BY name
            """;

        var routes = new List<SyncRouteInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            routes.Add(new SyncRouteInfo
            {
                Id = reader.GetString(0),
                WorkspaceId = reader.GetString(1),
                Name = reader.GetString(2),
                SourceConnectionId = GetNullableString(reader, 3),
                TargetConnectionId = GetNullableString(reader, 4),
                Mode = reader.GetString(5),
                Visibility = reader.GetString(6),
                ScheduleJson = GetNullableString(reader, 7),
                IsEnabled = reader.GetInt32(8) != 0,
                SettingsJson = GetNullableString(reader, 9),
                CreatedAt = ReadDate(reader, 10) ?? DateTimeOffset.UtcNow,
                UpdatedAt = ReadDate(reader, 11),
            });
        }

        return routes;
    }

    public async Task UpsertSyncRouteAsync(SyncRouteInfo route, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await UpsertSyncRouteCoreAsync(connection, route, cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertSyncRouteCoreAsync(SqliteConnection connection, SyncRouteInfo route, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_routes (id, workspace_id, name, source_connection_id, target_connection_id, mode, visibility,
                                     schedule, is_enabled, settings, created_at, updated_at)
            VALUES (@id, @workspace_id, @name, @source_connection_id, @target_connection_id, @mode, @visibility,
                    @schedule, @is_enabled, @settings, @created_at, @updated_at)
            ON CONFLICT(id) DO UPDATE SET
                workspace_id = excluded.workspace_id,
                name = excluded.name,
                source_connection_id = excluded.source_connection_id,
                target_connection_id = excluded.target_connection_id,
                mode = excluded.mode,
                visibility = excluded.visibility,
                schedule = excluded.schedule,
                is_enabled = excluded.is_enabled,
                settings = excluded.settings,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("@id", route.Id);
        command.Parameters.AddWithValue("@workspace_id", route.WorkspaceId);
        command.Parameters.AddWithValue("@name", route.Name);
        command.Parameters.AddWithValue("@source_connection_id", ToDbValue(route.SourceConnectionId));
        command.Parameters.AddWithValue("@target_connection_id", ToDbValue(route.TargetConnectionId));
        command.Parameters.AddWithValue("@mode", route.Mode);
        command.Parameters.AddWithValue("@visibility", route.Visibility);
        command.Parameters.AddWithValue("@schedule", ToDbValue(route.ScheduleJson));
        command.Parameters.AddWithValue("@is_enabled", route.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("@settings", ToDbValue(route.SettingsJson));
        command.Parameters.AddWithValue("@created_at", ToDbDate(route.CreatedAt));
        command.Parameters.AddWithValue("@updated_at", ToDbValue(route.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordSyncRouteRunAsync(SyncRouteRunInfo run, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_runs (id, route_id, connection_id, started_at, finished_at, status, summary, error)
            VALUES (@id, @route_id, @connection_id, @started_at, @finished_at, @status, @summary, @error)
            ON CONFLICT(id) DO UPDATE SET
                route_id = excluded.route_id,
                connection_id = excluded.connection_id,
                finished_at = excluded.finished_at,
                status = excluded.status,
                summary = excluded.summary,
                error = excluded.error
            """;
        command.Parameters.AddWithValue("@id", run.Id);
        command.Parameters.AddWithValue("@route_id", ToDbValue(run.RouteId));
        command.Parameters.AddWithValue("@connection_id", ToDbValue(run.ConnectionId));
        command.Parameters.AddWithValue("@started_at", ToDbDate(run.StartedAt));
        command.Parameters.AddWithValue("@finished_at", ToDbValue(run.FinishedAt));
        command.Parameters.AddWithValue("@status", run.Status);
        command.Parameters.AddWithValue("@summary", ToDbValue(run.SummaryJson));
        command.Parameters.AddWithValue("@error", ToDbValue(run.Error));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SyncRouteRunInfo>> GetSyncRouteRunsAsync(string? routeId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, route_id, connection_id, started_at, finished_at, status, summary, error
            FROM sync_runs
            WHERE @route_id IS NULL OR route_id = @route_id
            ORDER BY started_at DESC
            """;
        command.Parameters.AddWithValue("@route_id", (object?)routeId ?? DBNull.Value);

        var runs = new List<SyncRouteRunInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            runs.Add(new SyncRouteRunInfo
            {
                Id = reader.GetString(0),
                RouteId = GetNullableString(reader, 1),
                ConnectionId = GetNullableString(reader, 2),
                StartedAt = ReadDate(reader, 3) ?? DateTimeOffset.UtcNow,
                FinishedAt = ReadDate(reader, 4),
                Status = reader.GetString(5),
                SummaryJson = GetNullableString(reader, 6),
                Error = GetNullableString(reader, 7),
            });
        }

        return runs;
    }

    public async Task UpsertTaskAsync(TaskItem task, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await UpsertTaskCoreAsync(connection, task, cancellationToken).ConfigureAwait(false);
        await SetTaskLabelsCoreAsync(connection, task.Id, task.Labels, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PersonalCloudK1OutboxEntry> CreateTaskWithPersonalCloudIntentAsync(
        TaskItem task,
        string instanceId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var mutationId = $"mutation_{Guid.NewGuid():N}";
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var existingTaskCommand = connection.CreateCommand();
        existingTaskCommand.CommandText = "SELECT COUNT(*) FROM tasks WHERE id = @id";
        existingTaskCommand.Parameters.AddWithValue("@id", task.Id);
        if (Convert.ToInt64(await existingTaskCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0)
        {
            throw new PersonalCloudK0ContractException("entity_exists", "A task with this ID already exists.");
        }

        var initializeDeviceCommand = connection.CreateCommand();
        initializeDeviceCommand.CommandText = """
            INSERT OR IGNORE INTO personal_cloud_device_state (instance_id, device_id, next_device_sequence)
            VALUES (@instance_id, @device_id, 1)
            """;
        initializeDeviceCommand.Parameters.AddWithValue("@instance_id", instanceId);
        initializeDeviceCommand.Parameters.AddWithValue("@device_id", deviceId);
        await initializeDeviceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var readSequenceCommand = connection.CreateCommand();
        readSequenceCommand.CommandText = """
            SELECT next_device_sequence
            FROM personal_cloud_device_state
            WHERE instance_id = @instance_id AND device_id = @device_id
            """;
        readSequenceCommand.Parameters.AddWithValue("@instance_id", instanceId);
        readSequenceCommand.Parameters.AddWithValue("@device_id", deviceId);
        var deviceSequence = Convert.ToInt64(
            await readSequenceCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

        var mutation = PersonalCloudK0Mapper.CreateTaskMutation(
            task,
            instanceId,
            deviceId,
            mutationId,
            deviceSequence,
            createdAt);
        var canonicalJson = PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize(mutation));
        var canonicalHash = PersonalCloudK0Codec.ComputeCanonicalHash(canonicalJson);

        await UpsertTaskCoreAsync(connection, task, cancellationToken).ConfigureAwait(false);
        await SetTaskLabelsCoreAsync(connection, task.Id, task.Labels, cancellationToken).ConfigureAwait(false);

        var outboxCommand = connection.CreateCommand();
        outboxCommand.CommandText = """
            INSERT INTO personal_cloud_outbox
                (mutation_id, product, instance_id, device_id, device_sequence, operation, entity_id,
                 canonical_json, canonical_hash, created_at)
            VALUES
                (@mutation_id, @product, @instance_id, @device_id, @device_sequence, @operation, @entity_id,
                 @canonical_json, @canonical_hash, @created_at)
            RETURNING local_sequence
            """;
        outboxCommand.Parameters.AddWithValue("@mutation_id", mutationId);
        outboxCommand.Parameters.AddWithValue("@product", PersonalCloudK0Protocol.TasksProduct);
        outboxCommand.Parameters.AddWithValue("@instance_id", instanceId);
        outboxCommand.Parameters.AddWithValue("@device_id", deviceId);
        outboxCommand.Parameters.AddWithValue("@device_sequence", deviceSequence);
        outboxCommand.Parameters.AddWithValue("@operation", PersonalCloudK0Protocol.CreateTaskOperation);
        outboxCommand.Parameters.AddWithValue("@entity_id", task.Id);
        outboxCommand.Parameters.AddWithValue("@canonical_json", canonicalJson);
        outboxCommand.Parameters.AddWithValue("@canonical_hash", canonicalHash);
        outboxCommand.Parameters.AddWithValue("@created_at", createdAt.ToUnixTimeMilliseconds());
        var localSequence = Convert.ToInt64(
            await outboxCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

        var advanceSequenceCommand = connection.CreateCommand();
        advanceSequenceCommand.CommandText = """
            UPDATE personal_cloud_device_state
            SET next_device_sequence = @next_device_sequence
            WHERE instance_id = @instance_id
              AND device_id = @device_id
              AND next_device_sequence = @device_sequence
            """;
        advanceSequenceCommand.Parameters.AddWithValue("@next_device_sequence", checked(deviceSequence + 1));
        advanceSequenceCommand.Parameters.AddWithValue("@instance_id", instanceId);
        advanceSequenceCommand.Parameters.AddWithValue("@device_id", deviceId);
        advanceSequenceCommand.Parameters.AddWithValue("@device_sequence", deviceSequence);
        if (await advanceSequenceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The Personal Cloud device sequence could not be advanced atomically.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PersonalCloudK1OutboxEntry
        {
            LocalSequence = localSequence,
            MutationId = mutationId,
            Product = PersonalCloudK0Protocol.TasksProduct,
            InstanceId = instanceId,
            DeviceId = deviceId,
            DeviceSequence = deviceSequence,
            Operation = PersonalCloudK0Protocol.CreateTaskOperation,
            EntityId = task.Id,
            CanonicalJson = canonicalJson,
            CanonicalHash = canonicalHash,
            CreatedAt = createdAt,
        };
    }

    public async Task<IReadOnlyList<PersonalCloudK1OutboxEntry>> GetPendingPersonalCloudOutboxAsync(
        string instanceId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT local_sequence, mutation_id, product, instance_id, device_id, device_sequence, operation,
                   entity_id, canonical_json, canonical_hash, created_at
            FROM personal_cloud_outbox
            WHERE product = @product
              AND instance_id = @instance_id
              AND device_id = @device_id
              AND acknowledged_at IS NULL
            ORDER BY device_sequence, local_sequence
            """;
        command.Parameters.AddWithValue("@instance_id", instanceId);
        command.Parameters.AddWithValue("@device_id", deviceId);
        command.Parameters.AddWithValue("@product", PersonalCloudK0Protocol.TasksProduct);

        var entries = new List<PersonalCloudK1OutboxEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new PersonalCloudK1OutboxEntry
            {
                LocalSequence = reader.GetInt64(0),
                MutationId = reader.GetString(1),
                Product = reader.GetString(2),
                InstanceId = reader.GetString(3),
                DeviceId = reader.GetString(4),
                DeviceSequence = reader.GetInt64(5),
                Operation = reader.GetString(6),
                EntityId = reader.GetString(7),
                CanonicalJson = reader.GetString(8),
                CanonicalHash = reader.GetString(9),
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)),
            });
        }

        return entries;
    }

    public async Task<PersonalCloudK1OutboxEntry> EnqueueExistingTaskForPersonalCloudAsync(
        string taskId,string instanceId,string deviceId,CancellationToken cancellationToken=default)
    {
        if (await GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false) is null)
            throw new PersonalCloudK0ContractException("entity_not_found", "The Openza task does not exist.");
        if (await GetPersonalCloudEntityVersionAsync(instanceId, taskId, cancellationToken).ConfigureAwait(false) is not null)
            throw new PersonalCloudK0ContractException("entity_exists", "The Openza task is already queued or published to this Personal Cloud instance.");

        // Use the same normalized capture/restoration sequence as initial sync.
        // Staging all unpublished tasks also stages parent dependencies first.
        await StageUnpublishedTasksForPersonalCloudAsync(instanceId, deviceId, cancellationToken).ConfigureAwait(false);
        var pending = await GetPendingPersonalCloudOutboxAsync(instanceId, deviceId, cancellationToken).ConfigureAwait(false);
        return pending.FirstOrDefault(item => item.EntityId == taskId &&
                   item.Operation is PersonalCloudK0Protocol.CreateTaskOperation or "task.createRich")
            ?? throw new PersonalCloudK0ContractException("entity_exists", "The Openza task is already published to this Personal Cloud instance.");
    }

    public async Task EnsureNoPendingPersonalCloudIntentsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureNoPendingPersonalCloudIntentsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureNoPendingPersonalCloudIntentsAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "personal_cloud_outbox", cancellationToken).ConfigureAwait(false)) return;
        var hasDeferredTable = await TableExistsAsync(connection, "personal_cloud_deferred_changes", cancellationToken).ConfigureAwait(false);
        var hasBridgeTable = await TableExistsAsync(connection, "personal_cloud_provider_attempts", cancellationToken).ConfigureAwait(false);
        var hasCatalogActions = await TableExistsAsync(connection, "personal_cloud_provider_intent_actions", cancellationToken).ConfigureAwait(false);
        var hasLegacyCompletions = await TableExistsAsync(connection, "pending_completions", cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM personal_cloud_outbox WHERE product='tasks' AND acknowledged_at IS NULL)" +
            (hasCatalogActions ? " + (SELECT COUNT(*) FROM personal_cloud_provider_intent_actions WHERE phase!='settled')" : "") +
            (hasDeferredTable ? " + (SELECT COUNT(*) FROM personal_cloud_deferred_changes)" : "") +
            (hasBridgeTable ? " + (SELECT COUNT(*) FROM personal_cloud_provider_attempts WHERE state NOT IN ('settled','cancelled'))" : "") +
            (hasLegacyCompletions ? " + (SELECT COUNT(*) FROM pending_completions p WHERE EXISTS(SELECT 1 FROM personal_cloud_outbox o WHERE o.entity_id=p.task_id AND o.operation IN ('task.complete','task.reopen'))" +
                (hasBridgeTable ? " OR EXISTS(SELECT 1 FROM personal_cloud_provider_commands c WHERE c.task_id=p.task_id)" : "") + ")" : "");
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0)
            throw new PersonalCloudK0ContractException("pending_intents",
                "Pending Personal Cloud edits must be synced or explicitly resolved before replacing the replica or changing its connection. Your edits have been preserved.");
    }

    public async Task<int> GetDeferredPersonalCloudChangeCountAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM personal_cloud_deferred_changes WHERE instance_id=@i AND product='tasks'";
        command.Parameters.AddWithValue("@i", instanceId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<int> StageUnpublishedTasksForPersonalCloudAsync(
        string instanceId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (await SupportsPersonalCloudWorkspaceAsync(instanceId, cancellationToken).ConfigureAwait(false))
            return await StagePersonalCloudWorkspaceSeedAsync(instanceId, deviceId, cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var tasks = (await ReadTasksAsync(connection, cancellationToken).ConfigureAwait(false))
            .Where(task => task.IntegrationId == IntegrationIds.Local)
            .ToList();

        var alreadyPublished = new HashSet<string>(StringComparer.Ordinal);
        var publishedCommand = connection.CreateCommand();
        publishedCommand.CommandText = """
            SELECT entity_id FROM personal_cloud_outbox
            WHERE instance_id=@instance_id AND product=@product AND operation=@operation
            UNION
            SELECT entity_id FROM personal_cloud_replica_entities
            WHERE instance_id=@instance_id AND product=@product
            """;
        publishedCommand.Parameters.AddWithValue("@instance_id", instanceId);
        publishedCommand.Parameters.AddWithValue("@product", PersonalCloudK0Protocol.TasksProduct);
        publishedCommand.Parameters.AddWithValue("@operation", PersonalCloudK0Protocol.CreateTaskOperation);
        await using (var reader = await publishedCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) alreadyPublished.Add(reader.GetString(0));
        tasks = tasks.Where(task => !alreadyPublished.Contains(task.Id)).ToList();
        if (tasks.Count == 0)
        {
            await SealInitialSyncSeedAsync(connection, instanceId, deviceId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var spaces = new Dictionary<string, string>(StringComparer.Ordinal);
        var spacesCommand = connection.CreateCommand();
        spacesCommand.CommandText = "SELECT id,name FROM spaces";
        await using (var reader = await spacesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) spaces[reader.GetString(0)] = reader.GetString(1);
        var projects = new Dictionary<string, string>(StringComparer.Ordinal);
        var projectsCommand = connection.CreateCommand();
        projectsCommand.CommandText = "SELECT id,name FROM projects";
        await using (var reader = await projectsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) projects[reader.GetString(0)] = reader.GetString(1);

        var initializeDevice = connection.CreateCommand();
        initializeDevice.CommandText = "INSERT OR IGNORE INTO personal_cloud_device_state(instance_id,device_id,next_device_sequence) VALUES(@i,@d,1)";
        initializeDevice.Parameters.AddWithValue("@i", instanceId);
        initializeDevice.Parameters.AddWithValue("@d", deviceId);
        await initializeDevice.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var sequenceCommand = connection.CreateCommand();
        sequenceCommand.CommandText = "SELECT next_device_sequence FROM personal_cloud_device_state WHERE instance_id=@i AND device_id=@d";
        sequenceCommand.Parameters.AddWithValue("@i", instanceId);
        sequenceCommand.Parameters.AddWithValue("@d", deviceId);
        var sequence = Convert.ToInt64(await sequenceCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        var staged = 0;
        var versions = tasks.ToDictionary(task => task.Id, _ => 0L, StringComparer.Ordinal);

        async Task AppendAsync(string operation, string entityId, object mutation)
        {
            var json = PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize(mutation));
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO personal_cloud_outbox
                    (mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,
                     canonical_json,canonical_hash,created_at)
                VALUES(@m,@p,@i,@d,@q,@o,@e,@j,@h,@a)
                """;
            using var document = JsonDocument.Parse(json);
            command.Parameters.AddWithValue("@m", document.RootElement.GetProperty("mutationId").GetString()!);
            command.Parameters.AddWithValue("@p", PersonalCloudK0Protocol.TasksProduct);
            command.Parameters.AddWithValue("@i", instanceId);
            command.Parameters.AddWithValue("@d", deviceId);
            command.Parameters.AddWithValue("@q", sequence);
            command.Parameters.AddWithValue("@o", operation);
            command.Parameters.AddWithValue("@e", entityId);
            command.Parameters.AddWithValue("@j", json);
            command.Parameters.AddWithValue("@h", PersonalCloudK0Codec.ComputeCanonicalHash(json));
            command.Parameters.AddWithValue("@a", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            sequence = checked(sequence + 1);
            staged++;
        }

        string MutationId() => $"mutation_{Guid.NewGuid():N}";
        string Timestamp() => DateTimeOffset.UtcNow.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        object Relationship(string operation, TaskItem task, long version, object payload) => new
        {
            protocolVersion = 1,
            product = PersonalCloudK0Protocol.TasksProduct,
            instanceId,
            deviceId,
            mutationId = MutationId(),
            deviceSequence = sequence,
            operation,
            entityId = task.Id,
            baseEntityVersion = version,
            payload,
            clientCreatedAt = Timestamp(),
        };

        foreach (var task in tasks)
        {
            var initial = task with
            {
                SpaceId = SpaceIds.Default,
                ProjectId = null,
                ParentId = null,
                CompletionState = TaskCompletionState.Open,
                WorkflowStatus = TaskWorkflowStatus.Inbox,
                Priority = 2,
                PlannedOn = null,
                PlannedAt = null,
                CompletedAt = null,
                Labels = [],
            };
            var mutationId = MutationId();
            var mutation = PersonalCloudK0Mapper.CreateTaskMutation(
                initial, instanceId, deviceId, mutationId, sequence, DateTimeOffset.UtcNow);
            await AppendAsync(PersonalCloudK0Protocol.CreateTaskOperation, task.Id, mutation).ConfigureAwait(false);
            versions[task.Id] = 1;
        }

        foreach (var task in tasks.Where(task => task.SpaceId != SpaceIds.Default))
        {
            if (!spaces.TryGetValue(task.SpaceId, out var name))
                throw new PersonalCloudK0ContractException("parent_not_found", "An Openza task references a missing Space.");
            var version = versions[task.Id];
            await AppendAsync("task.move", task.Id, Relationship("task.move", task, version, new { space = new { id = task.SpaceId, name } })).ConfigureAwait(false);
            versions[task.Id] = checked(version + 1);
        }

        foreach (var task in tasks)
        {
            if (task.ProjectId is { } projectId)
            {
                if (!projects.TryGetValue(projectId, out var name))
                    throw new PersonalCloudK0ContractException("parent_not_found", "An Openza task references a missing project.");
                var version = versions[task.Id];
                await AppendAsync("task.setProject", task.Id, Relationship("task.setProject", task, version, new { project = new { id = projectId, name } })).ConfigureAwait(false);
                versions[task.Id] = checked(version + 1);
            }
            foreach (var label in task.Labels)
            {
                var version = versions[task.Id];
                await AppendAsync("task.addLabel", task.Id, Relationship("task.addLabel", task, version, new { label = new { id = label.Id, name = label.Name } })).ConfigureAwait(false);
                versions[task.Id] = checked(version + 1);
            }
            if (task.WorkflowStatus != TaskWorkflowStatus.Inbox)
            {
                var version = versions[task.Id];
                var mutation = PersonalCloudWorkflowStatusUpdateCodec.CreateMutation(
                    instanceId, deviceId, MutationId(), sequence, task.Id, version, task.WorkflowStatus.ToStorageValue(), Timestamp());
                await AppendAsync(PersonalCloudK0Protocol.UpdateOpenzaFieldsOperation, task.Id, mutation).ConfigureAwait(false);
                versions[task.Id] = checked(version + 1);
            }
            if (task.Priority != 2)
            {
                var version = versions[task.Id];
                var mutation = PersonalCloudPriorityUpdateCodec.CreateMutation(
                    instanceId, deviceId, MutationId(), sequence, task.Id, version, task.Priority, Timestamp());
                await AppendAsync(PersonalCloudK0Protocol.UpdateOpenzaFieldsOperation, task.Id, mutation).ConfigureAwait(false);
                versions[task.Id] = checked(version + 1);
            }
            if (task.PlannedAt is { } plannedAt)
            {
                var version = versions[task.Id];
                var mutation = PersonalCloudPlanningCodec.CreatePlannedAtMutation(
                    instanceId, deviceId, MutationId(), sequence, task.Id, version, plannedAt, Timestamp());
                await AppendAsync(PersonalCloudK0Protocol.UpdateOpenzaFieldsOperation, task.Id, mutation).ConfigureAwait(false);
                versions[task.Id] = checked(version + 1);
            }
            else if (task.PlannedOn is { } plannedOn)
            {
                var version = versions[task.Id];
                var mutation = PersonalCloudPlanningCodec.CreatePlannedOnMutation(
                    instanceId, deviceId, MutationId(), sequence, task.Id, version, plannedOn, Timestamp());
                await AppendAsync(PersonalCloudK0Protocol.UpdateOpenzaFieldsOperation, task.Id, mutation).ConfigureAwait(false);
                versions[task.Id] = checked(version + 1);
            }
            if (task.Notes is { } notes)
            {
                var version = versions[task.Id];
                var mutation = PersonalCloudNotesDeadlineCodec.CreateNotesMutation(
                    instanceId, deviceId, MutationId(), sequence, task.Id, version, notes, Timestamp());
                await AppendAsync(PersonalCloudK0Protocol.UpdateOpenzaFieldsOperation, task.Id, mutation).ConfigureAwait(false);
                versions[task.Id] = checked(version + 1);
            }
            if (task.DeadlineOn is { } deadlineOn)
            {
                var version = versions[task.Id];
                var mutation = PersonalCloudNotesDeadlineCodec.CreateDeadlineOnMutation(
                    instanceId, deviceId, MutationId(), sequence, task.Id, version,
                    deadlineOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), Timestamp());
                await AppendAsync(PersonalCloudK0Protocol.UpdateOpenzaFieldsOperation, task.Id, mutation).ConfigureAwait(false);
                versions[task.Id] = checked(version + 1);
            }
        }

        foreach (var task in tasks.Where(task => task.ParentId is not null))
        {
            var version = versions[task.Id];
            await AppendAsync("task.setParent", task.Id, Relationship("task.setParent", task, version, new { parentId = task.ParentId })).ConfigureAwait(false);
            versions[task.Id] = checked(version + 1);
        }

        foreach (var task in tasks.Where(task => task.CompletionState == TaskCompletionState.Completed))
        {
            var version = versions[task.Id];
            var completedAt = (task.CompletedAt ?? task.UpdatedAt ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
            var mutation = PersonalCloudCompletionCodec.CreateMutation(
                instanceId, deviceId, MutationId(), sequence, task.Id, version, completedAt, Timestamp());
            await AppendAsync(PersonalCloudK0Protocol.CompleteTaskOperation, task.Id, mutation).ConfigureAwait(false);
            versions[task.Id] = checked(version + 1);
        }

        var advance = connection.CreateCommand();
        advance.CommandText = "UPDATE personal_cloud_device_state SET next_device_sequence=@next WHERE instance_id=@i AND device_id=@d";
        advance.Parameters.AddWithValue("@next", sequence);
        advance.Parameters.AddWithValue("@i", instanceId);
        advance.Parameters.AddWithValue("@d", deviceId);
        if (await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The Personal Cloud device sequence could not be advanced.");
        await SealInitialSyncSeedAsync(connection, instanceId, deviceId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return staged;
    }

    public async Task<PersonalCloudK1OutboxEntry> UpdateTaskTitleWithPersonalCloudIntentAsync(
        string taskId,
        string title,
        long baseEntityVersion,
        string instanceId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        var mutationId = $"mutation_{Guid.NewGuid():N}";
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var timestamp = createdAt.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            System.Globalization.CultureInfo.InvariantCulture);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var taskCommand = connection.CreateCommand();
        taskCommand.CommandText = """
            SELECT integration_id, provider_connection_id, source_integration_id, source_external_id, title
            FROM tasks
            WHERE id = @id
            """;
        taskCommand.Parameters.AddWithValue("@id", taskId);
        await using (var reader = await taskCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new PersonalCloudK0ContractException("entity_not_found", "The task does not exist.");
            }
            if (!string.Equals(reader.GetString(0), IntegrationIds.Local, StringComparison.Ordinal) ||
                !reader.IsDBNull(1) || !reader.IsDBNull(2) || !reader.IsDBNull(3))
            {
                throw new PersonalCloudK0ContractException(
                    "invalid_request",
                    "The source-owned title has no Openza override and cannot be changed through Personal Cloud.");
            }
            if (string.Equals(reader.GetString(4), title, StringComparison.Ordinal))
            {
                throw new PersonalCloudK0ContractException("invalid_request", "The new title must differ from the current title.");
            }
        }

        var initializeDeviceCommand = connection.CreateCommand();
        initializeDeviceCommand.CommandText = """
            INSERT OR IGNORE INTO personal_cloud_device_state (instance_id, device_id, next_device_sequence)
            VALUES (@instance_id, @device_id, 1)
            """;
        initializeDeviceCommand.Parameters.AddWithValue("@instance_id", instanceId);
        initializeDeviceCommand.Parameters.AddWithValue("@device_id", deviceId);
        await initializeDeviceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var readSequenceCommand = connection.CreateCommand();
        readSequenceCommand.CommandText = """
            SELECT next_device_sequence
            FROM personal_cloud_device_state
            WHERE instance_id = @instance_id AND device_id = @device_id
            """;
        readSequenceCommand.Parameters.AddWithValue("@instance_id", instanceId);
        readSequenceCommand.Parameters.AddWithValue("@device_id", deviceId);
        var deviceSequence = Convert.ToInt64(
            await readSequenceCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

        var mutation = PersonalCloudTitleUpdateCodec.CreateMutation(
            instanceId,
            deviceId,
            mutationId,
            deviceSequence,
            taskId,
            baseEntityVersion,
            title,
            timestamp);
        var canonicalJson = PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize(mutation));
        var canonicalHash = PersonalCloudK0Codec.ComputeCanonicalHash(canonicalJson);

        var updateCommand = connection.CreateCommand();
        updateCommand.CommandText = """
            UPDATE tasks
            SET title = @title, updated_at = @updated_at, revision = revision + 1
            WHERE id = @id
            """;
        updateCommand.Parameters.AddWithValue("@title", title);
        updateCommand.Parameters.AddWithValue("@updated_at", createdAt.ToUnixTimeMilliseconds());
        updateCommand.Parameters.AddWithValue("@id", taskId);
        if (await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The local task title could not be updated.");
        }

        var outboxCommand = connection.CreateCommand();
        outboxCommand.CommandText = """
            INSERT INTO personal_cloud_outbox
                (mutation_id, product, instance_id, device_id, device_sequence, operation, entity_id,
                 canonical_json, canonical_hash, created_at)
            VALUES
                (@mutation_id, @product, @instance_id, @device_id, @device_sequence, @operation, @entity_id,
                 @canonical_json, @canonical_hash, @created_at)
            RETURNING local_sequence
            """;
        outboxCommand.Parameters.AddWithValue("@mutation_id", mutationId);
        outboxCommand.Parameters.AddWithValue("@product", PersonalCloudK0Protocol.TasksProduct);
        outboxCommand.Parameters.AddWithValue("@instance_id", instanceId);
        outboxCommand.Parameters.AddWithValue("@device_id", deviceId);
        outboxCommand.Parameters.AddWithValue("@device_sequence", deviceSequence);
        outboxCommand.Parameters.AddWithValue("@operation", PersonalCloudK0Protocol.UpdateOpenzaFieldsOperation);
        outboxCommand.Parameters.AddWithValue("@entity_id", taskId);
        outboxCommand.Parameters.AddWithValue("@canonical_json", canonicalJson);
        outboxCommand.Parameters.AddWithValue("@canonical_hash", canonicalHash);
        outboxCommand.Parameters.AddWithValue("@created_at", createdAt.ToUnixTimeMilliseconds());
        var localSequence = Convert.ToInt64(
            await outboxCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

        var advanceSequenceCommand = connection.CreateCommand();
        advanceSequenceCommand.CommandText = """
            UPDATE personal_cloud_device_state
            SET next_device_sequence = @next_device_sequence
            WHERE instance_id = @instance_id
              AND device_id = @device_id
              AND next_device_sequence = @device_sequence
            """;
        advanceSequenceCommand.Parameters.AddWithValue("@next_device_sequence", checked(deviceSequence + 1));
        advanceSequenceCommand.Parameters.AddWithValue("@instance_id", instanceId);
        advanceSequenceCommand.Parameters.AddWithValue("@device_id", deviceId);
        advanceSequenceCommand.Parameters.AddWithValue("@device_sequence", deviceSequence);
        if (await advanceSequenceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The Personal Cloud device sequence could not be advanced atomically.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PersonalCloudK1OutboxEntry
        {
            LocalSequence = localSequence,
            MutationId = mutationId,
            Product = PersonalCloudK0Protocol.TasksProduct,
            InstanceId = instanceId,
            DeviceId = deviceId,
            DeviceSequence = deviceSequence,
            Operation = PersonalCloudK0Protocol.UpdateOpenzaFieldsOperation,
            EntityId = taskId,
            CanonicalJson = canonicalJson,
            CanonicalHash = canonicalHash,
            CreatedAt = createdAt,
        };
    }

    public async Task<PersonalCloudK1OutboxEntry> UpdateTaskWorkflowStatusWithPersonalCloudIntentAsync(
        string taskId,
        TaskWorkflowStatus workflowStatus,
        long baseEntityVersion,
        string instanceId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(workflowStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(workflowStatus));
        }
        var storedWorkflowStatus = workflowStatus.ToStorageValue();
        var mutationId = $"mutation_{Guid.NewGuid():N}";
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var timestamp = createdAt.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            System.Globalization.CultureInfo.InvariantCulture);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var taskCommand = connection.CreateCommand();
        taskCommand.CommandText = """
            SELECT integration_id, provider_connection_id, source_integration_id, source_external_id,
                   completion_state, workflow_status
            FROM tasks
            WHERE id = @id
            """;
        taskCommand.Parameters.AddWithValue("@id", taskId);
        await using (var reader = await taskCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new PersonalCloudK0ContractException("entity_not_found", "The task does not exist.");
            }
            if (!string.Equals(reader.GetString(0), IntegrationIds.Local, StringComparison.Ordinal) || !reader.IsDBNull(1))
            {
                throw new PersonalCloudK0ContractException(
                    "invalid_request",
                    "This operation requires an Openza task row.");
            }
            if (!string.Equals(reader.GetString(4), TaskCompletionState.Open.ToStorageValue(), StringComparison.Ordinal))
            {
                throw new PersonalCloudK0ContractException(
                    "invalid_request",
                    "This checkpoint changes workflow status on an open task only.");
            }
            if (string.Equals(reader.GetString(5), storedWorkflowStatus, StringComparison.Ordinal))
            {
                throw new PersonalCloudK0ContractException(
                    "invalid_request",
                    "The new workflow status must differ from the current workflow status.");
            }
        }

        var initializeDeviceCommand = connection.CreateCommand();
        initializeDeviceCommand.CommandText = """
            INSERT OR IGNORE INTO personal_cloud_device_state (instance_id, device_id, next_device_sequence)
            VALUES (@instance_id, @device_id, 1)
            """;
        initializeDeviceCommand.Parameters.AddWithValue("@instance_id", instanceId);
        initializeDeviceCommand.Parameters.AddWithValue("@device_id", deviceId);
        await initializeDeviceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var readSequenceCommand = connection.CreateCommand();
        readSequenceCommand.CommandText = """
            SELECT next_device_sequence
            FROM personal_cloud_device_state
            WHERE instance_id = @instance_id AND device_id = @device_id
            """;
        readSequenceCommand.Parameters.AddWithValue("@instance_id", instanceId);
        readSequenceCommand.Parameters.AddWithValue("@device_id", deviceId);
        var deviceSequence = Convert.ToInt64(
            await readSequenceCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

        var mutation = PersonalCloudWorkflowStatusUpdateCodec.CreateMutation(
            instanceId,
            deviceId,
            mutationId,
            deviceSequence,
            taskId,
            baseEntityVersion,
            storedWorkflowStatus,
            timestamp);
        var canonicalJson = PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize(mutation));
        var canonicalHash = PersonalCloudK0Codec.ComputeCanonicalHash(canonicalJson);

        var updateCommand = connection.CreateCommand();
        updateCommand.CommandText = """
            UPDATE tasks
            SET workflow_status = @workflow_status, updated_at = @updated_at, revision = revision + 1
            WHERE id = @id AND completion_state = 'open'
            """;
        updateCommand.Parameters.AddWithValue("@workflow_status", storedWorkflowStatus);
        updateCommand.Parameters.AddWithValue("@updated_at", createdAt.ToUnixTimeMilliseconds());
        updateCommand.Parameters.AddWithValue("@id", taskId);
        if (await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The local task workflow status could not be updated.");
        }

        var outboxCommand = connection.CreateCommand();
        outboxCommand.CommandText = """
            INSERT INTO personal_cloud_outbox
                (mutation_id, product, instance_id, device_id, device_sequence, operation, entity_id,
                 canonical_json, canonical_hash, created_at)
            VALUES
                (@mutation_id, @product, @instance_id, @device_id, @device_sequence, @operation, @entity_id,
                 @canonical_json, @canonical_hash, @created_at)
            RETURNING local_sequence
            """;
        outboxCommand.Parameters.AddWithValue("@mutation_id", mutationId);
        outboxCommand.Parameters.AddWithValue("@product", PersonalCloudK0Protocol.TasksProduct);
        outboxCommand.Parameters.AddWithValue("@instance_id", instanceId);
        outboxCommand.Parameters.AddWithValue("@device_id", deviceId);
        outboxCommand.Parameters.AddWithValue("@device_sequence", deviceSequence);
        outboxCommand.Parameters.AddWithValue("@operation", PersonalCloudK0Protocol.UpdateOpenzaFieldsOperation);
        outboxCommand.Parameters.AddWithValue("@entity_id", taskId);
        outboxCommand.Parameters.AddWithValue("@canonical_json", canonicalJson);
        outboxCommand.Parameters.AddWithValue("@canonical_hash", canonicalHash);
        outboxCommand.Parameters.AddWithValue("@created_at", createdAt.ToUnixTimeMilliseconds());
        var localSequence = Convert.ToInt64(
            await outboxCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

        var advanceSequenceCommand = connection.CreateCommand();
        advanceSequenceCommand.CommandText = """
            UPDATE personal_cloud_device_state
            SET next_device_sequence = @next_device_sequence
            WHERE instance_id = @instance_id
              AND device_id = @device_id
              AND next_device_sequence = @device_sequence
            """;
        advanceSequenceCommand.Parameters.AddWithValue("@next_device_sequence", checked(deviceSequence + 1));
        advanceSequenceCommand.Parameters.AddWithValue("@instance_id", instanceId);
        advanceSequenceCommand.Parameters.AddWithValue("@device_id", deviceId);
        advanceSequenceCommand.Parameters.AddWithValue("@device_sequence", deviceSequence);
        if (await advanceSequenceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The Personal Cloud device sequence could not be advanced atomically.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PersonalCloudK1OutboxEntry
        {
            LocalSequence = localSequence,
            MutationId = mutationId,
            Product = PersonalCloudK0Protocol.TasksProduct,
            InstanceId = instanceId,
            DeviceId = deviceId,
            DeviceSequence = deviceSequence,
            Operation = PersonalCloudK0Protocol.UpdateOpenzaFieldsOperation,
            EntityId = taskId,
            CanonicalJson = canonicalJson,
            CanonicalHash = canonicalHash,
            CreatedAt = createdAt,
        };
    }

    public async Task<PersonalCloudK1OutboxEntry> UpdateTaskPriorityWithPersonalCloudIntentAsync(
        string taskId,
        int priority,
        long baseEntityVersion,
        string instanceId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (priority is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(priority), "Priority must be between 1 and 4.");
        }
        var mutationId = $"mutation_{Guid.NewGuid():N}";
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var timestamp = createdAt.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            System.Globalization.CultureInfo.InvariantCulture);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var taskCommand = connection.CreateCommand();
        taskCommand.CommandText = """
            SELECT integration_id, provider_connection_id, source_integration_id, source_external_id,
                   completion_state, priority
            FROM tasks
            WHERE id = @id
            """;
        taskCommand.Parameters.AddWithValue("@id", taskId);
        await using (var reader = await taskCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new PersonalCloudK0ContractException("entity_not_found", "The task does not exist.");
            }
            if (!string.Equals(reader.GetString(0), IntegrationIds.Local, StringComparison.Ordinal) || !reader.IsDBNull(1))
            {
                throw new PersonalCloudK0ContractException(
                    "invalid_request",
                    "This operation requires an Openza task row.");
            }
            if (!string.Equals(reader.GetString(4), TaskCompletionState.Open.ToStorageValue(), StringComparison.Ordinal))
            {
                throw new PersonalCloudK0ContractException(
                    "invalid_request",
                    "This checkpoint changes priority on an open task only.");
            }
            if (reader.GetInt32(5) == priority)
            {
                throw new PersonalCloudK0ContractException(
                    "invalid_request",
                    "The new priority must differ from the current priority.");
            }
        }

        var initializeDeviceCommand = connection.CreateCommand();
        initializeDeviceCommand.CommandText = """
            INSERT OR IGNORE INTO personal_cloud_device_state (instance_id, device_id, next_device_sequence)
            VALUES (@instance_id, @device_id, 1)
            """;
        initializeDeviceCommand.Parameters.AddWithValue("@instance_id", instanceId);
        initializeDeviceCommand.Parameters.AddWithValue("@device_id", deviceId);
        await initializeDeviceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var readSequenceCommand = connection.CreateCommand();
        readSequenceCommand.CommandText = """
            SELECT next_device_sequence
            FROM personal_cloud_device_state
            WHERE instance_id = @instance_id AND device_id = @device_id
            """;
        readSequenceCommand.Parameters.AddWithValue("@instance_id", instanceId);
        readSequenceCommand.Parameters.AddWithValue("@device_id", deviceId);
        var deviceSequence = Convert.ToInt64(
            await readSequenceCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

        var mutation = PersonalCloudPriorityUpdateCodec.CreateMutation(
            instanceId,
            deviceId,
            mutationId,
            deviceSequence,
            taskId,
            baseEntityVersion,
            priority,
            timestamp);
        var canonicalJson = PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize(mutation));
        var canonicalHash = PersonalCloudK0Codec.ComputeCanonicalHash(canonicalJson);

        var updateCommand = connection.CreateCommand();
        updateCommand.CommandText = """
            UPDATE tasks
            SET priority = @priority, updated_at = @updated_at, revision = revision + 1
            WHERE id = @id AND completion_state = 'open'
            """;
        updateCommand.Parameters.AddWithValue("@priority", priority);
        updateCommand.Parameters.AddWithValue("@updated_at", createdAt.ToUnixTimeMilliseconds());
        updateCommand.Parameters.AddWithValue("@id", taskId);
        if (await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The local task priority could not be updated.");
        }

        var outboxCommand = connection.CreateCommand();
        outboxCommand.CommandText = """
            INSERT INTO personal_cloud_outbox
                (mutation_id, product, instance_id, device_id, device_sequence, operation, entity_id,
                 canonical_json, canonical_hash, created_at)
            VALUES
                (@mutation_id, @product, @instance_id, @device_id, @device_sequence, @operation, @entity_id,
                 @canonical_json, @canonical_hash, @created_at)
            RETURNING local_sequence
            """;
        outboxCommand.Parameters.AddWithValue("@mutation_id", mutationId);
        outboxCommand.Parameters.AddWithValue("@product", PersonalCloudK0Protocol.TasksProduct);
        outboxCommand.Parameters.AddWithValue("@instance_id", instanceId);
        outboxCommand.Parameters.AddWithValue("@device_id", deviceId);
        outboxCommand.Parameters.AddWithValue("@device_sequence", deviceSequence);
        outboxCommand.Parameters.AddWithValue("@operation", PersonalCloudK0Protocol.UpdateOpenzaFieldsOperation);
        outboxCommand.Parameters.AddWithValue("@entity_id", taskId);
        outboxCommand.Parameters.AddWithValue("@canonical_json", canonicalJson);
        outboxCommand.Parameters.AddWithValue("@canonical_hash", canonicalHash);
        outboxCommand.Parameters.AddWithValue("@created_at", createdAt.ToUnixTimeMilliseconds());
        var localSequence = Convert.ToInt64(
            await outboxCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

        var advanceSequenceCommand = connection.CreateCommand();
        advanceSequenceCommand.CommandText = """
            UPDATE personal_cloud_device_state
            SET next_device_sequence = @next_device_sequence
            WHERE instance_id = @instance_id
              AND device_id = @device_id
              AND next_device_sequence = @device_sequence
            """;
        advanceSequenceCommand.Parameters.AddWithValue("@next_device_sequence", checked(deviceSequence + 1));
        advanceSequenceCommand.Parameters.AddWithValue("@instance_id", instanceId);
        advanceSequenceCommand.Parameters.AddWithValue("@device_id", deviceId);
        advanceSequenceCommand.Parameters.AddWithValue("@device_sequence", deviceSequence);
        if (await advanceSequenceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The Personal Cloud device sequence could not be advanced atomically.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PersonalCloudK1OutboxEntry
        {
            LocalSequence = localSequence,
            MutationId = mutationId,
            Product = PersonalCloudK0Protocol.TasksProduct,
            InstanceId = instanceId,
            DeviceId = deviceId,
            DeviceSequence = deviceSequence,
            Operation = PersonalCloudK0Protocol.UpdateOpenzaFieldsOperation,
            EntityId = taskId,
            CanonicalJson = canonicalJson,
            CanonicalHash = canonicalHash,
            CreatedAt = createdAt,
        };
    }

    public Task<PersonalCloudK1OutboxEntry> UpdateTaskPlannedOnWithPersonalCloudIntentAsync(
        string taskId, DateOnly? plannedOn, long baseEntityVersion, string instanceId, string deviceId,
        CancellationToken cancellationToken = default) =>
        UpdateTaskPlanningWithPersonalCloudIntentAsync(
            taskId, plannedOn, null, useExactTime: false, baseEntityVersion, instanceId, deviceId, cancellationToken);

    public Task<PersonalCloudK1OutboxEntry> UpdateTaskPlannedAtWithPersonalCloudIntentAsync(
        string taskId, DateTimeOffset? plannedAt, long baseEntityVersion, string instanceId, string deviceId,
        CancellationToken cancellationToken = default) =>
        UpdateTaskPlanningWithPersonalCloudIntentAsync(
            taskId, null, plannedAt, useExactTime: true, baseEntityVersion, instanceId, deviceId, cancellationToken);

    private async Task<PersonalCloudK1OutboxEntry> UpdateTaskPlanningWithPersonalCloudIntentAsync(
        string taskId, DateOnly? plannedOn, DateTimeOffset? plannedAt, bool useExactTime,
        long baseEntityVersion, string instanceId, string deviceId, CancellationToken cancellationToken)
    {
        var mutationId = $"mutation_{Guid.NewGuid():N}";
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        DateTimeOffset? normalizedPlannedAt = plannedAt is null
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(plannedAt.Value.ToUnixTimeMilliseconds());
        var timestamp = createdAt.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        TaskItem originalTask;
        var taskCommand = connection.CreateCommand();
        taskCommand.CommandText = """
            SELECT integration_id, provider_connection_id, source_integration_id, source_external_id,
                   completion_state, planned_on, planned_at
            FROM tasks WHERE id = @id
            """;
        taskCommand.Parameters.AddWithValue("@id", taskId);
        await using (var reader = await taskCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new PersonalCloudK0ContractException("entity_not_found", "The task does not exist.");
            if (reader.GetString(0) != IntegrationIds.Local || !reader.IsDBNull(1))
                throw new PersonalCloudK0ContractException("invalid_request", "This operation requires an Openza task row.");
            if (reader.GetString(4) != TaskCompletionState.Open.ToStorageValue())
                throw new PersonalCloudK0ContractException("invalid_transition", "Only an open task can be planned.");
            var currentOn = reader.IsDBNull(5) ? null : reader.GetString(5);
            var currentAt = reader.IsDBNull(6) ? (long?)null : reader.GetInt64(6);
            var same = useExactTime
                ? currentOn is null && currentAt == normalizedPlannedAt?.ToUnixTimeMilliseconds()
                : currentAt is null && currentOn == plannedOn?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            if (same) throw new PersonalCloudK0ContractException("invalid_request", "The planned value must change.");
        }
        originalTask = (await ReadTasksAsync(connection, cancellationToken).ConfigureAwait(false))
            .First(task => task.Id == taskId);

        var initialize = connection.CreateCommand();
        initialize.CommandText = "INSERT OR IGNORE INTO personal_cloud_device_state (instance_id, device_id, next_device_sequence) VALUES (@i,@d,1)";
        initialize.Parameters.AddWithValue("@i", instanceId);
        initialize.Parameters.AddWithValue("@d", deviceId);
        await initialize.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var sequenceCommand = connection.CreateCommand();
        sequenceCommand.CommandText = "SELECT next_device_sequence FROM personal_cloud_device_state WHERE instance_id=@i AND device_id=@d";
        sequenceCommand.Parameters.AddWithValue("@i", instanceId);
        sequenceCommand.Parameters.AddWithValue("@d", deviceId);
        var deviceSequence = Convert.ToInt64(await sequenceCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

        var serializedMutation = useExactTime
            ? PersonalCloudK0Codec.Serialize(PersonalCloudPlanningCodec.CreatePlannedAtMutation(
                instanceId, deviceId, mutationId, deviceSequence, taskId, baseEntityVersion, normalizedPlannedAt, timestamp))
            : PersonalCloudK0Codec.Serialize(PersonalCloudPlanningCodec.CreatePlannedOnMutation(
                instanceId, deviceId, mutationId, deviceSequence, taskId, baseEntityVersion, plannedOn, timestamp));
        var canonicalJson = PersonalCloudK0Codec.Canonicalize(serializedMutation);
        var canonicalHash = PersonalCloudK0Codec.ComputeCanonicalHash(canonicalJson);

        var update = connection.CreateCommand();
        update.CommandText = useExactTime
            ? "UPDATE tasks SET planned_on=NULL, planned_at=@value, updated_at=@updated, revision=revision+1 WHERE id=@id AND completion_state='open'"
            : "UPDATE tasks SET planned_on=@value, planned_at=NULL, updated_at=@updated, revision=revision+1 WHERE id=@id AND completion_state='open'";
        object planningDbValue;
        if (useExactTime)
            planningDbValue = normalizedPlannedAt is { } exact ? exact.ToUnixTimeMilliseconds() : DBNull.Value;
        else
            planningDbValue = plannedOn is { } date
                ? date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                : DBNull.Value;
        update.Parameters.AddWithValue("@value", planningDbValue);
        update.Parameters.AddWithValue("@updated", createdAt.ToUnixTimeMilliseconds());
        update.Parameters.AddWithValue("@id", taskId);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new PersonalCloudK0ContractException("invalid_transition", "Only an open task can be planned.");
        var updatedTask = (await ReadTasksAsync(connection, cancellationToken).ConfigureAwait(false))
            .First(task => task.Id == taskId);
        var pendingDateUpdate = ProviderWriteBackPlanner.CreateTodoistDateUpdate(originalTask, updatedTask, createdAt);
        if (pendingDateUpdate is not null)
        {
            await QueueTaskDateUpdateCoreAsync(connection, pendingDateUpdate, cancellationToken).ConfigureAwait(false);
        }

        var outbox = connection.CreateCommand();
        outbox.CommandText = """
            INSERT INTO personal_cloud_outbox
              (mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at)
            VALUES (@m,'tasks',@i,@d,@s,'task.updateOpenzaFields',@e,@j,@h,@a)
            RETURNING local_sequence
            """;
        outbox.Parameters.AddWithValue("@m", mutationId);
        outbox.Parameters.AddWithValue("@i", instanceId);
        outbox.Parameters.AddWithValue("@d", deviceId);
        outbox.Parameters.AddWithValue("@s", deviceSequence);
        outbox.Parameters.AddWithValue("@e", taskId);
        outbox.Parameters.AddWithValue("@j", canonicalJson);
        outbox.Parameters.AddWithValue("@h", canonicalHash);
        outbox.Parameters.AddWithValue("@a", createdAt.ToUnixTimeMilliseconds());
        var localSequence = Convert.ToInt64(await outbox.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        var advance = connection.CreateCommand();
        advance.CommandText = "UPDATE personal_cloud_device_state SET next_device_sequence=@n WHERE instance_id=@i AND device_id=@d AND next_device_sequence=@s";
        advance.Parameters.AddWithValue("@n", checked(deviceSequence + 1));
        advance.Parameters.AddWithValue("@i", instanceId);
        advance.Parameters.AddWithValue("@d", deviceId);
        advance.Parameters.AddWithValue("@s", deviceSequence);
        if (await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The Personal Cloud device sequence could not be advanced atomically.");

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PersonalCloudK1OutboxEntry
        {
            LocalSequence = localSequence, MutationId = mutationId, Product = "tasks", InstanceId = instanceId,
            DeviceId = deviceId, DeviceSequence = deviceSequence, Operation = "task.updateOpenzaFields",
            EntityId = taskId, CanonicalJson = canonicalJson, CanonicalHash = canonicalHash, CreatedAt = createdAt,
        };
    }

    public Task<PersonalCloudK1OutboxEntry> UpdateTaskNotesWithPersonalCloudIntentAsync(
        string taskId, string? notes, long baseEntityVersion, string instanceId, string deviceId,
        CancellationToken cancellationToken = default) =>
        UpdateTaskTextValueWithPersonalCloudIntentAsync(
            taskId, notes, updateNotes: true, baseEntityVersion, instanceId, deviceId, cancellationToken);

    public Task<PersonalCloudK1OutboxEntry> UpdateTaskDeadlineOnWithPersonalCloudIntentAsync(
        string taskId, DateOnly? deadlineOn, long baseEntityVersion, string instanceId, string deviceId,
        CancellationToken cancellationToken = default) =>
        UpdateTaskTextValueWithPersonalCloudIntentAsync(
            taskId, deadlineOn?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            updateNotes: false, baseEntityVersion, instanceId, deviceId, cancellationToken);

    private async Task<PersonalCloudK1OutboxEntry> UpdateTaskTextValueWithPersonalCloudIntentAsync(
        string taskId, string? value, bool updateNotes, long baseEntityVersion,
        string instanceId, string deviceId, CancellationToken cancellationToken)
    {
        var mutationId = $"mutation_{Guid.NewGuid():N}";
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var timestamp = createdAt.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var read = connection.CreateCommand();
        read.CommandText = updateNotes
            ? "SELECT integration_id,provider_connection_id,notes FROM tasks WHERE id=@id"
            : "SELECT integration_id,provider_connection_id,deadline_on,deadline_at FROM tasks WHERE id=@id";
        read.Parameters.AddWithValue("@id", taskId);
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new PersonalCloudK0ContractException("entity_not_found", "The task does not exist.");
            if (reader.GetString(0) != IntegrationIds.Local || !reader.IsDBNull(1))
                throw new PersonalCloudK0ContractException("invalid_request", "This operation requires an Openza task row.");
            var current = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (current == value && (updateNotes || reader.IsDBNull(3)))
                throw new PersonalCloudK0ContractException("invalid_request", "The task value must change.");
        }

        var initialize = connection.CreateCommand();
        initialize.CommandText = "INSERT OR IGNORE INTO personal_cloud_device_state(instance_id,device_id,next_device_sequence) VALUES(@i,@d,1)";
        initialize.Parameters.AddWithValue("@i", instanceId);
        initialize.Parameters.AddWithValue("@d", deviceId);
        await initialize.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var sequenceCommand = connection.CreateCommand();
        sequenceCommand.CommandText = "SELECT next_device_sequence FROM personal_cloud_device_state WHERE instance_id=@i AND device_id=@d";
        sequenceCommand.Parameters.AddWithValue("@i", instanceId);
        sequenceCommand.Parameters.AddWithValue("@d", deviceId);
        var sequence = Convert.ToInt64(await sequenceCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

        var mutation = updateNotes
            ? PersonalCloudK0Codec.Serialize(PersonalCloudNotesDeadlineCodec.CreateNotesMutation(
                instanceId, deviceId, mutationId, sequence, taskId, baseEntityVersion, value, timestamp))
            : PersonalCloudK0Codec.Serialize(PersonalCloudNotesDeadlineCodec.CreateDeadlineOnMutation(
                instanceId, deviceId, mutationId, sequence, taskId, baseEntityVersion, value, timestamp));
        var canonicalJson = PersonalCloudK0Codec.Canonicalize(mutation);
        var canonicalHash = PersonalCloudK0Codec.ComputeCanonicalHash(canonicalJson);

        var update = connection.CreateCommand();
        update.CommandText = updateNotes
            ? "UPDATE tasks SET notes=@value,updated_at=@now,revision=revision+1 WHERE id=@id"
            : "UPDATE tasks SET deadline_on=@value,deadline_at=NULL,updated_at=@now,revision=revision+1 WHERE id=@id";
        update.Parameters.AddWithValue("@value", (object?)value ?? DBNull.Value);
        update.Parameters.AddWithValue("@now", createdAt.ToUnixTimeMilliseconds());
        update.Parameters.AddWithValue("@id", taskId);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new PersonalCloudK0ContractException("entity_not_found", "The task does not exist.");

        var outbox = connection.CreateCommand();
        outbox.CommandText = """
            INSERT INTO personal_cloud_outbox
              (mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at)
            VALUES(@m,'tasks',@i,@d,@s,'task.updateOpenzaFields',@e,@j,@h,@a)
            RETURNING local_sequence
            """;
        outbox.Parameters.AddWithValue("@m", mutationId);
        outbox.Parameters.AddWithValue("@i", instanceId);
        outbox.Parameters.AddWithValue("@d", deviceId);
        outbox.Parameters.AddWithValue("@s", sequence);
        outbox.Parameters.AddWithValue("@e", taskId);
        outbox.Parameters.AddWithValue("@j", canonicalJson);
        outbox.Parameters.AddWithValue("@h", canonicalHash);
        outbox.Parameters.AddWithValue("@a", createdAt.ToUnixTimeMilliseconds());
        var localSequence = Convert.ToInt64(await outbox.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        var advance = connection.CreateCommand();
        advance.CommandText = "UPDATE personal_cloud_device_state SET next_device_sequence=@n WHERE instance_id=@i AND device_id=@d AND next_device_sequence=@s";
        advance.Parameters.AddWithValue("@n", checked(sequence + 1));
        advance.Parameters.AddWithValue("@i", instanceId);
        advance.Parameters.AddWithValue("@d", deviceId);
        advance.Parameters.AddWithValue("@s", sequence);
        if (await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The Personal Cloud device sequence could not advance atomically.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PersonalCloudK1OutboxEntry
        {
            LocalSequence = localSequence, MutationId = mutationId, Product = "tasks", InstanceId = instanceId,
            DeviceId = deviceId, DeviceSequence = sequence, Operation = "task.updateOpenzaFields",
            EntityId = taskId, CanonicalJson = canonicalJson, CanonicalHash = canonicalHash, CreatedAt = createdAt,
        };
    }

    public async Task<PersonalCloudK1OutboxEntry> CompleteTaskWithPersonalCloudIntentAsync(
        string taskId,
        DateTimeOffset completedAt,
        long baseEntityVersion,
        string instanceId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        var mutationId = $"mutation_{Guid.NewGuid():N}";
        var normalizedCompletedAt = DateTimeOffset.FromUnixTimeMilliseconds(completedAt.ToUnixTimeMilliseconds());
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var completedTimestamp = normalizedCompletedAt.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
        var clientTimestamp = createdAt.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            System.Globalization.CultureInfo.InvariantCulture);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var taskCommand = connection.CreateCommand();
        taskCommand.CommandText = """
            SELECT integration_id, provider_connection_id, source_integration_id, source_external_id,
                   completion_state
            FROM tasks
            WHERE id = @id
            """;
        taskCommand.Parameters.AddWithValue("@id", taskId);
        await using (var reader = await taskCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new PersonalCloudK0ContractException("entity_not_found", "The task does not exist.");
            if (!string.Equals(reader.GetString(0), IntegrationIds.Local, StringComparison.Ordinal) || !reader.IsDBNull(1))
                throw new PersonalCloudK0ContractException("invalid_request", "This operation requires an Openza task row.");
            if (!string.Equals(reader.GetString(4), TaskCompletionState.Open.ToStorageValue(), StringComparison.Ordinal))
                throw new PersonalCloudK0ContractException("invalid_transition", "Only an open task can be completed.");
        }
        var originalTask=(await ReadTasksAsync(connection,cancellationToken).ConfigureAwait(false)).First(task=>task.Id==taskId);

        var initializeDeviceCommand = connection.CreateCommand();
        initializeDeviceCommand.CommandText = """
            INSERT OR IGNORE INTO personal_cloud_device_state (instance_id, device_id, next_device_sequence)
            VALUES (@instance_id, @device_id, 1)
            """;
        initializeDeviceCommand.Parameters.AddWithValue("@instance_id", instanceId);
        initializeDeviceCommand.Parameters.AddWithValue("@device_id", deviceId);
        await initializeDeviceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var readSequenceCommand = connection.CreateCommand();
        readSequenceCommand.CommandText = """
            SELECT next_device_sequence FROM personal_cloud_device_state
            WHERE instance_id = @instance_id AND device_id = @device_id
            """;
        readSequenceCommand.Parameters.AddWithValue("@instance_id", instanceId);
        readSequenceCommand.Parameters.AddWithValue("@device_id", deviceId);
        var deviceSequence = Convert.ToInt64(
            await readSequenceCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

        var mutation = PersonalCloudCompletionCodec.CreateMutation(
            instanceId, deviceId, mutationId, deviceSequence, taskId, baseEntityVersion,
            completedTimestamp, clientTimestamp);
        var canonicalJson = PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize(mutation));
        var canonicalHash = PersonalCloudK0Codec.ComputeCanonicalHash(canonicalJson);

        var updateCommand = connection.CreateCommand();
        updateCommand.CommandText = """
            UPDATE tasks
            SET completion_state = 'completed', completed_at = @completed_at,
                updated_at = @updated_at, revision = revision + 1
            WHERE id = @id AND completion_state = 'open'
            """;
        updateCommand.Parameters.AddWithValue("@completed_at", normalizedCompletedAt.ToUnixTimeMilliseconds());
        updateCommand.Parameters.AddWithValue("@updated_at", createdAt.ToUnixTimeMilliseconds());
        updateCommand.Parameters.AddWithValue("@id", taskId);
        if (await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new PersonalCloudK0ContractException("invalid_transition", "Only an open task can be completed.");

        var outboxCommand = connection.CreateCommand();
        outboxCommand.CommandText = """
            INSERT INTO personal_cloud_outbox
                (mutation_id, product, instance_id, device_id, device_sequence, operation, entity_id,
                 canonical_json, canonical_hash, created_at)
            VALUES
                (@mutation_id, @product, @instance_id, @device_id, @device_sequence, @operation, @entity_id,
                 @canonical_json, @canonical_hash, @created_at)
            RETURNING local_sequence
            """;
        outboxCommand.Parameters.AddWithValue("@mutation_id", mutationId);
        outboxCommand.Parameters.AddWithValue("@product", PersonalCloudK0Protocol.TasksProduct);
        outboxCommand.Parameters.AddWithValue("@instance_id", instanceId);
        outboxCommand.Parameters.AddWithValue("@device_id", deviceId);
        outboxCommand.Parameters.AddWithValue("@device_sequence", deviceSequence);
        outboxCommand.Parameters.AddWithValue("@operation", PersonalCloudK0Protocol.CompleteTaskOperation);
        outboxCommand.Parameters.AddWithValue("@entity_id", taskId);
        outboxCommand.Parameters.AddWithValue("@canonical_json", canonicalJson);
        outboxCommand.Parameters.AddWithValue("@canonical_hash", canonicalHash);
        outboxCommand.Parameters.AddWithValue("@created_at", createdAt.ToUnixTimeMilliseconds());
        var localSequence = Convert.ToInt64(
            await outboxCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

        var advanceSequenceCommand = connection.CreateCommand();
        advanceSequenceCommand.CommandText = """
            UPDATE personal_cloud_device_state
            SET next_device_sequence = @next_device_sequence
            WHERE instance_id = @instance_id AND device_id = @device_id
              AND next_device_sequence = @device_sequence
            """;
        advanceSequenceCommand.Parameters.AddWithValue("@next_device_sequence", checked(deviceSequence + 1));
        advanceSequenceCommand.Parameters.AddWithValue("@instance_id", instanceId);
        advanceSequenceCommand.Parameters.AddWithValue("@device_id", deviceId);
        advanceSequenceCommand.Parameters.AddWithValue("@device_sequence", deviceSequence);
        if (await advanceSequenceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The Personal Cloud device sequence could not be advanced atomically.");

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PersonalCloudK1OutboxEntry
        {
            LocalSequence = localSequence,
            MutationId = mutationId,
            Product = PersonalCloudK0Protocol.TasksProduct,
            InstanceId = instanceId,
            DeviceId = deviceId,
            DeviceSequence = deviceSequence,
            Operation = PersonalCloudK0Protocol.CompleteTaskOperation,
            EntityId = taskId,
            CanonicalJson = canonicalJson,
            CanonicalHash = canonicalHash,
            CreatedAt = createdAt,
        };
    }

    public async Task<PersonalCloudK1OutboxEntry> ReopenTaskWithPersonalCloudIntentAsync(
        string taskId,
        DateTimeOffset reopenedAt,
        long baseEntityVersion,
        string instanceId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        var mutationId = $"mutation_{Guid.NewGuid():N}";
        var normalizedReopenedAt = DateTimeOffset.FromUnixTimeMilliseconds(reopenedAt.ToUnixTimeMilliseconds());
        var timestamp = normalizedReopenedAt.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            System.Globalization.CultureInfo.InvariantCulture);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var taskCommand = connection.CreateCommand();
        taskCommand.CommandText = """
            SELECT integration_id, provider_connection_id, source_integration_id, source_external_id,
                   completion_state
            FROM tasks
            WHERE id = @id
            """;
        taskCommand.Parameters.AddWithValue("@id", taskId);
        await using (var reader = await taskCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new PersonalCloudK0ContractException("entity_not_found", "The task does not exist.");
            if (!string.Equals(reader.GetString(0), IntegrationIds.Local, StringComparison.Ordinal) || !reader.IsDBNull(1))
                throw new PersonalCloudK0ContractException("invalid_request", "This operation requires an Openza task row.");
            if (!string.Equals(reader.GetString(4), TaskCompletionState.Completed.ToStorageValue(), StringComparison.Ordinal))
                throw new PersonalCloudK0ContractException("invalid_transition", "Only a completed task can be reopened.");
        }
        var originalTask=(await ReadTasksAsync(connection,cancellationToken).ConfigureAwait(false)).First(task=>task.Id==taskId);

        var initializeDeviceCommand = connection.CreateCommand();
        initializeDeviceCommand.CommandText = """
            INSERT OR IGNORE INTO personal_cloud_device_state (instance_id, device_id, next_device_sequence)
            VALUES (@instance_id, @device_id, 1)
            """;
        initializeDeviceCommand.Parameters.AddWithValue("@instance_id", instanceId);
        initializeDeviceCommand.Parameters.AddWithValue("@device_id", deviceId);
        await initializeDeviceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var readSequenceCommand = connection.CreateCommand();
        readSequenceCommand.CommandText = """
            SELECT next_device_sequence FROM personal_cloud_device_state
            WHERE instance_id = @instance_id AND device_id = @device_id
            """;
        readSequenceCommand.Parameters.AddWithValue("@instance_id", instanceId);
        readSequenceCommand.Parameters.AddWithValue("@device_id", deviceId);
        var deviceSequence = Convert.ToInt64(
            await readSequenceCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

        var mutation = PersonalCloudReopenCodec.CreateMutation(
            instanceId, deviceId, mutationId, deviceSequence, taskId, baseEntityVersion, timestamp, timestamp);
        var canonicalJson = PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize(mutation));
        var canonicalHash = PersonalCloudK0Codec.ComputeCanonicalHash(canonicalJson);

        var updateCommand = connection.CreateCommand();
        updateCommand.CommandText = """
            UPDATE tasks
            SET completion_state = 'open', completed_at = NULL,
                updated_at = @updated_at, revision = revision + 1
            WHERE id = @id AND completion_state = 'completed'
            """;
        updateCommand.Parameters.AddWithValue("@updated_at", normalizedReopenedAt.ToUnixTimeMilliseconds());
        updateCommand.Parameters.AddWithValue("@id", taskId);
        if (await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new PersonalCloudK0ContractException("invalid_transition", "Only a completed task can be reopened.");

        var outboxCommand = connection.CreateCommand();
        outboxCommand.CommandText = """
            INSERT INTO personal_cloud_outbox
                (mutation_id, product, instance_id, device_id, device_sequence, operation, entity_id,
                 canonical_json, canonical_hash, created_at)
            VALUES
                (@mutation_id, @product, @instance_id, @device_id, @device_sequence, @operation, @entity_id,
                 @canonical_json, @canonical_hash, @created_at)
            RETURNING local_sequence
            """;
        outboxCommand.Parameters.AddWithValue("@mutation_id", mutationId);
        outboxCommand.Parameters.AddWithValue("@product", PersonalCloudK0Protocol.TasksProduct);
        outboxCommand.Parameters.AddWithValue("@instance_id", instanceId);
        outboxCommand.Parameters.AddWithValue("@device_id", deviceId);
        outboxCommand.Parameters.AddWithValue("@device_sequence", deviceSequence);
        outboxCommand.Parameters.AddWithValue("@operation", PersonalCloudK0Protocol.ReopenTaskOperation);
        outboxCommand.Parameters.AddWithValue("@entity_id", taskId);
        outboxCommand.Parameters.AddWithValue("@canonical_json", canonicalJson);
        outboxCommand.Parameters.AddWithValue("@canonical_hash", canonicalHash);
        outboxCommand.Parameters.AddWithValue("@created_at", normalizedReopenedAt.ToUnixTimeMilliseconds());
        var localSequence = Convert.ToInt64(
            await outboxCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

        var advanceSequenceCommand = connection.CreateCommand();
        advanceSequenceCommand.CommandText = """
            UPDATE personal_cloud_device_state
            SET next_device_sequence = @next_device_sequence
            WHERE instance_id = @instance_id AND device_id = @device_id
              AND next_device_sequence = @device_sequence
            """;
        advanceSequenceCommand.Parameters.AddWithValue("@next_device_sequence", checked(deviceSequence + 1));
        advanceSequenceCommand.Parameters.AddWithValue("@instance_id", instanceId);
        advanceSequenceCommand.Parameters.AddWithValue("@device_id", deviceId);
        advanceSequenceCommand.Parameters.AddWithValue("@device_sequence", deviceSequence);
        if (await advanceSequenceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The Personal Cloud device sequence could not be advanced atomically.");

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PersonalCloudK1OutboxEntry
        {
            LocalSequence = localSequence,
            MutationId = mutationId,
            Product = PersonalCloudK0Protocol.TasksProduct,
            InstanceId = instanceId,
            DeviceId = deviceId,
            DeviceSequence = deviceSequence,
            Operation = PersonalCloudK0Protocol.ReopenTaskOperation,
            EntityId = taskId,
            CanonicalJson = canonicalJson,
            CanonicalHash = canonicalHash,
            CreatedAt = normalizedReopenedAt,
        };
    }

    public async Task<PersonalCloudK1OutboxEntry> DeleteTaskWithPersonalCloudIntentAsync(
        string taskId, DateTimeOffset deletedAt, long baseEntityVersion, string instanceId, string deviceId,
        CancellationToken cancellationToken = default)
    {
        var normalized = DateTimeOffset.FromUnixTimeMilliseconds(deletedAt.ToUnixTimeMilliseconds());
        var timestamp = normalized.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var mutationId = $"mutation_{Guid.NewGuid():N}";
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var task = connection.CreateCommand();
        task.CommandText = "SELECT integration_id,provider_connection_id,source_integration_id,source_external_id FROM tasks WHERE id=@id";
        task.Parameters.AddWithValue("@id", taskId);
        await using (var reader = await task.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new PersonalCloudK0ContractException("entity_not_found","The task does not exist.");
            if(reader.GetString(0)!=IntegrationIds.Local||!reader.IsDBNull(1)||!reader.IsDBNull(2)||!reader.IsDBNull(3))
                throw new PersonalCloudK0ContractException("invalid_request","Only a local Openza task can be deleted here.");
        }
        var already=connection.CreateCommand(); already.CommandText="SELECT COUNT(*) FROM personal_cloud_tombstones WHERE instance_id=@i AND product='tasks' AND entity_id=@e";
        already.Parameters.AddWithValue("@i",instanceId); already.Parameters.AddWithValue("@e",taskId);
        if(Convert.ToInt64(await already.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!=0)
            throw new PersonalCloudK0ContractException("invalid_transition","The task is already deleted.");
        var init=connection.CreateCommand(); init.CommandText="INSERT OR IGNORE INTO personal_cloud_device_state(instance_id,device_id,next_device_sequence) VALUES(@i,@d,1)";
        init.Parameters.AddWithValue("@i",instanceId);init.Parameters.AddWithValue("@d",deviceId);await init.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var seq=connection.CreateCommand();seq.CommandText="SELECT next_device_sequence FROM personal_cloud_device_state WHERE instance_id=@i AND device_id=@d";
        seq.Parameters.AddWithValue("@i",instanceId);seq.Parameters.AddWithValue("@d",deviceId);var deviceSequence=Convert.ToInt64(await seq.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        var mutation=PersonalCloudDeleteCodec.CreateMutation(instanceId,deviceId,mutationId,deviceSequence,taskId,baseEntityVersion,timestamp,timestamp);
        var json=PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize(mutation));var hash=PersonalCloudK0Codec.ComputeCanonicalHash(json);
        var tombstone=connection.CreateCommand();tombstone.CommandText="INSERT INTO personal_cloud_tombstones(instance_id,product,entity_id,entity_version,deleted_at,purge_after) VALUES(@i,'tasks',@e,@v,@a,NULL)";
        tombstone.Parameters.AddWithValue("@i",instanceId);tombstone.Parameters.AddWithValue("@e",taskId);tombstone.Parameters.AddWithValue("@v",checked(baseEntityVersion+1));tombstone.Parameters.AddWithValue("@a",normalized.ToUnixTimeMilliseconds());await tombstone.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var outbox=connection.CreateCommand();outbox.CommandText="""
          INSERT INTO personal_cloud_outbox(mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at)
          VALUES(@m,'tasks',@i,@d,@s,'task.delete',@e,@j,@h,@a) RETURNING local_sequence
          """;
        outbox.Parameters.AddWithValue("@m",mutationId);outbox.Parameters.AddWithValue("@i",instanceId);outbox.Parameters.AddWithValue("@d",deviceId);outbox.Parameters.AddWithValue("@s",deviceSequence);outbox.Parameters.AddWithValue("@e",taskId);outbox.Parameters.AddWithValue("@j",json);outbox.Parameters.AddWithValue("@h",hash);outbox.Parameters.AddWithValue("@a",normalized.ToUnixTimeMilliseconds());var localSequence=Convert.ToInt64(await outbox.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        var advance=connection.CreateCommand();advance.CommandText="UPDATE personal_cloud_device_state SET next_device_sequence=@n WHERE instance_id=@i AND device_id=@d AND next_device_sequence=@s";
        advance.Parameters.AddWithValue("@n",checked(deviceSequence+1));advance.Parameters.AddWithValue("@i",instanceId);advance.Parameters.AddWithValue("@d",deviceId);advance.Parameters.AddWithValue("@s",deviceSequence);
        if(await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false)!=1)throw new InvalidOperationException("The device sequence could not advance.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PersonalCloudK1OutboxEntry{LocalSequence=localSequence,MutationId=mutationId,Product="tasks",InstanceId=instanceId,DeviceId=deviceId,DeviceSequence=deviceSequence,Operation="task.delete",EntityId=taskId,CanonicalJson=json,CanonicalHash=hash,CreatedAt=normalized};
    }

    public async Task<PersonalCloudK1OutboxEntry> MutateTaskRelationshipWithPersonalCloudIntentAsync(
        string taskId, string operation, string? relatedId, string? relatedName, long baseEntityVersion,
        string instanceId, string deviceId, CancellationToken cancellationToken = default)
    {
        var allowed=new[]{"task.setProject","task.move","task.addLabel","task.removeLabel","task.setParent"};
        if(!allowed.Contains(operation,StringComparer.Ordinal)) throw new PersonalCloudK0ContractException("invalid_request","The relationship operation is unsupported.");
        if((operation is "task.move" or "task.addLabel" or "task.removeLabel") && string.IsNullOrWhiteSpace(relatedId)) throw new PersonalCloudK0ContractException("invalid_request","A related entity ID is required.");
        if(((operation=="task.setProject" && relatedId is not null) || operation is "task.move" or "task.addLabel") && string.IsNullOrWhiteSpace(relatedName)) throw new PersonalCloudK0ContractException("invalid_request","A related entity name is required.");
        await using var connection=await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);await using var transaction=await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var read=connection.CreateCommand();read.CommandText="SELECT space_id,integration_id FROM tasks WHERE id=@id";read.Parameters.AddWithValue("@id",taskId);
        string spaceId,integration;await using(var reader=await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)){if(!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))throw new PersonalCloudK0ContractException("entity_not_found","The task does not exist.");spaceId=reader.GetString(0);integration=reader.GetString(1);}
        if(integration!=IntegrationIds.Local)throw new PersonalCloudK0ContractException("invalid_request","Only a local Openza task can be changed here.");
        var init=connection.CreateCommand();init.CommandText="INSERT OR IGNORE INTO personal_cloud_device_state(instance_id,device_id,next_device_sequence) VALUES(@i,@d,1)";init.Parameters.AddWithValue("@i",instanceId);init.Parameters.AddWithValue("@d",deviceId);await init.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var seqCommand=connection.CreateCommand();seqCommand.CommandText="SELECT next_device_sequence FROM personal_cloud_device_state WHERE instance_id=@i AND device_id=@d";seqCommand.Parameters.AddWithValue("@i",instanceId);seqCommand.Parameters.AddWithValue("@d",deviceId);var sequence=Convert.ToInt64(await seqCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        var now=DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());var command=connection.CreateCommand();
        object payload;
        if(operation=="task.setProject") {if(relatedId is not null){command.CommandText="INSERT INTO projects(id,space_id,integration_id,name,created_at) VALUES(@r,@s,'openza_tasks',@n,@now) ON CONFLICT(id) DO UPDATE SET name=excluded.name";command.Parameters.AddWithValue("@r",relatedId);command.Parameters.AddWithValue("@s",spaceId);command.Parameters.AddWithValue("@n",relatedName!);command.Parameters.AddWithValue("@now",now.ToUnixTimeMilliseconds());await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);}command=connection.CreateCommand();command.CommandText="UPDATE tasks SET project_id=@r,revision=revision+1 WHERE id=@id";command.Parameters.AddWithValue("@r",(object?)relatedId??DBNull.Value);payload=relatedId is null?new Dictionary<string,object?>{{"project",null}}:new{project=new{id=relatedId,name=relatedName}};}
        else if(operation=="task.move") {command.CommandText="INSERT INTO spaces(id,name,created_at) VALUES(@r,@n,@now) ON CONFLICT(id) DO UPDATE SET name=excluded.name";command.Parameters.AddWithValue("@r",relatedId!);command.Parameters.AddWithValue("@n",relatedName!);command.Parameters.AddWithValue("@now",now.ToUnixTimeMilliseconds());await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);command=connection.CreateCommand();command.CommandText="UPDATE tasks SET space_id=@r,project_id=NULL,parent_id=NULL,revision=revision+1 WHERE id=@id";command.Parameters.AddWithValue("@r",relatedId!);payload=new{space=new{id=relatedId,name=relatedName}};}
        else if(operation=="task.addLabel") {command.CommandText="INSERT INTO labels(id,integration_id,name,created_at) VALUES(@r,'openza_tasks',@n,@now) ON CONFLICT(id) DO UPDATE SET name=excluded.name";command.Parameters.AddWithValue("@r",relatedId!);command.Parameters.AddWithValue("@n",relatedName!);command.Parameters.AddWithValue("@now",now.ToUnixTimeMilliseconds());await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);command=connection.CreateCommand();command.CommandText="INSERT INTO task_labels(task_id,label_id) VALUES(@id,@r)";command.Parameters.AddWithValue("@r",relatedId!);payload=new{label=new{id=relatedId,name=relatedName}};}
        else if(operation=="task.removeLabel") {command.CommandText="DELETE FROM task_labels WHERE task_id=@id AND label_id=@r";command.Parameters.AddWithValue("@r",relatedId!);payload=new{labelId=relatedId};}
        else {if(relatedId==taskId)throw new PersonalCloudK0ContractException("invalid_request","A task cannot parent itself.");if(relatedId is not null){var parent=connection.CreateCommand();parent.CommandText="SELECT COUNT(*) FROM tasks WHERE id=@p AND space_id=@s";parent.Parameters.AddWithValue("@p",relatedId);parent.Parameters.AddWithValue("@s",spaceId);if(Convert.ToInt64(await parent.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!=1)throw new PersonalCloudK0ContractException("parent_not_found","The parent must be in the same Space.");}command.CommandText="UPDATE tasks SET parent_id=@r,revision=revision+1 WHERE id=@id";command.Parameters.AddWithValue("@r",(object?)relatedId??DBNull.Value);payload=new{parentId=relatedId};}
        command.Parameters.AddWithValue("@id",taskId);if(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false)!=1)throw new PersonalCloudK0ContractException("invalid_request","The relationship did not change.");
        var mutationId=$"mutation_{Guid.NewGuid():N}";var envelope=new{protocolVersion=1,product="tasks",instanceId,deviceId,mutationId,deviceSequence=sequence,operation,entityId=taskId,baseEntityVersion,payload,clientCreatedAt=now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",System.Globalization.CultureInfo.InvariantCulture)};
        var json=PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize(envelope));var hash=PersonalCloudK0Codec.ComputeCanonicalHash(json);
        var outbox=connection.CreateCommand();outbox.CommandText="""
          INSERT INTO personal_cloud_outbox(mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at)
          VALUES(@m,'tasks',@i,@d,@q,@o,@e,@j,@h,@a) RETURNING local_sequence
          """;outbox.Parameters.AddWithValue("@m",mutationId);outbox.Parameters.AddWithValue("@i",instanceId);outbox.Parameters.AddWithValue("@d",deviceId);outbox.Parameters.AddWithValue("@q",sequence);outbox.Parameters.AddWithValue("@o",operation);outbox.Parameters.AddWithValue("@e",taskId);outbox.Parameters.AddWithValue("@j",json);outbox.Parameters.AddWithValue("@h",hash);outbox.Parameters.AddWithValue("@a",now.ToUnixTimeMilliseconds());var localSequence=Convert.ToInt64(await outbox.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        var advance=connection.CreateCommand();advance.CommandText="UPDATE personal_cloud_device_state SET next_device_sequence=@next WHERE instance_id=@i AND device_id=@d AND next_device_sequence=@q";advance.Parameters.AddWithValue("@next",sequence+1);advance.Parameters.AddWithValue("@i",instanceId);advance.Parameters.AddWithValue("@d",deviceId);advance.Parameters.AddWithValue("@q",sequence);if(await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false)!=1)throw new InvalidOperationException("The device sequence could not advance.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);return new PersonalCloudK1OutboxEntry{LocalSequence=localSequence,MutationId=mutationId,Product="tasks",InstanceId=instanceId,DeviceId=deviceId,DeviceSequence=sequence,Operation=operation,EntityId=taskId,CanonicalJson=json,CanonicalHash=hash,CreatedAt=now};
    }

    public async Task<IReadOnlyList<PersonalCloudK1OutboxEntry>> MoveTaskTreeToSpaceWithPersonalCloudIntentAsync(
        string taskId,
        string targetSpaceId,
        string targetSpaceName,
        IReadOnlyDictionary<string, long> baseEntityVersions,
        string instanceId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(targetSpaceId) ||
            string.IsNullOrWhiteSpace(targetSpaceName))
            throw new PersonalCloudK0ContractException("invalid_request", "The task and target Space are required.");

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var allTasks = await ReadTasksAsync(connection, cancellationToken).ConfigureAwait(false);
        var root = allTasks.FirstOrDefault(task => task.Id == taskId)
            ?? throw new PersonalCloudK0ContractException("entity_not_found", "The task does not exist.");
        if (root.IntegrationId != IntegrationIds.Local)
            throw new PersonalCloudK0ContractException("invalid_request", "Only a local Openza task can be moved here.");
        if (string.Equals(root.SpaceId, targetSpaceId, StringComparison.Ordinal))
            throw new PersonalCloudK0ContractException("invalid_request", "The task is already in this Space.");

        var spaceCommand = connection.CreateCommand();
        spaceCommand.CommandText = "SELECT name FROM spaces WHERE id=@id AND is_archived=0";
        spaceCommand.Parameters.AddWithValue("@id", targetSpaceId);
        var storedSpaceName = await spaceCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (storedSpaceName is null)
            throw new PersonalCloudK0ContractException("parent_not_found", "The target Space is not available.");
        if (!string.Equals(storedSpaceName, targetSpaceName, StringComparison.Ordinal))
            throw new PersonalCloudK0ContractException("invalid_request", "The target Space changed. Refresh and try again.");

        var children = allTasks
            .Where(task => !string.IsNullOrWhiteSpace(task.ParentId))
            .GroupBy(task => task.ParentId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var movedTasks = new List<TaskItem>();
        var workspaceMove = await WorkspaceEnabledAsync(connection, instanceId, cancellationToken).ConfigureAwait(false);
        var pending = new Queue<TaskItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            var task = pending.Dequeue();
            if (!seen.Add(task.Id))
                throw new PersonalCloudK0ContractException("invalid_request", "The task hierarchy contains a cycle.");
            if (task.IntegrationId != IntegrationIds.Local)
                throw new PersonalCloudK0ContractException("invalid_request", "Only Openza tasks can move with this task.");
            if ((!workspaceMove || task.Id == root.Id) && (!baseEntityVersions.TryGetValue(task.Id, out var version) || version <= 0))
                throw new PersonalCloudK0ContractException("invalid_request", "A cloud version is required for every moved task.");
            movedTasks.Add(task);
            if (children.TryGetValue(task.Id, out var childTasks))
            {
                foreach (var child in childTasks.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id, StringComparer.Ordinal))
                    pending.Enqueue(child);
            }
        }

        if (workspaceMove)
        {
            var indirectVersions = new List<(string Id, long Version)>();
            foreach (var task in movedTasks.Where(t => t.Id != root.Id))
                if (await ReadPersonalCloudEntityVersionAsync(connection, instanceId, task.Id, cancellationToken).ConfigureAwait(false) is { } taskVersion)
                    indirectVersions.Add((task.Id, taskVersion));
            var entry = await AppendWorkspaceMutationAsync(connection, instanceId, deviceId, "task.moveTree", root.Id,
                baseEntityVersions[root.Id], new { spaceId = targetSpaceId, fields = new { } }, cancellationToken).ConfigureAwait(false);
            foreach (var item in indirectVersions)
                await SaveIndirectWorkspaceVersionAsync(connection, instanceId, "tasks", item.Id, entry.MutationId, item.Version, cancellationToken).ConfigureAwait(false);
            foreach (var task in movedTasks)
                await UpsertTaskCoreAsync(connection, task with { SpaceId = targetSpaceId, ProjectId = null,
                    ParentId = task.Id == root.Id ? null : task.ParentId, UpdatedAt = DateTimeOffset.UtcNow, Revision = task.Revision + 1 }, cancellationToken).ConfigureAwait(false);
            await WorkspaceExecuteAsync(connection, """
                WITH RECURSIVE moved(id) AS(SELECT @id UNION ALL SELECT t.id FROM tasks t JOIN moved m ON t.parent_id=m.id)
                UPDATE provider_source_items SET suggested_space_id=@s WHERE adopted_task_id IN(SELECT id FROM moved)
                """, cancellationToken, ("@id", root.Id), ("@s", targetSpaceId)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return [entry];
        }

        var initializeDevice = connection.CreateCommand();
        initializeDevice.CommandText = "INSERT OR IGNORE INTO personal_cloud_device_state(instance_id,device_id,next_device_sequence) VALUES(@i,@d,1)";
        initializeDevice.Parameters.AddWithValue("@i", instanceId);
        initializeDevice.Parameters.AddWithValue("@d", deviceId);
        await initializeDevice.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var sequenceCommand = connection.CreateCommand();
        sequenceCommand.CommandText = "SELECT next_device_sequence FROM personal_cloud_device_state WHERE instance_id=@i AND device_id=@d";
        sequenceCommand.Parameters.AddWithValue("@i", instanceId);
        sequenceCommand.Parameters.AddWithValue("@d", deviceId);
        var initialSequence = Convert.ToInt64(await sequenceCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        var sequence = initialSequence;
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var entries = new List<PersonalCloudK1OutboxEntry>();

        async Task AppendAsync(string operation, TaskItem task, long baseEntityVersion, object payload)
        {
            var mutationId = $"mutation_{Guid.NewGuid():N}";
            var envelope = new
            {
                protocolVersion = 1,
                product = PersonalCloudK0Protocol.TasksProduct,
                instanceId,
                deviceId,
                mutationId,
                deviceSequence = sequence,
                operation,
                entityId = task.Id,
                baseEntityVersion,
                payload,
                clientCreatedAt = now.ToUniversalTime().ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            };
            var json = PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize(envelope));
            var hash = PersonalCloudK0Codec.ComputeCanonicalHash(json);
            var outbox = connection.CreateCommand();
            outbox.CommandText = """
                INSERT INTO personal_cloud_outbox
                    (mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at)
                VALUES(@m,'tasks',@i,@d,@q,@o,@e,@j,@h,@a)
                RETURNING local_sequence
                """;
            outbox.Parameters.AddWithValue("@m", mutationId);
            outbox.Parameters.AddWithValue("@i", instanceId);
            outbox.Parameters.AddWithValue("@d", deviceId);
            outbox.Parameters.AddWithValue("@q", sequence);
            outbox.Parameters.AddWithValue("@o", operation);
            outbox.Parameters.AddWithValue("@e", task.Id);
            outbox.Parameters.AddWithValue("@j", json);
            outbox.Parameters.AddWithValue("@h", hash);
            outbox.Parameters.AddWithValue("@a", now.ToUnixTimeMilliseconds());
            var localSequence = Convert.ToInt64(await outbox.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            entries.Add(new PersonalCloudK1OutboxEntry
            {
                LocalSequence = localSequence,
                MutationId = mutationId,
                Product = PersonalCloudK0Protocol.TasksProduct,
                InstanceId = instanceId,
                DeviceId = deviceId,
                DeviceSequence = sequence,
                Operation = operation,
                EntityId = task.Id,
                CanonicalJson = json,
                CanonicalHash = hash,
                CreatedAt = now,
            });
            sequence = checked(sequence + 1);
        }

        foreach (var task in movedTasks)
        {
            var version = baseEntityVersions[task.Id];
            await AppendAsync("task.move", task, version, new { space = new { id = targetSpaceId, name = targetSpaceName } }).ConfigureAwait(false);
            if (!string.Equals(task.Id, root.Id, StringComparison.Ordinal) && task.ParentId is { } parentId)
                await AppendAsync("task.setParent", task, checked(version + 1), new { parentId }).ConfigureAwait(false);
        }

        var updateTasks = connection.CreateCommand();
        updateTasks.CommandText = """
            WITH RECURSIVE moved_tasks(id) AS (
                SELECT id FROM tasks WHERE id=@task_id
                UNION ALL
                SELECT child.id FROM tasks child
                INNER JOIN moved_tasks parent ON child.parent_id=parent.id
            )
            UPDATE tasks
            SET space_id=@space_id,
                project_id=NULL,
                parent_id=CASE WHEN id=@task_id THEN NULL ELSE parent_id END,
                updated_at=@updated_at,
                revision=revision+1
            WHERE id IN (SELECT id FROM moved_tasks)
            """;
        updateTasks.Parameters.AddWithValue("@task_id", taskId);
        updateTasks.Parameters.AddWithValue("@space_id", targetSpaceId);
        updateTasks.Parameters.AddWithValue("@updated_at", ToDbDate(now));
        if (await updateTasks.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != movedTasks.Count)
            throw new InvalidOperationException("The complete task hierarchy could not be moved.");

        var updateSources = connection.CreateCommand();
        updateSources.CommandText = """
            WITH RECURSIVE moved_tasks(id) AS (
                SELECT id FROM tasks WHERE id=@task_id
                UNION ALL
                SELECT child.id FROM tasks child
                INNER JOIN moved_tasks parent ON child.parent_id=parent.id
            )
            UPDATE provider_source_items
            SET suggested_space_id=@space_id,updated_at=@updated_at
            WHERE adopted_task_id IN (SELECT id FROM moved_tasks)
            """;
        updateSources.Parameters.AddWithValue("@task_id", taskId);
        updateSources.Parameters.AddWithValue("@space_id", targetSpaceId);
        updateSources.Parameters.AddWithValue("@updated_at", ToDbDate(now));
        await updateSources.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var advance = connection.CreateCommand();
        advance.CommandText = "UPDATE personal_cloud_device_state SET next_device_sequence=@next WHERE instance_id=@i AND device_id=@d AND next_device_sequence=@initial";
        advance.Parameters.AddWithValue("@next", sequence);
        advance.Parameters.AddWithValue("@i", instanceId);
        advance.Parameters.AddWithValue("@d", deviceId);
        advance.Parameters.AddWithValue("@initial", initialSequence);
        if (await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The Personal Cloud device sequence could not be advanced atomically.");

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return entries;
    }

    public async Task AcknowledgePersonalCloudMutationAsync(
        string instanceId,
        string deviceId,
        string receiptJson,
        CancellationToken cancellationToken = default)
    {
        var receipt = PersonalCloudK0Codec.ParseMutationReceipt(receiptJson);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var readCommand = connection.CreateCommand();
        readCommand.CommandText = """
            SELECT entity_id, operation, canonical_json
            FROM personal_cloud_outbox
            WHERE mutation_id = @mutation_id
              AND product = @product
              AND instance_id = @instance_id
              AND device_id = @device_id
            """;
        readCommand.Parameters.AddWithValue("@mutation_id", receipt.MutationId);
        readCommand.Parameters.AddWithValue("@product", PersonalCloudK0Protocol.TasksProduct);
        readCommand.Parameters.AddWithValue("@instance_id", instanceId);
        readCommand.Parameters.AddWithValue("@device_id", deviceId);
        string? entityId = null;
        string? operation = null;
        string? canonicalJson = null;
        await using (var reader = await readCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entityId = reader.GetString(0);
                operation = reader.GetString(1);
                canonicalJson = reader.GetString(2);
            }
        }
        if (entityId is null || !string.Equals(entityId, receipt.EntityId, StringComparison.Ordinal))
        {
            throw new PersonalCloudK0ContractException(
                "invalid_request",
                "The mutation receipt does not match a local Personal Cloud outbox entry.");
        }
        var expectedEntityVersion = PersonalCloudMutationEntityVersion(operation!, canonicalJson!, instanceId);
        var auxiliary = operation is "source.refreshSnapshot" or "externalCommand.confirm" or "externalCommand.reject";
        if (!auxiliary && receipt.EntityVersion != expectedEntityVersion)
        {
            throw new PersonalCloudK0ContractException(
                "invalid_request",
                "The mutation receipt entity version does not match the local outbox entry.");
        }

        var acknowledgeCommand = connection.CreateCommand();
        acknowledgeCommand.CommandText = """
            UPDATE personal_cloud_outbox
            SET acknowledged_at = COALESCE(acknowledged_at, @acknowledged_at)
            WHERE mutation_id = @mutation_id
              AND product = @product
              AND instance_id = @instance_id
              AND device_id = @device_id
            """;
        acknowledgeCommand.Parameters.AddWithValue("@acknowledged_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        acknowledgeCommand.Parameters.AddWithValue("@mutation_id", receipt.MutationId);
        acknowledgeCommand.Parameters.AddWithValue("@product", PersonalCloudK0Protocol.TasksProduct);
        acknowledgeCommand.Parameters.AddWithValue("@instance_id", instanceId);
        acknowledgeCommand.Parameters.AddWithValue("@device_id", deviceId);
        if (await acknowledgeCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The Personal Cloud outbox acknowledgement was not stored.");
        }

        if (!auxiliary)
            await UpsertPersonalCloudEntityVersionAsync(
                connection, instanceId, entityId, receipt.EntityVersion, cancellationToken).ConfigureAwait(false);
        if (operation is "externalCommand.confirm" or "externalCommand.reject")
        {
            var settle = connection.CreateCommand();
            settle.CommandText = "UPDATE personal_cloud_provider_attempts SET state='settled' WHERE result_mutation_id=@m AND state='outcome'";
            settle.Parameters.AddWithValue("@m", receipt.MutationId);
            await settle.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long?> GetPersonalCloudEntityVersionAsync(
        string instanceId,
        string entityId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadPersonalCloudEntityVersionAsync(connection, instanceId, entityId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long?> ReadPersonalCloudEntityVersionAsync(SqliteConnection connection, string instanceId, string entityId, CancellationToken cancellationToken)
    {
        var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = """
            SELECT entity_version
            FROM personal_cloud_entity_versions
            WHERE instance_id = @instance_id AND product = @product AND entity_id = @entity_id
            """;
        versionCommand.Parameters.AddWithValue("@instance_id", instanceId);
        versionCommand.Parameters.AddWithValue("@product", PersonalCloudK0Protocol.TasksProduct);
        versionCommand.Parameters.AddWithValue("@entity_id", entityId);
        var stored = await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        long? version = stored is null or DBNull ? null : Convert.ToInt64(stored);

        var outboxCommand = connection.CreateCommand();
        outboxCommand.CommandText = """
            SELECT operation, canonical_json
            FROM personal_cloud_outbox
            WHERE instance_id = @instance_id AND product = @product AND entity_id = @entity_id
            ORDER BY device_sequence
            """;
        outboxCommand.Parameters.AddWithValue("@instance_id", instanceId);
        outboxCommand.Parameters.AddWithValue("@product", PersonalCloudK0Protocol.TasksProduct);
        outboxCommand.Parameters.AddWithValue("@entity_id", entityId);
        await using var reader = await outboxCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetString(0) is "source.refreshSnapshot" or "externalCommand.confirm" or "externalCommand.reject") continue;
            var outboxVersion = PersonalCloudMutationEntityVersion(reader.GetString(0), reader.GetString(1), instanceId);
            version = version is null ? outboxVersion : Math.Max(version.Value, outboxVersion);
        }
        return await WithIndirectWorkspaceVersionAsync(connection, instanceId, "tasks", entityId, version, cancellationToken).ConfigureAwait(false);
    }

    private static long PersonalCloudMutationEntityVersion(string operation, string canonicalJson, string instanceId) =>
        operation switch
        {
            PersonalCloudK0Protocol.CreateTaskOperation or "task.createRich" => 1L,
            PersonalCloudK0Protocol.UpdateOpenzaFieldsOperation =>
                checked(PersonalCloudOpenzaFieldsUpdateCodec.ParseMutation(canonicalJson, instanceId).BaseEntityVersion + 1),
            PersonalCloudK0Protocol.CompleteTaskOperation =>
                checked(PersonalCloudCompletionCodec.ParseMutation(canonicalJson, instanceId).BaseEntityVersion + 1),
            PersonalCloudK0Protocol.ReopenTaskOperation =>
                checked(PersonalCloudReopenCodec.ParseMutation(canonicalJson, instanceId).BaseEntityVersion + 1),
            PersonalCloudK0Protocol.DeleteTaskOperation =>
                checked(PersonalCloudDeleteCodec.ParseMutation(canonicalJson, instanceId).BaseEntityVersion + 1),
            "task.setProject" or "task.move" or "task.addLabel" or "task.removeLabel" or "task.setParent" =>
                checked(JsonDocument.Parse(canonicalJson).RootElement.GetProperty("baseEntityVersion").GetInt64() + 1),
            "source.refreshSnapshot" or "externalCommand.confirm" or "externalCommand.reject" => 0L,
            "space.put" or "project.put" or "project.delete" or "task.patch" or "task.moveTree" or
            "provider.scope.put" or "provider.catalog.put" or "provider.intent.request" or "provider.intent.start" or "provider.intent.resolve" =>
                WorkspaceMutationVersion(canonicalJson),
            _ => throw new PersonalCloudK0ContractException("invalid_request", "The local outbox operation is unsupported."),
        };

    private static async Task UpsertPersonalCloudEntityVersionAsync(
        SqliteConnection connection,
        string instanceId,
        string entityId,
        long entityVersion,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO personal_cloud_entity_versions
                (instance_id, product, entity_id, entity_version, updated_at)
            VALUES
                (@instance_id, @product, @entity_id, @entity_version, @updated_at)
            ON CONFLICT(instance_id, product, entity_id) DO UPDATE SET
                entity_version = excluded.entity_version,
                updated_at = excluded.updated_at
            WHERE personal_cloud_entity_versions.entity_version <= excluded.entity_version
            """;
        command.Parameters.AddWithValue("@instance_id", instanceId);
        command.Parameters.AddWithValue("@product", PersonalCloudK0Protocol.TasksProduct);
        command.Parameters.AddWithValue("@entity_id", entityId);
        command.Parameters.AddWithValue("@entity_version", entityVersion);
        command.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> GetPersonalCloudCheckpointAsync(
        string instanceId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadPersonalCloudCheckpointAsync(connection, instanceId, deviceId, cancellationToken).ConfigureAwait(false);
    }

    private static DateTimeOffset ParseProtocolTimestamp(string value) => DateTimeOffset.ParseExact(
        value,
        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
        System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    public async Task<long> ApplyPersonalCloudChangePageAsync(
        string instanceId,
        string deviceId,
        string changePageJson,
        bool failAfterTaskWrite = false,
        CancellationToken cancellationToken = default)
    {
        var page = PersonalCloudK3Codec.ParseChangePage(changePageJson);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var currentCheckpoint = await ReadPersonalCloudCheckpointAsync(connection, instanceId, deviceId, cancellationToken).ConfigureAwait(false);

        if (page.Changes.Count == 0)
        {
            if (page.NextCheckpoint != currentCheckpoint)
            {
                throw new PersonalCloudK0ContractException("sequence_gap", "The empty change page does not match the saved checkpoint.");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return currentCheckpoint;
        }

        var firstChange = page.Changes[0];
        if (firstChange.ChangeSeq <= currentCheckpoint)
        {
            foreach (var repeated in page.Changes)
            {
                var appliedCommand = connection.CreateCommand();
                appliedCommand.CommandText = """
                    SELECT mutation_id, entity_id FROM personal_cloud_applied_changes
                    WHERE instance_id=@instance_id AND product=@product AND change_seq=@change_seq
                    """;
                appliedCommand.Parameters.AddWithValue("@instance_id", instanceId);
                appliedCommand.Parameters.AddWithValue("@product", PersonalCloudK0Protocol.TasksProduct);
                appliedCommand.Parameters.AddWithValue("@change_seq", repeated.ChangeSeq);
                await using var reader = await appliedCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                    reader.GetString(0) != repeated.MutationId || reader.GetString(1) != repeated.EntityId)
                    throw new PersonalCloudK0ContractException("sequence_gap", "The repeated page does not match applied replica state.");
            }
            if (page.NextCheckpoint != page.Changes[^1].ChangeSeq || page.NextCheckpoint > currentCheckpoint)
                throw new PersonalCloudK0ContractException("sequence_gap", "The repeated page checkpoint is invalid.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return currentCheckpoint;
        }

        for (var index = 0; index < page.Changes.Count; index++)
        {
            if (page.Changes[index].ChangeSeq != checked(currentCheckpoint + index + 1))
                throw new PersonalCloudK0ContractException("sequence_gap", "The change page is not contiguous with the replica checkpoint.");
        }
        if (page.NextCheckpoint != page.Changes[^1].ChangeSeq)
            throw new PersonalCloudK0ContractException("sequence_gap", "The change page checkpoint does not match its final change.");

        foreach (var change in page.Changes)
        {
        var ownEchoCommand = connection.CreateCommand();
        ownEchoCommand.CommandText = """
            SELECT entity_id
            FROM personal_cloud_outbox
            WHERE mutation_id = @mutation_id
              AND product = @product
              AND instance_id = @instance_id
              AND acknowledged_at IS NOT NULL
            """;
        ownEchoCommand.Parameters.AddWithValue("@mutation_id", change.MutationId);
        ownEchoCommand.Parameters.AddWithValue("@product", PersonalCloudK0Protocol.TasksProduct);
        ownEchoCommand.Parameters.AddWithValue("@instance_id", instanceId);
        var ownEchoEntityId = (string?)await ownEchoCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var isOwnEcho = ownEchoEntityId is not null;
        if (isOwnEcho && !string.Equals(ownEchoEntityId, change.EntityId, StringComparison.Ordinal))
        {
            throw new PersonalCloudK0ContractException(
                "invalid_request",
                "The echoed Personal Cloud change does not match its local outbox entry.");
        }

        var pendingEntity = connection.CreateCommand();
        pendingEntity.CommandText = """
            SELECT COUNT(*) FROM personal_cloud_outbox
            WHERE instance_id=@i AND product='tasks' AND entity_id=@e AND acknowledged_at IS NULL
              AND operation NOT IN ('source.refreshSnapshot','externalCommand.confirm','externalCommand.reject')
            """;
        pendingEntity.Parameters.AddWithValue("@i", instanceId);
        pendingEntity.Parameters.AddWithValue("@e", change.EntityId);
        var hasPendingUserIntent = Convert.ToInt64(await pendingEntity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0;
        // Source descriptors and command receipts cannot overwrite optimistic Openza fields.
        // They remain useful while a user edit is pending, including our own source echoes.
        var isAuxiliaryChange = change is PersonalCloudSourceRefreshedChange or PersonalCloudExternalCommandChangedChange or PersonalCloudWorkspaceChange;
        var skipAuxiliaryTaskWrite = false;
        if (isAuxiliaryChange && change is not PersonalCloudWorkspaceChange)
        {
            var deleted = connection.CreateCommand();
            deleted.CommandText = """
                SELECT EXISTS(SELECT 1 FROM personal_cloud_tombstones WHERE instance_id=@i AND entity_id=@e)
                  OR EXISTS(SELECT 1 FROM personal_cloud_outbox WHERE instance_id=@i AND entity_id=@e
                    AND operation='task.delete' AND acknowledged_at IS NULL)
                """;
            deleted.Parameters.AddWithValue("@i", instanceId); deleted.Parameters.AddWithValue("@e", change.EntityId);
            skipAuxiliaryTaskWrite = Convert.ToInt64(await deleted.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
        }
        var deferChange = !isOwnEcho && !isAuxiliaryChange && hasPendingUserIntent;
        if (deferChange)
        {
            var defer = connection.CreateCommand();
            defer.CommandText = """
                INSERT INTO personal_cloud_deferred_changes(instance_id,product,change_seq,entity_id,canonical_json)
                VALUES(@i,'tasks',@q,@e,@json)
                """;
            defer.Parameters.AddWithValue("@i", instanceId);
            defer.Parameters.AddWithValue("@q", change.ChangeSeq);
            defer.Parameters.AddWithValue("@e", change.EntityId);
            defer.Parameters.AddWithValue("@json", PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize((object)change)));
            await defer.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if ((!isOwnEcho || isAuxiliaryChange) && !deferChange && !skipAuxiliaryTaskWrite)
        {
        switch (change)
        {
            case PersonalCloudWorkspaceChange workspace:
                await ApplyWorkspacePayloadAsync(connection, instanceId, workspace.Payload, cancellationToken).ConfigureAwait(false);
                break;
            case K0TaskCreatedChange created:
            {
                var existingTaskCommand = connection.CreateCommand();
                existingTaskCommand.CommandText = "SELECT COUNT(*) FROM tasks WHERE id = @id";
                existingTaskCommand.Parameters.AddWithValue("@id", created.EntityId);
                if (Convert.ToInt64(await existingTaskCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0)
                {
                    throw new PersonalCloudK0ContractException("entity_exists", "The replica already contains this task from another change.");
                }

                var taskPayload = created.Payload.Task;
                var sourcePayload = created.Payload.Source;
                var task = new TaskItem
                {
                    Id = taskPayload.Id,
                    SpaceId = taskPayload.SpaceId,
                    Title = taskPayload.Title,
                    CompletionState = TaskStatusExtensions.CompletionFromStorageValue(taskPayload.CompletionState),
                    WorkflowStatus = TaskStatusExtensions.WorkflowFromStorageValue(taskPayload.WorkflowStatus),
                    CreatedAt = DateTimeOffset.ParseExact(
                        taskPayload.CreatedAt,
                        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal),
                    SourceIntegrationId = sourcePayload?.IntegrationKind,
                    SourceUrl = sourcePayload?.SourceUrl,
                    SourceDescription = sourcePayload?.Description,
                    SourceMetadataJson = sourcePayload is null ? null : PersonalCloudK0Codec.Serialize(sourcePayload),
                };
                await UpsertTaskCoreAsync(connection, task, cancellationToken).ConfigureAwait(false);
                await SetTaskLabelsCoreAsync(connection, task.Id, task.Labels, cancellationToken).ConfigureAwait(false);
                break;
            }
            case PersonalCloudTaskTitleUpdatedChange updated:
            {
                var updateTaskCommand = connection.CreateCommand();
                updateTaskCommand.CommandText = """
                    UPDATE tasks
                    SET title = @title, updated_at = @updated_at, revision = revision + 1
                    WHERE id = @id
                    """;
                updateTaskCommand.Parameters.AddWithValue("@title", updated.Payload.Task.Title);
                updateTaskCommand.Parameters.AddWithValue(
                    "@updated_at",
                    DateTimeOffset.ParseExact(
                        updated.ServerAcceptedAt,
                        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal).ToUnixTimeMilliseconds());
                updateTaskCommand.Parameters.AddWithValue("@id", updated.EntityId);
                if (await updateTaskCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new PersonalCloudK0ContractException("entity_not_found", "The replica does not contain the task to update.");
                }
                break;
            }
            case PersonalCloudTaskWorkflowStatusUpdatedChange updated:
            {
                var updateTaskCommand = connection.CreateCommand();
                updateTaskCommand.CommandText = """
                    UPDATE tasks
                    SET workflow_status = @workflow_status, updated_at = @updated_at, revision = revision + 1
                    WHERE id = @id AND completion_state = 'open'
                    """;
                updateTaskCommand.Parameters.AddWithValue("@workflow_status", updated.Payload.Task.WorkflowStatus);
                updateTaskCommand.Parameters.AddWithValue(
                    "@updated_at",
                    DateTimeOffset.ParseExact(
                        updated.ServerAcceptedAt,
                        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal).ToUnixTimeMilliseconds());
                updateTaskCommand.Parameters.AddWithValue("@id", updated.EntityId);
                if (await updateTaskCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new PersonalCloudK0ContractException(
                        "entity_not_found",
                        "The replica does not contain the open task to update.");
                }
                break;
            }
            case PersonalCloudTaskPriorityUpdatedChange updated:
            {
                var updateTaskCommand = connection.CreateCommand();
                updateTaskCommand.CommandText = """
                    UPDATE tasks
                    SET priority = @priority, updated_at = @updated_at, revision = revision + 1
                    WHERE id = @id
                    """;
                updateTaskCommand.Parameters.AddWithValue("@priority", updated.Payload.Task.Priority);
                updateTaskCommand.Parameters.AddWithValue(
                    "@updated_at",
                    DateTimeOffset.ParseExact(
                        updated.ServerAcceptedAt,
                        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal).ToUnixTimeMilliseconds());
                updateTaskCommand.Parameters.AddWithValue("@id", updated.EntityId);
                if (await updateTaskCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new PersonalCloudK0ContractException("entity_not_found", "The replica does not contain the task to update.");
                }
                break;
            }
            case PersonalCloudTaskPlannedOnUpdatedChange updated:
            {
                var originalTask = (await ReadTasksAsync(connection, cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(task => task.Id == updated.EntityId);
                if (originalTask is null)
                {
                    throw new PersonalCloudK0ContractException("entity_not_found", "The replica does not contain the open task to plan.");
                }
                var updateTaskCommand = connection.CreateCommand();
                updateTaskCommand.CommandText = """
                    UPDATE tasks SET planned_on=@planned_on, planned_at=NULL,
                        updated_at=@updated_at, revision=revision+1
                    WHERE id=@id AND completion_state='open'
                    """;
                updateTaskCommand.Parameters.AddWithValue("@planned_on", (object?)updated.Payload.Task.PlannedOn ?? DBNull.Value);
                updateTaskCommand.Parameters.AddWithValue("@updated_at", ParseProtocolTimestamp(updated.ServerAcceptedAt).ToUnixTimeMilliseconds());
                updateTaskCommand.Parameters.AddWithValue("@id", updated.EntityId);
                if (await updateTaskCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new PersonalCloudK0ContractException("entity_not_found", "The replica does not contain the open task to plan.");
                var plannedTask = (await ReadTasksAsync(connection, cancellationToken).ConfigureAwait(false))
                    .First(task => task.Id == updated.EntityId);
                var pendingDateUpdate = ProviderWriteBackPlanner.CreateTodoistDateUpdate(
                    originalTask, plannedTask, ParseProtocolTimestamp(updated.ServerAcceptedAt));
                if (pendingDateUpdate is not null)
                {
                    await QueueTaskDateUpdateCoreAsync(connection, pendingDateUpdate, cancellationToken).ConfigureAwait(false);
                }
                break;
            }
            case PersonalCloudTaskPlannedAtUpdatedChange updated:
            {
                var originalTask = (await ReadTasksAsync(connection, cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(task => task.Id == updated.EntityId);
                if (originalTask is null)
                {
                    throw new PersonalCloudK0ContractException("entity_not_found", "The replica does not contain the open task to plan.");
                }
                var updateTaskCommand = connection.CreateCommand();
                updateTaskCommand.CommandText = """
                    UPDATE tasks SET planned_on=NULL, planned_at=@planned_at,
                        updated_at=@updated_at, revision=revision+1
                    WHERE id=@id AND completion_state='open'
                    """;
                updateTaskCommand.Parameters.AddWithValue("@planned_at", updated.Payload.Task.PlannedAt is { } plannedAt
                    ? ParseProtocolTimestamp(plannedAt).ToUnixTimeMilliseconds()
                    : DBNull.Value);
                updateTaskCommand.Parameters.AddWithValue("@updated_at", ParseProtocolTimestamp(updated.ServerAcceptedAt).ToUnixTimeMilliseconds());
                updateTaskCommand.Parameters.AddWithValue("@id", updated.EntityId);
                if (await updateTaskCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new PersonalCloudK0ContractException("entity_not_found", "The replica does not contain the open task to plan.");
                var plannedTask = (await ReadTasksAsync(connection, cancellationToken).ConfigureAwait(false))
                    .First(task => task.Id == updated.EntityId);
                var pendingDateUpdate = ProviderWriteBackPlanner.CreateTodoistDateUpdate(
                    originalTask, plannedTask, ParseProtocolTimestamp(updated.ServerAcceptedAt));
                if (pendingDateUpdate is not null)
                {
                    await QueueTaskDateUpdateCoreAsync(connection, pendingDateUpdate, cancellationToken).ConfigureAwait(false);
                }
                break;
            }
            case PersonalCloudTaskNotesUpdatedChange updated:
            {
                var command = connection.CreateCommand();
                command.CommandText = "UPDATE tasks SET notes=@value,updated_at=@now,revision=revision+1 WHERE id=@id";
                command.Parameters.AddWithValue("@value", (object?)updated.Payload.Task.Notes ?? DBNull.Value);
                command.Parameters.AddWithValue("@now", ParseProtocolTimestamp(updated.ServerAcceptedAt).ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("@id", updated.EntityId);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new PersonalCloudK0ContractException("entity_not_found", "The replica does not contain the task to update.");
                break;
            }
            case PersonalCloudTaskDeadlineOnUpdatedChange updated:
            {
                var command = connection.CreateCommand();
                command.CommandText = "UPDATE tasks SET deadline_on=@value,deadline_at=NULL,updated_at=@now,revision=revision+1 WHERE id=@id";
                command.Parameters.AddWithValue("@value", (object?)updated.Payload.Task.DeadlineOn ?? DBNull.Value);
                command.Parameters.AddWithValue("@now", ParseProtocolTimestamp(updated.ServerAcceptedAt).ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("@id", updated.EntityId);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new PersonalCloudK0ContractException("entity_not_found", "The replica does not contain the task to update.");
                break;
            }
            case PersonalCloudSourceRefreshedChange refreshed:
            {
                var source = refreshed.Payload.Task.Source;
                var updateSourceCommand = connection.CreateCommand();
                updateSourceCommand.CommandText = """
                    UPDATE tasks
                    SET source_integration_id = @integration_id,
                        source_url = @source_url,
                        source_description = @source_description,
                        source_metadata = @source_metadata,
                        updated_at = @updated_at
                    WHERE id = @id
                      AND (source_integration_id IS NULL OR source_integration_id = @integration_id)
                    """;
                updateSourceCommand.Parameters.AddWithValue("@integration_id", source.IntegrationKind);
                updateSourceCommand.Parameters.AddWithValue("@source_url", (object?)source.SourceUrl ?? DBNull.Value);
                updateSourceCommand.Parameters.AddWithValue("@source_description", (object?)source.Description ?? DBNull.Value);
                updateSourceCommand.Parameters.AddWithValue("@source_metadata", PersonalCloudK0Codec.Serialize(source));
                updateSourceCommand.Parameters.AddWithValue(
                    "@updated_at", ParseProtocolTimestamp(refreshed.ServerAcceptedAt).ToUnixTimeMilliseconds());
                updateSourceCommand.Parameters.AddWithValue("@id", refreshed.EntityId);
                if (await updateSourceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new PersonalCloudK0ContractException(
                        "entity_not_found", "The replica does not contain the source-linked task to refresh.");
                break;
            }
            case PersonalCloudExternalCommandChangedChange externalCommand:
            {
                var existsCommand = connection.CreateCommand();
                existsCommand.CommandText = "SELECT COUNT(*) FROM tasks WHERE id = @id";
                existsCommand.Parameters.AddWithValue("@id", externalCommand.EntityId);
                if (Convert.ToInt64(await existsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 1)
                    throw new PersonalCloudK0ContractException(
                        "entity_not_found", "The replica does not contain the task for the external command result.");
                break;
            }
            case PersonalCloudTaskCompletedChange completed:
            {
                var completedAt = DateTimeOffset.ParseExact(
                    completed.Payload.Task.CompletedAt,
                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
                var acceptedAt = DateTimeOffset.ParseExact(
                    completed.ServerAcceptedAt,
                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
                var updateTaskCommand = connection.CreateCommand();
                updateTaskCommand.CommandText = """
                    UPDATE tasks
                    SET completion_state = 'completed', completed_at = @completed_at,
                        updated_at = @updated_at, revision = revision + 1
                    WHERE id = @id AND completion_state = 'open'
                    """;
                updateTaskCommand.Parameters.AddWithValue("@completed_at", completedAt.ToUnixTimeMilliseconds());
                updateTaskCommand.Parameters.AddWithValue("@updated_at", acceptedAt.ToUnixTimeMilliseconds());
                updateTaskCommand.Parameters.AddWithValue("@id", completed.EntityId);
                if (await updateTaskCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    var existsCommand = connection.CreateCommand();
                    existsCommand.CommandText = "SELECT COUNT(*) FROM tasks WHERE id = @id";
                    existsCommand.Parameters.AddWithValue("@id", completed.EntityId);
                    var exists = Convert.ToInt64(await existsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
                    throw new PersonalCloudK0ContractException(
                        exists ? "invalid_transition" : "entity_not_found",
                        exists ? "Only an open replica task can be completed." : "The replica does not contain the task to complete.");
                }
                break;
            }
            case PersonalCloudTaskReopenedChange reopened:
            {
                var acceptedAt = DateTimeOffset.ParseExact(
                    reopened.ServerAcceptedAt,
                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
                var updateTaskCommand = connection.CreateCommand();
                updateTaskCommand.CommandText = """
                    UPDATE tasks
                    SET completion_state = 'open', completed_at = NULL,
                        updated_at = @updated_at, revision = revision + 1
                    WHERE id = @id AND completion_state = 'completed'
                    """;
                updateTaskCommand.Parameters.AddWithValue("@updated_at", acceptedAt.ToUnixTimeMilliseconds());
                updateTaskCommand.Parameters.AddWithValue("@id", reopened.EntityId);
                if (await updateTaskCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    var existsCommand = connection.CreateCommand();
                    existsCommand.CommandText = "SELECT COUNT(*) FROM tasks WHERE id = @id";
                    existsCommand.Parameters.AddWithValue("@id", reopened.EntityId);
                    var exists = Convert.ToInt64(await existsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
                    throw new PersonalCloudK0ContractException(
                        exists ? "invalid_transition" : "entity_not_found",
                        exists ? "Only a completed replica task can be reopened." : "The replica does not contain the task to reopen.");
                }
                break;
            }
            case PersonalCloudTaskDeletedChange deleted:
            {
                var existsCommand=connection.CreateCommand();existsCommand.CommandText="SELECT COUNT(*) FROM tasks WHERE id=@id";existsCommand.Parameters.AddWithValue("@id",deleted.EntityId);
                if(Convert.ToInt64(await existsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!=1)
                {
                    var known = connection.CreateCommand();
                    known.CommandText = "SELECT entity_version FROM personal_cloud_tombstones WHERE instance_id=@i AND product='tasks' AND entity_id=@e";
                    known.Parameters.AddWithValue("@i", instanceId); known.Parameters.AddWithValue("@e", deleted.EntityId);
                    var prior = await known.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    if (prior is null or DBNull || Convert.ToInt64(prior) != deleted.EntityVersion)
                        throw new PersonalCloudK0ContractException("entity_not_found", "The replica does not contain the task or matching tombstone to delete.");
                }
                var tombstone=connection.CreateCommand();tombstone.CommandText="""
                  INSERT INTO personal_cloud_tombstones(instance_id,product,entity_id,entity_version,deleted_at,purge_after)
                  VALUES(@i,'tasks',@e,@v,@a,NULL)
                  ON CONFLICT(instance_id,product,entity_id) DO UPDATE SET
                    entity_version=excluded.entity_version,deleted_at=excluded.deleted_at
                  WHERE personal_cloud_tombstones.entity_version<=excluded.entity_version
                  """;
                tombstone.Parameters.AddWithValue("@i",instanceId);tombstone.Parameters.AddWithValue("@e",deleted.EntityId);tombstone.Parameters.AddWithValue("@v",deleted.EntityVersion);tombstone.Parameters.AddWithValue("@a",ParseProtocolTimestamp(deleted.Payload.Task.DeletedAt).ToUnixTimeMilliseconds());
                await tombstone.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
            case PersonalCloudTaskRelationshipChange relationship:
            {
                await ApplyPersonalCloudRelationshipChangeAsync(connection, instanceId, relationship, cancellationToken).ConfigureAwait(false);
                break;
            }
            default:
                throw new PersonalCloudK0ContractException("invalid_request", "The change operation is not supported.");
        }
        }
        if (failAfterTaskWrite && !isOwnEcho)
        {
            throw new InvalidOperationException("Injected failure after replica task write.");
        }

        if (!deferChange)
            await RecordProviderBridgeChangeAsync(connection, instanceId, change, cancellationToken).ConfigureAwait(false);
        if (!deferChange && !isAuxiliaryChange)
            await UpsertPersonalCloudEntityVersionAsync(
                connection, instanceId, change.EntityId, change.EntityVersion, cancellationToken).ConfigureAwait(false);

        var appliedChangeCommand = connection.CreateCommand();
        appliedChangeCommand.CommandText = """
            INSERT INTO personal_cloud_applied_changes
                (instance_id, product, change_seq, mutation_id, entity_id, applied_at)
            VALUES
                (@instance_id, @product, @change_seq, @mutation_id, @entity_id, @applied_at)
            """;
        appliedChangeCommand.Parameters.AddWithValue("@instance_id", instanceId);
        appliedChangeCommand.Parameters.AddWithValue("@product", PersonalCloudK0Protocol.TasksProduct);
        appliedChangeCommand.Parameters.AddWithValue("@change_seq", change.ChangeSeq);
        appliedChangeCommand.Parameters.AddWithValue("@mutation_id", change.MutationId);
        appliedChangeCommand.Parameters.AddWithValue("@entity_id", change.EntityId);
        appliedChangeCommand.Parameters.AddWithValue("@applied_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await appliedChangeCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (!deferChange)
        {
            var trackCommand=connection.CreateCommand();trackCommand.CommandText="INSERT OR IGNORE INTO personal_cloud_replica_entities(instance_id,product,entity_id) SELECT @i,'tasks',@e WHERE EXISTS(SELECT 1 FROM tasks WHERE id=@e)";trackCommand.Parameters.AddWithValue("@i",instanceId);trackCommand.Parameters.AddWithValue("@e",change.EntityId);await trackCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        }

        var checkpointCommand = connection.CreateCommand();
        checkpointCommand.CommandText = """
            INSERT INTO personal_cloud_checkpoints
                (instance_id, device_id, product, last_change_seq, updated_at)
            VALUES
                (@instance_id, @device_id, @product, @last_change_seq, @updated_at)
            ON CONFLICT(instance_id, device_id, product) DO UPDATE SET
                last_change_seq = excluded.last_change_seq,
                updated_at = excluded.updated_at
            WHERE personal_cloud_checkpoints.last_change_seq < excluded.last_change_seq
            """;
        checkpointCommand.Parameters.AddWithValue("@instance_id", instanceId);
        checkpointCommand.Parameters.AddWithValue("@device_id", deviceId);
        checkpointCommand.Parameters.AddWithValue("@product", PersonalCloudK0Protocol.TasksProduct);
        checkpointCommand.Parameters.AddWithValue("@last_change_seq", page.NextCheckpoint);
        checkpointCommand.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (await checkpointCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The Personal Cloud replica checkpoint could not be advanced atomically.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return page.NextCheckpoint;
    }

    public async Task<long> ReplacePersonalCloudReplicaFromBootstrapAsync(
        string instanceId,string deviceId,string bootstrapJson,CancellationToken cancellationToken=default)
    {
        var bootstrap=PersonalCloudBootstrapCodec.Parse(bootstrapJson);await using var connection=await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);await using var transaction=await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureNoPendingPersonalCloudIntentsAsync(connection, cancellationToken).ConfigureAwait(false);
        var clear=connection.CreateCommand();clear.CommandText="""
          DELETE FROM personal_cloud_applied_changes WHERE instance_id=@i AND product='tasks';
          DELETE FROM personal_cloud_checkpoints WHERE instance_id=@i AND product='tasks';
          DELETE FROM personal_cloud_tombstones WHERE instance_id=@i AND product='tasks';
          DELETE FROM personal_cloud_entity_versions WHERE instance_id=@i AND product='tasks';
          DELETE FROM task_labels WHERE task_id IN(SELECT entity_id FROM personal_cloud_replica_entities WHERE instance_id=@i AND product='tasks');
          DELETE FROM tasks WHERE id IN(SELECT entity_id FROM personal_cloud_replica_entities WHERE instance_id=@i AND product='tasks')
            AND NOT EXISTS(SELECT 1 FROM personal_cloud_outbox o WHERE o.entity_id=tasks.id AND o.acknowledged_at IS NULL);
          DELETE FROM personal_cloud_replica_entities WHERE instance_id=@i AND product='tasks';
          DELETE FROM personal_cloud_provider_commands WHERE instance_id=@i;
          DELETE FROM personal_cloud_provider_sources WHERE instance_id=@i;
          """;clear.Parameters.AddWithValue("@i",instanceId);await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        foreach(var space in bootstrap.Snapshot.Spaces.Where(s=>s.DeletedAt is null)){var c=connection.CreateCommand();c.CommandText="INSERT INTO spaces(id,name,created_at) VALUES(@id,@name,@now) ON CONFLICT(id) DO UPDATE SET name=excluded.name";c.Parameters.AddWithValue("@id",space.Id);c.Parameters.AddWithValue("@name",space.Name);c.Parameters.AddWithValue("@now",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);}
        foreach(var project in bootstrap.Snapshot.Projects.Where(p=>p.DeletedAt is null)){var c=connection.CreateCommand();c.CommandText="INSERT INTO projects(id,space_id,integration_id,name,created_at) VALUES(@id,@space,'openza_tasks',@name,@now) ON CONFLICT(id) DO UPDATE SET space_id=excluded.space_id,name=excluded.name";c.Parameters.AddWithValue("@id",project.Id);c.Parameters.AddWithValue("@space",project.SpaceId);c.Parameters.AddWithValue("@name",project.Name);c.Parameters.AddWithValue("@now",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);}
        foreach(var task in bootstrap.Snapshot.Tasks)
        {
            var source=bootstrap.Snapshot.SourceDescriptors.FirstOrDefault(value=>value.TaskId==task.Id);
            var item=new TaskItem{Id=task.Id,SpaceId=task.SpaceId,ProjectId=task.ProjectId,ParentId=task.ParentId,Title=task.Title,Notes=task.Notes,CompletionState=TaskStatusExtensions.CompletionFromStorageValue(task.CompletionState),WorkflowStatus=TaskStatusExtensions.WorkflowFromStorageValue(task.WorkflowStatus),Priority=task.Priority,PlannedOn=task.PlannedOn is null?null:DateOnly.ParseExact(task.PlannedOn,"yyyy-MM-dd"),PlannedAt=task.PlannedAt is null?null:ParseProtocolTimestamp(task.PlannedAt),DeadlineOn=task.DeadlineOn is null?null:DateOnly.ParseExact(task.DeadlineOn,"yyyy-MM-dd"),CompletedAt=task.CompletedAt is null?null:ParseProtocolTimestamp(task.CompletedAt),CreatedAt=ParseProtocolTimestamp(task.CreatedAt),SourceIntegrationId=source?.IntegrationKind,SourceUrl=source?.SourceUrl,SourceDescription=source?.Description,SourceMetadataJson=source is null?null:PersonalCloudK0Codec.Serialize(source)};
            await UpsertTaskCoreAsync(connection,item,cancellationToken).ConfigureAwait(false);var track=connection.CreateCommand();track.CommandText="INSERT INTO personal_cloud_replica_entities(instance_id,product,entity_id) VALUES(@i,'tasks',@e)";track.Parameters.AddWithValue("@i",instanceId);track.Parameters.AddWithValue("@e",task.Id);await track.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);await UpsertPersonalCloudEntityVersionAsync(connection,instanceId,task.Id,task.EntityVersion,cancellationToken).ConfigureAwait(false);
        }
        foreach(var label in bootstrap.Snapshot.Labels.Where(l=>l.DeletedAt is null)){var c=connection.CreateCommand();c.CommandText="INSERT INTO labels(id,integration_id,name,created_at) VALUES(@id,'openza_tasks',@name,@now) ON CONFLICT(id) DO UPDATE SET name=excluded.name";c.Parameters.AddWithValue("@id",label.Id);c.Parameters.AddWithValue("@name",label.Name);c.Parameters.AddWithValue("@now",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);}
        foreach(var link in bootstrap.Snapshot.TaskLabels){var c=connection.CreateCommand();c.CommandText="INSERT INTO task_labels(task_id,label_id) VALUES(@task,@label)";c.Parameters.AddWithValue("@task",link.TaskId);c.Parameters.AddWithValue("@label",link.LabelId);await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);}
        foreach(var tombstone in bootstrap.Snapshot.Tombstones){var c=connection.CreateCommand();c.CommandText="INSERT INTO personal_cloud_tombstones(instance_id,product,entity_id,entity_version,deleted_at,purge_after) VALUES(@i,'tasks',@e,@v,@d,@p)";c.Parameters.AddWithValue("@i",instanceId);c.Parameters.AddWithValue("@e",tombstone.EntityId);c.Parameters.AddWithValue("@v",tombstone.EntityVersion);c.Parameters.AddWithValue("@d",ParseProtocolTimestamp(tombstone.DeletedAt).ToUnixTimeMilliseconds());c.Parameters.AddWithValue("@p",tombstone.PurgeAfter is null?DBNull.Value:ParseProtocolTimestamp(tombstone.PurgeAfter).ToUnixTimeMilliseconds());await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);await UpsertPersonalCloudEntityVersionAsync(connection,instanceId,tombstone.EntityId,tombstone.EntityVersion,cancellationToken).ConfigureAwait(false);}
        await RecordProviderBridgeBootstrapAsync(connection, instanceId, bootstrap, cancellationToken).ConfigureAwait(false);
        var checkpoint=connection.CreateCommand();checkpoint.CommandText="INSERT INTO personal_cloud_checkpoints(instance_id,device_id,product,last_change_seq,updated_at) VALUES(@i,@d,'tasks',@c,@now) ON CONFLICT(instance_id,device_id,product) DO UPDATE SET last_change_seq=excluded.last_change_seq,updated_at=excluded.updated_at";checkpoint.Parameters.AddWithValue("@i",instanceId);checkpoint.Parameters.AddWithValue("@d",deviceId);checkpoint.Parameters.AddWithValue("@c",bootstrap.Checkpoint);checkpoint.Parameters.AddWithValue("@now",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);return bootstrap.Checkpoint;
    }

    private static async Task ApplyPersonalCloudRelationshipChangeAsync(
        SqliteConnection connection, string instanceId, PersonalCloudTaskRelationshipChange change, CancellationToken cancellationToken)
    {
        var task = change.Payload.Task;
        var now = ParseProtocolTimestamp(change.ServerAcceptedAt).ToUnixTimeMilliseconds();
        if (change.Operation == "task.projectSet")
        {
            var project = task.GetProperty("project");
            if (project.ValueKind != JsonValueKind.Null &&
                await ReadWorkspaceRecordAsync(connection, instanceId, "projects", project.GetProperty("id").GetString()!, cancellationToken).ConfigureAwait(false) is { } known && WText(known, "deletedAt") is not null)
                throw new PersonalCloudK0ContractException("invalid_transition", "A stale relationship cannot restore a deleted project.");
            var command=connection.CreateCommand();
            if(project.ValueKind!=JsonValueKind.Null){command.CommandText="""
              INSERT INTO projects(id,space_id,integration_id,name,created_at)
              SELECT @project_id,space_id,'openza_tasks',@name,@now FROM tasks WHERE id=@task_id
              ON CONFLICT(id) DO NOTHING
              """;
            command.Parameters.AddWithValue("@project_id",project.GetProperty("id").GetString()!); command.Parameters.AddWithValue("@name",project.GetProperty("name").GetString()!);
            command.Parameters.AddWithValue("@now",now); command.Parameters.AddWithValue("@task_id",change.EntityId); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);}
            command=connection.CreateCommand(); command.CommandText="UPDATE tasks SET project_id=@value,updated_at=@now,revision=revision+1 WHERE id=@id";
            var projectId=task.GetProperty("projectId");command.Parameters.AddWithValue("@value",projectId.ValueKind==JsonValueKind.Null?DBNull.Value:projectId.GetString()!);command.Parameters.AddWithValue("@now",now);command.Parameters.AddWithValue("@id",change.EntityId);
            if(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false)!=1) throw new PersonalCloudK0ContractException("entity_not_found","The replica task does not exist.");
        }
        else if(change.Operation=="task.moved")
        {
            var space=task.GetProperty("space"); var command=connection.CreateCommand(); command.CommandText="INSERT INTO spaces(id,name,created_at) VALUES(@id,@name,@now) ON CONFLICT(id) DO NOTHING";
            command.Parameters.AddWithValue("@id",space.GetProperty("id").GetString()!);command.Parameters.AddWithValue("@name",space.GetProperty("name").GetString()!);command.Parameters.AddWithValue("@now",now);await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command=connection.CreateCommand();command.CommandText="UPDATE tasks SET space_id=@space,project_id=NULL,parent_id=NULL,updated_at=@now,revision=revision+1 WHERE id=@id";
            command.Parameters.AddWithValue("@space",task.GetProperty("spaceId").GetString()!);command.Parameters.AddWithValue("@now",now);command.Parameters.AddWithValue("@id",change.EntityId);await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else if(change.Operation=="task.labelAdded")
        {
            var label=task.GetProperty("label");var command=connection.CreateCommand();command.CommandText="INSERT INTO labels(id,integration_id,name,created_at) VALUES(@id,'openza_tasks',@name,@now) ON CONFLICT(id) DO UPDATE SET name=excluded.name";
            command.Parameters.AddWithValue("@id",label.GetProperty("id").GetString()!);command.Parameters.AddWithValue("@name",label.GetProperty("name").GetString()!);command.Parameters.AddWithValue("@now",now);await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command=connection.CreateCommand();command.CommandText="INSERT INTO task_labels(task_id,label_id) VALUES(@task,@label)";command.Parameters.AddWithValue("@task",change.EntityId);command.Parameters.AddWithValue("@label",label.GetProperty("id").GetString()!);await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else if(change.Operation=="task.labelRemoved")
        {
            var command=connection.CreateCommand();command.CommandText="DELETE FROM task_labels WHERE task_id=@task AND label_id=@label";command.Parameters.AddWithValue("@task",change.EntityId);command.Parameters.AddWithValue("@label",task.GetProperty("labelId").GetString()!);await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var command=connection.CreateCommand();command.CommandText="UPDATE tasks SET parent_id=@parent,updated_at=@now,revision=revision+1 WHERE id=@id";
            var parent=task.GetProperty("parentId");command.Parameters.AddWithValue("@parent",parent.ValueKind==JsonValueKind.Null?DBNull.Value:parent.GetString()!);command.Parameters.AddWithValue("@now",now);command.Parameters.AddWithValue("@id",change.EntityId);await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<long> ReadPersonalCloudCheckpointAsync(
        SqliteConnection connection,
        string instanceId,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            -- Tasks and the applied-change journal belong to this local replica,
            -- not its replaceable authentication device. Device acknowledgements
            -- remain scoped to the current device on the server.
            SELECT MAX(last_change_seq)
            FROM personal_cloud_checkpoints
            WHERE instance_id = @instance_id AND product = @product
            """;
        command.Parameters.AddWithValue("@instance_id", instanceId);
        command.Parameters.AddWithValue("@product", PersonalCloudK0Protocol.TasksProduct);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? 0L : Convert.ToInt64(value);
    }

    public async Task UpsertTaskWithPendingUpdatesAsync(
        TaskItem task,
        PendingCompletion? completion,
        PendingTaskDateUpdate? dateUpdate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await UpsertTaskCoreAsync(connection, task, cancellationToken).ConfigureAwait(false);
        await SetTaskLabelsCoreAsync(connection, task.Id, task.Labels, cancellationToken).ConfigureAwait(false);
        if (completion is not null)
        {
            await QueueCompletionCoreAsync(connection, completion, cancellationToken).ConfigureAwait(false);
        }
        if (dateUpdate is not null)
        {
            await QueueTaskDateUpdateCoreAsync(connection, dateUpdate, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryUpsertTaskWithPendingUpdatesAsync(
        TaskItem task,
        long expectedRevision,
        PendingCompletion? completion,
        PendingTaskDateUpdate? dateUpdate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var revisionCommand = connection.CreateCommand();
        revisionCommand.CommandText = "SELECT revision FROM tasks WHERE id = @id";
        revisionCommand.Parameters.AddWithValue("@id", task.Id);
        var current = await revisionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (current is null || current is DBNull || Convert.ToInt64(current) != expectedRevision)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await UpsertTaskCoreAsync(connection, task, cancellationToken).ConfigureAwait(false);
        await SetTaskLabelsCoreAsync(connection, task.Id, task.Labels, cancellationToken).ConfigureAwait(false);
        if (completion is not null)
        {
            await QueueCompletionCoreAsync(connection, completion, cancellationToken).ConfigureAwait(false);
        }
        if (dateUpdate is not null)
        {
            await QueueTaskDateUpdateCoreAsync(connection, dateUpdate, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task MoveTaskToSpaceAsync(string taskId, string targetSpaceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw new ArgumentException("Task id is required.", nameof(taskId));
        }

        if (string.IsNullOrWhiteSpace(targetSpaceId))
        {
            throw new ArgumentException("Target space id is required.", nameof(targetSpaceId));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var spaceCommand = connection.CreateCommand();
        spaceCommand.CommandText = "SELECT COUNT(*) FROM spaces WHERE id = @id AND is_archived = 0";
        spaceCommand.Parameters.AddWithValue("@id", targetSpaceId);
        var availableSpaceCount = (long)(await spaceCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        if (availableSpaceCount == 0)
        {
            throw new InvalidOperationException("The target space is not available.");
        }

        var taskCommand = connection.CreateCommand();
        taskCommand.CommandText = "SELECT parent_id FROM tasks WHERE id = @id";
        taskCommand.Parameters.AddWithValue("@id", taskId);
        string? parentId;
        await using (var reader = await taskCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The task no longer exists.");
            }

            parentId = GetNullableString(reader, 0);
        }

        var updateCommand = connection.CreateCommand();
        updateCommand.CommandText = """
            WITH RECURSIVE moved_tasks(id) AS (
                SELECT id FROM tasks WHERE id = @task_id
                UNION ALL
                SELECT child.id
                FROM tasks child
                INNER JOIN moved_tasks parent ON child.parent_id = parent.id
            )
            UPDATE tasks
            SET space_id = @space_id,
                project_id = NULL,
                parent_id = CASE WHEN id = @task_id AND @clear_parent = 1 THEN NULL ELSE parent_id END,
                updated_at = @updated_at,
                revision = revision + 1
            WHERE id IN (SELECT id FROM moved_tasks)
            """;
        updateCommand.Parameters.AddWithValue("@task_id", taskId);
        updateCommand.Parameters.AddWithValue("@space_id", targetSpaceId);
        updateCommand.Parameters.AddWithValue("@clear_parent", string.IsNullOrWhiteSpace(parentId) ? 0 : 1);
        updateCommand.Parameters.AddWithValue("@updated_at", ToDbDate(DateTimeOffset.UtcNow));
        await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var sourceCommand = connection.CreateCommand();
        sourceCommand.CommandText = """
            WITH RECURSIVE moved_tasks(id) AS (
                SELECT id FROM tasks WHERE id = @task_id
                UNION ALL
                SELECT child.id
                FROM tasks child
                INNER JOIN moved_tasks parent ON child.parent_id = parent.id
            )
            UPDATE provider_source_items
            SET suggested_space_id = @space_id,
                updated_at = @updated_at
            WHERE adopted_task_id IN (SELECT id FROM moved_tasks)
            """;
        sourceCommand.Parameters.AddWithValue("@task_id", taskId);
        sourceCommand.Parameters.AddWithValue("@space_id", targetSpaceId);
        sourceCommand.Parameters.AddWithValue("@updated_at", ToDbDate(DateTimeOffset.UtcNow));
        await sourceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertProjectAsync(ProjectItem project, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await UpsertProjectCoreAsync(connection, project, cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertProjectCoreAsync(SqliteConnection connection, ProjectItem project, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO projects (id, external_id, space_id, integration_id, provider_connection_id, name, description, color, icon, parent_id,
                                  sort_order, is_favorite, is_archived, status, provider_metadata, created_at, updated_at)
            VALUES (@id, @external_id, @space_id, @integration_id, @provider_connection_id, @name, @description, @color, @icon, @parent_id,
                    @sort_order, @is_favorite, @is_archived, @status, @provider_metadata, @created_at, @updated_at)
            ON CONFLICT(id) DO UPDATE SET
                external_id = excluded.external_id,
                space_id = excluded.space_id,
                integration_id = excluded.integration_id,
                provider_connection_id = excluded.provider_connection_id,
                name = excluded.name,
                description = excluded.description,
                color = excluded.color,
                icon = excluded.icon,
                parent_id = excluded.parent_id,
                sort_order = excluded.sort_order,
                is_favorite = excluded.is_favorite,
                is_archived = excluded.is_archived,
                status = excluded.status,
                provider_metadata = excluded.provider_metadata,
                updated_at = excluded.updated_at
            """;
        AddProjectParameters(command, project);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertLabelAsync(LabelItem label, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ResolveAndUpsertLabelCoreAsync(connection, label, cancellationToken, preserveExistingId: false).ConfigureAwait(false);
    }

    public async Task SetTaskLabelsAsync(string taskId, IReadOnlyList<LabelItem> labels, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetTaskLabelsCoreAsync(connection, taskId, labels, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        _ = await DeleteTaskCoreAsync(taskId, expectedRevision: null, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> TryDeleteTaskAsync(string taskId, long expectedRevision, CancellationToken cancellationToken = default) =>
        DeleteTaskCoreAsync(taskId, expectedRevision, cancellationToken);

    private async Task<bool> DeleteTaskCoreAsync(string taskId, long? expectedRevision, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (expectedRevision is not null)
        {
            var revisionCommand = connection.CreateCommand();
            revisionCommand.CommandText = "SELECT revision FROM tasks WHERE id = @id";
            revisionCommand.Parameters.AddWithValue("@id", taskId);
            var current = await revisionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (current is null || current is DBNull || Convert.ToInt64(current) != expectedRevision)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        var linkedProvider = await ReadAdoptedProviderLinkAsync(connection, taskId, cancellationToken).ConfigureAwait(false);
        if (linkedProvider is not null)
        {
            throw new ProviderLinkedTaskDeleteException(taskId, linkedProvider);
        }

        var linkCommand = connection.CreateCommand();
        linkCommand.CommandText = "UPDATE sync_item_links SET local_task_id = NULL, updated_at = @updated_at WHERE local_task_id = @task_id";
        linkCommand.Parameters.AddWithValue("@task_id", taskId);
        linkCommand.Parameters.AddWithValue("@updated_at", ToDbDate(DateTimeOffset.UtcNow));
        await linkCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var missingSourceCommand = connection.CreateCommand();
        missingSourceCommand.CommandText = """
            UPDATE provider_source_items
            SET adopted_task_id = NULL,
                updated_at = @updated_at
            WHERE adopted_task_id = @task_id
              AND adoption_state = @adoption_state
            """;
        missingSourceCommand.Parameters.AddWithValue("@task_id", taskId);
        missingSourceCommand.Parameters.AddWithValue("@adoption_state", ProviderSourceAdoptionStates.Missing);
        missingSourceCommand.Parameters.AddWithValue("@updated_at", ToDbDate(DateTimeOffset.UtcNow));
        await missingSourceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM tasks WHERE id = @id";
        command.Parameters.AddWithValue("@id", taskId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task DeleteProjectAsync(string projectId, bool moveTasksToInbox, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var taskCommand = connection.CreateCommand();
        taskCommand.CommandText = moveTasksToInbox
            ? "UPDATE tasks SET project_id = NULL, workflow_status = CASE WHEN completion_state = 'open' THEN 'inbox' ELSE workflow_status END, updated_at = @updated_at, revision = revision + 1 WHERE project_id = @project_id"
            : "DELETE FROM tasks WHERE project_id = @project_id";
        taskCommand.Parameters.AddWithValue("@project_id", projectId);
        if (moveTasksToInbox)
        {
            taskCommand.Parameters.AddWithValue("@updated_at", ToDbDate(DateTimeOffset.UtcNow));
        }
        await taskCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var projectCommand = connection.CreateCommand();
        projectCommand.CommandText = "DELETE FROM projects WHERE id = @id";
        projectCommand.Parameters.AddWithValue("@id", projectId);
        await projectCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await SetCompletionStateAsync(taskId, completed: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReopenTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await SetCompletionStateAsync(taskId, completed: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task QueueCompletionAsync(PendingCompletion completion, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await QueueCompletionCoreAsync(connection, completion, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetTaskCompletionWithPendingUpdateAsync(
        string taskId,
        bool completed,
        PendingCompletion? completion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (completion is not null)
        {
            await QueueCompletionCoreAsync(connection, completion, cancellationToken).ConfigureAwait(false);
        }
        await SetCompletionStateCoreAsync(connection, taskId, completed, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task QueueCompletionCoreAsync(SqliteConnection connection, PendingCompletion completion, CancellationToken cancellationToken)
    {
        await EnsurePendingCompletionsTableAsync(connection, cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO pending_completions
                (id, task_id, provider, provider_task_id, completed, completed_at, created_at, retry_count)
            VALUES (@id, @task_id, @provider, @provider_task_id, @completed, @completed_at, @created_at, @retry_count)
            """;
        command.Parameters.AddWithValue("@id", completion.Id);
        command.Parameters.AddWithValue("@task_id", completion.TaskId);
        command.Parameters.AddWithValue("@provider", completion.Provider);
        command.Parameters.AddWithValue("@provider_task_id", completion.ProviderTaskId);
        command.Parameters.AddWithValue("@completed", completion.Completed ? 1 : 0);
        command.Parameters.AddWithValue("@completed_at", ToDbValue(completion.CompletedAt));
        command.Parameters.AddWithValue("@created_at", ToDbDate(completion.CreatedAt));
        command.Parameters.AddWithValue("@retry_count", completion.RetryCount);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PendingCompletion>> GetPendingCompletionsAsync(string provider, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsurePendingCompletionsTableAsync(connection, cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, task_id, provider, provider_task_id, completed, completed_at, created_at, retry_count
            FROM pending_completions
            WHERE provider = @provider
            ORDER BY created_at
            """;
        command.Parameters.AddWithValue("@provider", provider);

        var completions = new List<PendingCompletion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            completions.Add(new PendingCompletion
            {
                Id = reader.GetString(0),
                TaskId = reader.GetString(1),
                Provider = reader.GetString(2),
                ProviderTaskId = reader.GetString(3),
                Completed = reader.GetInt32(4) != 0,
                CompletedAt = ReadDate(reader, 5),
                CreatedAt = ReadDate(reader, 6) ?? DateTimeOffset.UtcNow,
                RetryCount = reader.GetInt32(7),
            });
        }

        return completions;
    }

    public async Task<PendingProviderWriteSummary> GetPendingProviderWriteSummaryAsync(
        string provider,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var completions = 0;
        var reopens = 0;
        var dateUpdates = 0;

        if (await TableExistsAsync(connection, "pending_completions", cancellationToken).ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    COALESCE(SUM(CASE WHEN completed = 1 THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN completed = 0 THEN 1 ELSE 0 END), 0)
                FROM pending_completions
                WHERE provider = @provider
                """;
            command.Parameters.AddWithValue("@provider", provider);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                completions = reader.GetInt32(0);
                reopens = reader.GetInt32(1);
            }
        }

        if (await TableExistsAsync(connection, "pending_task_date_updates", cancellationToken).ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pending_task_date_updates WHERE provider = @provider";
            command.Parameters.AddWithValue("@provider", provider);
            dateUpdates = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        return new PendingProviderWriteSummary(completions, reopens, dateUpdates);
    }

    public async Task MarkCompletionSyncedAsync(string completionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM pending_completions WHERE id = @id";
        command.Parameters.AddWithValue("@id", completionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task QueueTaskDateUpdateAsync(PendingTaskDateUpdate update, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await QueueTaskDateUpdateCoreAsync(connection, update, cancellationToken).ConfigureAwait(false);
    }

    private static async Task QueueTaskDateUpdateCoreAsync(SqliteConnection connection, PendingTaskDateUpdate update, CancellationToken cancellationToken)
    {
        await EnsurePendingTaskDateUpdatesTableAsync(connection, cancellationToken).ConfigureAwait(false);
        var (plannedOn, plannedAt) = NormalizeDatePair(update.PlannedOn, update.PlannedAt);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pending_task_date_updates
                (id, task_id, provider, provider_task_id, planned_on, planned_at, created_at, retry_count)
            VALUES (@id, @task_id, @provider, @provider_task_id, @planned_on, @planned_at, @created_at, @retry_count)
            ON CONFLICT(provider, provider_task_id) DO UPDATE SET
                id = excluded.id,
                task_id = excluded.task_id,
                planned_on = excluded.planned_on,
                planned_at = excluded.planned_at,
                created_at = excluded.created_at,
                retry_count = excluded.retry_count
            """;
        command.Parameters.AddWithValue("@id", update.Id);
        command.Parameters.AddWithValue("@task_id", update.TaskId);
        command.Parameters.AddWithValue("@provider", update.Provider);
        command.Parameters.AddWithValue("@provider_task_id", update.ProviderTaskId);
        command.Parameters.AddWithValue("@planned_on", ToDbValue(plannedOn));
        command.Parameters.AddWithValue("@planned_at", ToDbValue(plannedAt));
        command.Parameters.AddWithValue("@created_at", ToDbDate(update.CreatedAt));
        command.Parameters.AddWithValue("@retry_count", update.RetryCount);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PendingTaskDateUpdate>> GetPendingTaskDateUpdatesAsync(string provider, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsurePendingTaskDateUpdatesTableAsync(connection, cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, task_id, provider, provider_task_id, planned_on, planned_at, created_at, retry_count
            FROM pending_task_date_updates
            WHERE provider = @provider
            ORDER BY created_at
            """;
        command.Parameters.AddWithValue("@provider", provider);

        var updates = new List<PendingTaskDateUpdate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            updates.Add(new PendingTaskDateUpdate
            {
                Id = reader.GetString(0),
                TaskId = reader.GetString(1),
                Provider = reader.GetString(2),
                ProviderTaskId = reader.GetString(3),
                PlannedOn = ReadDateOnly(reader, 4),
                PlannedAt = ReadDate(reader, 5),
                CreatedAt = ReadDate(reader, 6) ?? DateTimeOffset.UtcNow,
                RetryCount = reader.GetInt32(7),
            });
        }

        return updates;
    }

    public async Task MarkTaskDateUpdateSyncedAsync(string updateId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM pending_task_date_updates WHERE id = @id";
        command.Parameters.AddWithValue("@id", updateId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task SetIntegrationConfiguredAsync(string id, bool configured, CancellationToken cancellationToken = default)
    {
        return UpdateIntegrationFlagsAsync(id, configured: configured, active: configured, cancellationToken);
    }

    public Task SetIntegrationActiveAsync(string id, bool active, CancellationToken cancellationToken = default)
    {
        return UpdateIntegrationFlagsAsync(id, configured: null, active: active, cancellationToken);
    }

    public async Task UpdateIntegrationSyncAsync(string id, DateTimeOffset lastSyncAt, string? syncToken, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE integrations
            SET last_sync_at = @last_sync_at,
                sync_token = COALESCE(@sync_token, sync_token)
            WHERE id = @id
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@last_sync_at", ToDbDate(lastSyncAt));
        command.Parameters.AddWithValue("@sync_token", (object?)syncToken ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var connectionCommand = connection.CreateCommand();
        connectionCommand.CommandText = """
            UPDATE provider_connections
            SET last_sync_at = @last_sync_at,
                updated_at = @last_sync_at
            WHERE integration_id = @integration_id
            """;
        connectionCommand.Parameters.AddWithValue("@integration_id", id);
        connectionCommand.Parameters.AddWithValue("@last_sync_at", ToDbDate(lastSyncAt));
        await connectionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SetCompletionStateAsync(string taskId, bool completed, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await SetCompletionStateCoreAsync(connection, taskId, completed, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SetCompletionStateCoreAsync(SqliteConnection connection, string taskId, bool completed, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tasks
            SET completion_state = @completion_state,
                completed_at = @completed_at,
                updated_at = @updated_at,
                revision = revision + 1
            WHERE id = @id
            """;
        command.Parameters.AddWithValue("@id", taskId);
        command.Parameters.AddWithValue("@completion_state", completed ? "completed" : "open");
        command.Parameters.AddWithValue("@completed_at", completed ? ToDbDate(now) : DBNull.Value);
        command.Parameters.AddWithValue("@updated_at", ToDbDate(now));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed == 0)
        {
            throw new InvalidOperationException("The task no longer exists.");
        }
    }

    private async Task UpdateIntegrationFlagsAsync(string id, bool? configured, bool? active, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE integrations
            SET is_configured = COALESCE(@configured, is_configured),
                is_active = COALESCE(@active, is_active)
            WHERE id = @id
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@configured", configured.HasValue ? (configured.Value ? 1 : 0) : DBNull.Value);
        command.Parameters.AddWithValue("@active", active.HasValue ? (active.Value ? 1 : 0) : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (configured.HasValue)
        {
            var connectionCommand = connection.CreateCommand();
            connectionCommand.CommandText = """
                UPDATE provider_connections
                SET status = @status,
                    updated_at = @updated_at
                WHERE integration_id = @integration_id
                """;
            connectionCommand.Parameters.AddWithValue("@integration_id", id);
            connectionCommand.Parameters.AddWithValue("@status", configured.Value ? "connected" : "disconnected");
            connectionCommand.Parameters.AddWithValue("@updated_at", ToDbDate(DateTimeOffset.UtcNow));
            await connectionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

    }

    private static async Task<ProviderSourceItem?> ReadProviderSourceItemByIdAsync(SqliteConnection connection, string id, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, integration_id, provider_connection_id, external_id, provider_task_id, title, description,
                   source_project_id, source_project_name, parent_external_id, suggested_space_id, priority, completion_state,
                   planned_on, planned_at, deadline_on, deadline_at, source_url,
                   recurrence_rule, snapshot_json, adoption_state, adopted_task_id, first_seen_at, last_seen_at, updated_at
            FROM provider_source_items
            WHERE id = @id
            """;
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadProviderSourceItem(reader) : null;
    }

    private static async Task<ProviderSourceItem?> ReadProviderSourceItemByExternalAsync(SqliteConnection connection, string providerConnectionId, string externalId, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, integration_id, provider_connection_id, external_id, provider_task_id, title, description,
                   source_project_id, source_project_name, parent_external_id, suggested_space_id, priority, completion_state,
                   planned_on, planned_at, deadline_on, deadline_at, source_url,
                   recurrence_rule, snapshot_json, adoption_state, adopted_task_id, first_seen_at, last_seen_at, updated_at
            FROM provider_source_items
            WHERE provider_connection_id = @provider_connection_id AND external_id = @external_id
            """;
        command.Parameters.AddWithValue("@provider_connection_id", providerConnectionId);
        command.Parameters.AddWithValue("@external_id", externalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadProviderSourceItem(reader) : null;
    }

    private static async Task<string?> ReadAdoptedProviderLinkAsync(SqliteConnection connection, string taskId, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT integration_id
            FROM provider_source_items
            WHERE adopted_task_id = @task_id
              AND adoption_state = @adoption_state
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@task_id", taskId);
        command.Parameters.AddWithValue("@adoption_state", ProviderSourceAdoptionStates.Adopted);
        return (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))?.ToString();
    }

    private static async Task UpsertProviderSourceItemCoreAsync(SqliteConnection connection, ProviderSourceItem source, CancellationToken cancellationToken)
    {
        var (plannedOn, plannedAt) = NormalizeDatePair(source.PlannedOn, source.PlannedAt);
        var (deadlineOn, deadlineAt) = NormalizeDatePair(source.DeadlineOn, source.DeadlineAt);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO provider_source_items (id, integration_id, provider_connection_id, external_id, provider_task_id, title, description,
                                               source_project_id, source_project_name, parent_external_id, suggested_space_id, priority, completion_state,
                                               planned_on, planned_at, deadline_on, deadline_at,
                                               source_url, recurrence_rule, snapshot_json, adoption_state, adopted_task_id, first_seen_at, last_seen_at, updated_at)
            VALUES (@id, @integration_id, @provider_connection_id, @external_id, @provider_task_id, @title, @description,
                    @source_project_id, @source_project_name, @parent_external_id, @suggested_space_id, @priority, @completion_state,
                    @planned_on, @planned_at, @deadline_on, @deadline_at,
                    @source_url, @recurrence_rule, @snapshot_json, @adoption_state, @adopted_task_id, @first_seen_at, @last_seen_at, @updated_at)
            ON CONFLICT(provider_connection_id, external_id) DO UPDATE SET
                provider_task_id = excluded.provider_task_id,
                title = excluded.title,
                description = excluded.description,
                source_project_id = excluded.source_project_id,
                source_project_name = excluded.source_project_name,
                parent_external_id = excluded.parent_external_id,
                suggested_space_id = excluded.suggested_space_id,
                priority = excluded.priority,
                completion_state = excluded.completion_state,
                planned_on = excluded.planned_on,
                planned_at = excluded.planned_at,
                deadline_on = excluded.deadline_on,
                deadline_at = excluded.deadline_at,
                source_url = excluded.source_url,
                recurrence_rule = excluded.recurrence_rule,
                snapshot_json = excluded.snapshot_json,
                adoption_state = excluded.adoption_state,
                adopted_task_id = excluded.adopted_task_id,
                last_seen_at = excluded.last_seen_at,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("@id", source.Id);
        command.Parameters.AddWithValue("@integration_id", source.IntegrationId);
        command.Parameters.AddWithValue("@provider_connection_id", source.ProviderConnectionId);
        command.Parameters.AddWithValue("@external_id", source.ExternalId);
        command.Parameters.AddWithValue("@provider_task_id", source.ProviderTaskId);
        command.Parameters.AddWithValue("@title", source.Title);
        command.Parameters.AddWithValue("@description", ToDbValue(source.Description));
        command.Parameters.AddWithValue("@source_project_id", ToDbValue(source.SourceProjectId));
        command.Parameters.AddWithValue("@source_project_name", ToDbValue(source.SourceProjectName));
        command.Parameters.AddWithValue("@parent_external_id", ToDbValue(source.ParentExternalId));
        command.Parameters.AddWithValue("@suggested_space_id", ToDbValue(source.SuggestedSpaceId));
        command.Parameters.AddWithValue("@priority", source.Priority);
        command.Parameters.AddWithValue("@completion_state", source.CompletionState.ToStorageValue());
        command.Parameters.AddWithValue("@planned_on", ToDbValue(plannedOn));
        command.Parameters.AddWithValue("@planned_at", ToDbValue(plannedAt));
        command.Parameters.AddWithValue("@deadline_on", ToDbValue(deadlineOn));
        command.Parameters.AddWithValue("@deadline_at", ToDbValue(deadlineAt));
        command.Parameters.AddWithValue("@source_url", ToDbValue(source.SourceUrl));
        command.Parameters.AddWithValue("@recurrence_rule", ToDbValue(source.RecurrenceRule));
        command.Parameters.AddWithValue("@snapshot_json", ToDbValue(source.SnapshotJson));
        command.Parameters.AddWithValue("@adoption_state", source.AdoptionState);
        command.Parameters.AddWithValue("@adopted_task_id", ToDbValue(source.AdoptedTaskId));
        command.Parameters.AddWithValue("@first_seen_at", ToDbDate(source.FirstSeenAt));
        command.Parameters.AddWithValue("@last_seen_at", ToDbDate(source.LastSeenAt));
        command.Parameters.AddWithValue("@updated_at", ToDbValue(source.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RefreshAdoptedTaskFromSourceAsync(
        SqliteConnection connection,
        ProviderSourceItem source,
        ParentTaskContext? parent,
        bool syncPlannedDate,
        CancellationToken cancellationToken)
    {
        var (plannedOn, plannedAt) = NormalizeDatePair(source.PlannedOn, source.PlannedAt);
        var (deadlineOn, deadlineAt) = NormalizeDatePair(source.DeadlineOn, source.DeadlineAt);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tasks
            SET source_description = @source_description,
                space_id = CASE
                    WHEN @parent_id IS NOT NULL AND TRIM(@parent_id) != '' THEN @parent_space_id
                    ELSE space_id
                END,
                project_id = CASE
                    WHEN @parent_id IS NOT NULL
                         AND TRIM(@parent_id) != ''
                         AND (project_id IS NULL OR TRIM(project_id) = '')
                    THEN @parent_project_id
                    ELSE project_id
                END,
                parent_id = COALESCE(@parent_id, parent_id),
                workflow_status = CASE
                    WHEN @parent_id IS NOT NULL
                         AND TRIM(@parent_id) != ''
                         AND workflow_status = 'inbox'
                         AND (project_id IS NULL OR TRIM(project_id) = '')
                    THEN @parent_workflow_status
                    ELSE workflow_status
                END,
                completion_state = CASE
                    WHEN EXISTS (SELECT 1 FROM pending_completions WHERE task_id = @task_id) THEN completion_state
                    WHEN EXISTS (SELECT 1 FROM personal_cloud_outbox WHERE entity_id = @task_id AND operation IN ('task.complete','task.reopen'))
                      OR EXISTS (SELECT 1 FROM personal_cloud_provider_commands WHERE task_id = @task_id) THEN completion_state
                    ELSE @completion_state
                END,
                source_integration_id = @source_integration_id,
                source_connection_id = @source_connection_id,
                source_external_id = @source_external_id,
                source_provider_task_id = @source_provider_task_id,
                source_url = @source_url,
                source_metadata = @source_metadata,
                planned_on = CASE
                    WHEN @sync_planned_date = 1 AND NOT EXISTS (SELECT 1 FROM pending_task_date_updates WHERE task_id = @task_id) THEN @planned_on
                    ELSE planned_on
                END,
                planned_at = CASE
                    WHEN @sync_planned_date = 1 AND NOT EXISTS (SELECT 1 FROM pending_task_date_updates WHERE task_id = @task_id) THEN @planned_at
                    ELSE planned_at
                END,
                deadline_on = CASE
                    WHEN @recurrence_rule IS NOT NULL AND TRIM(@recurrence_rule) != '' THEN @deadline_on
                    ELSE deadline_on
                END,
                deadline_at = CASE
                    WHEN @recurrence_rule IS NOT NULL AND TRIM(@recurrence_rule) != '' THEN @deadline_at
                    ELSE deadline_at
                END,
                updated_at = @updated_at,
                revision = revision + 1,
                completed_at = CASE
                    WHEN EXISTS (SELECT 1 FROM pending_completions WHERE task_id = @task_id) THEN completed_at
                    WHEN EXISTS (SELECT 1 FROM personal_cloud_outbox WHERE entity_id = @task_id AND operation IN ('task.complete','task.reopen'))
                      OR EXISTS (SELECT 1 FROM personal_cloud_provider_commands WHERE task_id = @task_id) THEN completed_at
                    WHEN @completion_state = 'completed' THEN COALESCE(completed_at, @updated_at)
                    ELSE NULL
                END
            WHERE id = @task_id
            """;
        command.Parameters.AddWithValue("@task_id", source.AdoptedTaskId);
        command.Parameters.AddWithValue("@sync_planned_date", syncPlannedDate ? 1 : 0);
        command.Parameters.AddWithValue("@source_description", ToDbValue(source.Description));
        command.Parameters.AddWithValue("@parent_id", ToDbValue(parent?.Id));
        command.Parameters.AddWithValue("@parent_space_id", ToDbValue(parent?.SpaceId));
        command.Parameters.AddWithValue("@parent_project_id", ToDbValue(parent?.ProjectId));
        command.Parameters.AddWithValue("@parent_workflow_status", ToDbValue(parent?.WorkflowStatus.ToStorageValue()));
        command.Parameters.AddWithValue("@completion_state", source.CompletionState.ToStorageValue());
        command.Parameters.AddWithValue("@source_integration_id", source.IntegrationId);
        command.Parameters.AddWithValue("@source_connection_id", source.ProviderConnectionId);
        command.Parameters.AddWithValue("@source_external_id", source.ExternalId);
        command.Parameters.AddWithValue("@source_provider_task_id", source.ProviderTaskId);
        command.Parameters.AddWithValue("@source_url", ToDbValue(source.SourceUrl));
        command.Parameters.AddWithValue("@source_metadata", BuildSourceMetadataJson(source));
        command.Parameters.AddWithValue("@planned_on", ToDbValue(plannedOn));
        command.Parameters.AddWithValue("@planned_at", ToDbValue(plannedAt));
        command.Parameters.AddWithValue("@deadline_on", ToDbValue(deadlineOn));
        command.Parameters.AddWithValue("@deadline_at", ToDbValue(deadlineAt));
        command.Parameters.AddWithValue("@recurrence_rule", ToDbValue(source.RecurrenceRule));
        command.Parameters.AddWithValue("@updated_at", ToDbDate(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(string AdoptionState, string? AdoptedTaskId)> ResolveProviderSourceAdoptionAsync(
        SqliteConnection connection,
        ProviderSourceItem? existing,
        ProviderSourceItem incoming,
        CancellationToken cancellationToken)
    {
        if (existing is null)
        {
            return (incoming.AdoptionState, incoming.AdoptedTaskId);
        }

        if (existing.AdoptionState != ProviderSourceAdoptionStates.Missing)
        {
            return (existing.AdoptionState, existing.AdoptedTaskId);
        }

        if (!string.IsNullOrWhiteSpace(existing.AdoptedTaskId) &&
            await TaskExistsAsync(connection, existing.AdoptedTaskId, cancellationToken).ConfigureAwait(false))
        {
            return (ProviderSourceAdoptionStates.Adopted, existing.AdoptedTaskId);
        }

        return (incoming.AdoptionState, null);
    }

    private static async Task<bool> TaskExistsAsync(SqliteConnection connection, string taskId, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM tasks WHERE id = @id LIMIT 1";
        command.Parameters.AddWithValue("@id", taskId);
        return (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) is not null;
    }

    private static bool ShouldSyncPlannedDateFromSource(ProviderSourceItem source, ProviderSourceItem? previousSource)
    {
        if (!string.IsNullOrWhiteSpace(source.RecurrenceRule))
        {
            return true;
        }

        if (!string.Equals(source.IntegrationId, IntegrationIds.Todoist, StringComparison.Ordinal))
        {
            return false;
        }

        return source.PlannedOn is not null ||
            source.PlannedAt is not null ||
            previousSource?.PlannedOn is not null ||
            previousSource?.PlannedAt is not null;
    }

    private static ProviderSourceItem ReadProviderSourceItem(IDataRecord reader) => new()
    {
        Id = reader.GetString(0),
        IntegrationId = reader.GetString(1),
        ProviderConnectionId = reader.GetString(2),
        ExternalId = reader.GetString(3),
        ProviderTaskId = reader.GetString(4),
        Title = reader.GetString(5),
        Description = GetNullableString(reader, 6),
        SourceProjectId = GetNullableString(reader, 7),
        SourceProjectName = GetNullableString(reader, 8),
        ParentExternalId = GetNullableString(reader, 9),
        SuggestedSpaceId = GetNullableString(reader, 10),
        Priority = reader.GetInt32(11),
        CompletionState = TaskStatusExtensions.CompletionFromStorageValue(reader.GetString(12)),
        PlannedOn = ReadDateOnly(reader, 13),
        PlannedAt = ReadDate(reader, 14),
        DeadlineOn = ReadDateOnly(reader, 15),
        DeadlineAt = ReadDate(reader, 16),
        SourceUrl = GetNullableString(reader, 17),
        RecurrenceRule = GetNullableString(reader, 18),
        SnapshotJson = GetNullableString(reader, 19),
        AdoptionState = reader.GetString(20),
        AdoptedTaskId = GetNullableString(reader, 21),
        FirstSeenAt = ReadDate(reader, 22) ?? DateTimeOffset.UtcNow,
        LastSeenAt = ReadDate(reader, 23) ?? DateTimeOffset.UtcNow,
        UpdatedAt = ReadDate(reader, 24),
    };

    private static TaskExternalLinkInfo ReadTaskExternalLink(IDataRecord reader) => new()
    {
        Id = reader.GetString(0),
        TaskId = reader.GetString(1),
        IntegrationId = reader.GetString(2),
        ConnectionId = GetNullableString(reader, 3),
        ExternalId = reader.GetString(4),
        Kind = reader.GetString(5),
        DisplayName = reader.GetString(6),
        Url = reader.GetString(7),
        MetadataJson = GetNullableString(reader, 8),
        CreatedAt = ReadDate(reader, 9) ?? DateTimeOffset.UtcNow,
        UpdatedAt = ReadDate(reader, 10),
    };

    private static string BuildProviderSourceItemId(string providerConnectionId, string externalId) =>
        $"source_{ToStableId(providerConnectionId)}_{ToStableId(externalId)}";

    private static string ToStableId(string value) =>
        Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(value)).ToLowerInvariant();

    private static string BuildSourceMetadataJson(ProviderSourceItem source)
    {
        return JsonSerializer.Serialize(new
        {
            source = new
            {
                source.IntegrationId,
                source.ProviderConnectionId,
                source.ExternalId,
                source.ProviderTaskId,
                source.SourceProjectId,
                source.SourceProjectName,
                source.ParentExternalId,
                source.SourceUrl,
                source.RecurrenceRule,
                synced_at = DateTimeOffset.UtcNow,
            },
        });
    }

    private async Task<IReadOnlyList<TaskItem>> ReadTasksAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var labelsByTask = await ReadLabelsByTaskAsync(connection, cancellationToken).ConfigureAwait(false);
        var taskColumns = await GetColumnNamesAsync(connection, "tasks", cancellationToken).ConfigureAwait(false);
        var revisionProjection = taskColumns.Contains("revision") ? "t.revision" : "0 AS revision";
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id, t.external_id, t.space_id, t.integration_id, t.title, t.description, t.source_description, t.project_id, t.parent_id,
                   t.priority, t.source_integration_id, t.source_connection_id, t.source_external_id, t.source_provider_task_id,
                   t.source_url, t.completion_state, t.workflow_status, t.planned_on, t.planned_at,
                   t.deadline_on, t.deadline_at, t.scheduled_start, t.scheduled_end, t.duration_minutes, t.recurrence_rule,
                   t.notes, t.provider_metadata, t.source_metadata, t.local_metadata,
                   t.created_at, t.updated_at, t.completed_at, t.provider_connection_id, psi.title, psi.source_project_name,
                   psi.priority, psi.planned_on, psi.planned_at, psi.deadline_on, psi.deadline_at, @revision_projection
            FROM tasks t
            LEFT JOIN provider_source_items psi
              ON psi.provider_connection_id = t.source_connection_id
             AND psi.external_id = t.source_external_id
            WHERE NOT EXISTS (
              SELECT 1 FROM personal_cloud_tombstones pct
              WHERE pct.product = 'tasks' AND pct.entity_id = t.id
            )
            """.Replace("@revision_projection", revisionProjection, StringComparison.Ordinal);

        var tasks = new List<TaskItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            tasks.Add(new TaskItem
            {
                Id = id,
                ExternalId = GetNullableString(reader, 1),
                SpaceId = reader.GetString(2),
                IntegrationId = reader.GetString(3),
                Title = reader.GetString(4),
                Description = GetNullableString(reader, 5),
                SourceDescription = GetNullableString(reader, 6),
                ProjectId = GetNullableString(reader, 7),
                ParentId = GetNullableString(reader, 8),
                Priority = reader.GetInt32(9),
                SourceIntegrationId = GetNullableString(reader, 10),
                SourceConnectionId = GetNullableString(reader, 11),
                SourceExternalId = GetNullableString(reader, 12),
                SourceProviderTaskId = GetNullableString(reader, 13),
                SourceUrl = GetNullableString(reader, 14),
                CompletionState = TaskStatusExtensions.CompletionFromStorageValue(reader.GetString(15)),
                WorkflowStatus = TaskStatusExtensions.WorkflowFromStorageValue(reader.GetString(16)),
                PlannedOn = ReadDateOnly(reader, 17),
                PlannedAt = ReadDate(reader, 18),
                DeadlineOn = ReadDateOnly(reader, 19),
                DeadlineAt = ReadDate(reader, 20),
                ScheduledStart = ReadDate(reader, 21),
                ScheduledEnd = ReadDate(reader, 22),
                DurationMinutes = reader.IsDBNull(23) ? null : reader.GetInt32(23),
                RecurrenceRule = GetNullableString(reader, 24),
                Notes = GetNullableString(reader, 25),
                ProviderMetadataJson = GetNullableString(reader, 26),
                SourceMetadataJson = GetNullableString(reader, 27),
                LocalMetadataJson = GetNullableString(reader, 28),
                CreatedAt = ReadDate(reader, 29) ?? DateTimeOffset.UtcNow,
                UpdatedAt = ReadDate(reader, 30),
                CompletedAt = ReadDate(reader, 31),
                ProviderConnectionId = GetNullableString(reader, 32),
                SourceTitle = GetNullableString(reader, 33),
                SourceProjectName = GetNullableString(reader, 34),
                SourcePriority = reader.IsDBNull(35) ? null : reader.GetInt32(35),
                SourcePlannedOn = ReadDateOnly(reader, 36),
                SourcePlannedAt = ReadDate(reader, 37),
                SourceDeadlineOn = ReadDateOnly(reader, 38),
                SourceDeadlineAt = ReadDate(reader, 39),
                Revision = reader.GetInt64(40),
                Labels = labelsByTask.GetValueOrDefault(id, []),
            });
        }

        return tasks;
    }

    private static async Task<Dictionary<string, IReadOnlyList<LabelItem>>> ReadLabelsByTaskAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tl.task_id, l.id, l.external_id, l.integration_id, l.provider_connection_id, l.name, l.color, l.description,
                   l.sort_order, l.is_favorite, l.provider_metadata, l.created_at
            FROM task_labels tl
            INNER JOIN labels l ON l.id = tl.label_id
            ORDER BY l.sort_order, l.name
            """;

        var labels = new Dictionary<string, List<LabelItem>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var taskId = reader.GetString(0);
            if (!labels.TryGetValue(taskId, out var list))
            {
                list = [];
                labels[taskId] = list;
            }

            list.Add(new LabelItem
            {
                Id = reader.GetString(1),
                ExternalId = GetNullableString(reader, 2),
                IntegrationId = reader.GetString(3),
                ProviderConnectionId = GetNullableString(reader, 4),
                Name = reader.GetString(5),
                Color = reader.GetString(6),
                Description = GetNullableString(reader, 7),
                SortOrder = reader.GetInt32(8),
                IsFavorite = reader.GetInt32(9) != 0,
                ProviderMetadataJson = GetNullableString(reader, 10),
                CreatedAt = ReadDate(reader, 11) ?? DateTimeOffset.UtcNow,
            });
        }

        return labels.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<LabelItem>)kvp.Value);
    }

    private static async Task UpsertTaskCoreAsync(SqliteConnection connection, TaskItem task, CancellationToken cancellationToken)
    {
        var (plannedOn, plannedAt) = NormalizeDatePair(task.PlannedOn, task.PlannedAt);
        var (deadlineOn, deadlineAt) = NormalizeDatePair(task.DeadlineOn, task.DeadlineAt);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tasks (id, external_id, space_id, integration_id, provider_connection_id, title, description, source_description, project_id, parent_id,
                               priority, source_integration_id, source_connection_id, source_external_id, source_provider_task_id,
                               source_url, completion_state, workflow_status, planned_on, planned_at, deadline_on, deadline_at,
                               scheduled_start, scheduled_end, duration_minutes, recurrence_rule,
                               notes, provider_metadata, source_metadata, local_metadata,
                               created_at, updated_at, completed_at, revision)
            VALUES (@id, @external_id, @space_id, @integration_id, @provider_connection_id, @title, @description, @source_description, @project_id, @parent_id,
                    @priority, @source_integration_id, @source_connection_id, @source_external_id, @source_provider_task_id,
                    @source_url, @completion_state, @workflow_status, @planned_on, @planned_at, @deadline_on, @deadline_at,
                    @scheduled_start, @scheduled_end, @duration_minutes, @recurrence_rule,
                    @notes, @provider_metadata, @source_metadata, @local_metadata,
                    @created_at, @updated_at, @completed_at, @revision)
            ON CONFLICT(id) DO UPDATE SET
                external_id = excluded.external_id,
                space_id = excluded.space_id,
                integration_id = excluded.integration_id,
                provider_connection_id = excluded.provider_connection_id,
                title = excluded.title,
                description = excluded.description,
                source_description = excluded.source_description,
                project_id = excluded.project_id,
                parent_id = excluded.parent_id,
                priority = excluded.priority,
                source_integration_id = excluded.source_integration_id,
                source_connection_id = excluded.source_connection_id,
                source_external_id = excluded.source_external_id,
                source_provider_task_id = excluded.source_provider_task_id,
                source_url = excluded.source_url,
                completion_state = excluded.completion_state,
                workflow_status = excluded.workflow_status,
                planned_on = excluded.planned_on,
                planned_at = excluded.planned_at,
                deadline_on = excluded.deadline_on,
                deadline_at = excluded.deadline_at,
                scheduled_start = excluded.scheduled_start,
                scheduled_end = excluded.scheduled_end,
                duration_minutes = excluded.duration_minutes,
                recurrence_rule = excluded.recurrence_rule,
                notes = excluded.notes,
                provider_metadata = excluded.provider_metadata,
                source_metadata = excluded.source_metadata,
                local_metadata = excluded.local_metadata,
                updated_at = excluded.updated_at,
                completed_at = excluded.completed_at,
                revision = tasks.revision + 1
            """;
        command.Parameters.AddWithValue("@id", task.Id);
        command.Parameters.AddWithValue("@external_id", ToDbValue(task.ExternalId));
        command.Parameters.AddWithValue("@space_id", task.SpaceId);
        command.Parameters.AddWithValue("@integration_id", task.IntegrationId);
        command.Parameters.AddWithValue("@provider_connection_id", ToDbValue(task.ProviderConnectionId));
        command.Parameters.AddWithValue("@title", task.Title);
        command.Parameters.AddWithValue("@description", DBNull.Value);
        command.Parameters.AddWithValue("@source_description", ToDbValue(task.SourceDescription));
        command.Parameters.AddWithValue("@project_id", ToDbValue(task.ProjectId));
        command.Parameters.AddWithValue("@parent_id", ToDbValue(task.ParentId));
        command.Parameters.AddWithValue("@priority", task.Priority);
        command.Parameters.AddWithValue("@source_integration_id", ToDbValue(task.SourceIntegrationId));
        command.Parameters.AddWithValue("@source_connection_id", ToDbValue(task.SourceConnectionId));
        command.Parameters.AddWithValue("@source_external_id", ToDbValue(task.SourceExternalId));
        command.Parameters.AddWithValue("@source_provider_task_id", ToDbValue(task.SourceProviderTaskId));
        command.Parameters.AddWithValue("@source_url", ToDbValue(task.SourceUrl));
        command.Parameters.AddWithValue("@completion_state", task.CompletionState.ToStorageValue());
        command.Parameters.AddWithValue("@workflow_status", task.WorkflowStatus.ToStorageValue());
        command.Parameters.AddWithValue("@planned_on", ToDbValue(plannedOn));
        command.Parameters.AddWithValue("@planned_at", task.IntegrationId == IntegrationIds.Local ? ToDbTaskInstant(plannedAt) : ToDbValue(plannedAt));
        command.Parameters.AddWithValue("@deadline_on", ToDbValue(deadlineOn));
        command.Parameters.AddWithValue("@deadline_at", task.IntegrationId == IntegrationIds.Local ? ToDbTaskInstant(deadlineAt) : ToDbValue(deadlineAt));
        command.Parameters.AddWithValue("@scheduled_start", task.IntegrationId == IntegrationIds.Local ? ToDbTaskInstant(task.ScheduledStart) : ToDbValue(task.ScheduledStart));
        command.Parameters.AddWithValue("@scheduled_end", task.IntegrationId == IntegrationIds.Local ? ToDbTaskInstant(task.ScheduledEnd) : ToDbValue(task.ScheduledEnd));
        command.Parameters.AddWithValue("@duration_minutes", task.DurationMinutes is null ? DBNull.Value : task.DurationMinutes.Value);
        command.Parameters.AddWithValue("@recurrence_rule", ToDbValue(task.RecurrenceRule));
        command.Parameters.AddWithValue("@notes", ToDbValue(task.Notes));
        command.Parameters.AddWithValue("@provider_metadata", ToDbValue(task.ProviderMetadataJson));
        command.Parameters.AddWithValue("@source_metadata", ToDbValue(task.SourceMetadataJson));
        command.Parameters.AddWithValue("@local_metadata", ToDbValue(task.LocalMetadataJson));
        command.Parameters.AddWithValue("@created_at", ToDbDate(task.CreatedAt));
        command.Parameters.AddWithValue("@updated_at", ToDbValue(task.UpdatedAt));
        command.Parameters.AddWithValue("@completed_at", ToDbValue(task.CompletedAt));
        command.Parameters.AddWithValue("@revision", task.Revision);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SetTaskLabelsCoreAsync(SqliteConnection connection, string taskId, IReadOnlyList<LabelItem> labels, CancellationToken cancellationToken)
    {
        var deleteCommand = connection.CreateCommand();
        deleteCommand.CommandText = "DELETE FROM task_labels WHERE task_id = @task_id";
        deleteCommand.Parameters.AddWithValue("@task_id", taskId);
        await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        foreach (var label in labels)
        {
            var labelId = await ResolveAndUpsertLabelCoreAsync(connection, label, cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO task_labels (task_id, label_id) VALUES (@task_id, @label_id)";
            command.Parameters.AddWithValue("@task_id", taskId);
            command.Parameters.AddWithValue("@label_id", labelId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> ResolveAndUpsertLabelCoreAsync(
        SqliteConnection connection,
        LabelItem label,
        CancellationToken cancellationToken,
        bool preserveExistingId = true)
    {
        if (preserveExistingId)
        {
            var idCommand = connection.CreateCommand();
            idCommand.CommandText = "SELECT id FROM labels WHERE id = @id LIMIT 1";
            idCommand.Parameters.AddWithValue("@id", label.Id);
            if (await idCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string existingExactId)
            {
                return existingExactId;
            }
        }

        var existingId = await FindLabelIdByNameAsync(connection, label.Name, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(existingId) && !string.Equals(existingId, label.Id, StringComparison.Ordinal))
        {
            return existingId;
        }

        await UpsertLabelCoreAsync(connection, label, cancellationToken).ConfigureAwait(false);
        return label.Id;
    }

    private static async Task<string?> FindLabelIdByNameAsync(SqliteConnection connection, string name, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM labels WHERE lower(name) = lower(@name) LIMIT 1";
        command.Parameters.AddWithValue("@name", name.Trim());
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result as string;
    }

    private static async Task UpsertLabelCoreAsync(SqliteConnection connection, LabelItem label, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO labels (id, external_id, integration_id, provider_connection_id, name, color, description, sort_order,
                                is_favorite, provider_metadata, created_at)
            VALUES (@id, @external_id, @integration_id, @provider_connection_id, @name, @color, @description, @sort_order,
                    @is_favorite, @provider_metadata, @created_at)
            ON CONFLICT(id) DO UPDATE SET
                external_id = excluded.external_id,
                integration_id = excluded.integration_id,
                provider_connection_id = excluded.provider_connection_id,
                name = excluded.name,
                color = excluded.color,
                description = excluded.description,
                sort_order = excluded.sort_order,
                is_favorite = excluded.is_favorite,
                provider_metadata = excluded.provider_metadata
            """;
        command.Parameters.AddWithValue("@id", label.Id);
        command.Parameters.AddWithValue("@external_id", ToDbValue(label.ExternalId));
        command.Parameters.AddWithValue("@integration_id", label.IntegrationId);
        command.Parameters.AddWithValue("@provider_connection_id", ToDbValue(label.ProviderConnectionId));
        command.Parameters.AddWithValue("@name", label.Name);
        command.Parameters.AddWithValue("@color", label.Color);
        command.Parameters.AddWithValue("@description", ToDbValue(label.Description));
        command.Parameters.AddWithValue("@sort_order", label.SortOrder);
        command.Parameters.AddWithValue("@is_favorite", label.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("@provider_metadata", ToDbValue(label.ProviderMetadataJson));
        command.Parameters.AddWithValue("@created_at", ToDbDate(label.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddProjectParameters(SqliteCommand command, ProjectItem project)
    {
        command.Parameters.AddWithValue("@id", project.Id);
        command.Parameters.AddWithValue("@external_id", ToDbValue(project.ExternalId));
        command.Parameters.AddWithValue("@space_id", project.SpaceId);
        command.Parameters.AddWithValue("@integration_id", project.IntegrationId);
        command.Parameters.AddWithValue("@provider_connection_id", ToDbValue(project.ProviderConnectionId));
        command.Parameters.AddWithValue("@name", project.Name);
        command.Parameters.AddWithValue("@description", ToDbValue(project.Description));
        command.Parameters.AddWithValue("@color", project.Color);
        command.Parameters.AddWithValue("@icon", ToDbValue(project.Icon));
        command.Parameters.AddWithValue("@parent_id", ToDbValue(project.ParentId));
        command.Parameters.AddWithValue("@sort_order", project.SortOrder);
        command.Parameters.AddWithValue("@is_favorite", project.IsFavorite ? 1 : 0);
        var status = project.EffectiveStatus;
        command.Parameters.AddWithValue("@is_archived", status == ProjectLifecycleStates.Archived ? 1 : 0);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@provider_metadata", ToDbValue(project.ProviderMetadataJson));
        command.Parameters.AddWithValue("@created_at", ToDbDate(project.CreatedAt));
        command.Parameters.AddWithValue("@updated_at", ToDbValue(project.UpdatedAt));
    }

    private static ProjectItem ReadProject(IDataRecord reader)
    {
        var legacyArchived = reader.GetInt32(12) != 0;
        var status = legacyArchived
            ? ProjectLifecycleStates.Archived
            : ProjectLifecycleStates.Normalize(GetNullableString(reader, 13));

        return new ProjectItem
        {
            Id = reader.GetString(0),
            ExternalId = GetNullableString(reader, 1),
            SpaceId = reader.GetString(2),
            IntegrationId = reader.GetString(3),
            ProviderConnectionId = GetNullableString(reader, 4),
            Name = reader.GetString(5),
            Description = GetNullableString(reader, 6),
            Color = GetNullableString(reader, 7) ?? "#808080",
            Icon = GetNullableString(reader, 8),
            ParentId = GetNullableString(reader, 9),
            SortOrder = reader.GetInt32(10),
            IsFavorite = reader.GetInt32(11) != 0,
            IsArchived = status == ProjectLifecycleStates.Archived,
            Status = status,
            ProviderMetadataJson = GetNullableString(reader, 14),
            CreatedAt = ReadDate(reader, 15) ?? DateTimeOffset.UtcNow,
            UpdatedAt = ReadDate(reader, 16),
        };
    }

    private static SpaceItem ReadSpace(IDataRecord reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        Color = GetNullableString(reader, 2) ?? "#808080",
        Icon = GetNullableString(reader, 3),
        SortOrder = reader.GetInt32(4),
        IsArchived = reader.GetInt32(5) != 0,
        CreatedAt = ReadDate(reader, 6) ?? DateTimeOffset.UtcNow,
        UpdatedAt = ReadDate(reader, 7),
    };

    private static LabelItem ReadLabel(IDataRecord reader) => new()
    {
        Id = reader.GetString(0),
        ExternalId = GetNullableString(reader, 1),
        IntegrationId = reader.GetString(2),
        ProviderConnectionId = GetNullableString(reader, 3),
        Name = reader.GetString(4),
        Color = GetNullableString(reader, 5) ?? "#808080",
        Description = GetNullableString(reader, 6),
        SortOrder = reader.GetInt32(7),
        IsFavorite = reader.GetInt32(8) != 0,
        ProviderMetadataJson = GetNullableString(reader, 9),
        CreatedAt = ReadDate(reader, 10) ?? DateTimeOffset.UtcNow,
    };

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = IsReadOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = false,
        }.ToString());

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!IsReadOnly)
        {
            await ExecutePragmaAsync(connection, "PRAGMA journal_mode = DELETE", cancellationToken).ConfigureAwait(false);
            await ExecutePragmaAsync(connection, "PRAGMA synchronous = FULL", cancellationToken).ConfigureAwait(false);
        }
        await ExecutePragmaAsync(connection, "PRAGMA busy_timeout = 5000", cancellationToken).ConfigureAwait(false);
        await ExecutePragmaAsync(connection, "PRAGMA foreign_keys = ON", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        using (var source = typeof(SqliteTaskStore).Assembly.GetManifestResourceStream("Openza.Tasks.Core.Data.Migrations.0010_personal_cloud_initial_sync.sql")
            ?? throw new InvalidOperationException("The initial sync migration script is missing."))
        using (var reader = new StreamReader(source))
        {
            command.CommandText = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await EnsureTaskExternalLinksTableAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsurePendingCompletionsTableAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsurePendingTaskDateUpdatesTableAsync(connection, cancellationToken).ConfigureAwait(false);
        using (var source = typeof(SqliteTaskStore).Assembly.GetManifestResourceStream("Openza.Tasks.Core.Data.Migrations.0011_personal_cloud_workspace.sql")
            ?? throw new InvalidOperationException("The workspace migration script is missing."))
        using (var reader = new StreamReader(source))
        {
            command.CommandText = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await ExecutePragmaAsync(connection, $"PRAGMA user_version = {CurrentSchemaVersion}", cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureLegacySchemaCompatibilityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecutePragmaAsync(connection, "PRAGMA foreign_keys = OFF", cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureIntegrationsCompatibilityAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnsureSpacesCompatibilityAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnsureProjectsCompatibilityAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnsureTasksCompatibilityAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnsureProviderSourceItemsCompatibilityAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnsureLabelsCompatibilityAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnsureTaskExternalLinksTableAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnsurePersonalCloudJournalCompatibilityAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ExecutePragmaAsync(connection, "PRAGMA foreign_keys = ON", cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureTaskExternalLinksTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS task_external_links (
              id TEXT PRIMARY KEY NOT NULL,
              task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
              integration_id TEXT NOT NULL REFERENCES integrations(id),
              connection_id TEXT REFERENCES provider_connections(id),
              external_id TEXT NOT NULL,
              kind TEXT NOT NULL DEFAULT 'issue',
              display_name TEXT NOT NULL,
              url TEXT NOT NULL,
              metadata TEXT,
              created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
              updated_at INTEGER,
              UNIQUE(task_id, integration_id, external_id)
            );

            CREATE INDEX IF NOT EXISTS idx_task_external_links_task ON task_external_links(task_id, integration_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsurePersonalCloudJournalCompatibilityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        foreach (var table in new[] { "personal_cloud_outbox", "personal_cloud_applied_changes", "personal_cloud_tombstones" })
        {
            var inspect = connection.CreateCommand();
            inspect.CommandText = $"PRAGMA foreign_key_list({table})";
            var requiresMigration = false;
            await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    requiresMigration |= reader.GetString(2) == "tasks";
            if (!requiresMigration) continue;

            var resource = $"Openza.Tasks.Core.Data.Migrations.0009_{table}.sql";
            await using var script = typeof(SqliteTaskStore).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("The Personal Cloud journal migration script is missing.");
            using var source = new StreamReader(script);
            var sql = await source.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var migrate = connection.CreateCommand();
            migrate.CommandText = sql;
            await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureProviderSourceItemsCompatibilityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "provider_source_items", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await AddColumnIfMissingAsync(connection, "provider_source_items", "suggested_space_id", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "provider_source_items", "parent_external_id", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "provider_source_items", "planned_on", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "provider_source_items", "planned_at", "INTEGER", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "provider_source_items", "deadline_on", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "provider_source_items", "deadline_at", "INTEGER", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "provider_source_items", "recurrence_rule", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "provider_source_items", "due_date", "INTEGER", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "provider_source_items", "due_time", "TEXT", cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection, """
            UPDATE provider_source_items
            SET planned_on = COALESCE(
                    planned_on,
                    CASE WHEN due_date IS NOT NULL THEN date(due_date, 'unixepoch', 'localtime') END
                ),
                planned_at = COALESCE(
                    planned_at,
                    CASE
                        WHEN due_date IS NOT NULL
                             AND (due_time IS NOT NULL OR strftime('%H:%M', due_date, 'unixepoch', 'localtime') != '00:00')
                        THEN due_date
                    END
                )
            WHERE planned_on IS NULL OR planned_at IS NULL
            """, cancellationToken).ConfigureAwait(false);

        await NormalizeStoredDatePairsAsync(connection, "provider_source_items", cancellationToken).ConfigureAwait(false);

        await BackfillProviderParentExternalIdsAsync(connection, cancellationToken).ConfigureAwait(false);
        await RelinkAdoptedProviderSubtasksAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task BackfillProviderParentExternalIdsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var select = connection.CreateCommand();
        select.CommandText = """
            SELECT id, snapshot_json
            FROM provider_source_items
            WHERE (parent_external_id IS NULL OR TRIM(parent_external_id) = '')
              AND snapshot_json LIKE '%parentId%'
            """;

        var updates = new List<(string Id, string ParentExternalId)>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var parentExternalId = ExtractParentExternalIdFromSnapshot(GetNullableString(reader, 1));
                if (!string.IsNullOrWhiteSpace(parentExternalId))
                {
                    updates.Add((reader.GetString(0), parentExternalId));
                }
            }
        }

        foreach (var update in updates)
        {
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE provider_source_items SET parent_external_id = @parent_external_id WHERE id = @id";
            command.Parameters.AddWithValue("@id", update.Id);
            command.Parameters.AddWithValue("@parent_external_id", update.ParentExternalId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? ExtractParentExternalIdFromSnapshot(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(snapshotJson);
            if (document.RootElement.TryGetProperty("sourceTask", out var sourceTask) &&
                sourceTask.TryGetProperty("parentId", out var parentId) &&
                parentId.ValueKind != JsonValueKind.Null)
            {
                return parentId.ToString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static async Task RelinkAdoptedProviderSubtasksAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, """
            UPDATE tasks
            SET parent_id = (
                    SELECT parent_source.adopted_task_id
                    FROM provider_source_items child_source
                    INNER JOIN provider_source_items parent_source
                      ON parent_source.provider_connection_id = child_source.provider_connection_id
                     AND parent_source.external_id = child_source.parent_external_id
                    INNER JOIN tasks parent_task
                      ON parent_task.id = parent_source.adopted_task_id
                    WHERE child_source.adopted_task_id = tasks.id
                      AND child_source.parent_external_id IS NOT NULL
                      AND parent_source.adopted_task_id IS NOT NULL
                      AND parent_source.adopted_task_id != tasks.id
                      AND (parent_source.parent_external_id IS NULL OR TRIM(parent_source.parent_external_id) = '')
                      AND (parent_task.parent_id IS NULL OR TRIM(parent_task.parent_id) = '')
                    LIMIT 1
                ),
                space_id = COALESCE((
                    SELECT parent_task.space_id
                    FROM provider_source_items child_source
                    INNER JOIN provider_source_items parent_source
                      ON parent_source.provider_connection_id = child_source.provider_connection_id
                     AND parent_source.external_id = child_source.parent_external_id
                    INNER JOIN tasks parent_task
                      ON parent_task.id = parent_source.adopted_task_id
                    WHERE child_source.adopted_task_id = tasks.id
                      AND child_source.parent_external_id IS NOT NULL
                      AND parent_source.adopted_task_id IS NOT NULL
                      AND parent_source.adopted_task_id != tasks.id
                      AND (parent_source.parent_external_id IS NULL OR TRIM(parent_source.parent_external_id) = '')
                      AND (parent_task.parent_id IS NULL OR TRIM(parent_task.parent_id) = '')
                    LIMIT 1
                ), space_id),
                project_id = (
                    SELECT parent_task.project_id
                    FROM provider_source_items child_source
                    INNER JOIN provider_source_items parent_source
                      ON parent_source.provider_connection_id = child_source.provider_connection_id
                     AND parent_source.external_id = child_source.parent_external_id
                    INNER JOIN tasks parent_task
                      ON parent_task.id = parent_source.adopted_task_id
                    WHERE child_source.adopted_task_id = tasks.id
                      AND child_source.parent_external_id IS NOT NULL
                      AND parent_source.adopted_task_id IS NOT NULL
                      AND parent_source.adopted_task_id != tasks.id
                      AND (parent_source.parent_external_id IS NULL OR TRIM(parent_source.parent_external_id) = '')
                      AND (parent_task.parent_id IS NULL OR TRIM(parent_task.parent_id) = '')
                    LIMIT 1
                ),
                workflow_status = CASE
                    WHEN workflow_status = 'inbox'
                         AND (project_id IS NULL OR TRIM(project_id) = '')
                    THEN COALESCE((
                        SELECT parent_task.workflow_status
                        FROM provider_source_items child_source
                        INNER JOIN provider_source_items parent_source
                          ON parent_source.provider_connection_id = child_source.provider_connection_id
                         AND parent_source.external_id = child_source.parent_external_id
                        INNER JOIN tasks parent_task
                          ON parent_task.id = parent_source.adopted_task_id
                        WHERE child_source.adopted_task_id = tasks.id
                          AND child_source.parent_external_id IS NOT NULL
                          AND parent_source.adopted_task_id IS NOT NULL
                          AND parent_source.adopted_task_id != tasks.id
                          AND (parent_source.parent_external_id IS NULL OR TRIM(parent_source.parent_external_id) = '')
                          AND (parent_task.parent_id IS NULL OR TRIM(parent_task.parent_id) = '')
                        LIMIT 1
                    ), workflow_status)
                    ELSE workflow_status
                END,
                updated_at = strftime('%s', 'now')
            WHERE integration_id = 'openza_tasks'
              AND (parent_id IS NULL OR TRIM(parent_id) = '')
              AND EXISTS (
                    SELECT 1
                    FROM provider_source_items child_source
                    INNER JOIN provider_source_items parent_source
                      ON parent_source.provider_connection_id = child_source.provider_connection_id
                     AND parent_source.external_id = child_source.parent_external_id
                    INNER JOIN tasks parent_task
                      ON parent_task.id = parent_source.adopted_task_id
                    WHERE child_source.adopted_task_id = tasks.id
                      AND child_source.parent_external_id IS NOT NULL
                      AND parent_source.adopted_task_id IS NOT NULL
                      AND parent_source.adopted_task_id != tasks.id
                      AND (parent_source.parent_external_id IS NULL OR TRIM(parent_source.parent_external_id) = '')
                      AND (parent_task.parent_id IS NULL OR TRIM(parent_task.parent_id) = '')
                )
            """, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSpacesCompatibilityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, """
            CREATE TABLE IF NOT EXISTS spaces (
              id TEXT PRIMARY KEY NOT NULL,
              name TEXT NOT NULL,
              color TEXT DEFAULT '#808080',
              icon TEXT,
              sort_order INTEGER NOT NULL DEFAULT 0,
              is_archived INTEGER NOT NULL DEFAULT 0,
              created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
              updated_at INTEGER
            )
            """, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureIntegrationsCompatibilityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "integrations", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await AddColumnIfMissingAsync(connection, "integrations", "display_name", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "integrations", "color", "TEXT DEFAULT '#808080'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "integrations", "icon", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "integrations", "logo_path", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "integrations", "is_active", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "integrations", "is_configured", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "integrations", "config", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "integrations", "last_sync_at", "INTEGER", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "integrations", "sync_token", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "integrations", "created_at", "INTEGER", cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection, """
            UPDATE integrations
            SET display_name = COALESCE(NULLIF(display_name, ''), CASE id
                WHEN 'todoist' THEN 'Todoist'
                WHEN 'msToDo' THEN 'Microsoft To Do'
                WHEN 'obsidian' THEN 'Obsidian'
                ELSE 'Openza Tasks'
            END),
            color = COALESCE(color, '#808080'),
            created_at = COALESCE(created_at, strftime('%s', 'now'))
            """, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureProjectsCompatibilityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "projects", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await AddColumnIfMissingAsync(connection, "projects", "external_id", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "projects", "space_id", $"TEXT NOT NULL DEFAULT '{SpaceIds.Default}'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "projects", "integration_id", "TEXT DEFAULT 'openza_tasks'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "projects", "provider_connection_id", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "projects", "description", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "projects", "color", "TEXT DEFAULT '#808080'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "projects", "icon", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "projects", "parent_id", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "projects", "sort_order", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "projects", "is_favorite", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "projects", "is_archived", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "projects", "status", "TEXT NOT NULL DEFAULT 'active'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "projects", "provider_metadata", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "projects", "created_at", "INTEGER", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "projects", "updated_at", "INTEGER", cancellationToken).ConfigureAwait(false);

        var columns = await GetColumnNamesAsync(connection, "projects", cancellationToken).ConfigureAwait(false);
        if (columns.Contains("integrations"))
        {
            await ExecuteNonQueryAsync(connection, "UPDATE projects SET provider_metadata = integrations WHERE provider_metadata IS NULL", cancellationToken).ConfigureAwait(false);
        }

        await ExecuteNonQueryAsync(connection, """
            UPDATE projects
            SET space_id = COALESCE(NULLIF(space_id, ''), 'space_default'),
                integration_id = COALESCE(NULLIF(integration_id, ''), 'openza_tasks'),
                color = COALESCE(color, '#808080'),
                sort_order = COALESCE(sort_order, 0),
                is_favorite = COALESCE(is_favorite, 0),
                is_archived = COALESCE(is_archived, 0),
                status = CASE WHEN COALESCE(is_archived, 0) = 1 THEN 'archived' ELSE COALESCE(NULLIF(status, ''), 'active') END,
                created_at = COALESCE(created_at, strftime('%s', 'now'))
            """, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureTasksCompatibilityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "tasks", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // Column creation and value conversion must succeed together. Otherwise
        // a retry could mistake newly added defaults for existing user choices.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var initialColumns = await GetColumnNamesAsync(connection, "tasks", cancellationToken).ConfigureAwait(false);
        var hasLegacyStatusColumn = initialColumns.Contains("status");
        var hadWorkflowStatus = initialColumns.Contains("workflow_status");
        var hadCompletionState = initialColumns.Contains("completion_state");

        await AddColumnIfMissingAsync(connection, "tasks", "external_id", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "space_id", $"TEXT NOT NULL DEFAULT '{SpaceIds.Default}'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "integration_id", "TEXT DEFAULT 'openza_tasks'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "description", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "source_description", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "project_id", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "parent_id", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "priority", "INTEGER NOT NULL DEFAULT 2", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "source_integration_id", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "source_connection_id", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "source_external_id", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "source_provider_task_id", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "source_url", "TEXT", cancellationToken).ConfigureAwait(false);
        if (hasLegacyStatusColumn)
        {
            await AddColumnIfMissingAsync(connection, "tasks", "status", "TEXT DEFAULT 'none'", cancellationToken).ConfigureAwait(false);
        }
        await AddColumnIfMissingAsync(connection, "tasks", "completion_state", "TEXT NOT NULL DEFAULT 'open'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "workflow_status", "TEXT NOT NULL DEFAULT 'none'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "planned_on", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "planned_at", "INTEGER", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "deadline_on", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "deadline_at", "INTEGER", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "due_date", "INTEGER", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "due_time", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "planned_date", "INTEGER", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "scheduled_start", "INTEGER", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "scheduled_end", "INTEGER", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "duration_minutes", "INTEGER", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "recurrence_rule", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "notes", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "provider_metadata", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "source_metadata", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "local_metadata", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "provider_connection_id", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "created_at", "INTEGER", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "updated_at", "INTEGER", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "completed_at", "INTEGER", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "revision", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection, """
            UPDATE tasks
            SET source_description = description,
                description = NULL
            WHERE description IS NOT NULL
              AND TRIM(description) != ''
              AND (source_description IS NULL OR TRIM(source_description) = '')
              AND source_integration_id IS NOT NULL
              AND TRIM(source_integration_id) != ''
              AND source_external_id IS NOT NULL
              AND TRIM(source_external_id) != ''
            """, cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection, """
            UPDATE tasks
            SET notes = CASE
                    WHEN notes IS NULL OR TRIM(notes) = '' THEN description
                    ELSE notes || char(10) || char(10) || '---' || char(10) || description
                END,
                description = NULL
            WHERE description IS NOT NULL
              AND TRIM(description) != ''
            """, cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection, """
            UPDATE tasks
            SET planned_on = COALESCE(
                    planned_on,
                    CASE WHEN due_date IS NOT NULL THEN date(due_date, 'unixepoch', 'localtime') END,
                    CASE WHEN planned_date IS NOT NULL THEN date(planned_date, 'unixepoch', 'localtime') END,
                    CASE WHEN scheduled_start IS NOT NULL THEN date(scheduled_start, 'unixepoch', 'localtime') END
                ),
                planned_at = COALESCE(
                    planned_at,
                    CASE
                        WHEN due_date IS NOT NULL
                             AND (due_time IS NOT NULL OR strftime('%H:%M', due_date, 'unixepoch', 'localtime') != '00:00')
                        THEN due_date
                    END
                )
            WHERE planned_on IS NULL OR planned_at IS NULL
            """, cancellationToken).ConfigureAwait(false);

        await NormalizeStoredDatePairsAsync(connection, "tasks", cancellationToken).ConfigureAwait(false);

        var columns = await GetColumnNamesAsync(connection, "tasks", cancellationToken).ConfigureAwait(false);
        if (columns.Contains("integrations"))
        {
            await ExecuteNonQueryAsync(connection, "UPDATE tasks SET provider_metadata = integrations WHERE provider_metadata IS NULL", cancellationToken).ConfigureAwait(false);
        }

        if (hasLegacyStatusColumn)
        {
            // Legacy status is only authoritative while introducing the modern
            // fields (or repairing empty values). Replaying it on every startup
            // would overwrite later workflow edits and reopen operations.
            await ExecuteNonQueryAsync(connection, $"""
                UPDATE tasks
                SET space_id = COALESCE(NULLIF(space_id, ''), 'space_default'),
                    integration_id = COALESCE(NULLIF(integration_id, ''), 'openza_tasks'),
                    priority = COALESCE(priority, 2),
                    status = COALESCE(NULLIF(status, ''), 'none'),
                    completion_state = CASE
                        WHEN {(hadCompletionState ? "0" : "1")} = 1 OR completion_state IS NULL OR completion_state = '' THEN
                            CASE
                                WHEN status IN ('completed', 'done') THEN 'completed'
                                WHEN status IN ('cancelled', 'canceled') THEN 'cancelled'
                                ELSE 'open'
                            END
                        ELSE completion_state
                    END,
                    workflow_status = CASE
                        WHEN {(hadWorkflowStatus ? "0" : "1")} = 1 OR workflow_status IS NULL OR workflow_status = '' THEN
                            CASE
                                WHEN status IN ('inbox', 'next', 'waiting', 'someday') THEN status
                                WHEN status IN ('pending', 'active', 'in_progress', 'inProgress', 'none') OR status IS NULL OR status = '' THEN 'inbox'
                                ELSE 'none'
                            END
                        ELSE workflow_status
                    END,
                    created_at = COALESCE(created_at, strftime('%s', 'now'))
                """, cancellationToken).ConfigureAwait(false);

            await ExecuteNonQueryAsync(connection, """
                UPDATE tasks
                SET status = CASE
                        WHEN status IN ('pending', 'active', 'in_progress', 'inProgress') THEN 'none'
                        WHEN status = 'done' THEN 'completed'
                        ELSE status
                    END,
                    project_id = NULLIF(project_id, 'proj_inbox')
                """, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ExecuteNonQueryAsync(connection, """
                UPDATE tasks
                SET space_id = COALESCE(NULLIF(space_id, ''), 'space_default'),
                    integration_id = COALESCE(NULLIF(integration_id, ''), 'openza_tasks'),
                    priority = COALESCE(priority, 2),
                    completion_state = COALESCE(NULLIF(completion_state, ''), 'open'),
                    workflow_status = COALESCE(NULLIF(workflow_status, ''), 'none'),
                    project_id = NULLIF(project_id, 'proj_inbox'),
                    created_at = COALESCE(created_at, strftime('%s', 'now'))
                """, cancellationToken).ConfigureAwait(false);
        }

        if (await TableExistsAsync(connection, "projects", cancellationToken).ConfigureAwait(false))
        {
            await ExecuteNonQueryAsync(connection, "DELETE FROM projects WHERE id = 'proj_inbox'", cancellationToken).ConfigureAwait(false);
        }

        await ExecuteNonQueryAsync(connection, """
            UPDATE tasks
            SET workflow_status = 'someday',
                updated_at = COALESCE(updated_at, strftime('%s', 'now'))
            WHERE completion_state = 'open'
              AND workflow_status = 'inbox'
              AND recurrence_rule IS NOT NULL
              AND TRIM(recurrence_rule) != ''
              AND (
                    planned_on IS NOT NULL OR
                    planned_at IS NOT NULL OR
                    deadline_on IS NOT NULL OR
                    deadline_at IS NOT NULL OR
                    scheduled_start IS NOT NULL
                  )
            """, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureLabelsCompatibilityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "labels", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await AddColumnIfMissingAsync(connection, "labels", "external_id", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "labels", "integration_id", "TEXT DEFAULT 'openza_tasks'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "labels", "provider_connection_id", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "labels", "color", "TEXT DEFAULT '#808080'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "labels", "description", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "labels", "sort_order", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "labels", "is_favorite", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "labels", "provider_metadata", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "labels", "created_at", "INTEGER", cancellationToken).ConfigureAwait(false);

        var columns = await GetColumnNamesAsync(connection, "labels", cancellationToken).ConfigureAwait(false);
        if (columns.Contains("integrations"))
        {
            await ExecuteNonQueryAsync(connection, "UPDATE labels SET provider_metadata = integrations WHERE provider_metadata IS NULL", cancellationToken).ConfigureAwait(false);
        }

        await ExecuteNonQueryAsync(connection, """
            UPDATE labels
            SET integration_id = COALESCE(NULLIF(integration_id, ''), 'openza_tasks'),
                color = COALESCE(color, '#808080'),
                sort_order = COALESCE(sort_order, 0),
                is_favorite = COALESCE(is_favorite, 0),
                created_at = COALESCE(created_at, strftime('%s', 'now'))
            """, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddColumnIfMissingAsync(SqliteConnection connection, string tableName, string columnName, string definition, CancellationToken cancellationToken)
    {
        var columns = await GetColumnNamesAsync(connection, tableName, cancellationToken).ConfigureAwait(false);
        if (columns.Contains(columnName))
        {
            return;
        }

        await ExecuteNonQueryAsync(connection, $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition}", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name";
        command.Parameters.AddWithValue("@name", tableName);
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        return count > 0;
    }

    private static async Task<string?> ResolveProviderSourceSpaceIdAsync(
        SqliteConnection connection,
        string? existingSpaceId,
        string? incomingSpaceId,
        CancellationToken cancellationToken)
    {
        if (await ActiveSpaceExistsAsync(connection, existingSpaceId, cancellationToken).ConfigureAwait(false))
        {
            return existingSpaceId;
        }

        if (await ActiveSpaceExistsAsync(connection, incomingSpaceId, cancellationToken).ConfigureAwait(false))
        {
            return incomingSpaceId;
        }

        return null;
    }

    private static async Task<string> ResolveTaskCreationSpaceIdAsync(SqliteConnection connection, string? requestedSpaceId, CancellationToken cancellationToken)
    {
        if (await ActiveSpaceExistsAsync(connection, requestedSpaceId, cancellationToken).ConfigureAwait(false))
        {
            return requestedSpaceId!;
        }

        return await ReadFirstActiveSpaceIdAsync(connection, cancellationToken).ConfigureAwait(false) ?? SpaceIds.Default;
    }

    private static async Task<bool> ActiveSpaceExistsAsync(SqliteConnection connection, string? spaceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spaceId))
        {
            return false;
        }

        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM spaces WHERE id = @id AND is_archived = 0";
        command.Parameters.AddWithValue("@id", spaceId);
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        return count > 0;
    }

    private static async Task<string?> ReadFirstActiveSpaceIdAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id
            FROM spaces
            WHERE is_archived = 0
            ORDER BY sort_order, name
            LIMIT 1
            """;

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static async Task<HashSet<string>> GetColumnNamesAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task InsertDefaultDataAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO workspaces (id, name, created_at)
            VALUES ('default', 'My workspace', strftime('%s', 'now'));

            INSERT OR IGNORE INTO spaces (id, name, color, icon, sort_order, is_archived, created_at)
            SELECT 'space_default', 'My space', '#808080', 'Folder', 0, 0, strftime('%s', 'now')
            WHERE NOT EXISTS (SELECT 1 FROM spaces);

            INSERT OR IGNORE INTO integrations (id, name, display_name, color, icon, logo_path, is_active, is_configured, created_at)
            VALUES
              ('openza_tasks', 'openza_tasks', 'Openza Tasks', '#6366f1', 'database', NULL, 1, 1, strftime('%s', 'now')),
              ('todoist', 'todoist', 'Todoist', '#E44332', 'check-circle', 'assets/logos/todoist.svg', 0, 0, strftime('%s', 'now')),
              ('msToDo', 'msToDo', 'Microsoft To Do', '#00A4EF', 'layout-grid', 'assets/logos/microsoft.svg', 0, 0, strftime('%s', 'now')),
              ('github', 'github', 'GitHub', '#24292f', 'code', NULL, 0, 0, strftime('%s', 'now'));

            INSERT OR IGNORE INTO provider_connections (id, workspace_id, integration_id, display_name, status, created_at)
            VALUES
              ('local_default', 'default', 'openza_tasks', 'Openza Tasks', 'connected', strftime('%s', 'now')),
              ('todoist_default', 'default', 'todoist', 'Todoist', 'disconnected', strftime('%s', 'now')),
              ('mstodo_default', 'default', 'msToDo', 'Microsoft To Do', 'disconnected', strftime('%s', 'now')),
              ('github_default', 'default', 'github', 'GitHub', 'disconnected', strftime('%s', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsurePendingCompletionsTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS pending_completions (
              id TEXT PRIMARY KEY NOT NULL,
              task_id TEXT NOT NULL,
              provider TEXT NOT NULL,
              provider_task_id TEXT NOT NULL,
              completed INTEGER NOT NULL DEFAULT 1,
              completed_at INTEGER,
              created_at INTEGER NOT NULL,
              retry_count INTEGER NOT NULL DEFAULT 0
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsurePendingTaskDateUpdatesTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS pending_task_date_updates (
              id TEXT PRIMARY KEY NOT NULL,
              task_id TEXT NOT NULL,
              provider TEXT NOT NULL,
              provider_task_id TEXT NOT NULL,
              planned_on TEXT,
              planned_at INTEGER,
              created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
              retry_count INTEGER NOT NULL DEFAULT 0,
              UNIQUE(provider, provider_task_id)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecutePragmaAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task NormalizeStoredDatePairsAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        if (tableName is not "tasks" and not "provider_source_items")
        {
            throw new ArgumentOutOfRangeException(nameof(tableName), tableName, "Unsupported date-pair table.");
        }

        await ExecuteNonQueryAsync(connection, $"""
            UPDATE {tableName}
            SET planned_on = date(planned_at, 'unixepoch', 'localtime')
            WHERE planned_on IS NULL
              AND planned_at IS NOT NULL
            """, cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection, $"""
            UPDATE {tableName}
            SET planned_at = NULL
            WHERE planned_on IS NOT NULL
              AND planned_at IS NOT NULL
              AND planned_on != date(planned_at, 'unixepoch', 'localtime')
            """, cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection, $"""
            UPDATE {tableName}
            SET deadline_on = date(deadline_at, 'unixepoch', 'localtime')
            WHERE deadline_on IS NULL
              AND deadline_at IS NOT NULL
            """, cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection, $"""
            UPDATE {tableName}
            SET deadline_at = NULL
            WHERE deadline_on IS NOT NULL
              AND deadline_at IS NOT NULL
              AND deadline_on != date(deadline_at, 'unixepoch', 'localtime')
            """, cancellationToken).ConfigureAwait(false);
    }

    private static bool Contains(string? value, string search) =>
        value?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true;

    private static IReadOnlyList<string> SplitSearchTokens(string? searchText) =>
        string.IsNullOrWhiteSpace(searchText)
            ? []
            : searchText
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();

    private static bool MatchesSearchTokens(IReadOnlyList<string> tokens, params string?[] values) =>
        tokens.All(token => values.Any(value => Contains(value, token)));

    private static int CalculateSearchScore(string searchText, IReadOnlyList<string> tokens, string title, params string?[] bodyValues)
    {
        var normalizedSearch = searchText.Trim();
        if (title.Equals(normalizedSearch, StringComparison.CurrentCultureIgnoreCase))
        {
            return 1000;
        }

        if (title.StartsWith(normalizedSearch, StringComparison.CurrentCultureIgnoreCase))
        {
            return 900;
        }

        if (title.Contains(normalizedSearch, StringComparison.CurrentCultureIgnoreCase))
        {
            return 800;
        }

        var titleTokenScore = tokens.Any(token => title.StartsWith(token, StringComparison.CurrentCultureIgnoreCase))
            ? 700
            : tokens.Any(token => title.Contains(token, StringComparison.CurrentCultureIgnoreCase))
                ? 600
                : 0;
        if (titleTokenScore > 0)
        {
            return titleTokenScore;
        }

        return bodyValues.Any(value => tokens.Any(token => Contains(value, token))) ? 300 : 0;
    }

    private static string BuildTaskSearchSubtitle(TaskItem task, string? projectName)
    {
        var state = task.IsCompleted ? "Completed task" : "Task";
        return string.IsNullOrWhiteSpace(projectName) ? state : $"{state} · {projectName}";
    }

    private static string BuildSearchSnippet(IReadOnlyList<string> tokens, params string?[] values)
    {
        var value = values
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .FirstOrDefault(text => tokens.Any(token => Contains(text, token)));
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim().ReplaceLineEndings(" ");
        return trimmed.Length <= 140 ? trimmed : $"{trimmed[..137]}...";
    }

    private static string? GetNullableString(IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int ReadNullableInt(IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    private static object ToDbValue(string? value) => string.IsNullOrEmpty(value) ? DBNull.Value : value;

    private static object ToDbValue(DateOnly? value) => value is null ? DBNull.Value : TaskDateValues.ToStorageValue(value.Value)!;

    private static object ToDbValue(DateTimeOffset? value) => value is null ? DBNull.Value : ToDbDate(value.Value);

    private static long ToDbDate(DateTimeOffset value) => value.ToUnixTimeSeconds();

    // Editable Openza task instants keep protocol millisecond precision without
    // rewriting existing INTEGER epochs or changing other tables' storage units.
    private static object ToDbTaskInstant(DateTimeOffset? value)
    {
        if (value is null) return DBNull.Value;
        var milliseconds = value.Value.ToUnixTimeMilliseconds();
        return milliseconds % 1000 == 0 ? (object)(milliseconds / 1000) : milliseconds / 1000d;
    }

    private static (DateOnly? Date, DateTimeOffset? ExactTime) NormalizeDatePair(DateOnly? date, DateTimeOffset? exactTime)
    {
        if (exactTime is null)
        {
            return (date, null);
        }

        var exactDate = TaskDateValues.FromDateTimeOffset(exactTime);
        if (date is null)
        {
            return (exactDate, exactTime);
        }

        return date == exactDate ? (date, exactTime) : (date, null);
    }

    private static DateTimeOffset? ReadDate(IDataRecord reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        if (reader.GetValue(ordinal) is double precise)
            return DateTimeOffset.FromUnixTimeMilliseconds(checked((long)Math.Round(precise * 1000, MidpointRounding.AwayFromZero)));
        var value = reader.GetInt64(ordinal);
        return value > 100_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : DateTimeOffset.FromUnixTimeSeconds(value);
    }

    private static DateOnly? ReadDateOnly(IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : TaskDateValues.FromStorageValue(reader.GetString(ordinal));

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS integrations (
          id TEXT PRIMARY KEY NOT NULL,
          name TEXT NOT NULL,
          display_name TEXT NOT NULL,
          color TEXT DEFAULT '#808080',
          icon TEXT,
          logo_path TEXT,
          is_active INTEGER NOT NULL DEFAULT 0,
          is_configured INTEGER NOT NULL DEFAULT 0,
          config TEXT,
          last_sync_at INTEGER,
          sync_token TEXT,
          created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now'))
        );

        CREATE TABLE IF NOT EXISTS workspaces (
          id TEXT PRIMARY KEY NOT NULL,
          name TEXT NOT NULL,
          created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now'))
        );

        CREATE TABLE IF NOT EXISTS spaces (
          id TEXT PRIMARY KEY NOT NULL,
          name TEXT NOT NULL,
          color TEXT DEFAULT '#808080',
          icon TEXT,
          sort_order INTEGER NOT NULL DEFAULT 0,
          is_archived INTEGER NOT NULL DEFAULT 0,
          created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
          updated_at INTEGER
        );

        CREATE TABLE IF NOT EXISTS provider_connections (
          id TEXT PRIMARY KEY NOT NULL,
          workspace_id TEXT NOT NULL DEFAULT 'default' REFERENCES workspaces(id),
          integration_id TEXT NOT NULL REFERENCES integrations(id),
          display_name TEXT NOT NULL,
          account_key TEXT,
          status TEXT NOT NULL DEFAULT 'disconnected',
          settings TEXT,
          last_sync_at INTEGER,
          created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
          updated_at INTEGER
        );

        CREATE TABLE IF NOT EXISTS projects (
          id TEXT PRIMARY KEY NOT NULL,
          external_id TEXT,
          space_id TEXT NOT NULL DEFAULT 'space_default' REFERENCES spaces(id),
          integration_id TEXT NOT NULL DEFAULT 'openza_tasks' REFERENCES integrations(id),
          provider_connection_id TEXT REFERENCES provider_connections(id),
          name TEXT NOT NULL,
          description TEXT,
          color TEXT DEFAULT '#808080',
          icon TEXT,
          parent_id TEXT,
          sort_order INTEGER NOT NULL DEFAULT 0,
          is_favorite INTEGER NOT NULL DEFAULT 0,
          is_archived INTEGER NOT NULL DEFAULT 0,
          status TEXT NOT NULL DEFAULT 'active',
          provider_metadata TEXT,
          created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
          updated_at INTEGER
        );

        CREATE TABLE IF NOT EXISTS tasks (
          id TEXT PRIMARY KEY NOT NULL,
          external_id TEXT,
          space_id TEXT NOT NULL DEFAULT 'space_default' REFERENCES spaces(id),
          integration_id TEXT NOT NULL DEFAULT 'openza_tasks' REFERENCES integrations(id),
          provider_connection_id TEXT REFERENCES provider_connections(id),
          title TEXT NOT NULL,
          description TEXT,
          source_description TEXT,
          project_id TEXT REFERENCES projects(id),
          parent_id TEXT,
          priority INTEGER NOT NULL DEFAULT 2,
          source_integration_id TEXT REFERENCES integrations(id),
          source_connection_id TEXT REFERENCES provider_connections(id),
          source_external_id TEXT,
          source_provider_task_id TEXT,
          source_url TEXT,
          completion_state TEXT NOT NULL DEFAULT 'open',
          workflow_status TEXT NOT NULL DEFAULT 'none',
          planned_on TEXT,
          planned_at INTEGER,
          deadline_on TEXT,
          deadline_at INTEGER,
          scheduled_start INTEGER,
          scheduled_end INTEGER,
          duration_minutes INTEGER,
          recurrence_rule TEXT,
          notes TEXT,
          provider_metadata TEXT,
          source_metadata TEXT,
          local_metadata TEXT,
          created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
          updated_at INTEGER,
          completed_at INTEGER
          , revision INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS provider_source_items (
          id TEXT PRIMARY KEY NOT NULL,
          integration_id TEXT NOT NULL REFERENCES integrations(id),
          provider_connection_id TEXT NOT NULL REFERENCES provider_connections(id),
          external_id TEXT NOT NULL,
          provider_task_id TEXT NOT NULL,
          title TEXT NOT NULL,
          description TEXT,
          source_project_id TEXT,
          source_project_name TEXT,
          parent_external_id TEXT,
          suggested_space_id TEXT REFERENCES spaces(id),
          priority INTEGER NOT NULL DEFAULT 2,
          completion_state TEXT NOT NULL DEFAULT 'open',
          planned_on TEXT,
          planned_at INTEGER,
          deadline_on TEXT,
          deadline_at INTEGER,
          recurrence_rule TEXT,
          source_url TEXT,
          snapshot_json TEXT,
          adoption_state TEXT NOT NULL DEFAULT 'not_adopted',
          adopted_task_id TEXT REFERENCES tasks(id),
          first_seen_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
          last_seen_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
          updated_at INTEGER,
          UNIQUE(provider_connection_id, external_id)
        );

        CREATE TABLE IF NOT EXISTS labels (
          id TEXT PRIMARY KEY NOT NULL,
          external_id TEXT,
          integration_id TEXT NOT NULL DEFAULT 'openza_tasks' REFERENCES integrations(id),
          provider_connection_id TEXT REFERENCES provider_connections(id),
          name TEXT NOT NULL,
          color TEXT DEFAULT '#808080',
          description TEXT,
          sort_order INTEGER NOT NULL DEFAULT 0,
          is_favorite INTEGER NOT NULL DEFAULT 0,
          provider_metadata TEXT,
          created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now'))
        );

        CREATE TABLE IF NOT EXISTS task_labels (
          task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
          label_id TEXT NOT NULL REFERENCES labels(id) ON DELETE CASCADE,
          PRIMARY KEY (task_id, label_id)
        );

        CREATE TABLE IF NOT EXISTS time_entries (
          id TEXT PRIMARY KEY NOT NULL,
          task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
          start_time INTEGER NOT NULL,
          end_time INTEGER,
          duration INTEGER,
          description TEXT,
          energy_used INTEGER,
          focus_quality INTEGER,
          created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now'))
        );

        CREATE TABLE IF NOT EXISTS task_enhancements (
          id TEXT PRIMARY KEY NOT NULL,
          task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
          type TEXT NOT NULL,
          content TEXT NOT NULL,
          sort_order INTEGER NOT NULL DEFAULT 0,
          completed INTEGER NOT NULL DEFAULT 0,
          created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now'))
        );

        CREATE TABLE IF NOT EXISTS task_external_links (
          id TEXT PRIMARY KEY NOT NULL,
          task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
          integration_id TEXT NOT NULL REFERENCES integrations(id),
          connection_id TEXT REFERENCES provider_connections(id),
          external_id TEXT NOT NULL,
          kind TEXT NOT NULL DEFAULT 'issue',
          display_name TEXT NOT NULL,
          url TEXT NOT NULL,
          metadata TEXT,
          created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
          updated_at INTEGER,
          UNIQUE(task_id, integration_id, external_id)
        );

        CREATE TABLE IF NOT EXISTS sync_routes (
          id TEXT PRIMARY KEY NOT NULL,
          workspace_id TEXT NOT NULL DEFAULT 'default' REFERENCES workspaces(id),
          name TEXT NOT NULL,
          source_connection_id TEXT REFERENCES provider_connections(id),
          target_connection_id TEXT REFERENCES provider_connections(id),
          mode TEXT NOT NULL DEFAULT 'one_way',
          visibility TEXT NOT NULL DEFAULT 'optional',
          schedule TEXT,
          is_enabled INTEGER NOT NULL DEFAULT 0,
          settings TEXT,
          created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
          updated_at INTEGER
        );

        CREATE TABLE IF NOT EXISTS sync_route_mappings (
          id TEXT PRIMARY KEY NOT NULL,
          route_id TEXT NOT NULL REFERENCES sync_routes(id) ON DELETE CASCADE,
          source_field TEXT NOT NULL,
          target_field TEXT NOT NULL,
          policy TEXT NOT NULL DEFAULT 'source_wins',
          transform TEXT
        );

        CREATE TABLE IF NOT EXISTS sync_item_links (
          id TEXT PRIMARY KEY NOT NULL,
          route_id TEXT NOT NULL REFERENCES sync_routes(id) ON DELETE CASCADE,
          source_connection_id TEXT REFERENCES provider_connections(id),
          source_external_id TEXT NOT NULL,
          local_task_id TEXT REFERENCES tasks(id),
          target_connection_id TEXT REFERENCES provider_connections(id),
          target_external_id TEXT,
          target_kind TEXT NOT NULL DEFAULT 'task',
          state TEXT NOT NULL DEFAULT 'active',
          created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
          updated_at INTEGER
        );

        CREATE TABLE IF NOT EXISTS sync_field_state (
          id TEXT PRIMARY KEY NOT NULL,
          item_link_id TEXT NOT NULL REFERENCES sync_item_links(id) ON DELETE CASCADE,
          field_key TEXT NOT NULL,
          source_hash TEXT,
          target_hash TEXT,
          last_written_hash TEXT,
          authority_policy TEXT NOT NULL DEFAULT 'source_wins',
          conflict_state TEXT NOT NULL DEFAULT 'none',
          updated_at INTEGER
        );

        CREATE TABLE IF NOT EXISTS sync_operations (
          id TEXT PRIMARY KEY NOT NULL,
          route_id TEXT REFERENCES sync_routes(id) ON DELETE CASCADE,
          connection_id TEXT REFERENCES provider_connections(id),
          item_link_id TEXT REFERENCES sync_item_links(id),
          operation_type TEXT NOT NULL,
          payload TEXT,
          status TEXT NOT NULL DEFAULT 'pending',
          retry_count INTEGER NOT NULL DEFAULT 0,
          last_error TEXT,
          created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
          updated_at INTEGER
        );

        CREATE TABLE IF NOT EXISTS sync_runs (
          id TEXT PRIMARY KEY NOT NULL,
          route_id TEXT REFERENCES sync_routes(id) ON DELETE SET NULL,
          connection_id TEXT REFERENCES provider_connections(id),
          started_at INTEGER NOT NULL,
          finished_at INTEGER,
          status TEXT NOT NULL,
          summary TEXT,
          error TEXT
        );

        CREATE TABLE IF NOT EXISTS personal_cloud_device_state (
          instance_id TEXT NOT NULL,
          device_id TEXT NOT NULL,
          next_device_sequence INTEGER NOT NULL CHECK (next_device_sequence > 0),
          PRIMARY KEY (instance_id, device_id)
        );

        CREATE TABLE IF NOT EXISTS personal_cloud_outbox (
          local_sequence INTEGER PRIMARY KEY AUTOINCREMENT,
          mutation_id TEXT NOT NULL UNIQUE,
          product TEXT NOT NULL,
          instance_id TEXT NOT NULL,
          device_id TEXT NOT NULL,
          device_sequence INTEGER NOT NULL CHECK (device_sequence > 0),
          operation TEXT NOT NULL,
          entity_id TEXT NOT NULL,
          canonical_json TEXT NOT NULL,
          canonical_hash TEXT NOT NULL CHECK (length(canonical_hash) = 64),
          created_at INTEGER NOT NULL,
          acknowledged_at INTEGER,
          UNIQUE (instance_id, device_id, device_sequence)
        );

        CREATE TABLE IF NOT EXISTS personal_cloud_checkpoints (
          instance_id TEXT NOT NULL,
          device_id TEXT NOT NULL,
          product TEXT NOT NULL CHECK (product = 'tasks'),
          last_change_seq INTEGER NOT NULL CHECK (last_change_seq >= 0),
          updated_at INTEGER NOT NULL,
          PRIMARY KEY (instance_id, device_id, product)
        );

        CREATE TABLE IF NOT EXISTS personal_cloud_provider_sources (
          instance_id TEXT NOT NULL, task_id TEXT NOT NULL, source_json TEXT NOT NULL,
          PRIMARY KEY(instance_id,task_id)
        );
        CREATE TABLE IF NOT EXISTS personal_cloud_provider_commands (
          instance_id TEXT NOT NULL, task_id TEXT NOT NULL, requested_seq INTEGER NOT NULL CHECK(requested_seq>0),
          command TEXT NOT NULL CHECK(command IN ('complete','reopen')),
          state TEXT NOT NULL CHECK(state IN ('pending_desktop','confirmed','rejected','cancelled')),
          error_code TEXT, retry_id TEXT,
          PRIMARY KEY(instance_id,task_id)
        );
        CREATE TABLE IF NOT EXISTS personal_cloud_provider_attempts (
          claim_id TEXT PRIMARY KEY, instance_id TEXT NOT NULL, task_id TEXT NOT NULL,
          requested_seq INTEGER NOT NULL, device_id TEXT NOT NULL, account_binding TEXT NOT NULL,
          command TEXT NOT NULL, source_json TEXT NOT NULL,
          state TEXT NOT NULL CHECK(state IN ('claiming','reserved','starting','dispatching','uncertain','outcome','settled','cancelled')),
          result_mutation_id TEXT,
          UNIQUE(instance_id,task_id,requested_seq,device_id)
        );
        CREATE TABLE IF NOT EXISTS personal_cloud_deferred_changes (
          instance_id TEXT NOT NULL,
          product TEXT NOT NULL CHECK (product = 'tasks'),
          change_seq INTEGER NOT NULL CHECK (change_seq > 0),
          entity_id TEXT NOT NULL,
          canonical_json TEXT NOT NULL,
          PRIMARY KEY(instance_id,product,change_seq)
        );

        CREATE TABLE IF NOT EXISTS personal_cloud_applied_changes (
          instance_id TEXT NOT NULL,
          product TEXT NOT NULL CHECK (product = 'tasks'),
          change_seq INTEGER NOT NULL CHECK (change_seq > 0),
          mutation_id TEXT NOT NULL,
          entity_id TEXT NOT NULL,
          applied_at INTEGER NOT NULL,
          PRIMARY KEY (instance_id, product, change_seq),
          UNIQUE (instance_id, product, mutation_id)
        );

        CREATE TABLE IF NOT EXISTS personal_cloud_tombstones (
          instance_id TEXT NOT NULL,
          product TEXT NOT NULL CHECK (product = 'tasks'),
          entity_id TEXT NOT NULL,
          entity_version INTEGER NOT NULL CHECK (entity_version > 0),
          deleted_at INTEGER NOT NULL,
          purge_after INTEGER,
          PRIMARY KEY (instance_id, product, entity_id)
        );

        CREATE TABLE IF NOT EXISTS personal_cloud_replica_entities (
          instance_id TEXT NOT NULL,
          product TEXT NOT NULL CHECK (product = 'tasks'),
          entity_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
          PRIMARY KEY(instance_id,product,entity_id)
        );

        CREATE TABLE IF NOT EXISTS personal_cloud_entity_versions (
          instance_id TEXT NOT NULL,
          product TEXT NOT NULL CHECK (product = 'tasks'),
          entity_id TEXT NOT NULL,
          entity_version INTEGER NOT NULL CHECK (entity_version > 0),
          updated_at INTEGER NOT NULL,
          PRIMARY KEY(instance_id,product,entity_id)
        );

        CREATE INDEX IF NOT EXISTS idx_tasks_integration_id ON tasks(integration_id);
        CREATE INDEX IF NOT EXISTS idx_tasks_provider_connection_id ON tasks(provider_connection_id);
        CREATE INDEX IF NOT EXISTS idx_tasks_source ON tasks(source_connection_id, source_external_id);
        CREATE INDEX IF NOT EXISTS idx_tasks_parent_id ON tasks(parent_id);
        CREATE INDEX IF NOT EXISTS idx_tasks_project_id ON tasks(project_id);
        CREATE INDEX IF NOT EXISTS idx_tasks_completion_state ON tasks(completion_state);
        CREATE INDEX IF NOT EXISTS idx_tasks_workflow_status ON tasks(workflow_status);
        CREATE INDEX IF NOT EXISTS idx_tasks_planned_on ON tasks(planned_on);
        CREATE INDEX IF NOT EXISTS idx_tasks_planned_at ON tasks(planned_at);
        CREATE INDEX IF NOT EXISTS idx_tasks_deadline_on ON tasks(deadline_on);
        CREATE INDEX IF NOT EXISTS idx_tasks_deadline_at ON tasks(deadline_at);
        CREATE INDEX IF NOT EXISTS idx_tasks_scheduled_start ON tasks(scheduled_start);
        CREATE INDEX IF NOT EXISTS idx_projects_integration_id ON projects(integration_id);
        CREATE INDEX IF NOT EXISTS idx_labels_integration_id ON labels(integration_id);
        CREATE INDEX IF NOT EXISTS idx_provider_source_items_connection ON provider_source_items(provider_connection_id, adoption_state);
        CREATE INDEX IF NOT EXISTS idx_provider_source_items_parent ON provider_source_items(provider_connection_id, parent_external_id);
        CREATE INDEX IF NOT EXISTS idx_task_external_links_task ON task_external_links(task_id, integration_id);
        CREATE INDEX IF NOT EXISTS idx_sync_item_links_route_source ON sync_item_links(route_id, source_external_id);
        CREATE INDEX IF NOT EXISTS idx_personal_cloud_outbox_pending
          ON personal_cloud_outbox(product, instance_id, device_id, acknowledged_at, device_sequence);
        """;
}
