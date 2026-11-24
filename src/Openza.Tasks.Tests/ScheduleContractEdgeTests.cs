using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Application.Tasks;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using static Openza.Tasks.Tests.PersonalCloudWorkspaceTests;

namespace Openza.Tasks.Tests;

public sealed class ScheduleContractEdgeTests : IDisposable
{
    private const string Instance = "instance_workspace_test";
    private const string Device = "device_workspace_test";
    private readonly string _dir = Directory.CreateTempSubdirectory("pr148-review-").FullName;
    private string Db => Path.Combine(_dir, "tasks.db");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-13T09:00:00Z");

    private TaskApplicationService Service(SqliteTaskStore store) => new(store, () => new()
    {
        ServiceOrigin = new Uri("https://openza-personal-test.workers.dev/"),
        InstanceId = Instance, DeviceId = Device, ArtifactVersion = "1.2.3", ConnectedAt = DateTimeOffset.UtcNow,
    });

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Legacy_startup_matrix(bool hasSchedule, bool hasDate)
    {
        await using (var db = new SqliteConnection($"Data Source={Db};Pooling=False"))
        {
            await db.OpenAsync();
            await using var cmd = db.CreateCommand();
            cmd.CommandText = $"CREATE TABLE tasks (id TEXT PRIMARY KEY NOT NULL, title TEXT NOT NULL, created_at INTEGER NOT NULL, due_date INTEGER{(hasSchedule ? ", scheduled_start INTEGER" : "")}{(hasDate ? ", planned_on TEXT" : "")});"
                + $"INSERT INTO tasks (id,title,created_at{(hasSchedule ? ",scheduled_start" : "")}) VALUES ('task_test','Legacy',1710000000{(hasSchedule ? $",{Start.ToUnixTimeSeconds()}" : "")});"
                + "INSERT INTO tasks (id,title,created_at,due_date) VALUES ('task_due','Due',1710000000,1710000000);";
            await cmd.ExecuteNonQueryAsync();
        }
        var store = new SqliteTaskStore(Db);
        for (var i = 0; i < 2; ++i)
        {
            await store.InitializeAsync();
            var task = (await store.GetTaskAsync("task_test"))!;
            Assert.Equal(hasSchedule ? Start : (DateTimeOffset?)null, task.ScheduledStart);
            Assert.Equal(hasSchedule && !hasDate ? TaskDateValues.FromDateTimeOffset(Start) : null, task.PlannedOn);
            Assert.Equal(TaskDateValues.FromDateTimeOffset(DateTimeOffset.FromUnixTimeSeconds(1710000000)),
                (await store.GetTaskAsync("task_due"))!.PlannedOn);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Combined_update_versions(bool published, bool reopening)
    {
        var store = new SqliteTaskStore(Db);
        await store.InitializeAsync();
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true);
        var record = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(TaskRecord()))!;
        if (reopening)
        {
            record["completionState"] = JsonSerializer.SerializeToElement("completed");
            record["completedAt"] = record["createdAt"];
        }
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device,
            Page(1, [Space(), Space("space_other", name: "Other")], [Project()], published ? [record] : []));
        if (!published)
            await store.UpsertTaskAsync(new TaskItem { Id = "task_test", Title = "Unpublished", SpaceId = "space_test",
                CompletionState = reopening ? TaskCompletionState.Completed : TaskCompletionState.Open,
                CompletedAt = reopening ? Start : null });
        var before = (await store.GetTaskAsync("task_test"))!;
        var updated = await Service(store).UpdateTaskAsync(new()
        {
            TaskId = before.Id, ExpectedRevision = before.Revision,
            ScheduledStart = OptionalValue<DateTimeOffset?>.Set(Start),
            ScheduledEnd = OptionalValue<DateTimeOffset?>.Set(Start.AddHours(1)),
            Title = OptionalValue<string?>.Set("Renamed"),
            Space = OptionalValue<string?>.Set("space_other"),
            Completed = OptionalValue<bool>.Set(!reopening),
        });
        Assert.Equal(Start, updated.ScheduledStart);
        Assert.Equal(Start.AddHours(1), updated.ScheduledEnd);
        Assert.Equal("Renamed", updated.Title);
        Assert.Equal("space_other", updated.SpaceId);
        Assert.Equal(!reopening, updated.IsCompleted);
        Assert.Equal(before.Revision + 4, updated.Revision);
        var operations = (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Where(x => x.EntityId == before.Id).ToArray();
        string[] expected = reopening ? ["task.patch", "task.reopen", "task.moveTree", "task.updateOpenzaFields"] :
            ["task.patch", "task.moveTree", "task.updateOpenzaFields", "task.complete"];
        var prefix = published ? Array.Empty<string>() : reopening ? ["task.createRich", "task.complete"] : ["task.createRich"];
        Assert.Equal(prefix.Concat(expected), operations.Select(x => x.Operation));
        var version = 1L;
        foreach (var entry in operations.Where(x => x.Operation != "task.createRich"))
        {
            using var doc = JsonDocument.Parse(entry.CanonicalJson);
            Assert.Equal(version++, doc.RootElement.GetProperty("baseEntityVersion").GetInt64());
        }
        Assert.Equal(version, await store.GetPersonalCloudEntityVersionAsync(Instance, before.Id));
    }

