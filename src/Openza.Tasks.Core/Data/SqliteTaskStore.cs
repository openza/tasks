using System.Data;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Data;

public sealed class SqliteTaskStore(string databasePath) : ITaskStore
{
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
        var today = DateTimeOffset.Now.Date;

        IEnumerable<TaskItem> filtered = tasks;
        filtered = query.Kind switch
        {
            TaskListKind.Inbox => filtered.Where(t => t.IsOpen && t.Status == TaskItemStatus.None && t.ProjectId is null),
            TaskListKind.NextActions => filtered.Where(t => t.IsOpen && t.Status == TaskItemStatus.Next),
            TaskListKind.Waiting => filtered.Where(t => t.IsOpen && t.Status == TaskItemStatus.Waiting),
            TaskListKind.Someday => filtered.Where(t => t.IsOpen && t.Status == TaskItemStatus.Someday),
            TaskListKind.Today => filtered.Where(t => t.IsOpen && t.DueDate?.LocalDateTime.Date == today),
            TaskListKind.Overdue => filtered.Where(t => t.IsOpen && t.DueDate?.LocalDateTime.Date < today),
            TaskListKind.Open => filtered.Where(t => t.IsOpen),
            TaskListKind.Completed => filtered.Where(t => t.IsCompleted),
            _ => filtered,
        };

