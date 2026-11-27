using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Desktop.ViewModels;
using Openza.Tasks.Desktop.ViewModels.Navigation;

namespace Openza.Tasks.Tests;

public sealed class CalendarBlockPickerViewModelTests
{
    private static readonly DateOnly TestDay = new(2026, 6, 17);

    private static DateTimeOffset At(DateOnly day, int hour, int minute = 0) =>
        TaskDateValues.ToLocalDateTime(day)!.Value.AddHours(hour).AddMinutes(minute);

    private static TimeZoneInfo FindTimeZone(string ianaId, string windowsId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
        }
    }

    private static TaskListItemViewModel Item(
        string id,
        TaskWorkflowStatus status,
        TaskCompletionState completion = TaskCompletionState.Open,
        string? parentId = null)
    {
        var start = At(TestDay, 9);
        return new TaskListItemViewModel(new TaskItem
        {
            Id = id,
            Title = id,
            CreatedAt = start,
            WorkflowStatus = status,
            CompletionState = completion,
            ParentId = parentId,
        }, null);
    }

    [Fact]
    public void Picker_defaults_to_one_hour_and_lists_every_open_task_with_next_actions_first()
    {
        var calendarDay = new CalendarDayViewModel();
        calendarDay.SetSelectedDate(TestDay);
        var inbox = Item("Inbox task", TaskWorkflowStatus.Inbox);
        var child = Item("A next subtask", TaskWorkflowStatus.Next, parentId: "parent");
        var next = Item("Z next task", TaskWorkflowStatus.Next);
        var completed = Item("Completed task", TaskWorkflowStatus.Next, TaskCompletionState.Completed);
        calendarDay.SetDayTasks([inbox, next, completed, child]);
        var start = At(TestDay, 9, 7);

        var picker = new CalendarBlockPickerViewModel(calendarDay, start);

        Assert.Equal(60m, picker.DurationMinutes);
        Assert.Equal(start, picker.Start);
        Assert.Equal(start.AddHours(1), picker.End);
        Assert.Equal(new[] { "Z next task", "Inbox task" }, picker.FilteredTasks.Select(item => item.Title));
        Assert.DoesNotContain(child, calendarDay.BlockPickerTasks);
        Assert.DoesNotContain(child, picker.FilteredTasks);
        Assert.Empty(calendarDay.Blocks);

        picker.SearchText = "inbox";
        picker.SelectedTask = inbox;
        Assert.Equal(inbox, Assert.Single(picker.FilteredTasks));
        var result = Assert.IsType<CalendarBlockPickerResult>(picker.CreateResult());
        Assert.Equal(inbox, result.Item);
        Assert.Equal(start, result.Start);
        Assert.Equal(start.AddHours(1), result.End);
    }

    [Theory]
    [InlineData(45, 0)]
    [InlineData(45, 30)]
    [InlineData(46, 0)]
    [InlineData(58, 0)]
    public void Add_block_late_today_defaults_to_one_hour_across_midnight(int minute, int second)
    {
        var calendarDay = new CalendarDayViewModel(TimeZoneInfo.Utc);
        calendarDay.SetSelectedDate(TestDay);
        var task = Item("Late task", TaskWorkflowStatus.Next);
        calendarDay.SetDayTasks([task]);
        var now = new DateTimeOffset(TestDay.ToDateTime(new TimeOnly(23, minute, second)), TimeSpan.Zero);
        var picker = new CalendarBlockPickerViewModel(calendarDay, calendarDay.GetAddBlockStart(now)) { SelectedTask = task };

        var result = Assert.IsType<CalendarBlockPickerResult>(picker.CreateResult());

        Assert.Equal("23:45", picker.StartTimeText);
        Assert.Equal(now.Date.AddHours(23).AddMinutes(45), result.Start.UtcDateTime);
        Assert.Equal(now.Date.AddDays(1).AddMinutes(45), result.End.UtcDateTime);
    }

    [Fact]
    public void Picker_keeps_the_exact_typed_start_after_a_snapped_suggestion()
    {
        var calendarDay = new CalendarDayViewModel(TimeZoneInfo.Utc);
        calendarDay.SetSelectedDate(TestDay);
        var task = Item("Exact start task", TaskWorkflowStatus.Next);
        calendarDay.SetDayTasks([task]);
        var now = new DateTimeOffset(TestDay.ToDateTime(new TimeOnly(11, 4)), TimeSpan.Zero);
        var picker = new CalendarBlockPickerViewModel(calendarDay, calendarDay.GetAddBlockStart(now)) { SelectedTask = task };
        Assert.Equal("11:15", picker.StartTimeText);

        picker.StartTimeText = "11:04";

        var result = Assert.IsType<CalendarBlockPickerResult>(picker.CreateResult());
        Assert.Equal(now, result.Start);
        Assert.Equal(now.AddHours(1), result.End);
    }

    [Fact]
    public void Day_task_refresh_keeps_identical_snapshot_but_rebuilds_when_context_changes()
    {
        var calendarDay = new CalendarDayViewModel();
        calendarDay.SetSelectedDate(TestDay);
        var label = new LabelItem { Id = "calendar-refresh-label", Name = "Calendar" };
        var task = new TaskItem
        {
            Id = "calendar-refresh-task",
            Title = "Calendar refresh task",
            CreatedAt = At(TestDay, 8),
            WorkflowStatus = TaskWorkflowStatus.Next,
            PlannedOn = TestDay,
            ScheduledStart = At(TestDay, 9),
            ScheduledEnd = At(TestDay, 10),
            Labels = [label],
        };
        var initialContext = new TaskViewContext(null, TaskListKind.Calendar);
        TaskListItemViewModel ItemFor(TaskItem snapshot) =>
            new(snapshot, null, TaskListKind.Calendar);

        calendarDay.SetDayTasks([ItemFor(task)], initialContext);
        var originalBlock = Assert.Single(calendarDay.Blocks);

        calendarDay.SetDayTasks([ItemFor(task with { Labels = [label with { }] })], initialContext);

        Assert.Same(originalBlock, Assert.Single(calendarDay.Blocks));

        calendarDay.SetDayTasks(
            [ItemFor(task with { Labels = [label with { }] })],
            new TaskViewContext(SpaceIds.Default, TaskListKind.Calendar));

        Assert.NotSame(originalBlock, Assert.Single(calendarDay.Blocks));
    }

    [Fact]
    public void Edit_picker_keeps_task_fixed_and_updates_start_and_duration()
    {
        var calendarDay = new CalendarDayViewModel();
        var task = Item("Scheduled task", TaskWorkflowStatus.Waiting);
        calendarDay.SetDayTasks([task]);
        var start = At(TestDay, 9);
        var picker = new CalendarBlockPickerViewModel(calendarDay, start, task, 45);

        Assert.True(picker.IsEditing);
        Assert.False(picker.IsTaskSelectionVisible);
        Assert.Equal(task, picker.SelectedTask);
        Assert.Equal(45m, picker.DurationMinutes);

        picker.StartTimeText = "10:30";
        Assert.True(picker.CanSchedule);
        Assert.Equal(start.AddHours(1).AddMinutes(30), picker.Start);
        Assert.Equal(start.AddHours(2).AddMinutes(15), picker.End);

        picker.StartTimeText = "not a time";
        Assert.False(picker.CanSchedule);
        Assert.Null(picker.CreateResult());
    }

    [Fact]
    public void Picker_rejects_a_start_time_skipped_by_daylight_saving()
    {
        var timeZone = FindTimeZone("America/New_York", "Eastern Standard Time");
        var day = new DateOnly(2026, 3, 8);
        var start = new DateTimeOffset(2026, 3, 8, 1, 30, 0, TimeSpan.FromHours(-5));
        var calendarDay = new CalendarDayViewModel(timeZone);
        var task = Item("Scheduled task", TaskWorkflowStatus.Waiting);
        calendarDay.SetDayTasks([task]);
        var picker = new CalendarBlockPickerViewModel(calendarDay, start, task, 60);

        picker.StartTimeText = "02:30";

        Assert.True(picker.HasInvalidStartTime);
        Assert.False(picker.CanSchedule);
        Assert.Null(picker.CreateResult());
    }

    [Fact]
    public void Editing_an_ambiguous_start_preserves_its_original_offset()
    {
        var timeZone = FindTimeZone("America/New_York", "Eastern Standard Time");
        var day = new DateOnly(2026, 11, 1);
        var start = new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-4));
        var calendarDay = new CalendarDayViewModel(timeZone);
        var task = Item("Scheduled task", TaskWorkflowStatus.Waiting);
        calendarDay.SetDayTasks([task]);
        var picker = new CalendarBlockPickerViewModel(calendarDay, start, task, 60);

        Assert.Equal(start, picker.Start);
        Assert.Equal(start, Assert.IsType<CalendarBlockPickerResult>(picker.CreateResult()).Start);
    }

    [Fact]
    public void Editing_a_utc_loaded_second_fall_back_occurrence_keeps_the_local_offset()
    {
        var timeZone = FindTimeZone("America/New_York", "Eastern Standard Time");
        var storedStart = new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero);
        var expectedStart = new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-5));
        var calendarDay = new CalendarDayViewModel(timeZone);
        var task = Item("Scheduled task", TaskWorkflowStatus.Waiting);
        calendarDay.SetDayTasks([task]);
        var picker = new CalendarBlockPickerViewModel(calendarDay, storedStart, task, 60);

        Assert.Equal(expectedStart, picker.Start);
        Assert.Equal(expectedStart, Assert.IsType<CalendarBlockPickerResult>(picker.CreateResult()).Start);
    }

    [Fact]
    public void Already_scheduled_open_tasks_remain_searchable_and_can_receive_a_replacement_interval()
    {
        var timeZone = FindTimeZone("America/New_York", "Eastern Standard Time");
        var existingStart = new DateTimeOffset(2026, 6, 17, 13, 0, 0, TimeSpan.Zero);
        var existingEnd = new DateTimeOffset(2026, 6, 17, 14, 0, 0, TimeSpan.Zero);
        var replacementStart = new DateTimeOffset(2026, 6, 17, 15, 0, 0, TimeSpan.Zero);
        var scheduledTask = new TaskListItemViewModel(Item("Already scheduled", TaskWorkflowStatus.Next).Task with
        {
            ScheduledStart = existingStart,
            ScheduledEnd = existingEnd,
        }, null);
        var calendarDay = new CalendarDayViewModel(timeZone);
        calendarDay.SetDayTasks([scheduledTask]);
        var picker = new CalendarBlockPickerViewModel(calendarDay, replacementStart);

        picker.SearchText = "scheduled";

        Assert.Equal(scheduledTask, Assert.Single(picker.FilteredTasks));
        picker.SelectedTask = scheduledTask;
        Assert.True(picker.HasSelectedTaskSchedule);
        var localStart = calendarDay.LocalDateTimeFor(existingStart);
        var localEnd = calendarDay.LocalDateTimeFor(existingEnd);
        Assert.Equal($"Existing interval: {localStart:ddd, d MMM · HH:mm} – {localEnd:ddd, d MMM · HH:mm}", picker.ExistingScheduleText);
        Assert.Equal("This task already has a scheduled interval. Adding it here will replace that interval.", picker.ReplacementNoticeText);
        Assert.Equal("Replace scheduled interval", picker.AddActionText);
        var result = Assert.IsType<CalendarBlockPickerResult>(picker.CreateResult());
        Assert.Equal(replacementStart, result.Start);
        Assert.Equal(replacementStart.AddHours(1), result.End);
        Assert.NotEqual(existingStart, result.Start);
    }

    [Fact]
    public void Picker_warns_for_start_only_schedule_using_timeline_one_hour_fallback()
    {
        var timeZone = FindTimeZone("America/New_York", "Eastern Standard Time");
        var day = new DateOnly(2026, 6, 17);
        var existingStart = new DateTimeOffset(2026, 6, 17, 13, 0, 0, TimeSpan.Zero);
        var scheduledTask = new TaskListItemViewModel(Item("Start-only schedule", TaskWorkflowStatus.Next).Task with
        {
            ScheduledStart = existingStart,
            ScheduledEnd = null,
        }, null);
        var calendarDay = new CalendarDayViewModel(timeZone);
        calendarDay.SetSelectedDate(day);
        calendarDay.SetDayTasks([scheduledTask]);
        var timelineBlock = Assert.Single(calendarDay.Blocks);
        var picker = new CalendarBlockPickerViewModel(
            calendarDay,
            new DateTimeOffset(2026, 6, 17, 15, 0, 0, TimeSpan.Zero));
        picker.SelectedTask = scheduledTask;

        Assert.Equal(existingStart.AddHours(1), timelineBlock.End);
        Assert.True(picker.HasSelectedTaskSchedule);
        var localStart = calendarDay.LocalDateTimeFor(existingStart);
        var localEnd = calendarDay.LocalDateTimeFor(existingStart.AddHours(1));
        Assert.Equal($"Existing interval: {localStart:ddd, d MMM · HH:mm} – {localEnd:ddd, d MMM · HH:mm}", picker.ExistingScheduleText);
        Assert.Equal("This task already has a scheduled interval. Adding it here will replace that interval.", picker.ReplacementNoticeText);
        Assert.Equal("Replace scheduled interval", picker.AddActionText);
    }
}