    [Fact]
    public async Task Unsupported_schedule_edit_does_not_enqueue_unpublished_task()
    {
        var store = new SqliteTaskStore(Db);
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new TaskItem { Id = "task_test", Title = "Unpublished" });
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => Service(store).UpdateTaskAsync(new()
        {
            TaskId = "task_test", ScheduledStart = OptionalValue<DateTimeOffset?>.Set(Start),
        }));
        Assert.Equal("upgrade_required", error.Code);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_combined_edit_does_not_apply_schedule(bool includeReminder)
    {
        var store = new SqliteTaskStore(Db);
        await store.InitializeAsync();
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true);
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, Page(1, [Space()], [Project()], [TaskRecord()]));
        var before = (await store.GetTaskAsync("task_test"))!;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Service(store).UpdateTaskAsync(new()
        {
            TaskId = before.Id, ExpectedRevision = before.Revision, Priority = OptionalValue<int>.Set(5),
            ReminderAt = includeReminder ? OptionalValue<DateTimeOffset?>.Set(Start) : default,
            ScheduledStart = OptionalValue<DateTimeOffset?>.Set(Start),
            ScheduledEnd = OptionalValue<DateTimeOffset?>.Set(Start.AddHours(1)),
        }));
        Assert.Equal(before.ScheduledStart, (await store.GetTaskAsync(before.Id))!.ScheduledStart);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Fact]
    public async Task Concurrent_schedule_change_rejects_stale_expected_revision()
    {
        var store = new SqliteTaskStore(Db);
        await store.InitializeAsync();
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true);
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, Page(1, [Space()], [Project()], [TaskRecord()]));
        var before = (await store.GetTaskAsync("task_test"))!;
        var service = new TaskApplicationService(store, () =>
        {
            Service(store).UpdateTaskAsync(new()
            {
                TaskId = before.Id, ExpectedRevision = before.Revision,
                ScheduledStart = OptionalValue<DateTimeOffset?>.Set(Start.AddHours(2)),
                ScheduledEnd = OptionalValue<DateTimeOffset?>.Set(Start.AddHours(3)),
            }).GetAwaiter().GetResult();
            return new()
            {
                ServiceOrigin = new Uri("https://openza-personal-test.workers.dev/"),
                InstanceId = Instance, DeviceId = Device, ArtifactVersion = "1.2.3", ConnectedAt = DateTimeOffset.UtcNow,
            };
        });
        await Assert.ThrowsAsync<TaskConflictException>(() => service.UpdateTaskAsync(new()
        {
            TaskId = before.Id, ExpectedRevision = before.Revision,
            ScheduledStart = OptionalValue<DateTimeOffset?>.Set(Start),
            ScheduledEnd = OptionalValue<DateTimeOffset?>.Set(Start.AddHours(1)),
        }));
        Assert.Equal(Start.AddHours(2), (await store.GetTaskAsync(before.Id))!.ScheduledStart);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Archived_project_task_can_move(bool schedule)
    {
        var store = new SqliteTaskStore(Db);
        await store.InitializeAsync();
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true);
        var project = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(Project()))!;
        project["status"] = JsonSerializer.SerializeToElement("archived");
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, Page(1, [Space(), Space("space_other", name: "Other")], [project], [TaskRecord()]));
        var before = (await store.GetTaskAsync("task_test"))!;
        Assert.Equal("project_test", before.ProjectId);
        var updated = await Service(store).UpdateTaskAsync(new()
        {
            TaskId = before.Id, ExpectedRevision = before.Revision,
            Space = OptionalValue<string?>.Set("space_other"),
            ScheduledStart = schedule ? OptionalValue<DateTimeOffset?>.Set(Start) : default,
            ScheduledEnd = schedule ? OptionalValue<DateTimeOffset?>.Set(Start.AddHours(1)) : default,
        });
        Assert.Equal("space_other", updated.SpaceId);
        Assert.Equal(schedule ? Start : before.ScheduledStart, updated.ScheduledStart);
    }

    [Theory]
    [InlineData(3, 8)]
    [InlineData(11, 1)]
    public async Task Dated_view_parity_per_task(int month, int day)
    {
        var today = new DateOnly(2026, month, day);
        var (start, end) = TaskDateValues.LocalDayBounds(today, TimeZoneInfo.Local);
        var store = new SqliteTaskStore(Db, timeProvider: new Clock(start.AddHours(12)));
        await store.InitializeAsync();
        var task = new TaskItem { Id = "task_test", Title = "Synthetic" };
        var cases = new (TaskItem Row, bool Today, bool Overdue, bool Calendar)[]
        {
            (task with { PlannedOn = today }, true, false, true),
            (task with { DeadlineOn = today }, true, false, true),
            (task with { PlannedOn = today.AddDays(-1), DeadlineOn = today, RecurrenceRule = "weekly" }, true, true, true),
            (task with { PlannedOn = today, DeadlineOn = today.AddDays(-1) }, true, true, true),
            (task with { PlannedOn = today, CompletionState = TaskCompletionState.Completed }, false, false, false),
            (task with { ScheduledStart = start.AddMilliseconds(-1), ScheduledEnd = start.AddHours(1), RecurrenceRule = "weekly" }, false, false, true),
            (task with { ScheduledStart = start }, true, false, true),
            (task with { ScheduledStart = end.AddMilliseconds(-1) }, true, false, true),
            (task with { ScheduledStart = end }, false, false, true),
            (task with { DeadlineAt = start.AddMilliseconds(-1) }, false, true, true),
            (task with { PlannedOn = today, PlannedAt = start.AddMilliseconds(-1) }, true, false, true),
            (task with { ScheduledEnd = start, DurationMinutes = 30, RecurrenceRule = "weekly" }, false, false, false),
        };
        foreach (var (row, inToday, inOverdue, inCalendar) in cases)
        {
            await store.UpsertTaskAsync(row);
            var counts = await store.GetTaskCountsAsync();
            foreach (var (kind, member, count) in new[] {
                (TaskListKind.Today, inToday, counts.Today), (TaskListKind.Overdue, inOverdue, counts.Overdue),
                (TaskListKind.Calendar, inCalendar, counts.Calendar) })
            {
                Assert.Equal(member ? 1 : 0, count);
                Assert.Equal(member, (await store.GetTasksAsync(new() { Kind = kind })).Any());
            }
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}
