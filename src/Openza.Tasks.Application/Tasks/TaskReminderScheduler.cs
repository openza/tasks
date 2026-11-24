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
        var delivered = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(path)) ?? []
            : [];
        var schedules = await store.GetReminderSchedulesAsync(cancellationToken).ConfigureAwait(false);
        var scheduledTimes = schedules.ToDictionary(reminder => reminder.TaskId, reminder => reminder.At, StringComparer.Ordinal);
        var obsolete = delivered.Where(entry => !scheduledTimes.TryGetValue(entry.Key, out var at) || at != entry.Value)
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
            try
            {
                await notify(reminder, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                Trace.TraceWarning("Could not send a native task reminder notification.");
                continue;
            }
            delivered[reminder.TaskId] = reminder.At;
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

    private static void SaveDelivered(string path, Dictionary<string, DateTimeOffset> delivered)
    {
        var staged = path + ".tmp";
        File.WriteAllText(staged, JsonSerializer.Serialize(delivered));
        PrivateFilePermissions.EnsureFile(staged);
        File.Move(staged, path, overwrite: true);
    }
}