        if (!string.IsNullOrWhiteSpace(query.ProjectId))
        {
            filtered = filtered.Where(t => t.ProjectId == query.ProjectId);
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
                Contains(t.Description, search) ||
                Contains(t.Notes, search));
        }

        filtered = query.SortMode switch
        {
            TaskSortMode.DueDate => filtered.OrderBy(t => t.DueDate is null).ThenBy(t => t.DueDate),
            TaskSortMode.CreatedNewest => filtered.OrderByDescending(t => t.CreatedAt),
            TaskSortMode.Title => filtered.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
            TaskSortMode.Project => filtered.OrderBy(t => t.ProjectId ?? string.Empty).ThenBy(t => t.Priority),
            _ => filtered.OrderBy(t => t.IsCompleted).ThenBy(t => t.Priority).ThenBy(t => t.DueDate is null).ThenBy(t => t.DueDate).ThenByDescending(t => t.CreatedAt),
        };

        return filtered.ToList();
    }

    public async Task<TaskCountSummary> GetTaskCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var todayStart = new DateTimeOffset(DateTimeOffset.Now.Date, DateTimeOffset.Now.Offset).ToUnixTimeSeconds();
        var tomorrowStart = new DateTimeOffset(DateTimeOffset.Now.Date.AddDays(1), DateTimeOffset.Now.Offset).ToUnixTimeSeconds();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              SUM(CASE WHEN status = 'none' AND project_id IS NULL THEN 1 ELSE 0 END) AS inbox,
              SUM(CASE WHEN status = 'next' THEN 1 ELSE 0 END) AS next_actions,
              SUM(CASE WHEN status = 'waiting' THEN 1 ELSE 0 END) AS waiting,
              SUM(CASE WHEN status = 'someday' THEN 1 ELSE 0 END) AS someday,
              SUM(CASE WHEN status NOT IN ('completed', 'cancelled') AND due_date >= @today_start AND due_date < @tomorrow_start THEN 1 ELSE 0 END) AS today,
              SUM(CASE WHEN status NOT IN ('completed', 'cancelled') AND due_date IS NOT NULL AND due_date < @today_start THEN 1 ELSE 0 END) AS overdue,
              SUM(CASE WHEN status NOT IN ('completed', 'cancelled') THEN 1 ELSE 0 END) AS open_tasks,
              COUNT(*) AS all_tasks,
              SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END) AS completed
            FROM tasks
            """;
        command.Parameters.AddWithValue("@today_start", todayStart);
        command.Parameters.AddWithValue("@tomorrow_start", tomorrowStart);

        var inbox = 0;
        var nextActions = 0;
        var waiting = 0;
        var someday = 0;
        var today = 0;
        var overdue = 0;
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
                open = ReadNullableInt(reader, 6);
                all = ReadNullableInt(reader, 7);
                completed = ReadNullableInt(reader, 8);
            }
        }

        var byProjectCommand = connection.CreateCommand();
        byProjectCommand.CommandText = """
            SELECT project_id, COUNT(*)
            FROM tasks
            WHERE status NOT IN ('completed', 'cancelled') AND project_id IS NOT NULL
            GROUP BY project_id
            """;

        var byProject = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var reader = await byProjectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0))
                {
                    byProject[reader.GetString(0)] = reader.GetInt32(1);
                }
            }
        }

        return new TaskCountSummary(inbox, nextActions, waiting, someday, today, overdue, open, all, completed, byProject);
    }

    public async Task<TaskItem?> GetTaskAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await ReadTasksAsync(connection, cancellationToken).ConfigureAwait(false)).FirstOrDefault(t => t.Id == id);
    }

    public async Task<IReadOnlyList<ProjectItem>> GetProjectsAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, external_id, integration_id, name, description, color, icon, parent_id,
                   sort_order, is_favorite, is_archived, provider_metadata, created_at, updated_at
            FROM projects
            WHERE @include_archived = 1 OR is_archived = 0
            ORDER BY is_favorite DESC, sort_order, name
            """;
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
            SELECT id, external_id, integration_id, name, color, description, sort_order,
                   is_favorite, provider_metadata, created_at
            FROM labels
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
            INSERT INTO projects (id, external_id, integration_id, name, description, color, icon, parent_id,
                                  sort_order, is_favorite, is_archived, provider_metadata, created_at, updated_at)
            VALUES (@id, @external_id, @integration_id, @name, @description, @color, @icon, @parent_id,
                    @sort_order, @is_favorite, @is_archived, @provider_metadata, @created_at, @updated_at)
            ON CONFLICT(id) DO UPDATE SET
                external_id = excluded.external_id,
                integration_id = excluded.integration_id,
                name = excluded.name,
                description = excluded.description,
                color = excluded.color,
                icon = excluded.icon,
                parent_id = excluded.parent_id,
                sort_order = excluded.sort_order,
                is_favorite = excluded.is_favorite,
                is_archived = excluded.is_archived,
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
            ? "UPDATE tasks SET project_id = NULL, updated_at = @updated_at WHERE project_id = @project_id"
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
    }

    private async Task SetCompletionStateAsync(string taskId, bool completed, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tasks
            SET status = @status,
                completed_at = @completed_at,
                updated_at = @updated_at
            WHERE id = @id
            """;
        command.Parameters.AddWithValue("@id", taskId);
        command.Parameters.AddWithValue("@status", completed ? "completed" : "none");
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
    }

    private async Task<IReadOnlyList<TaskItem>> ReadTasksAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var labelsByTask = await ReadLabelsByTaskAsync(connection, cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, external_id, integration_id, title, description, project_id, parent_id,
                   priority, status, due_date, due_time, notes, provider_metadata,
                   created_at, updated_at, completed_at
            FROM tasks
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
                IntegrationId = reader.GetString(2),
                Title = reader.GetString(3),
                Description = GetNullableString(reader, 4),
                ProjectId = GetNullableString(reader, 5),
                ParentId = GetNullableString(reader, 6),
                Priority = reader.GetInt32(7),
                Status = TaskStatusExtensions.FromStorageValue(reader.GetString(8)),
                DueDate = ReadDate(reader, 9),
                DueTime = GetNullableString(reader, 10),
                Notes = GetNullableString(reader, 11),
                ProviderMetadataJson = GetNullableString(reader, 12),
                CreatedAt = ReadDate(reader, 13) ?? DateTimeOffset.UtcNow,
                UpdatedAt = ReadDate(reader, 14),
                CompletedAt = ReadDate(reader, 15),
                Labels = labelsByTask.GetValueOrDefault(id, []),
            });
        }

        return tasks;
    }

    private static async Task<Dictionary<string, IReadOnlyList<LabelItem>>> ReadLabelsByTaskAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tl.task_id, l.id, l.external_id, l.integration_id, l.name, l.color, l.description,
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
                Name = reader.GetString(4),
                Color = reader.GetString(5),
                Description = GetNullableString(reader, 6),
                SortOrder = reader.GetInt32(7),
                IsFavorite = reader.GetInt32(8) != 0,
                ProviderMetadataJson = GetNullableString(reader, 9),
                CreatedAt = ReadDate(reader, 10) ?? DateTimeOffset.UtcNow,
            });
        }

        return labels.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<LabelItem>)kvp.Value);
    }

    private static async Task UpsertTaskCoreAsync(SqliteConnection connection, TaskItem task, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tasks (id, external_id, integration_id, title, description, project_id, parent_id,
                               priority, status, due_date, due_time, notes, provider_metadata,
                               created_at, updated_at, completed_at)
            VALUES (@id, @external_id, @integration_id, @title, @description, @project_id, @parent_id,
                    @priority, @status, @due_date, @due_time, @notes, @provider_metadata,
                    @created_at, @updated_at, @completed_at)
            ON CONFLICT(id) DO UPDATE SET
                external_id = excluded.external_id,
                integration_id = excluded.integration_id,
                title = excluded.title,
                description = excluded.description,
                project_id = excluded.project_id,
                parent_id = excluded.parent_id,
                priority = excluded.priority,
                status = excluded.status,
                due_date = excluded.due_date,
                due_time = excluded.due_time,
                notes = excluded.notes,
                provider_metadata = excluded.provider_metadata,
                updated_at = excluded.updated_at,
                completed_at = excluded.completed_at
            """;
        command.Parameters.AddWithValue("@id", task.Id);
        command.Parameters.AddWithValue("@external_id", ToDbValue(task.ExternalId));
        command.Parameters.AddWithValue("@integration_id", task.IntegrationId);
        command.Parameters.AddWithValue("@title", task.Title);
        command.Parameters.AddWithValue("@description", ToDbValue(task.Description));
        command.Parameters.AddWithValue("@project_id", ToDbValue(task.ProjectId));
        command.Parameters.AddWithValue("@parent_id", ToDbValue(task.ParentId));
        command.Parameters.AddWithValue("@priority", task.Priority);
        command.Parameters.AddWithValue("@status", task.Status.ToStorageValue());
        command.Parameters.AddWithValue("@due_date", ToDbValue(task.DueDate));
        command.Parameters.AddWithValue("@due_time", ToDbValue(task.DueTime));
        command.Parameters.AddWithValue("@notes", ToDbValue(task.Notes));
        command.Parameters.AddWithValue("@provider_metadata", ToDbValue(task.ProviderMetadataJson));
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
            INSERT INTO labels (id, external_id, integration_id, name, color, description, sort_order,
                                is_favorite, provider_metadata, created_at)
            VALUES (@id, @external_id, @integration_id, @name, @color, @description, @sort_order,
                    @is_favorite, @provider_metadata, @created_at)
            ON CONFLICT(id) DO UPDATE SET
                external_id = excluded.external_id,
                integration_id = excluded.integration_id,
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
        command.Parameters.AddWithValue("@integration_id", project.IntegrationId);
        command.Parameters.AddWithValue("@name", project.Name);
        command.Parameters.AddWithValue("@description", ToDbValue(project.Description));
        command.Parameters.AddWithValue("@color", project.Color);
        command.Parameters.AddWithValue("@icon", ToDbValue(project.Icon));
        command.Parameters.AddWithValue("@parent_id", ToDbValue(project.ParentId));
        command.Parameters.AddWithValue("@sort_order", project.SortOrder);
        command.Parameters.AddWithValue("@is_favorite", project.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("@is_archived", project.IsArchived ? 1 : 0);
        command.Parameters.AddWithValue("@provider_metadata", ToDbValue(project.ProviderMetadataJson));
        command.Parameters.AddWithValue("@created_at", ToDbDate(project.CreatedAt));
        command.Parameters.AddWithValue("@updated_at", ToDbValue(project.UpdatedAt));
    }

    private static ProjectItem ReadProject(IDataRecord reader) => new()
    {
        Id = reader.GetString(0),
        ExternalId = GetNullableString(reader, 1),
        IntegrationId = reader.GetString(2),
        Name = reader.GetString(3),
        Description = GetNullableString(reader, 4),
        Color = GetNullableString(reader, 5) ?? "#808080",
        Icon = GetNullableString(reader, 6),
        ParentId = GetNullableString(reader, 7),
        SortOrder = reader.GetInt32(8),
        IsFavorite = reader.GetInt32(9) != 0,
        IsArchived = reader.GetInt32(10) != 0,
        ProviderMetadataJson = GetNullableString(reader, 11),
        CreatedAt = ReadDate(reader, 12) ?? DateTimeOffset.UtcNow,
        UpdatedAt = ReadDate(reader, 13),
    };

    private static LabelItem ReadLabel(IDataRecord reader) => new()
    {
        Id = reader.GetString(0),
        ExternalId = GetNullableString(reader, 1),
        IntegrationId = reader.GetString(2),
        Name = reader.GetString(3),
        Color = GetNullableString(reader, 4) ?? "#808080",
        Description = GetNullableString(reader, 5),
        SortOrder = reader.GetInt32(6),
        IsFavorite = reader.GetInt32(7) != 0,
        ProviderMetadataJson = GetNullableString(reader, 8),
        CreatedAt = ReadDate(reader, 9) ?? DateTimeOffset.UtcNow,
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
    }

    private static async Task EnsureLegacySchemaCompatibilityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecutePragmaAsync(connection, "PRAGMA foreign_keys = OFF", cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureIntegrationsCompatibilityAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnsureProjectsCompatibilityAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnsureTasksCompatibilityAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnsureLabelsCompatibilityAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ExecutePragmaAsync(connection, "PRAGMA foreign_keys = ON", cancellationToken).ConfigureAwait(false);
        }
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
        await AddColumnIfMissingAsync(connection, "projects", "integration_id", "TEXT DEFAULT 'openza_tasks'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "projects", "description", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "projects", "color", "TEXT DEFAULT '#808080'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "projects", "icon", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "projects", "parent_id", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "projects", "sort_order", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "projects", "is_favorite", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "projects", "is_archived", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
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
            SET integration_id = COALESCE(NULLIF(integration_id, ''), 'openza_tasks'),
                color = COALESCE(color, '#808080'),
                sort_order = COALESCE(sort_order, 0),
                is_favorite = COALESCE(is_favorite, 0),
                is_archived = COALESCE(is_archived, 0),
                created_at = COALESCE(created_at, strftime('%s', 'now'))
            """, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureTasksCompatibilityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "tasks", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await AddColumnIfMissingAsync(connection, "tasks", "external_id", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "integration_id", "TEXT DEFAULT 'openza_tasks'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "description", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "project_id", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "parent_id", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "priority", "INTEGER NOT NULL DEFAULT 2", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "status", "TEXT DEFAULT 'none'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "due_date", "INTEGER", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "due_time", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "notes", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "provider_metadata", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "created_at", "INTEGER", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "updated_at", "INTEGER", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "tasks", "completed_at", "INTEGER", cancellationToken).ConfigureAwait(false);

        var columns = await GetColumnNamesAsync(connection, "tasks", cancellationToken).ConfigureAwait(false);
        if (columns.Contains("integrations"))
        {
            await ExecuteNonQueryAsync(connection, "UPDATE tasks SET provider_metadata = integrations WHERE provider_metadata IS NULL", cancellationToken).ConfigureAwait(false);
        }

        await ExecuteNonQueryAsync(connection, """
            UPDATE tasks
            SET integration_id = COALESCE(NULLIF(integration_id, ''), 'openza_tasks'),
                priority = COALESCE(priority, 2),
                status = COALESCE(NULLIF(status, ''), 'none'),
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

        if (await TableExistsAsync(connection, "projects", cancellationToken).ConfigureAwait(false))
        {
            await ExecuteNonQueryAsync(connection, "DELETE FROM projects WHERE id = 'proj_inbox'", cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureLabelsCompatibilityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "labels", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await AddColumnIfMissingAsync(connection, "labels", "external_id", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "labels", "integration_id", "TEXT DEFAULT 'openza_tasks'", cancellationToken).ConfigureAwait(false);
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
            INSERT OR IGNORE INTO integrations (id, name, display_name, color, icon, logo_path, is_active, is_configured, created_at)
            VALUES
              ('openza_tasks', 'openza_tasks', 'Openza Tasks', '#6366f1', 'database', NULL, 1, 1, strftime('%s', 'now')),
              ('todoist', 'todoist', 'Todoist', '#E44332', 'check-circle', 'assets/logos/todoist.svg', 0, 0, strftime('%s', 'now')),
              ('msToDo', 'msToDo', 'Microsoft To Do', '#00A4EF', 'layout-grid', 'assets/logos/microsoft.svg', 0, 0, strftime('%s', 'now'));

            INSERT OR IGNORE INTO projects (id, integration_id, name, description, color, icon, created_at)
            VALUES
              ('proj_work', 'openza_tasks', 'Work', 'Work-related tasks', '#3b82f6', 'briefcase', strftime('%s', 'now')),
              ('proj_personal', 'openza_tasks', 'Personal', 'Personal tasks and goals', '#10b981', 'user', strftime('%s', 'now'));

            INSERT OR IGNORE INTO labels (id, integration_id, name, color, created_at)
            VALUES
              ('label_urgent', 'openza_tasks', 'urgent', '#ef4444', strftime('%s', 'now')),
              ('label_important', 'openza_tasks', 'important', '#f59e0b', strftime('%s', 'now')),
              ('label_learning', 'openza_tasks', 'learning', '#3b82f6', strftime('%s', 'now')),
              ('label_review', 'openza_tasks', 'review', '#8b5cf6', strftime('%s', 'now'));
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

    private static bool Contains(string? value, string search) =>
        value?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true;

    private static string? GetNullableString(IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int ReadNullableInt(IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    private static object ToDbValue(string? value) => string.IsNullOrEmpty(value) ? DBNull.Value : value;

    private static object ToDbValue(DateTimeOffset? value) => value is null ? DBNull.Value : ToDbDate(value.Value);

    private static long ToDbDate(DateTimeOffset value) => value.ToUnixTimeSeconds();

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

        CREATE TABLE IF NOT EXISTS projects (
          id TEXT PRIMARY KEY NOT NULL,
          external_id TEXT,
          integration_id TEXT NOT NULL DEFAULT 'openza_tasks' REFERENCES integrations(id),
          name TEXT NOT NULL,
          description TEXT,
          color TEXT DEFAULT '#808080',
          icon TEXT,
          parent_id TEXT,
          sort_order INTEGER NOT NULL DEFAULT 0,
          is_favorite INTEGER NOT NULL DEFAULT 0,
          is_archived INTEGER NOT NULL DEFAULT 0,
          provider_metadata TEXT,
          created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
          updated_at INTEGER
        );

        CREATE TABLE IF NOT EXISTS tasks (
          id TEXT PRIMARY KEY NOT NULL,
          external_id TEXT,
          integration_id TEXT NOT NULL DEFAULT 'openza_tasks' REFERENCES integrations(id),
          title TEXT NOT NULL,
          description TEXT,
          project_id TEXT REFERENCES projects(id),
          parent_id TEXT,
          priority INTEGER NOT NULL DEFAULT 2,
          status TEXT NOT NULL DEFAULT 'none',
          due_date INTEGER,
          due_time TEXT,
          notes TEXT,
          provider_metadata TEXT,
          created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
          updated_at INTEGER,
          completed_at INTEGER
        );

        CREATE TABLE IF NOT EXISTS labels (
          id TEXT PRIMARY KEY NOT NULL,
          external_id TEXT,
          integration_id TEXT NOT NULL DEFAULT 'openza_tasks' REFERENCES integrations(id),
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

        CREATE INDEX IF NOT EXISTS idx_tasks_integration_id ON tasks(integration_id);
        CREATE INDEX IF NOT EXISTS idx_tasks_project_id ON tasks(project_id);
        CREATE INDEX IF NOT EXISTS idx_tasks_status ON tasks(status);
        CREATE INDEX IF NOT EXISTS idx_tasks_due_date ON tasks(due_date);
        CREATE INDEX IF NOT EXISTS idx_projects_integration_id ON projects(integration_id);
        CREATE INDEX IF NOT EXISTS idx_labels_integration_id ON labels(integration_id);
        """;
}
