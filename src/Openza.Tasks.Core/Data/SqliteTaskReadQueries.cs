using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Models;
using static Openza.Tasks.Core.Data.SqliteReadValues;

namespace Openza.Tasks.Core.Data;

/// <summary>Reads task rows/counts on a caller-owned connection; never opens or commits a transaction.</summary>
internal static class SqliteTaskReadQueries
{
    public static async Task<TaskCountSummary> ReadCountsAsync(
        SqliteConnection connection, string? spaceId, DateOnly todayDate, CancellationToken cancellationToken)
    {
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

    public static async Task<IReadOnlyList<TaskItem>> ReadTasksAsync(SqliteConnection connection, CancellationToken cancellationToken)
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

}
