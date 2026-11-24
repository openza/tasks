using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Data;

public sealed record TaskReminderSchedule(string TaskId, string Title, DateTimeOffset At, bool IsOpen, string? Version = null);

public sealed partial class SqliteTaskStore
{
    public Task<IReadOnlyList<TaskReminderSchedule>> GetReminderSchedulesAsync(CancellationToken cancellationToken = default) =>
        ReadReminderSchedulesAsync(null, cancellationToken);

    public async Task<TaskReminderSchedule?> GetReminderScheduleAsync(string taskId, CancellationToken cancellationToken = default) =>
        (await ReadReminderSchedulesAsync(taskId, cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    private async Task<IReadOnlyList<TaskReminderSchedule>> ReadReminderSchedulesAsync(string? taskId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.id, t.title, t.reminder_at, t.completion_state, t.reminder_version
            FROM tasks t
            WHERE t.reminder_at IS NOT NULL AND NOT EXISTS (
                SELECT 1 FROM personal_cloud_tombstones pct
                WHERE pct.product = 'tasks' AND pct.entity_id = t.id
            )
            """;
        if (taskId is not null)
        {
            command.CommandText += " AND t.id = @id";
            command.Parameters.AddWithValue("@id", taskId);
        }

        var schedules = new List<TaskReminderSchedule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            if (ReadDate(reader, 2) is { } at)
                schedules.Add(new TaskReminderSchedule(reader.GetString(0), reader.GetString(1), at,
                    TaskStatusExtensions.CompletionFromStorageValue(reader.GetString(3)) == TaskCompletionState.Open,
                    SqliteReadValues.GetNullableString(reader, 4)));

        return schedules;
    }
}
