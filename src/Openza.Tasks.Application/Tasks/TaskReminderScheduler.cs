using System.Diagnostics;
using System.Text.Json;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Services;

namespace Openza.Tasks.Application.Tasks;

public sealed class TaskReminderScheduler(
    SqliteTaskStore store,
    string stateDirectory,
    Func<TaskReminderSchedule, CancellationToken, Task> notify,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private sealed record ReminderDelivery(DateTimeOffset At, string? Version)
    {
        public static ReminderDelivery FromSchedule(TaskReminderSchedule reminder) => new(reminder.At, reminder.Version);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(CheckInterval, _clock);
        try
        {
            do
            {
                try { await CheckAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    Trace.TraceWarning("Could not check local task reminders.");
                }
            } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { }
    }

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        PrivateFilePermissions.EnsureOwnedDirectory(stateDirectory);
        using var lease = TryAcquireLease();
        if (lease is null) return;

        var path = Path.Combine(stateDirectory, "reminder-deliveries.json");
        var delivered = ReadDelivered(path);
        var schedules = await store.GetReminderSchedulesAsync(cancellationToken).ConfigureAwait(false);
        var scheduled = schedules.ToDictionary(reminder => reminder.TaskId, ReminderDelivery.FromSchedule, StringComparer.Ordinal);
        var obsolete = delivered.Where(entry => !scheduled.TryGetValue(entry.Key, out var current) || current != entry.Value)
            .Select(entry => entry.Key).ToArray();
        if (obsolete.Length > 0)
        {
            foreach (var id in obsolete) delivered.Remove(id);
            SaveDelivered(path, delivered);
        }

        var now = _clock.GetUtcNow();
        foreach (var reminder in schedules.OrderBy(reminder => reminder.At))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reminder.IsOpen || reminder.At > now || delivered.ContainsKey(reminder.TaskId)) continue;
            var current = await store.GetReminderScheduleAsync(reminder.TaskId, cancellationToken).ConfigureAwait(false);
            if (current is not { IsOpen: true } || current.At != reminder.At || current.Version != reminder.Version) continue;
            try
            {
                await notify(current, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                Trace.TraceWarning("Could not send a native task reminder notification.");
                continue;
            }
            delivered[current.TaskId] = ReminderDelivery.FromSchedule(current);
            SaveDelivered(path, delivered);
        }
    }

    private FileStream? TryAcquireLease()
    {
        try
        {
            return new FileStream(Path.Combine(stateDirectory, "reminder-deliveries.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; }
    }

    private static Dictionary<string, ReminderDelivery> ReadDelivered(string path)
    {
        if (!File.Exists(path)) return [];
        var stored = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path)) ?? [];
        return stored.ToDictionary(entry => entry.Key, entry => entry.Value.ValueKind == JsonValueKind.String
            ? new ReminderDelivery(entry.Value.GetDateTimeOffset(), null)
            : entry.Value.Deserialize<ReminderDelivery>()!, StringComparer.Ordinal);
    }

    private static void SaveDelivered(string path, Dictionary<string, ReminderDelivery> delivered)
    {
        var staged = path + ".tmp";
        File.WriteAllText(staged, JsonSerializer.Serialize(delivered));
        PrivateFilePermissions.EnsureFile(staged);
        File.Move(staged, path, overwrite: true);
    }
}
