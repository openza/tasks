using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Data;

public sealed partial class SqliteTaskStore
{
    public async Task<bool> TrySetTaskReminderAsync(string taskId, DateTimeOffset? reminderAt, long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tasks SET reminder_at=@r,revision=revision+1,
                reminder_version=CASE WHEN reminder_at IS NOT @r THEN lower(hex(randomblob(16))) ELSE reminder_version END
            WHERE id=@id AND (@v IS NULL OR revision=@v)
            """;
        command.Parameters.AddWithValue("@id", taskId);
        command.Parameters.AddWithValue("@r", ToDbReminderInstant(reminderAt));
        command.Parameters.AddWithValue("@v", expectedRevision is { } revision ? revision : DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> TryReplaceTaskReminderAsync(string taskId, DateTimeOffset? expectedReminderAt,
        DateTimeOffset? reminderAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tasks SET reminder_at=@r,revision=revision+1,
                reminder_version=CASE WHEN reminder_at IS NOT @r THEN lower(hex(randomblob(16))) ELSE reminder_version END
            WHERE id=@id AND reminder_at IS @expected
            """;
        command.Parameters.AddWithValue("@id", taskId);
        command.Parameters.AddWithValue("@r", ToDbReminderInstant(reminderAt));
        command.Parameters.AddWithValue("@expected", ToDbTaskInstant(expectedReminderAt));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async Task ApplyMicrosoftReminderAsync(SqliteConnection connection, ProviderSourceItem source,
        string taskId, CancellationToken cancellationToken)
    {
        if (source.IntegrationId != IntegrationIds.MicrosoftToDo) return;
        using var snapshot = JsonDocument.Parse(source.SnapshotJson ?? "{}");
        if (!snapshot.RootElement.TryGetProperty("msToDo", out var microsoft) || microsoft.ValueKind != JsonValueKind.Object ||
            !microsoft.TryGetProperty("reminder", out var reminder) || reminder.ValueKind != JsonValueKind.Object ||
            !reminder.TryGetProperty("isReminderOn", out var enabled) ||
            enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return;
        DateTimeOffset? at = null;
        if (enabled.GetBoolean())
        {
            if (!reminder.TryGetProperty("reminderDateTime", out var value) || value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("dateTime", out var date) || date.ValueKind != JsonValueKind.String ||
                !value.TryGetProperty("timeZone", out var zone) || zone.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(zone.GetString()) ||
                !date.GetString()!.Contains('T') ||
                !DateTime.TryParse(date.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var local)) return;
            try
            {
                var timezone = TimeZoneInfo.FindSystemTimeZoneById(zone.GetString()!);
                if (local.Kind == DateTimeKind.Unspecified)
                {
                    if (timezone.IsInvalidTime(local)) return;
                    at = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timezone), TimeSpan.Zero);
                }
                else at = new DateTimeOffset(local).ToUniversalTime();
            }
            catch (TimeZoneNotFoundException)
            {
                System.Diagnostics.Trace.TraceWarning("Microsoft To Do reminder skipped: unknown time zone.");
                return;
            }
            catch (InvalidTimeZoneException)
            {
                System.Diagnostics.Trace.TraceWarning("Microsoft To Do reminder skipped: invalid time zone.");
                return;
            }
            if (!ReminderValues.IsInRange(at))
            {
                System.Diagnostics.Trace.TraceWarning("Microsoft To Do reminder skipped: instant outside the supported range.");
                at = null;
            }
        }
        await WorkspaceExecuteAsync(connection, """
            UPDATE tasks SET reminder_at=COALESCE(reminder_at,@r),
                reminder_version=CASE WHEN reminder_at IS NULL AND @r IS NOT NULL THEN lower(hex(randomblob(16))) ELSE reminder_version END,
                source_metadata=json_set(COALESCE(source_metadata,'{}'),'$.msToDoReminderApplied',json('true')),
                revision=revision+1
            WHERE id=@id AND COALESCE(json_extract(source_metadata,'$.msToDoReminderApplied'),0)=0
            """, cancellationToken, ("@id", taskId), ("@r", ToDbReminderInstant(at))).ConfigureAwait(false);
    }

    private static object ToDbReminderInstant(DateTimeOffset? value) => ToDbTaskInstant(ReminderValues.Validate(value));

    private static async Task<TaskItem> RetainDeviceReminderAsync(SqliteConnection connection, TaskItem task,
        bool retainValue, CancellationToken cancellationToken)
    {
        var read = connection.CreateCommand();
        read.CommandText = "SELECT reminder_at,COALESCE(json_extract(source_metadata,'$.msToDoReminderApplied'),0) FROM tasks WHERE id=@id";
        read.Parameters.AddWithValue("@id", task.Id);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return task;
        if (retainValue) task = task with { ReminderAt = ReadDate(reader, 0) };
        if (reader.GetInt64(1) == 0) return task;
        var metadata = JsonNode.Parse(task.SourceMetadataJson ?? "{}")!.AsObject();
        metadata["msToDoReminderApplied"] = true;
        return task with { SourceMetadataJson = metadata.ToJsonString() };
    }
}
