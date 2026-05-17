using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Data;

public sealed class SqliteTaskStore(string databasePath) : ITaskStore
{
    private sealed record ParentTaskContext(string Id, string SpaceId, string? ProjectId, TaskWorkflowStatus WorkflowStatus);

    public string DatabasePath { get; } = databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? ".");
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureLegacySchemaCompatibilityAsync(connection, cancellationToken).ConfigureAwait(false);
        await CreateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await InsertDefaultDataAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TaskItem>> GetTasksAsync(TaskQuery query, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var tasks = await ReadTasksAsync(connection, cancellationToken).ConfigureAwait(false);
        var today = DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime);

        IEnumerable<TaskItem> filtered = tasks.Where(t => t.IntegrationId == IntegrationIds.Local);
        if (!string.IsNullOrWhiteSpace(query.SpaceId))
        {
            filtered = filtered.Where(t => t.SpaceId == query.SpaceId);
        }

        filtered = query.Kind switch
        {
            TaskListKind.Inbox => filtered.Where(t => t.IsOpen && t.WorkflowStatus == TaskWorkflowStatus.Inbox && string.IsNullOrWhiteSpace(t.ProjectId)),
            TaskListKind.NextActions => filtered.Where(t => t.IsOpen && t.WorkflowStatus == TaskWorkflowStatus.Next),
            TaskListKind.Waiting => filtered.Where(t => t.IsOpen && t.WorkflowStatus == TaskWorkflowStatus.Waiting),
            TaskListKind.Someday => filtered.Where(t => t.IsOpen && t.WorkflowStatus == TaskWorkflowStatus.Someday),
            TaskListKind.Today => filtered.Where(t => t.IsOpen && HasRelevantDateOn(t, today, query.DateScope)),
            TaskListKind.Calendar => filtered.Where(t => t.IsOpen && HasRelevantDate(t, query.DateScope)),
            TaskListKind.Overdue => filtered.Where(t => t.IsOpen && HasRelevantDateBefore(t, today, query.DateScope)),
            TaskListKind.Open => filtered.Where(t => t.IsOpen),
            TaskListKind.Completed => filtered.Where(t => t.IsCompleted),
            _ => filtered,
        };

        if (query.RepeatScope != TaskRepeatScope.Include)
        {
            filtered = filtered.Where(t => MatchesRepeatScope(t, query.RepeatScope));
        }

        if (!string.IsNullOrWhiteSpace(query.ProjectId))
        {
            filtered = filtered.Where(t => t.ProjectId == query.ProjectId);
        }

        if (!string.IsNullOrWhiteSpace(query.ParentId))
        {
            filtered = filtered.Where(t => t.ParentId == query.ParentId);
        }

        if (!string.IsNullOrWhiteSpace(query.LabelId))
        {
            filtered = filtered.Where(t => t.Labels.Any(label => label.Id == query.LabelId));
        }

        if (query.Priority is not null)
        {
            filtered = filtered.Where(t => t.Priority == query.Priority);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var search = query.SearchText.Trim();
            filtered = filtered.Where(t =>
                Contains(t.Title, search) ||
                Contains(t.Notes, search) ||
                Contains(t.SourceDescription, search));
        }

        filtered = query.SortMode switch
        {
            TaskSortMode.Date => filtered.OrderBy(t => RelevantDate(t, query.DateScope) is null).ThenBy(t => RelevantDate(t, query.DateScope)),
            TaskSortMode.CreatedNewest => filtered.OrderByDescending(t => t.CreatedAt),
            TaskSortMode.Title => filtered.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
            TaskSortMode.Project => filtered.OrderBy(t => t.ProjectId ?? string.Empty).ThenBy(t => t.Priority),
            _ => filtered.OrderBy(t => t.IsCompleted).ThenBy(t => t.Priority).ThenBy(t => RelevantDate(t, query.DateScope) is null).ThenBy(t => RelevantDate(t, query.DateScope)).ThenByDescending(t => t.CreatedAt),
        };

        var sorted = filtered.ToList();
        return query.IncludeSubtasks || !string.IsNullOrWhiteSpace(query.ParentId)
            ? sorted
            : ArrangeWithSubtasks(tasks, sorted, query);
    }

    private static IReadOnlyList<TaskItem> ArrangeWithSubtasks(
        IReadOnlyList<TaskItem> allTasks,
        IReadOnlyList<TaskItem> matchedTasks,
        TaskQuery query)
    {
        var byId = allTasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var childrenByParent = allTasks
            .Where(task => !string.IsNullOrWhiteSpace(task.ParentId))
            .Where(task => task.IntegrationId == IntegrationIds.Local)
            .Where(task => string.IsNullOrWhiteSpace(query.SpaceId) || task.SpaceId == query.SpaceId)
            .GroupBy(task => task.ParentId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(task => task.IsCompleted).ThenBy(task => task.Priority).ThenBy(task => task.CreatedAt).ToList(), StringComparer.Ordinal);
        var matchedIds = matchedTasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        var added = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<TaskItem>();

        foreach (var task in matchedTasks)
        {
            if (!string.IsNullOrWhiteSpace(task.ParentId) && byId.TryGetValue(task.ParentId, out var parent))
            {
                AddTask(parent, includeAllChildren: false);
                continue;
            }

            AddTask(task, includeAllChildren: true);
        }

        return result;

        void AddTask(TaskItem task, bool includeAllChildren)
        {
            if (!added.Add(task.Id))
            {
                return;
            }

            result.Add(task);
            if (!childrenByParent.TryGetValue(task.Id, out var children))
            {
                return;
            }

            foreach (var child in children)
            {
                if (includeAllChildren || matchedIds.Contains(child.Id))
                {
                    AddTask(child, includeAllChildren: true);
                }
            }
        }
    }

    private static bool HasRelevantDate(TaskItem task, TaskDateScope scope)
    {
        return RelevantDates(task, scope).Any();
    }

    private static bool MatchesRepeatScope(TaskItem task, TaskRepeatScope scope)
    {
        var isRepeating = !string.IsNullOrWhiteSpace(task.RecurrenceRule);
        return scope switch
        {
            TaskRepeatScope.Exclude => !isRepeating,
            TaskRepeatScope.Only => isRepeating,
            _ => true,
        };
    }

    private static bool HasRelevantDateOn(TaskItem task, DateOnly date, TaskDateScope scope)
    {
        return RelevantDates(task, scope).Any(value => DateOnly.FromDateTime(value.LocalDateTime) == date);
    }

    private static bool HasRelevantDateBefore(TaskItem task, DateOnly date, TaskDateScope scope)
    {
        return RelevantDates(task, scope).Any(value => DateOnly.FromDateTime(value.LocalDateTime) < date);
    }

    private static DateTimeOffset? RelevantDate(TaskItem task, TaskDateScope scope)
    {
        var dates = RelevantDates(task, scope).ToList();
        return dates.Count == 0 ? null : dates.Min();
    }

    private static IEnumerable<DateTimeOffset> RelevantDates(TaskItem task, TaskDateScope scope)
    {
        if (task.PlannedMoment is { } planned)
        {
            yield return planned;
        }

        if (task.DeadlineMoment is { } deadline)
        {
            yield return deadline;
        }

        if (task.ScheduledStart is { } scheduledStart)
        {
            yield return scheduledStart;
        }
    }

    public async Task<TaskCountSummary> GetTaskCountsAsync(string? spaceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var todayDate = DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime);
        var todayStart = new DateTimeOffset(DateTimeOffset.Now.Date, DateTimeOffset.Now.Offset).ToUnixTimeSeconds();
        var tomorrowStart = new DateTimeOffset(DateTimeOffset.Now.Date.AddDays(1), DateTimeOffset.Now.Offset).ToUnixTimeSeconds();

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
                OR NOT EXISTS (SELECT 1 FROM spaces WHERE spaces.id = provider_source_items.suggested_space_id)
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
        var source = item with
        {
            Id = string.IsNullOrWhiteSpace(item.Id) ? BuildProviderSourceItemId(item.ProviderConnectionId, item.ExternalId) : item.Id,
            AdoptionState = existing?.AdoptionState == ProviderSourceAdoptionStates.Missing
                ? item.AdoptionState
                : existing?.AdoptionState ?? item.AdoptionState,
            AdoptedTaskId = existing?.AdoptedTaskId ?? item.AdoptedTaskId,
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
            var task = CreateTaskFromSource(source, parentContext?.SpaceId ?? source.SuggestedSpaceId ?? SpaceIds.Default, now, parentContext);
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
            await RefreshAdoptedTaskFromSourceAsync(connection, source, parentContext, cancellationToken).ConfigureAwait(false);
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
                    updated_at = @updated_at
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
                adopted_task_id = NULL,
                updated_at = @updated_at
            WHERE id = @id
            """;
        sourceCommand.Parameters.AddWithValue("@id", sourceItemId);
        sourceCommand.Parameters.AddWithValue("@adoption_state", ProviderSourceAdoptionStates.Missing);
        sourceCommand.Parameters.AddWithValue("@updated_at", ToDbDate(DateTimeOffset.UtcNow));
        await sourceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
        var targetSpaceId = parentContext?.SpaceId ?? (string.IsNullOrWhiteSpace(spaceId) ? source.SuggestedSpaceId ?? SpaceIds.Default : spaceId);
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
            RecurrenceRule = source.RecurrenceRule,
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

        return new ParentTaskContext(
            reader.GetString(0),
            reader.GetString(1),
            GetNullableString(reader, 2),
            TaskStatusExtensions.WorkflowFromStorageValue(reader.GetString(3)));
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

    public async Task UpsertProjectAsync(ProjectItem project, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
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
        await ResolveAndUpsertLabelCoreAsync(connection, label, cancellationToken).ConfigureAwait(false);
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
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var linkedProvider = await ReadAdoptedProviderLinkAsync(connection, taskId, cancellationToken).ConfigureAwait(false);
        if (linkedProvider is not null)
        {
            throw new ProviderLinkedTaskDeleteException(taskId, linkedProvider);
        }

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM tasks WHERE id = @id";
        command.Parameters.AddWithValue("@id", taskId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteProjectAsync(string projectId, bool moveTasksToInbox, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var taskCommand = connection.CreateCommand();
        taskCommand.CommandText = moveTasksToInbox
            ? "UPDATE tasks SET project_id = NULL, workflow_status = CASE WHEN completion_state = 'open' THEN 'inbox' ELSE workflow_status END, updated_at = @updated_at WHERE project_id = @project_id"
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

    public async Task MarkCompletionSyncedAsync(string completionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM pending_completions WHERE id = @id";
        command.Parameters.AddWithValue("@id", completionId);
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
        var now = DateTimeOffset.UtcNow;
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tasks
            SET completion_state = @completion_state,
                completed_at = @completed_at,
                updated_at = @updated_at
            WHERE id = @id
            """;
        command.Parameters.AddWithValue("@id", taskId);
        command.Parameters.AddWithValue("@completion_state", completed ? "completed" : "open");
        command.Parameters.AddWithValue("@completed_at", completed ? ToDbDate(now) : DBNull.Value);
        command.Parameters.AddWithValue("@updated_at", ToDbDate(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task RefreshAdoptedTaskFromSourceAsync(SqliteConnection connection, ProviderSourceItem source, ParentTaskContext? parent, CancellationToken cancellationToken)
    {
        var (plannedOn, plannedAt) = NormalizeDatePair(source.PlannedOn, source.PlannedAt);
        var (deadlineOn, deadlineAt) = NormalizeDatePair(source.DeadlineOn, source.DeadlineAt);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tasks
            SET title = @title,
                source_description = @source_description,
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
                completion_state = @completion_state,
                source_provider_task_id = @source_provider_task_id,
                source_url = @source_url,
                source_metadata = @source_metadata,
                planned_on = CASE
                    WHEN @recurrence_rule IS NOT NULL AND TRIM(@recurrence_rule) != '' THEN @planned_on
                    ELSE planned_on
                END,
                planned_at = CASE
                    WHEN @recurrence_rule IS NOT NULL AND TRIM(@recurrence_rule) != '' THEN @planned_at
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
                recurrence_rule = CASE
                    WHEN @recurrence_rule IS NOT NULL AND TRIM(@recurrence_rule) != '' THEN @recurrence_rule
                    ELSE recurrence_rule
                END,
                updated_at = @updated_at,
                completed_at = CASE
                    WHEN @completion_state = 'completed' THEN COALESCE(completed_at, @updated_at)
                    ELSE NULL
                END
            WHERE id = @task_id
            """;
        command.Parameters.AddWithValue("@task_id", source.AdoptedTaskId);
        command.Parameters.AddWithValue("@title", source.Title);
        command.Parameters.AddWithValue("@source_description", ToDbValue(source.Description));
        command.Parameters.AddWithValue("@parent_id", ToDbValue(parent?.Id));
        command.Parameters.AddWithValue("@parent_space_id", ToDbValue(parent?.SpaceId));
        command.Parameters.AddWithValue("@parent_project_id", ToDbValue(parent?.ProjectId));
        command.Parameters.AddWithValue("@parent_workflow_status", ToDbValue(parent?.WorkflowStatus.ToStorageValue()));
        command.Parameters.AddWithValue("@completion_state", source.CompletionState.ToStorageValue());
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
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id, t.external_id, t.space_id, t.integration_id, t.title, t.description, t.source_description, t.project_id, t.parent_id,
                   t.priority, t.source_integration_id, t.source_connection_id, t.source_external_id, t.source_provider_task_id,
                   t.source_url, t.completion_state, t.workflow_status, t.planned_on, t.planned_at,
                   t.deadline_on, t.deadline_at, t.scheduled_start, t.scheduled_end, t.duration_minutes, t.recurrence_rule,
                   t.notes, t.provider_metadata, t.source_metadata, t.local_metadata,
                   t.created_at, t.updated_at, t.completed_at, t.provider_connection_id, psi.source_project_name,
                   psi.priority, psi.planned_on, psi.planned_at, psi.deadline_on, psi.deadline_at
            FROM tasks t
            LEFT JOIN provider_source_items psi
              ON psi.provider_connection_id = t.source_connection_id
             AND psi.external_id = t.source_external_id
            """;

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
                SourceProjectName = GetNullableString(reader, 33),
                SourcePriority = reader.IsDBNull(34) ? null : reader.GetInt32(34),
                SourcePlannedOn = ReadDateOnly(reader, 35),
                SourcePlannedAt = ReadDate(reader, 36),
                SourceDeadlineOn = ReadDateOnly(reader, 37),
                SourceDeadlineAt = ReadDate(reader, 38),
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
                               created_at, updated_at, completed_at)
            VALUES (@id, @external_id, @space_id, @integration_id, @provider_connection_id, @title, @description, @source_description, @project_id, @parent_id,
                    @priority, @source_integration_id, @source_connection_id, @source_external_id, @source_provider_task_id,
                    @source_url, @completion_state, @workflow_status, @planned_on, @planned_at, @deadline_on, @deadline_at,
                    @scheduled_start, @scheduled_end, @duration_minutes, @recurrence_rule,
                    @notes, @provider_metadata, @source_metadata, @local_metadata,
                    @created_at, @updated_at, @completed_at)
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
                completed_at = excluded.completed_at
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
        command.Parameters.AddWithValue("@planned_at", ToDbValue(plannedAt));
        command.Parameters.AddWithValue("@deadline_on", ToDbValue(deadlineOn));
        command.Parameters.AddWithValue("@deadline_at", ToDbValue(deadlineAt));
        command.Parameters.AddWithValue("@scheduled_start", ToDbValue(task.ScheduledStart));
        command.Parameters.AddWithValue("@scheduled_end", ToDbValue(task.ScheduledEnd));
        command.Parameters.AddWithValue("@duration_minutes", task.DurationMinutes is null ? DBNull.Value : task.DurationMinutes.Value);
        command.Parameters.AddWithValue("@recurrence_rule", ToDbValue(task.RecurrenceRule));
        command.Parameters.AddWithValue("@notes", ToDbValue(task.Notes));
        command.Parameters.AddWithValue("@provider_metadata", ToDbValue(task.ProviderMetadataJson));
        command.Parameters.AddWithValue("@source_metadata", ToDbValue(task.SourceMetadataJson));
        command.Parameters.AddWithValue("@local_metadata", ToDbValue(task.LocalMetadataJson));
        command.Parameters.AddWithValue("@created_at", ToDbDate(task.CreatedAt));
        command.Parameters.AddWithValue("@updated_at", ToDbValue(task.UpdatedAt));
        command.Parameters.AddWithValue("@completed_at", ToDbValue(task.CompletedAt));
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

    private static async Task<string> ResolveAndUpsertLabelCoreAsync(SqliteConnection connection, LabelItem label, CancellationToken cancellationToken)
    {
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
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = false,
        }.ToString());

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecutePragmaAsync(connection, "PRAGMA journal_mode = DELETE", cancellationToken).ConfigureAwait(false);
        await ExecutePragmaAsync(connection, "PRAGMA busy_timeout = 5000", cancellationToken).ConfigureAwait(false);
        await ExecutePragmaAsync(connection, "PRAGMA synchronous = FULL", cancellationToken).ConfigureAwait(false);
        await ExecutePragmaAsync(connection, "PRAGMA foreign_keys = ON", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await EnsurePendingCompletionsTableAsync(connection, cancellationToken).ConfigureAwait(false);
        await ExecutePragmaAsync(connection, "PRAGMA user_version = 3", cancellationToken).ConfigureAwait(false);
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
        }
        finally
        {
            await ExecutePragmaAsync(connection, "PRAGMA foreign_keys = ON", cancellationToken).ConfigureAwait(false);
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

        var initialColumns = await GetColumnNamesAsync(connection, "tasks", cancellationToken).ConfigureAwait(false);
        var hasLegacyStatusColumn = initialColumns.Contains("status");

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
            await ExecuteNonQueryAsync(connection, """
                UPDATE tasks
                SET space_id = COALESCE(NULLIF(space_id, ''), 'space_default'),
                    integration_id = COALESCE(NULLIF(integration_id, ''), 'openza_tasks'),
                    priority = COALESCE(priority, 2),
                    status = COALESCE(NULLIF(status, ''), 'none'),
                    completion_state = CASE
                        WHEN status IN ('completed', 'done') THEN 'completed'
                        WHEN status IN ('cancelled', 'canceled') THEN 'cancelled'
                        ELSE COALESCE(NULLIF(completion_state, ''), 'open')
                    END,
                    workflow_status = CASE
                        WHEN status = 'next' THEN 'next'
                        WHEN status = 'waiting' THEN 'waiting'
                        WHEN status = 'someday' THEN 'someday'
                        WHEN status IN ('pending', 'active', 'in_progress', 'inProgress', 'none') THEN 'inbox'
                        ELSE COALESCE(NULLIF(workflow_status, ''), 'none')
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
                    completion_state = CASE
                        WHEN status = 'completed' THEN 'completed'
                        WHEN status = 'cancelled' THEN 'cancelled'
                        ELSE completion_state
                    END,
                    workflow_status = CASE
                        WHEN status = 'next' THEN 'next'
                        WHEN status = 'waiting' THEN 'waiting'
                        WHEN status = 'someday' THEN 'someday'
                        WHEN status = 'none' AND workflow_status = 'none' THEN 'inbox'
                        ELSE workflow_status
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
        if (await SpaceExistsAsync(connection, existingSpaceId, cancellationToken).ConfigureAwait(false))
        {
            return existingSpaceId;
        }

        if (await SpaceExistsAsync(connection, incomingSpaceId, cancellationToken).ConfigureAwait(false))
        {
            return incomingSpaceId;
        }

        return null;
    }

    private static async Task<bool> SpaceExistsAsync(SqliteConnection connection, string? spaceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spaceId))
        {
            return false;
        }

        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM spaces WHERE id = @id";
        command.Parameters.AddWithValue("@id", spaceId);
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        return count > 0;
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
              ('msToDo', 'msToDo', 'Microsoft To Do', '#00A4EF', 'layout-grid', 'assets/logos/microsoft.svg', 0, 0, strftime('%s', 'now'));

            INSERT OR IGNORE INTO provider_connections (id, workspace_id, integration_id, display_name, status, created_at)
            VALUES
              ('local_default', 'default', 'openza_tasks', 'Openza Tasks', 'connected', strftime('%s', 'now')),
              ('todoist_default', 'default', 'todoist', 'Todoist', 'disconnected', strftime('%s', 'now')),
              ('mstodo_default', 'default', 'msToDo', 'Microsoft To Do', 'disconnected', strftime('%s', 'now'));
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

    private static string? GetNullableString(IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int ReadNullableInt(IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    private static object ToDbValue(string? value) => string.IsNullOrEmpty(value) ? DBNull.Value : value;

    private static object ToDbValue(DateOnly? value) => value is null ? DBNull.Value : TaskDateValues.ToStorageValue(value.Value)!;

    private static object ToDbValue(DateTimeOffset? value) => value is null ? DBNull.Value : ToDbDate(value.Value);

    private static long ToDbDate(DateTimeOffset value) => value.ToUnixTimeSeconds();

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
        CREATE INDEX IF NOT EXISTS idx_sync_item_links_route_source ON sync_item_links(route_id, source_external_id);
        """;
}
