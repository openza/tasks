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
        Assert.True(morning.CanResizeEnd);

        var fullDay = Assert.Single(viewModel.Blocks, block => block.Item == acrossDay);
        Assert.Equal(0, fullDay.Top);
        Assert.Equal(CalendarDayViewModel.HoursPerDay * CalendarDayViewModel.HourHeight, fullDay.Height);
        Assert.Equal("00:00 – 24:00", fullDay.TimeText);
        Assert.False(fullDay.CanResizeEnd);
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
        Assert.Equal(new DateTime(2026, 11, 1, 1, 30, 0), block.LocalEnd);
        Assert.Equal("01:30 – 01:30", block.TimeText);
        Assert.Equal(CalendarDayViewModel.HourHeight, block.Height);

        var midnightEnd = TimeZoneInfo.TransitionTime.CreateFixedDateRule(
            new DateTime(1, 1, 1, 0, 0, 0), 11, 1);
        var midnightZone = TimeZoneInfo.CreateCustomTimeZone(
            "Calendar test midnight Eastern",
            TimeSpan.FromHours(-5),
            "Calendar test midnight Eastern",
            "EST",
            "EDT",
            [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2026, 1, 1),
                new DateTime(2026, 12, 31),
                TimeSpan.FromHours(1),
                daylightStart,
                midnightEnd)]);
        var midnightViewModel = new CalendarDayViewModel(midnightZone);
        var previousDate = new DateOnly(2026, 10, 31);
        var beforeMidnight = new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.FromHours(-4));
        var afterFallback = new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.FromHours(-5));
        var midnightTask = Item("midnight fallback", beforeMidnight, afterFallback);
        midnightViewModel.SetSelectedDate(previousDate);
        midnightViewModel.SetScheduledTasks([midnightTask]);
        var lastHour = Assert.Single(midnightViewModel.Blocks);
        Assert.Equal("23:30 – 23:30", lastHour.TimeText);
        Assert.Equal(CalendarDayViewModel.HourHeight, lastHour.Height);

        midnightViewModel.SetSelectedDate(previousDate.AddDays(1));
        Assert.Empty(midnightViewModel.Blocks);
    }

    [Fact]
    public void Local_day_boundaries_use_the_first_real_instant_for_repeated_skipped_and_normal_midnight()
    {
        var repeatedMidnightZone = FindTimeZone("America/Goose_Bay", "Atlantic Standard Time");
        var repeatedDay = new DateOnly(2000, 10, 29);
        var repeatedStart = new DateTimeOffset(2000, 10, 29, 0, 0, 30, TimeSpan.FromHours(-3));
        var repeatedEnd = new DateTimeOffset(2000, 10, 29, 0, 0, 45, TimeSpan.FromHours(-3));
        var repeatedViewModel = new CalendarDayViewModel(repeatedMidnightZone);
        repeatedViewModel.SetSelectedDate(repeatedDay);
        repeatedViewModel.SetScheduledTasks([Item("before second midnight", repeatedStart, repeatedEnd)]);

        var repeatedBlock = Assert.Single(repeatedViewModel.Blocks);
        Assert.Equal("00:00 – 00:00", repeatedBlock.TimeText);

        var skippedMidnightZone = FindTimeZone("Africa/Cairo", "Egypt Standard Time");
        var skippedDay = new DateOnly(2010, 4, 30);
        var skippedStart = new DateTimeOffset(2010, 4, 30, 1, 30, 0, TimeSpan.FromHours(3));
        var skippedEnd = new DateTimeOffset(2010, 4, 30, 2, 30, 0, TimeSpan.FromHours(3));
        var skippedViewModel = new CalendarDayViewModel(skippedMidnightZone);
        skippedViewModel.SetSelectedDate(skippedDay);
        skippedViewModel.SetScheduledTasks([Item("after skipped midnight", skippedStart, skippedEnd)]);

        var skippedBlock = Assert.Single(skippedViewModel.Blocks);
        Assert.Equal(new DateTime(2010, 4, 30, 1, 30, 0), skippedBlock.LocalStart);
        Assert.Equal("01:30 – 02:30", skippedBlock.TimeText);

        var normalDstZone = FindTimeZone("America/New_York", "Eastern Standard Time");
        var normalDstDay = new DateOnly(2026, 3, 8);
        var normalDstStart = new DateTimeOffset(2026, 3, 8, 3, 30, 0, TimeSpan.FromHours(-4));
        var normalDstEnd = new DateTimeOffset(2026, 3, 8, 4, 30, 0, TimeSpan.FromHours(-4));
        var normalDstViewModel = new CalendarDayViewModel(normalDstZone);
        normalDstViewModel.SetSelectedDate(normalDstDay);
        normalDstViewModel.SetScheduledTasks([Item("after normal DST", normalDstStart, normalDstEnd)]);

        var normalDstBlock = Assert.Single(normalDstViewModel.Blocks);
        Assert.Equal("03:30 – 04:30", normalDstBlock.TimeText);
    }

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
    public void Adjacent_short_blocks_use_separate_lanes_for_their_rendered_extents()
    {
        var viewModel = new CalendarDayViewModel();
        viewModel.SetSelectedDate(TestDay);
        var firstStart = At(TestDay, 9);
        viewModel.SetScheduledTasks([
            Item("first short", firstStart, firstStart.AddMinutes(15)),
            Item("second short", firstStart.AddMinutes(15), firstStart.AddMinutes(30)),
        ]);

        var first = viewModel.Blocks.Single(block => block.Title == "first short");
        var second = viewModel.Blocks.Single(block => block.Title == "second short");
        Assert.Equal(firstStart.AddMinutes(15).LocalDateTime, first.LocalEnd);
        Assert.Equal(32, first.Height);
        Assert.Equal(0, first.LeftFraction);
        Assert.Equal(0.5, first.WidthFraction);
        Assert.Equal(0.5, second.LeftFraction);
        Assert.Equal(0.5, second.WidthFraction);
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

    [Fact]
    public void Timeline_move_and_resize_snap_to_quarter_hours_without_shifting_other_blocks()
    {
        var viewModel = new CalendarDayViewModel();
        viewModel.SetSelectedDate(TestDay);
        var firstStart = At(TestDay, 9);
        viewModel.SetScheduledTasks([
            Item("first", firstStart, firstStart.AddHours(1)),
            Item("second", firstStart.AddMinutes(30), firstStart.AddMinutes(90)),
        ]);

        var first = viewModel.Blocks.Single(block => block.Title == "first");
        var second = viewModel.Blocks.Single(block => block.Title == "second");
        var top = 9 * CalendarDayViewModel.HourHeight + 8 * CalendarDayViewModel.HourHeight / 60;
        var moved = viewModel.MoveBlockTo(first, top);
        var resized = viewModel.ResizeBlockEndBy(first, 8 * CalendarDayViewModel.HourHeight / 60);
        var minimumSize = viewModel.ResizeBlockEndBy(first, -60 * CalendarDayViewModel.HourHeight);

        Assert.Equal(firstStart.AddMinutes(15), moved.Start);
        Assert.Equal(moved.Start.AddHours(1), moved.End);
        Assert.Equal(firstStart, resized.Start);
        Assert.Equal(firstStart.AddHours(1).AddMinutes(15), resized.End);
        Assert.Equal(firstStart.AddMinutes(15), minimumSize.End);
        Assert.Equal(firstStart.AddMinutes(30), second.Start);
    }

    [Fact]
    public void Moving_a_clipped_block_keeps_its_full_duration_and_preview_matches_timeline_layout()
    {
        var viewModel = new CalendarDayViewModel();
        viewModel.SetSelectedDate(TestDay);
        var start = At(TestDay.AddDays(-1), 23);
        var end = At(TestDay.AddDays(1), 1);
        viewModel.SetScheduledTasks([Item("long block", start, end)]);
        var block = Assert.Single(viewModel.Blocks);
        var duration = end - start;

        var moved = viewModel.MoveBlockTo(block, viewModel.TimelineHeight);
        var layout = viewModel.TimelineLayoutFor(moved.Start, moved.End);

        Assert.Equal(duration, moved.End - moved.Start);
        Assert.Equal(At(TestDay, 23).AddMinutes(45).Offset, moved.Start.Offset);
        Assert.NotNull(layout);
        Assert.Equal(viewModel.TimelineHeight - CalendarDayViewModel.HourHeight / 4, layout.Value.Top);
        Assert.Equal(32, layout.Value.Height);

        viewModel.SetScheduledTasks([Item("long block", moved.Start, moved.End)]);
        var committed = Assert.Single(viewModel.Blocks);
        Assert.Equal(layout.Value.Top, committed.Top);
        Assert.Equal(layout.Value.Height, committed.Height);
    }

    [Fact]
    public void Resize_in_a_repeated_hour_preserves_the_existing_utc_offset()
    {
        var timeZone = FindTimeZone("America/New_York", "Eastern Standard Time");
        var day = new DateOnly(2026, 11, 1);
        var start = new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.FromHours(-5));
        var end = new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-5));
        var viewModel = new CalendarDayViewModel(timeZone);
        viewModel.SetSelectedDate(day);
        viewModel.SetScheduledTasks([Item("repeated hour", start, end)]);

        var block = Assert.Single(viewModel.Blocks);
        var resized = viewModel.ResizeBlockEndBy(block, 15 * CalendarDayViewModel.HourHeight / 60);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 1, 45, 0, TimeSpan.FromHours(-5)), resized.End);
    }

    [Fact]
    public void Move_to_a_repeated_hour_preserves_start_offset_and_converts_end_offset()
    {
        var timeZone = FindTimeZone("America/New_York", "Eastern Standard Time");
        var day = new DateOnly(2026, 11, 1);
        var start = new DateTimeOffset(2026, 11, 1, 0, 30, 0, TimeSpan.FromHours(-4));
        var end = new DateTimeOffset(2026, 11, 1, 2, 0, 0, TimeSpan.FromHours(-5));
        var viewModel = new CalendarDayViewModel(timeZone);
        viewModel.SetSelectedDate(day);
        viewModel.SetScheduledTasks([Item("repeated-hour", start, end)]);
        var block = Assert.Single(viewModel.Blocks);

        var moved = viewModel.MoveBlockTo(block, 90 * CalendarDayViewModel.HourHeight / 60d);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-4)), moved.Start);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.FromHours(-5)), moved.End);
        Assert.Equal(end - start, moved.End - moved.Start);
    }
}
