using System.Text.Json;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Tests;

public sealed class TaskListProjectionTests
{
    private static readonly DateOnly Today = new(2026, 6, 17);
    private static DateTimeOffset Moment(DateOnly day) => TaskDateValues.ToLocalDateTime(day)!.Value;
    private static TaskItem Row(string id) => new() { Id = id, Title = id, CreatedAt = Moment(Today) };
    private static string[] Ids(IReadOnlyList<TaskItem> rows, TaskListKind kind, TaskRepeatScope repeat = TaskRepeatScope.Include) =>
        TaskListProjection.Build(rows, new() { Kind = kind, RepeatScope = repeat, IncludeSubtasks = true }, Today)
            .Select(task => task.Id).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void Dated_views_use_each_local_day_and_scheduled_start_not_end_or_duration()
    {
        TaskItem[] rows =
        [
            Row("date") with { PlannedOn = Today },
            Row("instant") with { PlannedAt = Moment(Today).AddMinutes(10).ToOffset(TimeSpan.FromHours(-12)) },
            Row("deadline") with { DeadlineOn = Today },
            Row("scheduled") with { ScheduledStart = Moment(Today).AddHours(9), ScheduledEnd = Moment(Today.AddDays(1)) },
            Row("crossing") with { ScheduledStart = Moment(Today).AddMinutes(-10), ScheduledEnd = Moment(Today).AddMinutes(10) },
            Row("end-only") with { ScheduledEnd = Moment(Today), DurationMinutes = 30 },
            Row("duration-only") with { DurationMinutes = 45 },
            Row("past-planned") with { PlannedOn = Today.AddDays(-1), DeadlineOn = Today.AddDays(3) },
            Row("done") with { PlannedOn = Today, CompletionState = TaskCompletionState.Completed },
        ];
        Assert.Equal(new[] { "date", "deadline", "instant", "scheduled" }, Ids(rows, TaskListKind.Today));
        Assert.Equal(new[] { "crossing", "past-planned" }, Ids(rows, TaskListKind.Overdue));
        Assert.Equal(new[] { "crossing", "date", "deadline", "instant", "past-planned", "scheduled" }, Ids(rows, TaskListKind.Calendar));
        Assert.Equal(new[] { "done" }, Ids(rows, TaskListKind.Completed));
        Assert.Equal(4, TaskListProjection.Counts(rows, null, Today).Today);
        Assert.Equal(2, TaskListProjection.Counts(rows, null, Today).Overdue);
    }

    [Theory]
    [InlineData(null, "every month", true)]
    [InlineData("", "every month", false)]
    [InlineData("  ", "every month", false)]
    [InlineData("local opaque rule", "every month", true)]
    [InlineData(null, null, false)]
    public void Repeat_filters_preserve_nonnull_local_precedence_and_do_not_parse_rules(string? local, string? source, bool repeating)
    {
        var task = Row("recurrence") with
        {
            PlannedOn = Today, RecurrenceRule = local,
            SourceIntegrationId = IntegrationIds.Todoist, SourceExternalId = "synthetic-source",
            SourceMetadataJson = JsonSerializer.Serialize(new { source = new { RecurrenceRule = source } }),
        };
        Assert.Equal(local ?? source, task.EffectiveRecurrenceRule);
        Assert.Equal(repeating ? new[] { task.Id } : [], Ids([task], TaskListKind.Today, TaskRepeatScope.Only));
        Assert.Equal(repeating ? [] : new[] { task.Id }, Ids([task], TaskListKind.Today, TaskRepeatScope.Exclude));
    }

    [Fact]
    public void Canonical_day_wins_a_mismatched_instant_and_null_dates_sort_last_in_both_directions()
    {
        var mismatch = Row("mismatch") with { PlannedOn = Today, PlannedAt = Moment(Today.AddDays(-1)).AddHours(12) };
        Assert.Equal(Moment(Today), mismatch.PlannedMoment);
        var earlier = Row("earlier") with { PlannedOn = Today.AddDays(3), DeadlineOn = Today.AddDays(-2) };
        var undated = Row("undated");
        var ascending = TaskListProjection.Build([mismatch, earlier, undated], new() { Kind = TaskListKind.Open, SortMode = TaskSortMode.Date }, Today);
        var descending = TaskListProjection.Build([mismatch, earlier, undated], new() { Kind = TaskListKind.Open, SortMode = TaskSortMode.Date, SortDirection = TaskSortDirection.Descending }, Today);
        Assert.Equal(new[] { "earlier", "mismatch", "undated" }, ascending.Select(task => task.Id));
        Assert.Equal(new[] { "mismatch", "earlier", "undated" }, descending.Select(task => task.Id));
    }

    [Fact]
    public void Matching_child_keeps_parent_context_and_its_descendants_without_unmatched_sibling()
    {
        TaskItem[] rows =
        [
            Row("parent"), Row("matching") with { ParentId = "parent", Title = "needle" },
            Row("sibling") with { ParentId = "parent" }, Row("grandchild") with { ParentId = "matching" },
        ];
        var visible = TaskListProjection.Build(rows, new() { SearchText = "needle", Kind = TaskListKind.Open }, Today);
        Assert.Equal(new[] { "parent", "matching", "grandchild" }, visible.Select(task => task.Id));
        Assert.Equal(1, TaskListProjection.Counts(rows, null, Today).All);
    }

    [Fact]
    public async Task Canonical_persisted_rows_reconcile_snapshot_counts_with_independent_sql_counts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"openza-query-contract-{Guid.NewGuid():N}");
        try
        {
            var store = new SqliteTaskStore(Path.Combine(root, "tasks.db"));
            await store.InitializeAsync();
            var today = DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime);
            await store.UpsertProjectAsync(new() { Id = "project-test", Name = "Synthetic project" });
            TaskItem[] rows =
            [
                Row("inbox") with { PlannedOn = today },
                Row("project") with { ProjectId = "project-test", WorkflowStatus = TaskWorkflowStatus.Next, DeadlineOn = today.AddDays(-1) },
                Row("schedule") with { ScheduledStart = Moment(today).AddHours(12), WorkflowStatus = TaskWorkflowStatus.Waiting },
                Row("completed") with { CompletionState = TaskCompletionState.Completed },
                Row("child") with { ParentId = "project", PlannedOn = today },
            ];
            foreach (var row in rows) await store.UpsertTaskAsync(row);
            var snapshot = await store.GetTaskListRefreshSnapshotAsync(new() { Kind = TaskListKind.Today });
            var sql = await store.GetTaskCountsAsync();
            Assert.Equal(Scalars(sql), Scalars(snapshot.Counts));
            Assert.Equal(sql.ActiveByProject.OrderBy(pair => pair.Key), snapshot.Counts.ActiveByProject.OrderBy(pair => pair.Key));
            Assert.Equal(sql.NextByProject.OrderBy(pair => pair.Key), snapshot.Counts.NextByProject.OrderBy(pair => pair.Key));
            Assert.Equal(5, snapshot.AllSpaceTasks.Count);
        }
        finally { TestDirectory.Delete(root); }
    }

    private static int[] Scalars(TaskCountSummary value) =>
        [value.Inbox, value.NextActions, value.Waiting, value.Someday, value.Today, value.Calendar, value.Overdue, value.Open, value.All, value.Completed];
}
