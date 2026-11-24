using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed class CalendarDayViewModelTests
{
    private static readonly DateOnly TestDay = new(2026, 6, 17);

    private static DateTimeOffset At(DateOnly day, int hour) => TaskDateValues.ToLocalDateTime(day)!.Value.AddHours(hour);

    private static TaskListItemViewModel Item(
        string id,
        DateTimeOffset start,
        DateTimeOffset? end = null,
        TaskCompletionState state = TaskCompletionState.Open,
        DateOnly? plannedOn = null,
        string? projectName = null) => new(new TaskItem
        {
            Id = id,
            Title = id,
            CreatedAt = start,
            ScheduledStart = start,
            ScheduledEnd = end,
            CompletionState = state,
            PlannedOn = plannedOn,
        }, projectName);

    [Fact]
    public void Blocks_use_local_selected_day_one_hour_fallback_and_done_states()
    {
        var viewModel = new CalendarDayViewModel();
        viewModel.SetSelectedDate(TestDay);
        viewModel.SetNow(At(TestDay, 11));
        var missingEnd = Item("missing end", At(TestDay, 9));
        var completed = Item("completed", At(TestDay, 10), At(TestDay, 11), TaskCompletionState.Completed);
        var tomorrow = Item("tomorrow", At(TestDay.AddDays(1), 9), At(TestDay.AddDays(1), 10));
        var invalid = Item("invalid", At(TestDay, 13), At(TestDay, 12));

        viewModel.SetScheduledTasks([missingEnd, completed, tomorrow, invalid]);

        var fallback = Assert.Single(viewModel.Blocks, block => block.Item == missingEnd);
        Assert.Equal(At(TestDay, 10), fallback.End);
        Assert.Equal(64, fallback.Height);
        Assert.True(fallback.IsNotDone);
        Assert.Equal("Not done", fallback.StateText);
        Assert.Equal(0.55, fallback.Opacity);

        var done = Assert.Single(viewModel.Blocks, block => block.Item == completed);
        Assert.True(done.IsCompleted);
        Assert.Equal("Done", done.StateText);
        Assert.False(done.IsNotDone);
        Assert.Equal(2, viewModel.Blocks.Count);
    }

    [Fact]
    public void Blocks_crossing_day_boundaries_are_clipped_to_the_selected_day()
    {
        var viewModel = new CalendarDayViewModel();
        viewModel.SetSelectedDate(TestDay);
        var intoDay = Item("into day", At(TestDay.AddDays(-1), 23), At(TestDay, 2));
        var acrossDay = Item("across day", At(TestDay.AddDays(-1), 23), At(TestDay.AddDays(1), 1));
        var outOfDay = Item("out of day", At(TestDay.AddDays(-1), 21), At(TestDay.AddDays(-1), 23));

        viewModel.SetScheduledTasks([intoDay, acrossDay, outOfDay]);

        var morning = Assert.Single(viewModel.Blocks, block => block.Item == intoDay);
        Assert.Equal(0, morning.Top);
        Assert.Equal(2 * CalendarDayViewModel.HourHeight, morning.Height);
        Assert.Equal("00:00 – 02:00", morning.TimeText);

        var fullDay = Assert.Single(viewModel.Blocks, block => block.Item == acrossDay);
        Assert.Equal(0, fullDay.Top);
        Assert.Equal(CalendarDayViewModel.HoursPerDay * CalendarDayViewModel.HourHeight, fullDay.Height);
        Assert.Equal("00:00 – 24:00", fullDay.TimeText);
        Assert.Equal(2, viewModel.Blocks.Count);
    }

    [Fact]
    public void Block_crossing_the_fall_back_hour_uses_its_absolute_duration()
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var timeZone = TimeZoneInfo.CreateCustomTimeZone(
            "Calendar test Eastern",
            TimeSpan.FromHours(-5),
            "Calendar test Eastern",
            "EST",
            "EDT",
            [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2026, 1, 1),
                new DateTime(2026, 12, 31),
                TimeSpan.FromHours(1),
                daylightStart,
                daylightEnd)]);
        var viewModel = new CalendarDayViewModel(timeZone);
        var day = new DateOnly(2026, 11, 1);
        viewModel.SetSelectedDate(day);
        var start = new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-4));
        var end = new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-5));
        var task = Item("fall back", start, end);

        viewModel.SetScheduledTasks([task]);

        var block = Assert.Single(viewModel.Blocks);
        Assert.Equal(start, block.Start);
        Assert.Equal(end, block.End);
        Assert.Equal(new DateTime(2026, 11, 1, 1, 30, 0), block.LocalStart);
        Assert.Equal(new DateTime(2026, 11, 1, 2, 30, 0), block.LocalEnd);
        Assert.Equal(CalendarDayViewModel.HourHeight, block.Height);
    }

    [Fact]
    public void Short_blocks_use_compact_content_and_keep_accessible_time()
    {
        var viewModel = new CalendarDayViewModel();
        viewModel.SetSelectedDate(TestDay);
        var task = Item("short", At(TestDay, 9), At(TestDay, 9).AddMinutes(15));
        viewModel.SetScheduledTasks([task]);

        var block = Assert.Single(viewModel.Blocks);
        Assert.Equal(32, block.Height);
        Assert.True(block.IsCompact);
        Assert.Contains("09:00 – 09:15", block.AccessibleName);
    }

    [Fact]
    public void Overlapping_blocks_share_lanes_and_reuse_a_lane_after_an_overlap_ends()
    {
        var viewModel = new CalendarDayViewModel();
        viewModel.SetSelectedDate(TestDay);
        viewModel.SetScheduledTasks([
            Item("first", At(TestDay, 9), At(TestDay, 11)),
            Item("second", At(TestDay, 10), At(TestDay, 12)),
            Item("third", At(TestDay, 11), At(TestDay, 13)),
            Item("separate", At(TestDay, 13), At(TestDay, 14)),
        ]);

        var first = viewModel.Blocks.Single(block => block.Title == "first");
        var second = viewModel.Blocks.Single(block => block.Title == "second");
        var third = viewModel.Blocks.Single(block => block.Title == "third");
        var separate = viewModel.Blocks.Single(block => block.Title == "separate");
        Assert.Equal(0, first.LeftFraction);
        Assert.Equal(0.5, first.WidthFraction);
        Assert.Equal(0.5, second.LeftFraction);
        Assert.Equal(0.5, third.WidthFraction);
        Assert.Equal(0, third.LeftFraction);
        Assert.Equal(0, separate.LeftFraction);
        Assert.Equal(1, separate.WidthFraction);
    }

    [Fact]
    public void Day_navigation_reprojects_blocks_and_today_uses_the_current_local_date()
    {
        var viewModel = new CalendarDayViewModel();
        viewModel.SetNow(At(TestDay, 9));
        viewModel.SetSelectedDate(TestDay);
        viewModel.SetScheduledTasks([Item("today", At(TestDay, 9), At(TestDay, 10))]);

        viewModel.PreviousDay();
        Assert.Empty(viewModel.Blocks);
        viewModel.NextDay();
        Assert.Single(viewModel.Blocks);
        viewModel.NextDay();
        Assert.Empty(viewModel.Blocks);
        viewModel.GoToToday();

        Assert.Equal(TestDay, viewModel.SelectedDate);
        Assert.True(viewModel.IsSelectedDayToday);
        Assert.Equal(9 * CalendarDayViewModel.HourHeight, viewModel.NowTop);
        Assert.Single(viewModel.Blocks);
    }

    [Fact]
    public void Today_panel_search_filters_existing_task_metadata_and_is_closed_by_default()
    {
        var viewModel = new CalendarDayViewModel();
        var task = Item("Design review", At(TestDay, 9), At(TestDay, 10), plannedOn: TestDay, projectName: "Project X");
        viewModel.SetNow(At(TestDay, 9));
        viewModel.SetTodayTaskSource([task], null);

        Assert.False(viewModel.IsTodayPanelOpen);
        Assert.Single(viewModel.FilteredTodayTasks);
        Assert.Contains("1 task", viewModel.TodaySummaryText);
        viewModel.TodaySearchText = "project x";
        Assert.Single(viewModel.FilteredTodayTasks);
        viewModel.TodaySearchText = "missing";
        Assert.Empty(viewModel.FilteredTodayTasks);
        Assert.Equal("No matching tasks", viewModel.TodayEmptyText);
        viewModel.ToggleTodayPanel();
        Assert.True(viewModel.IsTodayPanelOpen);
        viewModel.CloseTodayPanel();
        Assert.False(viewModel.IsTodayPanelOpen);
    }

    [Fact]
    public void Set_now_reprojects_today_membership_when_the_local_date_changes()
    {
        var viewModel = new CalendarDayViewModel();
        var yesterday = Item("yesterday", At(TestDay, 8), plannedOn: TestDay);
        var tomorrow = Item("tomorrow", At(TestDay.AddDays(1), 8), plannedOn: TestDay.AddDays(1));
        viewModel.SetNow(At(TestDay, 23));
        viewModel.SetTodayTaskSource([yesterday, tomorrow], null);

        Assert.Equal("yesterday", Assert.Single(viewModel.FilteredTodayTasks).Title);

        viewModel.SetNow(At(TestDay.AddDays(1), 0));

        Assert.Equal("tomorrow", Assert.Single(viewModel.FilteredTodayTasks).Title);
        Assert.Contains(At(TestDay.AddDays(1), 0).ToString("dddd, d MMMM"), viewModel.TodaySummaryText);
    }
}
