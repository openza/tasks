using Microsoft.Data.Sqlite;
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
    public async Task Concurrent_initialization_upgrades_and_preserves_existing_reminders()
    {
        var store = await OpenStoreAsync();
        await store.UpsertTaskAsync(new TaskItem { Id = "remind", Title = "Existing reminder", ReminderAt = _clock.Now });
        await store.InitializeAsync();
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE tasks DROP COLUMN reminder_version; PRAGMA user_version=14;";
            await command.ExecuteNonQueryAsync();
        }

        for (var attempt = 0; attempt < 12; attempt++)
        {
            var database = Path.Combine(_directory, $"upgrade-{attempt}.db");
            File.Copy(store.DatabasePath, database);
            using var barrier = new Barrier(8);
            await Task.WhenAll(Enumerable.Range(0, barrier.ParticipantCount).Select(_ =>
                Task.Factory.StartNew(() =>
                {
                    Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(15)));
                    return new SqliteTaskStore(database).InitializeAsync();
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()));

            var upgraded = new SqliteTaskStore(database);
            var reminder = await upgraded.GetReminderScheduleAsync("remind");
            Assert.Equal(new TaskReminderSchedule("remind", "Existing reminder", _clock.Now, true), reminder);
            await using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            Assert.Equal((long)SqliteTaskStore.CurrentSchemaVersion, await command.ExecuteScalarAsync());
        }
    }

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
        Assert.Equal(new TaskReminderSchedule("remind", "Remember this", due, true), Assert.Single(_delivered) with { Version = null });
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
    public async Task Rechecks_later_tasks_after_earlier_delivery_changes_them()
    {
        var store = await OpenStoreAsync();
        await store.UpsertTaskAsync(new TaskItem { Id = "first", Title = "First", ReminderAt = _clock.Now.AddMinutes(-1) });
        foreach (var id in new[] { "deleted", "completed", "rescheduled", "renamed" })
            await store.UpsertTaskAsync(new TaskItem { Id = id, Title = id, ReminderAt = _clock.Now });

        var scheduler = new TaskReminderScheduler(store, _directory, async (reminder, _) =>
        {
            _delivered.Add(reminder);
            if (reminder.TaskId != "first") return;
            await store.DeleteTaskAsync("deleted");
            var completed = (await store.GetTaskAsync("completed"))!;
            await store.UpsertTaskAsync(completed with { CompletionState = TaskCompletionState.Completed });
            await store.TrySetTaskReminderAsync("rescheduled", _clock.Now.AddHours(1));
            var renamed = (await store.GetTaskAsync("renamed"))!;
            await store.UpsertTaskAsync(renamed with { Title = "New title" });
        }, _clock);

        await scheduler.CheckAsync();
        Assert.Equal(new[] { "first", "renamed" }, _delivered.Select(reminder => reminder.TaskId));
        Assert.Equal("New title", _delivered[1].Title);
    }

    [Fact]
    public async Task Clearing_and_rearming_the_same_instant_between_polls_delivers_it_again()
    {
        var store = await OpenStoreAsync();
        var due = _clock.Now;
        await store.UpsertTaskAsync(new TaskItem { Id = "remind", Title = "Remember this", ReminderAt = due });
        await Scheduler(store).CheckAsync();
        Assert.Single(_delivered);

        Assert.True(await store.TryReplaceTaskReminderAsync("remind", due, null));
        Assert.True(await store.TrySetTaskReminderAsync("remind", due));
        await Scheduler(store).CheckAsync();
        Assert.Equal(new[] { due, due }, _delivered.Select(reminder => reminder.At));

        var task = (await store.GetTaskAsync("remind"))!;
        await store.UpsertTaskAsync(task with { Title = "Remember the new title" });
        Assert.True(await store.TrySetTaskReminderAsync("remind", due));
        await Scheduler(store).CheckAsync();
        Assert.Equal(2, _delivered.Count);
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
