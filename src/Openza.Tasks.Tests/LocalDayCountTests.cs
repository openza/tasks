using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

[Collection(nameof(EditorTestCollection))]
public sealed class LocalDayCountTests(EditorTestSession fixture) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-local-day-counts", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(3, 8, -5, -4, 23)]
    [InlineData(11, 1, -4, -5, 25)]
    public void Boundaries_resolve_each_midnight_offset_across_dst(int month, int day, int startOffset, int endOffset, int hours)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var date = new DateOnly(2026, month, day);
        var range = TaskDateValues.LocalDayBounds(date, zone);
        Assert.Equal(TimeSpan.FromHours(startOffset), range.Start.Offset);
        Assert.Equal(TimeSpan.FromHours(endOffset), range.End.Offset);
        Assert.Equal(TimeSpan.FromHours(hours), range.End - range.Start);
        Assert.Equal(date.ToDateTime(TimeOnly.MinValue), range.Start.DateTime);
        Assert.Equal(date.AddDays(1).ToDateTime(TimeOnly.MinValue), range.End.DateTime);
    }

    [Fact]
    public void Midnight_transition_uses_first_existing_or_first_repeated_wall_time()
    {
        var atMidnight = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1), 3, 8);
        var atOne = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 11, 1);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1), atMidnight, atOne);
        var zone = TimeZoneInfo.CreateCustomTimeZone("Synthetic midnight DST", TimeSpan.FromHours(-5), "Synthetic", "Standard", "Daylight", [rule]);
        var spring = TaskDateValues.LocalDayBounds(new(2026, 3, 8), zone);
        Assert.Equal(1, spring.Start.Hour);
        Assert.Equal(TimeSpan.FromHours(23), spring.End - spring.Start);
        var autumn = TaskDateValues.LocalDayBounds(new(2026, 11, 1), zone);
        Assert.Equal(0, autumn.Start.Hour);
        Assert.Equal(TimeSpan.FromHours(-4), autumn.Start.Offset);
        Assert.Equal(TimeSpan.FromHours(25), autumn.End - autumn.Start);
    }

    // Run these same cases with TZ=America/New_York to exercise SQLite's actual
    // localtime conversion and projection in a separate process; no machine TZ change.
    [Theory]
    [InlineData(3, 8)]
    [InlineData(11, 1)]
    public async Task Sql_and_projection_counts_agree_at_every_local_day_boundary(int month, int day)
    {
        var date = new DateOnly(2026, month, day);
        var wallNoon = date.ToDateTime(new TimeOnly(12, 0));
        var noon = new DateTimeOffset(wallNoon, TimeZoneInfo.Local.GetUtcOffset(wallNoon));
        var bounds = TaskDateValues.LocalDayBounds(date, TimeZoneInfo.Local);
        if (Environment.GetEnvironmentVariable("TZ") == "America/New_York")
        {
            Assert.Equal("America/New_York", TimeZoneInfo.Local.Id);
            Assert.Equal(TimeSpan.FromHours(month == 3 ? 23 : 25), bounds.End - bounds.Start);
        }
        DateTimeOffset[] moments = [bounds.Start.AddSeconds(-1), bounds.Start, bounds.Start.AddMinutes(30),
            bounds.Start.AddMinutes(90), bounds.Start.AddMinutes(150), noon, bounds.End.AddSeconds(-1), bounds.End];
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"), timeProvider: new FixedClock(noon));
        await store.InitializeAsync();
        foreach (var field in new[] { "schedule", "planned", "deadline" })
        {
            for (var index = 0; index < moments.Length; index++)
            {
                var task = new TaskItem { Id = $"{field}_{index}", Title = $"Synthetic {field} {index}" };
                task = field switch
                {
                    "schedule" => task with { ScheduledStart = moments[index] },
                    "planned" => task with { PlannedAt = moments[index] },
                    _ => task with { DeadlineAt = moments[index] },
                };
                await store.UpsertTaskAsync(task);
            }
        }
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE tasks SET planned_on = NULL, deadline_on = NULL";
            await command.ExecuteNonQueryAsync();
        }
        var rows = await store.GetTasksAsync(new() { Kind = TaskListKind.All, IncludeSubtasks = true });
        var projection = TaskListProjection.Counts(rows, null, date);
        var sql = await store.GetTaskCountsAsync();
        Assert.Equal(18, sql.Today); Assert.Equal(3, sql.Overdue); Assert.Equal(24, sql.Calendar);
        Assert.Equal(projection.Today, sql.Today);
        Assert.Equal(projection.Overdue, sql.Overdue);
        Assert.Equal(projection.Calendar, sql.Calendar);
        Assert.Equal(projection.Open, sql.Open);
        Assert.Equal(projection.All, sql.All);
        foreach (var (kind, expected) in new[] { (TaskListKind.Today, 18), (TaskListKind.Overdue, 3), (TaskListKind.Calendar, 24) })
        {
            var query = new TaskQuery { Kind = kind };
            Assert.Equal(expected, (await store.GetTasksAsync(query)).Count);
            var snapshot = await store.GetTaskListRefreshSnapshotAsync(query);
            Assert.Equal(expected, snapshot.VisibleTasks.Count);
            Assert.Equal(24, snapshot.AllSpaceTasks.Count);
            Assert.Equal(sql.Today, snapshot.Counts.Today);
            Assert.Equal(sql.Overdue, snapshot.Counts.Overdue);
            Assert.Equal(sql.Calendar, snapshot.Counts.Calendar);
        }
        await fixture.Session.Dispatch(async () =>
        {
            var preferences = new DesktopPreferencesStore(Path.Combine(_directory, "settings.json"));
            await preferences.SaveAsync(new() { AutomaticSyncEnabled = false, AutomaticRestorePointsEnabled = false, ShowGetStarted = false });
            var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences);
            await vm.InitializeAsync();
            foreach (var (kind, expected) in new[] { (TaskListKind.Today, 18), (TaskListKind.Overdue, 3), (TaskListKind.Calendar, 24) })
            {
                var navigation = vm.NavigationItems.Single(item => item.Kind == kind);
                Assert.Equal(expected, navigation.Count);
                await vm.SelectNavigationAsync(navigation);
                Assert.Equal(expected, vm.Tasks.Count);
            }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Each_read_captures_one_day_even_when_clock_crosses_midnight()
    {
        var date = new DateOnly(2031, 2, 3);
        var midnight = date.ToDateTime(TimeOnly.MinValue);
        var now = new DateTimeOffset(midnight, TimeZoneInfo.Local.GetUtcOffset(midnight));
        var clock = new AdvancingClock(now);
        var store = new SqliteTaskStore(Path.Combine(_directory, "capture.db"), timeProvider: clock);
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new() { Id = "today", Title = "Synthetic today", PlannedOn = date });
        await store.UpsertTaskAsync(new() { Id = "tomorrow", Title = "Synthetic tomorrow", PlannedOn = date.AddDays(1) });
        var query = new TaskQuery { Kind = TaskListKind.Today };
        clock.Reads = 0;
        Assert.Equal("today", Assert.Single(await store.GetTasksAsync(query)).Id);
        Assert.Equal(1, clock.Reads);
        clock.Reads = 0;
        var snapshot = await store.GetTaskListRefreshSnapshotAsync(query);
        Assert.Equal("today", Assert.Single(snapshot.VisibleTasks).Id);
        Assert.Equal(1, snapshot.Counts.Today);
        Assert.Equal(0, snapshot.Counts.Overdue);
        Assert.Equal(2, snapshot.AllSpaceTasks.Count);
        Assert.Equal(1, clock.Reads);
        clock.Reads = 0;
        var counts = await store.GetTaskCountsAsync();
        Assert.Equal(1, counts.Today);
        Assert.Equal(0, counts.Overdue);
        Assert.Equal(1, clock.Reads);
    }

    private sealed class AdvancingClock(DateTimeOffset now) : TimeProvider
    {
        public int Reads { get; set; }
        public override DateTimeOffset GetUtcNow() => (Reads++ == 0 ? now : now.AddDays(1)).ToUniversalTime();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    public void Dispose() => TestDirectory.Delete(_directory);
}
