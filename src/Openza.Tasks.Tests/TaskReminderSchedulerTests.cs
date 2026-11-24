using Openza.Tasks.Application.Tasks;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Tests;

public sealed class TaskReminderSchedulerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-reminder-tests", Guid.NewGuid().ToString("N"));
    private readonly ReminderClock _clock = new();
    private readonly List<TaskReminderSchedule> _delivered = [];

    [Fact]
    public async Task Delivers_at_the_due_instant_and_does_not_repeat_after_restart()
    {
        var store = await OpenStoreAsync();
        var due = _clock.Now.AddMinutes(1);
        await store.UpsertTaskAsync(new TaskItem { Id = "remind", Title = "Remember this", ReminderAt = due });
        await Scheduler(store).CheckAsync();
        Assert.Empty(_delivered);

        _clock.Now = due;
        await Scheduler(store).CheckAsync();
        await Scheduler(store).CheckAsync();
        Assert.Equal(new TaskReminderSchedule("remind", "Remember this", due, true), Assert.Single(_delivered));
    }

    [Fact]
    public async Task Checks_current_reminders_after_edits_and_startup()
    {
        var store = await OpenStoreAsync();
        var initial = _clock.Now.AddMinutes(1);
        var changed = initial.AddMinutes(10);
        foreach (var id in new[] { "rescheduled", "cleared", "deleted", "completed" })
            await store.UpsertTaskAsync(new TaskItem { Id = id, Title = id, ReminderAt = initial });

        var scheduler = Scheduler(store);
        await scheduler.CheckAsync();
        await store.TrySetTaskReminderAsync("rescheduled", changed);
        await store.TrySetTaskReminderAsync("cleared", null);
        await store.DeleteTaskAsync("deleted");
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "completed", Title = "Completed", ReminderAt = initial, CompletionState = TaskCompletionState.Completed,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "cancelled", Title = "Cancelled", ReminderAt = initial, CompletionState = TaskCompletionState.Cancelled,
        });
        _clock.Now = initial;
        await scheduler.CheckAsync();
        Assert.Empty(_delivered);

        _clock.Now = changed.AddHours(1);
        await Scheduler(store).CheckAsync();
        Assert.Equal("rescheduled", Assert.Single(_delivered).TaskId);

        await store.TrySetTaskReminderAsync("rescheduled", _clock.Now);
        await scheduler.CheckAsync();
        Assert.Equal(new[] { changed, _clock.Now }, _delivered.Select(reminder => reminder.At));
    }

    [Fact]
    public async Task Failed_native_delivery_is_not_marked_as_delivered()
    {
        var store = await OpenStoreAsync();
        await store.UpsertTaskAsync(new TaskItem { Id = "retry", Title = "Retry", ReminderAt = _clock.Now });
        var failed = new TaskReminderScheduler(store, _directory,
            (_, _) => Task.FromException(new InvalidOperationException()), _clock);
        await failed.CheckAsync();
        await Scheduler(store).CheckAsync();
        Assert.Equal("retry", Assert.Single(_delivered).TaskId);
    }

    private async Task<SqliteTaskStore> OpenStoreAsync()
    {
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        return store;
    }

    private TaskReminderScheduler Scheduler(SqliteTaskStore store) => new(store, _directory, (reminder, _) =>
    {
        _delivered.Add(reminder);
        return Task.CompletedTask;
    }, _clock);

    public void Dispose() => TestDirectory.Delete(_directory);

    private sealed class ReminderClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2027, 7, 1, 16, 30, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
