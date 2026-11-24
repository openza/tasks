using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Data;

public sealed record TaskReminderSchedule(string TaskId, string Title, DateTimeOffset At, bool IsOpen);

public sealed partial class SqliteTaskStore
{
    public async Task<IReadOnlyList<TaskReminderSchedule>> GetReminderSchedulesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id, t.title, t.reminder_at, t.completion_state
            FROM tasks t
            WHERE t.reminder_at IS NOT NULL AND NOT EXISTS (
                SELECT 1 FROM personal_cloud_tombstones pct
                WHERE pct.product = 'tasks' AND pct.entity_id = t.id
            )
            """;

        var schedules = new List<TaskReminderSchedule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            if (ReadDate(reader, 2) is { } at)
                schedules.Add(new TaskReminderSchedule(reader.GetString(0), reader.GetString(1), at,
                    TaskStatusExtensions.CompletionFromStorageValue(reader.GetString(3)) == TaskCompletionState.Open));

        return schedules;
    }
}
