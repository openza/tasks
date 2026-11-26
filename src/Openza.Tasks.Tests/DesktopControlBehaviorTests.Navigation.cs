using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Desktop.Dialogs;
using Openza.Tasks.Desktop.Shell;
using Openza.Tasks.Desktop.ViewModels;
using Openza.Tasks.Desktop.Views;

namespace Openza.Tasks.Tests;

public sealed partial class DesktopControlBehaviorTests
{
    [Fact]
    public Task Calendar_day_switch_keeps_list_available_and_opens_the_today_panel_on_request() => Run(async context =>
    {
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        var dayView = context.Window.FindControl<Control>("CalendarDayView")!;
        var filterGrid = context.Window.FindControl<Control>("TaskFilterGrid")!;
        var taskList = context.Window.FindControl<Control>("TaskListContent")!;
        Assert.True(context.Window.FindControl<Control>("CalendarListViewButton")!.IsVisible);
        Assert.False(dayView.IsVisible);

        context.Window.FindControl<Control>("CalendarDayViewButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Vm.IsCalendarDayView);
        Assert.True(dayView.IsVisible);
        Assert.False(filterGrid.IsVisible);
        Assert.False(taskList.IsVisible);
        Assert.False(context.Vm.CalendarDay.IsTodayPanelOpen);

        context.Window.FindControl<Control>("CalendarTodayPanelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(context.Vm.CalendarDay.IsTodayPanelOpen);
        context.Window.FindControl<Control>("CalendarListViewButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(context.Vm.IsCalendarDayView);
        Assert.True(filterGrid.IsVisible);
        Assert.True(taskList.IsVisible);
        Assert.False(dayView.IsVisible);
    });

    [Fact]
    public Task Calendar_day_header_uses_block_subtitle_without_changing_the_filtered_list_count() => Run(async context =>
    {
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.SearchText = "yesterday-only-filter";
        await context.Vm.ApplySearchAsync();
        var listSubtitle = context.Vm.PageSubtitle;

        context.Vm.SetCalendarDayView(true);

        Assert.Equal("Scheduled time blocks", context.Vm.PageSubtitle);
        context.Vm.SetCalendarDayView(false);
        Assert.Equal(listSubtitle, context.Vm.PageSubtitle);
    });

    [Fact]
    public Task Calendar_day_and_today_panel_include_tasks_from_all_spaces() => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        await context.Vm.CreateSpaceAsync("Calendar second space");
        var secondSpaceId = context.Vm.SpaceItems.Single(item => item.Title == "Calendar second space").SpaceId!;
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var inDefaultSpace = existing with
        {
            Id = "calendar-default-space",
            Title = "Calendar default-space task",
            ProjectId = null,
            ParentId = null,
            PlannedOn = today,
            ScheduledStart = now.AddMinutes(15),
            ScheduledEnd = now.AddMinutes(45),
            CompletionState = TaskCompletionState.Open,
        };
        var inSecondSpace = inDefaultSpace with
        {
            Id = "calendar-second-space",
            Title = "Calendar second-space task",
            SpaceId = secondSpaceId,
        };
        await context.Store.UpsertTaskAsync(inDefaultSpace);
        await context.Store.UpsertTaskAsync(inSecondSpace);

        await context.Vm.SelectSpaceAsync(context.Vm.SpaceItems.Single(item => item.SpaceId is null));
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.SetCalendarDayView(true);

        Assert.Contains(context.Vm.Tasks, item => item.Task.Id == inDefaultSpace.Id);
        Assert.Contains(context.Vm.Tasks, item => item.Task.Id == inSecondSpace.Id);
        Assert.Contains(context.Vm.CalendarDay.Blocks, block => block.Task.Id == inDefaultSpace.Id);
        Assert.Contains(context.Vm.CalendarDay.Blocks, block => block.Task.Id == inSecondSpace.Id);
        Assert.Contains(context.Vm.CalendarDay.FilteredTodayTasks, item => item.Task.Id == inDefaultSpace.Id);
        Assert.Contains(context.Vm.CalendarDay.FilteredTodayTasks, item => item.Task.Id == inSecondSpace.Id);
    });

    [Fact]
    public Task Calendar_block_schedule_updates_and_removal_preserve_task_status_and_dates() => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var task = existing with
        {
            Id = "calendar-schedule-edit-task",
            Title = "Calendar schedule edit",
            ProjectId = null,
            ParentId = null,
            PlannedOn = DateOnly.FromDateTime(DateTime.Today),
            ScheduledStart = null,
            ScheduledEnd = null,
            CompletionState = TaskCompletionState.Open,
            WorkflowStatus = TaskWorkflowStatus.Waiting,
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        var item = Assert.Single(context.Vm.CalendarDay.BlockPickerTasks, candidate => candidate.Task.Id == task.Id);
        var start = DateTimeOffset.Now.AddHours(2);
        var end = start.AddMinutes(75);

        await context.Vm.SetCalendarTaskScheduleAsync(item, start, end);

        var scheduled = (await context.Store.GetTaskAsync(task.Id))!;
        Assert.Equal(start.ToUnixTimeMilliseconds(), scheduled.ScheduledStart!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(end.ToUnixTimeMilliseconds(), scheduled.ScheduledEnd!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(task.WorkflowStatus, scheduled.WorkflowStatus);
        Assert.Equal(task.CompletionState, scheduled.CompletionState);
        Assert.Equal(task.PlannedOn, scheduled.PlannedOn);

        await context.Vm.SetCalendarTaskScheduleAsync(item, null, null);

        var removed = (await context.Store.GetTaskAsync(task.Id))!;
        Assert.Null(removed.ScheduledStart);
        Assert.Null(removed.ScheduledEnd);
        Assert.Equal(task.WorkflowStatus, removed.WorkflowStatus);
        Assert.Equal(task.CompletionState, removed.CompletionState);
        Assert.Equal(task.PlannedOn, removed.PlannedOn);
    });

    [Fact]
    public Task Rejected_detail_draft_blocks_calendar_schedule_reload() => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var task = existing with
        {
            Id = "calendar-invalid-detail-draft",
            Title = "Stored title",
            Notes = "Stored notes",
            PlannedOn = DateOnly.FromDateTime(DateTime.Today),
            DeadlineOn = DateOnly.FromDateTime(DateTime.Today.AddDays(5)),
            ScheduledStart = null,
            ScheduledEnd = null,
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        var item = Assert.Single(context.Vm.CalendarDay.BlockPickerTasks, candidate => candidate.Task.Id == task.Id);
        await context.Vm.SelectTaskAsync(item);
        var draftDate = DateTimeOffset.Now.AddDays(2);
        var draftDeadline = DateTimeOffset.Now.AddDays(8);
        context.Vm.DetailNotes = "Uncommitted notes";
        context.Vm.DetailDate = draftDate;
        context.Vm.DetailDeadline = draftDeadline;
        context.Vm.DetailTitle = "   ";

        Assert.False(await context.Vm.SaveSelectedAsync());

        await context.Vm.SetCalendarTaskScheduleAsync(item, DateTimeOffset.Now.AddHours(2), DateTimeOffset.Now.AddHours(3));

        var persisted = (await context.Store.GetTaskAsync(task.Id))!;
        Assert.Null(persisted.ScheduledStart);
        Assert.Null(persisted.ScheduledEnd);
        Assert.Equal(task.Title, persisted.Title);
        Assert.Equal(task.Notes, persisted.Notes);
        Assert.Equal(task.PlannedOn, persisted.PlannedOn);
        Assert.Equal(task.DeadlineOn, persisted.DeadlineOn);
        Assert.Equal("   ", context.Vm.DetailTitle);
        Assert.Equal("Uncommitted notes", context.Vm.DetailNotes);
        Assert.Equal(draftDate, context.Vm.DetailDate);
        Assert.Equal(draftDeadline, context.Vm.DetailDeadline);
    });

    [Theory]
    [InlineData(520d)]
    [InlineData(640d)]
    public Task Calendar_add_block_button_opens_bounded_picker(double height) => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var task = existing with
        {
            Id = "calendar-picker-layout-task",
            Title = "Calendar picker layout",
            ProjectId = null,
            ParentId = null,
            ScheduledStart = null,
            ScheduledEnd = null,
            CompletionState = TaskCompletionState.Open,
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Window.FindControl<Control>("CalendarDayViewButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Vm.IsCalendarDayView);
        context.Window.UpdateLayout();
        var dayView = context.Window.FindControl<CalendarDayView>("CalendarDayView")!;
        var addButton = dayView.FindControl<Button>("CalendarAddBlockButton")!;
        Assert.True(addButton.IsVisible);
        Assert.Equal("Add a time block", AutomationProperties.GetName(addButton));
        addButton.Focus();
        addButton.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

        await Until(() => context.Window.OwnedWindows.OfType<CalendarBlockPickerWindow>().Any());
        var dialog = context.Window.OwnedWindows.OfType<CalendarBlockPickerWindow>().Single();
        dialog.Height = height;
        dialog.Width = 520;
        dialog.UpdateLayout();

        var taskList = dialog.FindControl<ListBox>("TaskList")!;
        var taskPickerGrid = dialog.FindControl<Grid>("TaskPickerGrid")!;
        var duration = dialog.FindControl<NumericUpDown>("DurationPicker")!;
        Assert.Equal(60m, duration.Value);
        Assert.True(taskList.Bounds.Height > 0);
        Assert.True(taskList.Bounds.Bottom <= taskPickerGrid.Bounds.Height + 1);
        Assert.Contains(task.Title, ((CalendarBlockPickerViewModel)dialog.DataContext!).FilteredTasks.Select(item => item.Title));

        dialog.Close();
    });

    [Fact]
    public Task Calendar_picker_request_guard_is_shared_until_the_handler_finishes() => Run(async _ =>
    {
        var dayView = new CalendarDayView { DataContext = new CalendarDayViewModel() };
        var window = new Window { Width = 1440, Height = 900, Content = dayView };
        window.Show();
        window.UpdateLayout();
        try
        {
            var firstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requestCount = 0;
            dayView.BlockPickerRequested += _ => ++requestCount == 1 ? firstRequest.Task : Task.CompletedTask;

            dayView.FindControl<Button>("CalendarAddBlockButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => requestCount == 1);

            var timeline = dayView.FindControl<Canvas>("TimelineCanvas")!;
            var pointer = new Pointer(70, PointerType.Mouse, isPrimary: true);
            RaisePointerPressed(timeline, window, pointer);
            Assert.Equal(1, requestCount);

            firstRequest.SetResult();
            await UntilAsync(() =>
            {
                if (requestCount == 1) RaisePointerPressed(timeline, window, pointer);
                return Task.FromResult(requestCount == 2);
            });
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Calendar_picker_request_reports_handler_failures_and_resets_guard() => Run(async _ =>
    {
        var dayView = new CalendarDayView { DataContext = new CalendarDayViewModel() };
        var window = new Window { Width = 1440, Height = 900, Content = dayView };
        window.Show();
        window.UpdateLayout();
        try
        {
            var requestCount = 0;
            var reportedExceptions = new List<Exception>();
            dayView.BlockPickerRequested += async _ =>
            {
                requestCount++;
                await Task.Yield();
                if (requestCount == 1) throw new InvalidOperationException("picker failed");
            };
            dayView.BlockPickerRequestFailed += reportedExceptions.Add;

            var addButton = dayView.FindControl<Button>("CalendarAddBlockButton")!;
            addButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => reportedExceptions.Count == 1);
            Assert.Equal("picker failed", reportedExceptions[0].Message);

            addButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => requestCount == 2);
            Assert.Single(reportedExceptions);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Calendar_picker_warns_before_replacing_an_existing_interval() => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var start = DateTimeOffset.Now.AddHours(2);
        var task = existing with
        {
            Id = "calendar-picker-replacement-task",
            Title = "Already scheduled picker task",
            ProjectId = null,
            ParentId = null,
            ScheduledStart = start,
            ScheduledEnd = start.AddHours(1),
            WorkflowStatus = TaskWorkflowStatus.Next,
            CompletionState = TaskCompletionState.Open,
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Window.FindControl<Control>("CalendarDayViewButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Vm.IsCalendarDayView);
        context.Window.FindControl<CalendarDayView>("CalendarDayView")!.FindControl<Button>("CalendarAddBlockButton")!
            .RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

        await Until(() => context.Window.OwnedWindows.OfType<CalendarBlockPickerWindow>().Any());
        var dialog = context.Window.OwnedWindows.OfType<CalendarBlockPickerWindow>().Single();
        var picker = (CalendarBlockPickerViewModel)dialog.DataContext!;
        Assert.Contains(picker.FilteredTasks, item => item.Task.Id == task.Id);
        dialog.FindControl<ListBox>("TaskList")!.SelectedItem = Assert.Single(picker.FilteredTasks, item => item.Task.Id == task.Id);
        dialog.UpdateLayout();

        Assert.Contains("Existing interval:", dialog.FindControl<TextBlock>("ExistingScheduleText")!.Text);
        Assert.Contains("will replace that interval", dialog.FindControl<TextBlock>("ReplacementNoticeText")!.Text);
        Assert.Equal("Replace scheduled interval", dialog.FindControl<Button>("AddBlockButton")!.Content);
        Assert.True(dialog.FindControl<Button>("AddBlockButton")!.IsEnabled);
        dialog.Close();
    });

    [Fact]
    public Task Calendar_timeline_blocks_remain_hit_testable_for_short_block_tooltips() => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var start = DateTimeOffset.Now.AddMinutes(15);
        var task = existing with
        {
            Id = "calendar-tooltip-task",
            Title = "Calendar tooltip task",
            ParentId = null,
            PlannedOn = DateOnly.FromDateTime(start.LocalDateTime),
            ScheduledStart = start,
            ScheduledEnd = start.AddMinutes(15),
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.SetCalendarDayView(true);
        context.Window.UpdateLayout();

        var dayView = context.Window.FindControl<Control>("CalendarDayView")!;
        var block = Assert.Single(dayView.GetVisualDescendants().OfType<Border>(),
            child => ToolTip.GetTip(child) is string tip && tip.Contains(task.Title, StringComparison.Ordinal));
        var canvas = block.GetVisualAncestors().OfType<Canvas>().First();
        Assert.True(canvas.IsHitTestVisible);
        Assert.Contains(start.ToLocalTime().ToString("HH:mm"), Assert.IsType<string>(ToolTip.GetTip(block)));
    });

    [Fact]
    public Task Calendar_end_of_day_resize_handle_stays_inside_the_timeline() => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var day = new DateOnly(2026, 6, 17);
        var startLocal = day.ToDateTime(new TimeOnly(23, 45));
        var endLocal = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var start = new DateTimeOffset(startLocal, TimeZoneInfo.Local.GetUtcOffset(startLocal));
        var end = new DateTimeOffset(endLocal, TimeZoneInfo.Local.GetUtcOffset(endLocal));
        var task = existing with
        {
            Id = "calendar-end-of-day-resize-task",
            Title = "Calendar end of day resize task",
            ParentId = null,
            PlannedOn = day,
            ScheduledStart = start,
            ScheduledEnd = end,
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.CalendarDay.SetSelectedDate(day);
        context.Vm.SetCalendarDayView(true);
        context.Window.UpdateLayout();

        var dayView = context.Window.FindControl<CalendarDayView>("CalendarDayView")!;
        var canvas = dayView.FindControl<Canvas>("TimelineCanvas")!;
        var block = Assert.Single(dayView.GetVisualDescendants().OfType<Border>(), border =>
            border.DataContext is CalendarDayBlockViewModel blockViewModel && blockViewModel.Task.Id == task.Id &&
            ToolTip.GetTip(border) is string tip && tip.Contains(task.Title, StringComparison.Ordinal));
        var blockViewModel = (CalendarDayBlockViewModel)block.DataContext!;
        var resizeHandle = Assert.Single(block.GetVisualDescendants().OfType<Border>(), border =>
            border.Classes.Contains("calendar-block-resize-handle"));

        Assert.True(blockViewModel.CanResizeEnd);
        Assert.Equal(16, block.Bounds.Height);
        Assert.True(resizeHandle.IsVisible);
        Assert.True(resizeHandle.Bounds.Height > 0);
        Assert.True(Canvas.GetTop(block) + block.Bounds.Height <= canvas.Bounds.Height + 1);
    });

    [Fact]
    public Task Calendar_short_end_of_day_block_hit_tests_its_rendered_grip_and_body() => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var day = new DateOnly(2026, 6, 17);
        var startLocal = day.ToDateTime(new TimeOnly(23, 50));
        var endLocal = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var start = new DateTimeOffset(startLocal, TimeZoneInfo.Local.GetUtcOffset(startLocal));
        var end = new DateTimeOffset(endLocal, TimeZoneInfo.Local.GetUtcOffset(endLocal));
        var task = existing with
        {
            Id = "calendar-short-end-of-day-resize-task",
            Title = "Calendar short end of day resize task",
            ParentId = null,
            PlannedOn = day,
            ScheduledStart = start,
            ScheduledEnd = end,
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.CalendarDay.SetSelectedDate(day);
        context.Vm.SetCalendarDayView(true);
        context.Window.UpdateLayout();

        var dayView = context.Window.FindControl<CalendarDayView>("CalendarDayView")!;
        Border FindBlock() => Assert.Single(dayView.GetVisualDescendants().OfType<Border>(), border =>
            border.Classes.Contains("calendar-day-block") &&
            border.DataContext is CalendarDayBlockViewModel block && block.Task.Id == task.Id);
        var block = FindBlock();
        var resizeHandle = Assert.Single(block.GetVisualDescendants().OfType<Border>(), border =>
            border.Classes.Contains("calendar-block-resize-handle"));
        var resizeHandleOrigin = resizeHandle.TranslatePoint(default, block)!.Value;
        Assert.True(block.Bounds.Height < 12);
        Assert.True(resizeHandle.IsVisible);
        Assert.InRange(resizeHandleOrigin.Y, 0, block.Bounds.Height);
        Assert.True(resizeHandleOrigin.Y + resizeHandle.Bounds.Height <= block.Bounds.Height);
        var resizeGesture = typeof(CalendarDayView).GetField(
            "_resizeGesture", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var pointer = new Pointer(2, PointerType.Mouse, isPrimary: true);

        void AssertResizeGestureAt(Point localPosition, bool expected)
        {
            var point = block.TranslatePoint(localPosition, context.Window)!.Value;
            var pressed = RaisePointerPressed(block, context.Window, pointer, point);
            Assert.True(pressed.Handled);
            Assert.Equal(expected, resizeGesture.GetValue(dayView));
            RaisePointerCaptureLost(block, pointer);
        }

        var gripPoint = new Point(
            resizeHandleOrigin.X + resizeHandle.Bounds.Width / 2,
            resizeHandleOrigin.Y + resizeHandle.Bounds.Height / 2);
        AssertResizeGestureAt(gripPoint, expected: true);
        AssertResizeGestureAt(new Point(block.Bounds.Width / 2, resizeHandleOrigin.Y / 2), expected: false);
        AssertResizeGestureAt(new Point(resizeHandleOrigin.X / 2, gripPoint.Y), expected: false);
    });

    private static async Task WaitForCalendarSearchDebounceAsync(Context context)
    {
        var searchTimer = (DispatcherTimer)context.Window.GetType().GetField(
            "_taskSearchTimer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(context.Window)!;
        await Task.Delay(searchTimer.Interval + TimeSpan.FromMilliseconds(50));
        await UntilAsync(() => Task.FromResult(!searchTimer.IsEnabled && !context.Vm.IsBusy));
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var dayView = context.Window.FindControl<CalendarDayView>("CalendarDayView")!;
        Assert.False(searchTimer.IsEnabled);
        Assert.False(context.Vm.IsBusy);
        Assert.False((bool)typeof(CalendarDayView).GetField(
            "_timelineRenderQueued",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dayView)!);
        Assert.False((bool)typeof(CalendarDayView).GetField(
            "_clockVisualUpdateQueued",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dayView)!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Calendar_minute_refresh_does_not_cancel_an_active_block_gesture(bool resize) => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var day = DateOnly.FromDateTime(DateTime.Today);
        var startLocal = day.ToDateTime(new TimeOnly(11, 30));
        var endLocal = day.ToDateTime(new TimeOnly(13, 0));
        var start = new DateTimeOffset(startLocal, TimeZoneInfo.Local.GetUtcOffset(startLocal));
        var end = new DateTimeOffset(endLocal, TimeZoneInfo.Local.GetUtcOffset(endLocal));
        var task = existing with
        {
            Id = "calendar-minute-refresh-gesture-task",
            Title = "Calendar minute refresh gesture task",
            ParentId = null,
            PlannedOn = day,
            ScheduledStart = start,
            ScheduledEnd = end,
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.CalendarDay.SetSelectedDate(day);
        context.Vm.SetCalendarDayView(true);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();
        context.Vm.DismissStatusMessage();
        context.Window.UpdateLayout();
        await WaitForCalendarSearchDebounceAsync(context);

        var dayView = context.Window.FindControl<CalendarDayView>("CalendarDayView")!;
        var scrollViewer = dayView.FindControl<ScrollViewer>("TimelineScrollViewer")!;
        var canvas = dayView.FindControl<Canvas>("TimelineCanvas")!;
        var statusOverlay = context.Window.FindControl<Border>("StatusOverlay")!;
        var block = Assert.Single(dayView.GetVisualDescendants().OfType<Border>(), border =>
            border.Classes.Contains("calendar-day-block") &&
            border.DataContext is CalendarDayBlockViewModel blockViewModel && blockViewModel.Task.Id == task.Id);
        block.BringIntoView();
        context.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        await UntilAsync(() => Task.FromResult(
            Canvas.GetTop(block) >= scrollViewer.Offset.Y &&
            Canvas.GetTop(block) + block.Bounds.Height <= scrollViewer.Offset.Y + scrollViewer.Viewport.Height),
            () => $"Block did not enter the viewport: top={Canvas.GetTop(block)}, height={block.Bounds.Height}, offset={scrollViewer.Offset.Y}, viewport={scrollViewer.Viewport}.");
        var pointer = new Pointer(3, PointerType.Mouse, isPrimary: true);
        var pressLocal = new Point(block.Bounds.Width / 2, resize ? block.Bounds.Height - 4 : block.Bounds.Height / 2);
        var pressPoint = block.TranslatePoint(pressLocal, context.Window)!.Value;
        var movedPoint = new Point(pressPoint.X, pressPoint.Y + 30);
        var startTop = Canvas.GetTop(block);
        var startHeight = block.Bounds.Height;
        var canvasOrigin = canvas.TranslatePoint(default, context.Window)!.Value;
        var viewport = scrollViewer.Viewport;
        var scrollOffset = scrollViewer.Offset;
        Assert.False(statusOverlay.IsVisible);
        var captureLostCount = 0;
        var activeBlockBorder = typeof(CalendarDayView).GetField(
            "_activeBlockBorder",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        block.PointerCaptureLost += (_, _) => captureLostCount++;

        Assert.True(RaisePointerPressed(block, context.Window, pointer, pressPoint).Handled);
        RaisePointerMoved(block, context.Window, pointer, movedPoint);
        context.Vm.CalendarDay.SetNow(context.Vm.CalendarDay.Now.AddMinutes(1));
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.False(statusOverlay.IsVisible);
        Assert.Equal(viewport, scrollViewer.Viewport);
        Assert.Equal(scrollOffset, scrollViewer.Offset);
        Assert.Equal(canvasOrigin, canvas.TranslatePoint(default, context.Window)!.Value);
        Assert.Contains(block, canvas.Children);
        Assert.Equal(0, captureLostCount);
        Assert.Same(block, activeBlockBorder.GetValue(dayView));
        Assert.False((bool)typeof(CalendarDayView).GetField(
            "_clockVisualUpdateQueued",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dayView)!);

        RaisePointerMoved(block, context.Window, pointer, movedPoint);
        if (resize)
        {
            Assert.Equal(startHeight + 32, block.Height, 3);
        }
        else
        {
            Assert.Equal(startTop + 32, Canvas.GetTop(block), 3);
        }
        RaisePointerReleasedAt(block, context.Window, pointer, movedPoint);
        Assert.Null(activeBlockBorder.GetValue(dayView));

        var expectedStart = resize ? start : start.AddMinutes(30);
        var expectedEnd = end.AddMinutes(30);
        TaskItem? lastSaved = null;
        await UntilAsync(async () =>
        {
            lastSaved = await context.Store.GetTaskAsync(task.Id);
            return lastSaved?.ScheduledStart?.ToUnixTimeMilliseconds() == expectedStart.ToUnixTimeMilliseconds() &&
                lastSaved.ScheduledEnd?.ToUnixTimeMilliseconds() == expectedEnd.ToUnixTimeMilliseconds();
        }, () => $"Expected {expectedStart:o}–{expectedEnd:o}, got {lastSaved?.ScheduledStart:o}–{lastSaved?.ScheduledEnd:o}; active={activeBlockBorder.GetValue(dayView) is not null}, attached={canvas.Children.Contains(block)}, captureLost={captureLostCount}, status={context.Vm.StatusMessage}.");
    });

    [Fact]
    public Task Calendar_viewport_resize_cancels_active_block_resize_without_saving() => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var day = DateOnly.FromDateTime(DateTime.Today);
        var startLocal = day.ToDateTime(new TimeOnly(22, 0));
        var start = new DateTimeOffset(startLocal, TimeZoneInfo.Local.GetUtcOffset(startLocal));
        var end = start.AddHours(1);
        var task = existing with
        {
            Id = "calendar-viewport-resize-task",
            Title = "Calendar viewport resize task",
            ParentId = null,
            PlannedOn = day,
            ScheduledStart = start,
            ScheduledEnd = end,
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.CalendarDay.SetSelectedDate(day);
        context.Vm.SetCalendarDayView(true);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();

        var dayView = context.Window.FindControl<CalendarDayView>("CalendarDayView")!;
        var canvas = dayView.FindControl<Canvas>("TimelineCanvas")!;
        var scrollViewer = dayView.FindControl<ScrollViewer>("TimelineScrollViewer")!;
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        context.Window.UpdateLayout();

        var block = Assert.Single(dayView.GetVisualDescendants().OfType<Border>(), border =>
            border.Classes.Contains("calendar-day-block") &&
            border.DataContext is CalendarDayBlockViewModel blockViewModel && blockViewModel.Task.Id == task.Id);
        var pointer = new Pointer(6, PointerType.Mouse, isPrimary: true);
        var startHeight = block.Height;
        var pressPoint = block.TranslatePoint(
            new Point(block.Bounds.Width / 2, block.Bounds.Height - 4), context.Window)!.Value;
        var movedPoint = new Point(pressPoint.X, pressPoint.Y + 20);
        (DateTimeOffset? Start, DateTimeOffset? End)? requestedSchedule = null;
        var activeBorder = typeof(CalendarDayView).GetField(
            "_activeBlockBorder",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        dayView.BlockScheduleChangeRequested += (_, requestedStart, requestedEnd) =>
        {
            requestedSchedule = (requestedStart, requestedEnd);
            return Task.CompletedTask;
        };

        Assert.True(RaisePointerPressed(block, context.Window, pointer, pressPoint).Handled);
        RaisePointerMoved(block, context.Window, pointer, movedPoint);
        context.Window.UpdateLayout();
        Assert.True(block.Height > startHeight);

        var initialWidth = canvas.Bounds.Width;
        var initialViewportHeight = scrollViewer.Viewport.Height;
        var initialOffset = scrollViewer.Offset.Y;
        canvas.Width = initialWidth - 50;
        context.Window.Height += 100;
        context.Window.UpdateLayout();

        Assert.True(canvas.Bounds.Width < initialWidth);
        Assert.True(scrollViewer.Viewport.Height > initialViewportHeight);
        Assert.True(scrollViewer.Offset.Y < initialOffset);
        var activeGestureAfterReflow = activeBorder.GetValue(dayView);

        RaisePointerReleasedAt(block, context.Window, pointer, movedPoint);
        Dispatcher.UIThread.RunJobs();

        Assert.True(requestedSchedule is null, $"Unexpected schedule request after viewport resize: {requestedSchedule}");
        Assert.Null(activeGestureAfterReflow);
        var saved = await context.Store.GetTaskAsync(task.Id);
        Assert.Equal(start.ToUnixTimeMilliseconds(), saved?.ScheduledStart?.ToUnixTimeMilliseconds());
        Assert.Equal(end.ToUnixTimeMilliseconds(), saved?.ScheduledEnd?.ToUnixTimeMilliseconds());
    });

    [Fact]
    public Task Calendar_stationary_release_after_minute_refresh_keeps_action_flyout_open() => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var day = DateOnly.FromDateTime(DateTime.Today);
        var startLocal = day.ToDateTime(new TimeOnly(11, 30));
        var start = new DateTimeOffset(startLocal, TimeZoneInfo.Local.GetUtcOffset(startLocal));
        var end = start.AddMinutes(60);
        var task = existing with
        {
            Id = "calendar-stationary-refresh-flyout-task",
            Title = "Calendar stationary refresh flyout task",
            ParentId = null,
            PlannedOn = day,
            ScheduledStart = start,
            ScheduledEnd = end,
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.CalendarDay.SetSelectedDate(day);
        context.Vm.SetCalendarDayView(true);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();
        context.Vm.CalendarDay.SetNow(end.AddMinutes(-2));
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();
        await WaitForCalendarSearchDebounceAsync(context);

        var dayView = context.Window.FindControl<CalendarDayView>("CalendarDayView")!;
        var canvas = dayView.FindControl<Canvas>("TimelineCanvas")!;
        var block = Assert.Single(dayView.GetVisualDescendants().OfType<Border>(), border =>
            border.Classes.Contains("calendar-day-block") &&
            border.DataContext is CalendarDayBlockViewModel blockViewModel && blockViewModel.Task.Id == task.Id);
        var pointer = new Pointer(5, PointerType.Mouse, isPrimary: true);
        var point = block.TranslatePoint(new Point(block.Bounds.Width / 2, block.Bounds.Height / 2), context.Window)!.Value;
        var activeBorder = typeof(CalendarDayView).GetField(
            "_activeBlockBorder",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var activeFlyout = typeof(CalendarDayView).GetField(
            "_activeBlockFlyout",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var nowLine = Assert.Single(canvas.Children.OfType<Border>(), border => border.Classes.Contains("calendar-now-rule"));
        var initialNowTop = Canvas.GetTop(nowLine);
        var state = Assert.Single(block.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.Classes.Contains("calendar-block-state"));
        var captureLostCount = 0;
        var timelineSizeChangedCount = 0;
        block.PointerCaptureLost += (_, _) => captureLostCount++;
        canvas.SizeChanged += (_, _) => timelineSizeChangedCount++;
        Assert.Equal(1, block.Opacity);
        Assert.False(state.IsVisible);

        Assert.True(RaisePointerPressed(block, context.Window, pointer, point).Handled);
        context.Vm.CalendarDay.SetNow(end.AddMinutes(-1));
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();

        Assert.True(ReferenceEquals(block, activeBorder.GetValue(dayView)),
            $"Gesture canceled after {timelineSizeChangedCount} timeline size changes and {captureLostCount} capture losses; block attached={canvas.Children.Contains(block)}.");
        Assert.Equal(0, timelineSizeChangedCount);
        Assert.Equal(0, captureLostCount);
        Assert.Contains(block, canvas.Children);
        Assert.Same(nowLine, Assert.Single(canvas.Children.OfType<Border>(), border => border.Classes.Contains("calendar-now-rule")));
        Assert.True(Canvas.GetTop(nowLine) > initialNowTop);
        Assert.Equal(1, block.Opacity);
        Assert.False(state.IsVisible);

        RaisePointerReleasedAt(block, context.Window, pointer, point);
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();

        var flyout = Assert.IsType<Flyout>(activeFlyout.GetValue(dayView));
        Assert.True(flyout.IsOpen);
        Assert.Contains(block, canvas.Children);
        Assert.Same(nowLine, Assert.Single(canvas.Children.OfType<Border>(), border => border.Classes.Contains("calendar-now-rule")));
    });

    [Fact]
    public Task Calendar_clock_refresh_updates_block_state_in_place() => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var day = DateOnly.FromDateTime(DateTime.Today);
        var startLocal = day.ToDateTime(new TimeOnly(11, 30));
        var start = new DateTimeOffset(startLocal, TimeZoneInfo.Local.GetUtcOffset(startLocal));
        var end = start.AddMinutes(60);
        var task = existing with
        {
            Id = "calendar-clock-state-task",
            Title = "Calendar clock state task",
            ParentId = null,
            PlannedOn = day,
            ScheduledStart = start,
            ScheduledEnd = end,
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.CalendarDay.SetSelectedDate(day);
        context.Vm.SetCalendarDayView(true);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();
        await WaitForCalendarSearchDebounceAsync(context);
        context.Vm.CalendarDay.SetNow(end.AddMinutes(-1));
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();

        var dayView = context.Window.FindControl<CalendarDayView>("CalendarDayView")!;
        var canvas = dayView.FindControl<Canvas>("TimelineCanvas")!;
        var block = Assert.Single(dayView.GetVisualDescendants().OfType<Border>(), border =>
            border.Classes.Contains("calendar-day-block") &&
            border.DataContext is CalendarDayBlockViewModel blockViewModel && blockViewModel.Task.Id == task.Id);
        var state = Assert.Single(block.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.Classes.Contains("calendar-block-state"));
        var nowLine = Assert.Single(canvas.Children.OfType<Border>(), border => border.Classes.Contains("calendar-now-rule"));
        var initialNowTop = Canvas.GetTop(nowLine);
        Assert.Equal(1, block.Opacity);
        Assert.False(state.IsVisible);

        context.Vm.CalendarDay.SetNow(end);
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();

        Assert.Contains(block, canvas.Children);
        Assert.Same(nowLine, Assert.Single(canvas.Children.OfType<Border>(), border => border.Classes.Contains("calendar-now-rule")));
        Assert.True(Canvas.GetTop(nowLine) > initialNowTop);
        Assert.Equal(0.55, block.Opacity);
        Assert.Equal("Not done", state.Text);
        Assert.True(state.IsVisible);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public Task Calendar_unchanged_sync_refresh_preserves_active_block_gesture(bool resize, int wheelCount = 0) => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var day = DateOnly.FromDateTime(DateTime.Today);
        var startLocal = day.ToDateTime(new TimeOnly(11, 30));
        var start = new DateTimeOffset(startLocal, TimeZoneInfo.Local.GetUtcOffset(startLocal));
        var end = start.AddMinutes(90);
        var task = existing with
        {
            Id = "calendar-unchanged-sync-refresh-task",
            Title = "Calendar unchanged sync refresh task",
            ParentId = null,
            PlannedOn = day,
            ScheduledStart = start,
            ScheduledEnd = end,
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.CalendarDay.SetSelectedDate(day);
        context.Vm.SetCalendarDayView(true);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();
        context.Vm.DismissStatusMessage();
        await WaitForCalendarSearchDebounceAsync(context);

        var dayView = context.Window.FindControl<CalendarDayView>("CalendarDayView")!;
        var canvas = dayView.FindControl<Canvas>("TimelineCanvas")!;
        var scrollViewer = dayView.FindControl<ScrollViewer>("TimelineScrollViewer")!;
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, 300);
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();
        var statusOverlay = context.Window.FindControl<Border>("StatusOverlay")!;
        var block = Assert.Single(dayView.GetVisualDescendants().OfType<Border>(), border =>
            border.Classes.Contains("calendar-day-block") &&
            border.DataContext is CalendarDayBlockViewModel blockViewModel && blockViewModel.Task.Id == task.Id);
        var pointer = new Pointer(8, PointerType.Mouse, isPrimary: true);
        var pressPoint = block.TranslatePoint(
            new Point(block.Bounds.Width / 2, resize ? block.Bounds.Height - 4 : block.Bounds.Height / 2),
            context.Window)!.Value;
        var movedPoint = new Point(pressPoint.X, pressPoint.Y + 30);
        var startTop = Canvas.GetTop(block);
        var startHeight = block.Bounds.Height;
        var canvasOrigin = canvas.TranslatePoint(default, context.Window)!.Value;
        var canvasSize = canvas.Bounds.Size;
        var scrollOffset = scrollViewer.Offset;
        Assert.Equal(300, scrollOffset.Y, 3);
        var captureLostCount = 0;
        var timelineSizeChangedCount = 0;
        var scrollChanges = new List<ScrollChangedEventArgs>();
        var activeBlockBorder = typeof(CalendarDayView).GetField(
            "_activeBlockBorder",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        block.PointerCaptureLost += (_, _) => captureLostCount++;
        canvas.SizeChanged += (_, _) => timelineSizeChangedCount++;
        scrollViewer.ScrollChanged += (_, e) => scrollChanges.Add(e);

        Assert.True(RaisePointerPressed(block, context.Window, pointer, pressPoint).Handled);
        RaisePointerMoved(block, context.Window, pointer, movedPoint);
        Assert.Same(block, activeBlockBorder.GetValue(dayView));

        var syncTask = context.Vm.RunTodoistSyncAsync();
        await UntilAsync(() => Task.FromResult(
            context.Vm.IsSyncing && context.Vm.StatusMessage == "Syncing connected apps…"));
        var wheelOffsetDelta = 0d;
        var wheelOffsetChanges = new List<double>();
        for (var wheel = 0; wheel < wheelCount; wheel++)
        {
            var offsetBeforeWheel = scrollViewer.Offset.Y;
            RaisePointerWheelChanged(block, context.Window, pointer, movedPoint, new Vector(0, -1));
            var offsetDelta = scrollViewer.Offset.Y - offsetBeforeWheel;
            wheelOffsetChanges.Add(offsetDelta);
            wheelOffsetDelta += offsetDelta;
        }
        if (wheelCount != 0)
        {
            Assert.True(wheelOffsetDelta != 0, $"Wheel offsets: {string.Join(", ", wheelOffsetChanges)}.");
        }
        context.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(statusOverlay.IsVisible);
        var statusCanvasOrigin = canvas.TranslatePoint(default, context.Window)!.Value;
        var statusCanvasSize = canvas.Bounds.Size;
        var statusScrollOffset = scrollViewer.Offset;
        var scrollChangedOffsetDelta = scrollChanges.Sum(change => change.OffsetDelta.Y);
        Assert.Contains(scrollChanges, change => change.ViewportDelta.Y != 0 || change.ExtentDelta.Y != 0);
        if (wheelCount != 0)
        {
            Assert.NotEqual(0, scrollChangedOffsetDelta);
            Assert.Contains(scrollChanges, change => change.OffsetDelta.Y != 0 &&
                (change.ViewportDelta.Y != 0 || change.ExtentDelta.Y != 0));
        }
        Assert.Equal(canvasSize, statusCanvasSize);
        Assert.Equal(scrollOffset.Y + scrollChangedOffsetDelta, statusScrollOffset.Y);
        Assert.Equal(-scrollChangedOffsetDelta, statusCanvasOrigin.Y - canvasOrigin.Y);
        Assert.Equal(0, timelineSizeChangedCount);
        Assert.Same(block, activeBlockBorder.GetValue(dayView));

        await syncTask;
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();

        Assert.Contains("Personal Cloud sync failed: This desktop is not paired with Personal Cloud.", context.Vm.LastSyncResult);
        Assert.Same(block, activeBlockBorder.GetValue(dayView));
        Assert.Contains(block, canvas.Children);
        Assert.Equal(0, captureLostCount);
        Assert.Equal(0, timelineSizeChangedCount);
        var additionalExplicitScrollDelta = 0d;
        if (wheelCount == 2)
        {
            var scrollChangeStart = scrollChanges.Count;
            var offsetBeforeExplicitScroll = scrollViewer.Offset.Y;
            scrollViewer.Offset = new Vector(
                scrollViewer.Offset.X,
                scrollViewer.Offset.Y - Math.Sign(wheelOffsetDelta) * 48);
            Dispatcher.UIThread.RunJobs();
            context.Window.UpdateLayout();
            additionalExplicitScrollDelta = scrollViewer.Offset.Y - offsetBeforeExplicitScroll;
            Assert.NotEqual(0, additionalExplicitScrollDelta);
            var explicitScrollChanges = scrollChanges.Skip(scrollChangeStart).ToArray();
            Assert.Equal(additionalExplicitScrollDelta, explicitScrollChanges.Sum(change => change.OffsetDelta.Y), 3);
            Assert.All(explicitScrollChanges, change =>
            {
                Assert.Equal(0, change.ViewportDelta.Y);
                Assert.Equal(0, change.ExtentDelta.Y);
            });
        }
        var followupMovedPoint = new Point(movedPoint.X, movedPoint.Y - 16);
        RaisePointerMoved(block, context.Window, pointer, followupMovedPoint);
        var expectedPixels = Math.Round((wheelOffsetDelta + additionalExplicitScrollDelta + 14) / 16,
            MidpointRounding.AwayFromZero) * 16;
        if (resize)
        {
            Assert.Equal(startHeight + expectedPixels, block.Height, 3);
        }
        else
        {
            Assert.Equal(startTop + expectedPixels, Canvas.GetTop(block), 3);
        }
        RaisePointerReleasedAt(block, context.Window, pointer, followupMovedPoint);
        Assert.Null(activeBlockBorder.GetValue(dayView));

        var expectedMinutes = (int)(expectedPixels / 16) * 15;
        var expectedStart = resize ? start : start.AddMinutes(expectedMinutes);
        var expectedEnd = end.AddMinutes(expectedMinutes);
        TaskItem? lastSaved = null;
        await UntilAsync(async () =>
        {
            lastSaved = await context.Store.GetTaskAsync(task.Id);
            return lastSaved?.ScheduledStart?.ToUnixTimeMilliseconds() == expectedStart.ToUnixTimeMilliseconds() &&
                lastSaved.ScheduledEnd?.ToUnixTimeMilliseconds() == expectedEnd.ToUnixTimeMilliseconds();
        }, () => $"Expected {expectedStart:o}–{expectedEnd:o}, got {lastSaved?.ScheduledStart:o}–{lastSaved?.ScheduledEnd:o}; current block top={Canvas.GetTop(block)}, height={block.Height}, attached={canvas.Children.Contains(block)}, busy={context.Vm.IsBusy}, syncing={context.Vm.IsSyncing}, status={context.Vm.StatusMessage}.");
    }, new UnpairedPersonalCloudSetupService());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Calendar_zero_net_coalesced_wheel_reflow_preserves_active_block_gesture(bool resize) => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var day = DateOnly.FromDateTime(DateTime.Today.AddDays(1));
        var startLocal = day.ToDateTime(new TimeOnly(20, 0));
        var start = new DateTimeOffset(startLocal, TimeZoneInfo.Local.GetUtcOffset(startLocal));
        var end = start.AddMinutes(90);
        var task = existing with
        {
            Id = "calendar-zero-net-wheel-reflow-task",
            Title = "Calendar zero-net wheel reflow task",
            ParentId = null,
            PlannedOn = day,
            ScheduledStart = start,
            ScheduledEnd = end,
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.CalendarDay.SetSelectedDate(day);
        context.Vm.SetCalendarDayView(true);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();
        context.Vm.DismissStatusMessage();
        await WaitForCalendarSearchDebounceAsync(context);

        var dayView = context.Window.FindControl<CalendarDayView>("CalendarDayView")!;
        var canvas = dayView.FindControl<Canvas>("TimelineCanvas")!;
        var scrollViewer = dayView.FindControl<ScrollViewer>("TimelineScrollViewer")!;
        var statusOverlay = context.Window.FindControl<Border>("StatusOverlay")!;
        Assert.False(statusOverlay.IsVisible);
        const double wheelDeltaPerInput = 50;
        const int wheelInputCount = 2;
        var totalWheelDelta = wheelDeltaPerInput * wheelInputCount;
        var initialMaxOffset = scrollViewer.Extent.Height - scrollViewer.Viewport.Height;
        Assert.True(initialMaxOffset > totalWheelDelta);
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, initialMaxOffset - totalWheelDelta);
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();

        var initialOffset = scrollViewer.Offset.Y;
        var initialViewport = scrollViewer.Viewport;
        var initialExtent = scrollViewer.Extent;
        var initialWindowHeight = context.Window.Height;
        var maxOffset = scrollViewer.Extent.Height - scrollViewer.Viewport.Height;
        Assert.Equal(maxOffset - totalWheelDelta, initialOffset, 3);
        var block = Assert.Single(dayView.GetVisualDescendants().OfType<Border>(), border =>
            border.Classes.Contains("calendar-day-block") &&
            border.DataContext is CalendarDayBlockViewModel blockViewModel && blockViewModel.Task.Id == task.Id);
        Assert.True(Canvas.GetTop(block) >= initialOffset);
        Assert.True(Canvas.GetTop(block) + block.Bounds.Height <= initialOffset + initialViewport.Height);

        var pointer = new Pointer(81, PointerType.Mouse, isPrimary: true);
        var pressPoint = block.TranslatePoint(
            new Point(block.Bounds.Width / 2, resize ? block.Bounds.Height - 4 : block.Bounds.Height / 2),
            context.Window)!.Value;
        var movedPoint = new Point(pressPoint.X, pressPoint.Y + 30);
        var startTop = Canvas.GetTop(block);
        var startHeight = block.Bounds.Height;
        var scrollChanges = new List<ScrollChangedEventArgs>();
        var captureLostCount = 0;
        var timelineSizeChangedCount = 0;
        var activeBlockBorder = typeof(CalendarDayView).GetField(
            "_activeBlockBorder",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        scrollViewer.ScrollChanged += (_, e) => scrollChanges.Add(e);
        block.PointerCaptureLost += (_, _) => captureLostCount++;
        canvas.SizeChanged += (_, _) => timelineSizeChangedCount++;

        Assert.True(RaisePointerPressed(block, context.Window, pointer, pressPoint).Handled);
        RaisePointerMoved(block, context.Window, pointer, movedPoint);
        Assert.Same(block, activeBlockBorder.GetValue(dayView));

        context.Window.Height = initialWindowHeight + totalWheelDelta;
        Assert.Equal(initialViewport.Height, scrollViewer.Viewport.Height, 3);
        var wheelOffsetChanges = new List<double>();
        for (var wheel = 0; wheel < wheelInputCount; wheel++)
        {
            var offsetBeforeWheel = scrollViewer.Offset.Y;
            RaisePointerWheelChanged(block, context.Window, pointer, movedPoint, new Vector(0, -1));
            wheelOffsetChanges.Add(scrollViewer.Offset.Y - offsetBeforeWheel);
        }
        Assert.All(wheelOffsetChanges, offsetDelta => Assert.Equal(wheelDeltaPerInput, offsetDelta, 3));
        var wheelOffsetDelta = wheelOffsetChanges.Sum();
        Assert.Equal(totalWheelDelta, wheelOffsetDelta, 3);
        Assert.Equal(maxOffset, scrollViewer.Offset.Y, 3);

        context.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(initialViewport.Height + wheelOffsetDelta, scrollViewer.Viewport.Height, 3);
        Assert.Equal(initialExtent.Height, scrollViewer.Extent.Height, 3);
        Assert.Equal(initialOffset, scrollViewer.Offset.Y, 3);
        Assert.Equal(2, scrollChanges.Count);
        Assert.Equal(0, scrollChanges.Sum(change => change.OffsetDelta.Y), 3);
        Assert.Equal(wheelOffsetDelta, scrollChanges.Sum(change => change.ViewportDelta.Y), 3);
        Assert.Equal(0, scrollChanges.Sum(change => change.ExtentDelta.Y), 3);
        Assert.Equal(wheelOffsetDelta, scrollChanges[0].OffsetDelta.Y, 3);
        Assert.Equal(0, scrollChanges[0].ViewportDelta.Y, 3);
        Assert.Equal(-wheelOffsetDelta, scrollChanges[1].OffsetDelta.Y, 3);
        Assert.Equal(wheelOffsetDelta, scrollChanges[1].ViewportDelta.Y, 3);
        Assert.All(scrollChanges, change => Assert.Equal(0, change.ExtentDelta.Y, 3));
        Assert.Equal(0, captureLostCount);
        Assert.Equal(0, timelineSizeChangedCount);
        Assert.Same(block, activeBlockBorder.GetValue(dayView));

        var followupMovedPoint = new Point(movedPoint.X, movedPoint.Y - 16);
        RaisePointerMoved(block, context.Window, pointer, followupMovedPoint);
        var expectedPixels = Math.Round((wheelOffsetDelta + 14) / 16, MidpointRounding.AwayFromZero) * 16;
        if (resize)
        {
            Assert.Equal(startHeight + expectedPixels, block.Height, 3);
        }
        else
        {
            Assert.Equal(startTop + expectedPixels, Canvas.GetTop(block), 3);
        }
        RaisePointerReleasedAt(block, context.Window, pointer, followupMovedPoint);
        Assert.Null(activeBlockBorder.GetValue(dayView));

        var expectedMinutes = (int)(expectedPixels / 16) * 15;
        var expectedStart = resize ? start : start.AddMinutes(expectedMinutes);
        var expectedEnd = end.AddMinutes(expectedMinutes);
        await UntilAsync(async () =>
        {
            var saved = await context.Store.GetTaskAsync(task.Id);
            return saved?.ScheduledStart?.ToUnixTimeMilliseconds() == expectedStart.ToUnixTimeMilliseconds() &&
                saved.ScheduledEnd?.ToUnixTimeMilliseconds() == expectedEnd.ToUnixTimeMilliseconds();
        });
    });

    [Fact]
    public Task Calendar_switching_to_list_cancels_an_active_block_gesture() => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var day = DateOnly.FromDateTime(DateTime.Today);
        var startLocal = day.ToDateTime(new TimeOnly(11, 30));
        var start = new DateTimeOffset(startLocal, TimeZoneInfo.Local.GetUtcOffset(startLocal));
        var task = existing with
        {
            Id = "calendar-list-switch-gesture-task",
            Title = "Calendar list switch gesture task",
            ParentId = null,
            PlannedOn = day,
            ScheduledStart = start,
            ScheduledEnd = start.AddMinutes(90),
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.CalendarDay.SetSelectedDate(day);
        context.Vm.SetCalendarDayView(true);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();

        var dayView = context.Window.FindControl<CalendarDayView>("CalendarDayView")!;
        var block = Assert.Single(dayView.GetVisualDescendants().OfType<Border>(), border =>
            border.Classes.Contains("calendar-day-block") &&
            border.DataContext is CalendarDayBlockViewModel blockViewModel && blockViewModel.Task.Id == task.Id);
        var pointer = new Pointer(10, PointerType.Mouse, isPrimary: true);
        var pressPoint = block.TranslatePoint(new Point(block.Bounds.Width / 2, block.Bounds.Height / 2), context.Window)!.Value;
        var movedPoint = new Point(pressPoint.X, pressPoint.Y + 30);
        var activeBlockBorder = typeof(CalendarDayView).GetField("_activeBlockBorder", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        Assert.True(RaisePointerPressed(block, context.Window, pointer, pressPoint).Handled);
        RaisePointerMoved(block, context.Window, pointer, movedPoint);
        Assert.Same(block, activeBlockBorder.GetValue(dayView));

        var listButton = context.Window.FindControl<Control>("CalendarListViewButton")!;
        Assert.True(listButton.Focus());
        listButton.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await Until(() => !context.Vm.IsCalendarDayView);
        Assert.False(dayView.IsVisible);
        Assert.Null(activeBlockBorder.GetValue(dayView));

        RaisePointerReleasedAt(block, context.Window, pointer, movedPoint);
        await Task.Delay(50);
        var saved = await context.Store.GetTaskAsync(task.Id);
        Assert.Equal(start.ToUnixTimeMilliseconds(), saved!.ScheduledStart!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(task.ScheduledEnd!.Value.ToUnixTimeMilliseconds(), saved.ScheduledEnd!.Value.ToUnixTimeMilliseconds());
    });

    [Fact]
    public Task Calendar_task_view_context_change_cancels_an_active_block_gesture() => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var day = DateOnly.FromDateTime(DateTime.Today);
        var startLocal = day.ToDateTime(new TimeOnly(11, 30));
        var start = new DateTimeOffset(startLocal, TimeZoneInfo.Local.GetUtcOffset(startLocal));
        var task = existing with
        {
            Id = "calendar-context-change-gesture-task",
            Title = "Calendar context-change gesture task",
            ParentId = null,
            PlannedOn = day,
            ScheduledStart = start,
            ScheduledEnd = start.AddMinutes(90),
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.CalendarDay.SetSelectedDate(day);
        context.Vm.SetCalendarDayView(true);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();

        var dayView = context.Window.FindControl<CalendarDayView>("CalendarDayView")!;
        var block = Assert.Single(dayView.GetVisualDescendants().OfType<Border>(), border =>
            border.Classes.Contains("calendar-day-block") &&
            border.DataContext is CalendarDayBlockViewModel blockViewModel && blockViewModel.Task.Id == task.Id);
        var item = ((CalendarDayBlockViewModel)block.DataContext!).Item;
        var pointer = new Pointer(11, PointerType.Mouse, isPrimary: true);
        var pressPoint = block.TranslatePoint(new Point(block.Bounds.Width / 2, block.Bounds.Height / 2), context.Window)!.Value;
        var movedPoint = new Point(pressPoint.X, pressPoint.Y + 30);
        var activeBlockBorder = typeof(CalendarDayView).GetField("_activeBlockBorder", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        Assert.True(RaisePointerPressed(block, context.Window, pointer, pressPoint).Handled);
        RaisePointerMoved(block, context.Window, pointer, movedPoint);
        context.Vm.CalendarDay.SetDayTasks(
            [item],
            new Openza.Tasks.Desktop.ViewModels.Navigation.TaskViewContext("another-space", TaskListKind.Calendar));
        Assert.Null(activeBlockBorder.GetValue(dayView));

        RaisePointerReleasedAt(block, context.Window, pointer, movedPoint);
        await Task.Delay(50);
        var saved = await context.Store.GetTaskAsync(task.Id);
        Assert.Equal(start.ToUnixTimeMilliseconds(), saved!.ScheduledStart!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(task.ScheduledEnd!.Value.ToUnixTimeMilliseconds(), saved.ScheduledEnd!.Value.ToUnixTimeMilliseconds());
    });

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public Task Calendar_explicit_scroll_during_resize_remains_part_of_pointer_delta(int wheelCount) => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var atScrollBoundary = wheelCount < 0;
        var day = DateOnly.FromDateTime(DateTime.Today);
        var startLocal = day.ToDateTime(atScrollBoundary ? new TimeOnly(21, 0) : new TimeOnly(11, 30));
        var start = new DateTimeOffset(startLocal, TimeZoneInfo.Local.GetUtcOffset(startLocal));
        var end = start.AddMinutes(90);
        var task = existing with
        {
            Id = "calendar-explicit-scroll-resize-task",
            Title = "Calendar explicit scroll resize task",
            ParentId = null,
            PlannedOn = day,
            ScheduledStart = start,
            ScheduledEnd = end,
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.CalendarDay.SetSelectedDate(day);
        context.Vm.SetCalendarDayView(true);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();
        await WaitForCalendarSearchDebounceAsync(context);

        var dayView = context.Window.FindControl<CalendarDayView>("CalendarDayView")!;
        var scrollViewer = dayView.FindControl<ScrollViewer>("TimelineScrollViewer")!;
        var scrollableHeight = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        scrollViewer.Offset = new Vector(0, atScrollBoundary ? Math.Max(0, scrollableHeight - 20) : 300);
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();
        var block = Assert.Single(dayView.GetVisualDescendants().OfType<Border>(), border =>
            border.Classes.Contains("calendar-day-block") &&
            border.DataContext is CalendarDayBlockViewModel blockViewModel && blockViewModel.Task.Id == task.Id);
        var pointer = new Pointer(9, PointerType.Mouse, isPrimary: true);
        var pressPoint = block.TranslatePoint(
            new Point(block.Bounds.Width / 2, block.Bounds.Height - 4),
            context.Window)!.Value;
        var movedPoint = new Point(pressPoint.X, pressPoint.Y + 30);
        var activeBlockBorder = typeof(CalendarDayView).GetField(
            "_activeBlockBorder",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var scrollChanges = new List<ScrollChangedEventArgs>();
        scrollViewer.ScrollChanged += (_, e) => scrollChanges.Add(e);

        Assert.True(RaisePointerPressed(block, context.Window, pointer, pressPoint).Handled);
        RaisePointerMoved(block, context.Window, pointer, movedPoint);
        var initialOffset = scrollViewer.Offset;
        var wheelOffsetDelta = 0d;
        var wheelEvents = atScrollBoundary ? 2 : wheelCount;
        for (var wheel = 0; wheel < wheelEvents; wheel++)
        {
            var offsetBeforeWheel = scrollViewer.Offset.Y;
            RaisePointerWheelChanged(block, context.Window, pointer, movedPoint, new Vector(0, -1));
            wheelOffsetDelta += scrollViewer.Offset.Y - offsetBeforeWheel;
        }
        var programmaticOffsetDelta = atScrollBoundary ? 0 : 48;
        if (programmaticOffsetDelta != 0)
        {
            scrollViewer.Offset = new Vector(initialOffset.X, scrollViewer.Offset.Y + programmaticOffsetDelta);
        }
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();

        if (wheelCount == 0)
        {
            var explicitScroll = Assert.Single(scrollChanges, change => change.OffsetDelta.Y != 0);
            Assert.Equal(48, explicitScroll.OffsetDelta.Y);
            Assert.Equal(0, explicitScroll.ViewportDelta.Y);
            Assert.Equal(0, explicitScroll.ExtentDelta.Y);
        }
        else
        {
            Assert.Equal(wheelOffsetDelta + programmaticOffsetDelta, scrollChanges.Sum(change => change.OffsetDelta.Y), 3);
            Assert.All(scrollChanges, change =>
            {
                Assert.Equal(0, change.ViewportDelta.Y);
                Assert.Equal(0, change.ExtentDelta.Y);
            });
        }
        if (atScrollBoundary)
        {
            var maxOffset = scrollViewer.Extent.Height - scrollViewer.Viewport.Height;
            Assert.Equal(20, wheelOffsetDelta, 3);
            Assert.Equal(maxOffset, scrollViewer.Offset.Y, 3);
        }
        Assert.Same(block, activeBlockBorder.GetValue(dayView));

        RaisePointerMoved(block, context.Window, pointer, movedPoint);
        RaisePointerReleasedAt(block, context.Window, pointer, movedPoint);

        var expectedMinutes = (int)Math.Round(
            (30 + wheelOffsetDelta + programmaticOffsetDelta) / 16,
            MidpointRounding.AwayFromZero) * 15;
        var expectedEnd = end.AddMinutes(expectedMinutes);
        await UntilAsync(async () =>
        {
            var saved = await context.Store.GetTaskAsync(task.Id);
            return saved?.ScheduledStart?.ToUnixTimeMilliseconds() == start.ToUnixTimeMilliseconds() &&
                saved.ScheduledEnd?.ToUnixTimeMilliseconds() == expectedEnd.ToUnixTimeMilliseconds();
        });
    });

    private sealed class UnpairedPersonalCloudSetupService : IPersonalCloudSetupService
    {
        public bool IsConfigured => true;
        public PersonalCloudConnectionProfile? CurrentConnection { get; set; } = new()
        {
            ServiceOrigin = new Uri("https://cloud.example/"),
            InstanceId = "calendar-sync-test",
            DeviceId = "calendar-sync-test",
            ArtifactVersion = "1.0.0",
            ConnectedAt = DateTimeOffset.UtcNow,
        };

        public Task OpenAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<PersonalCloudProvisioningResult> ConnectAsync(
            CancellationToken cancellationToken = default,
            IProgress<PersonalCloudSetupProgress>? progress = null) =>
            Task.FromException<PersonalCloudProvisioningResult>(new NotSupportedException());

        public Task<PersonalCloudConnectionProfile> PairExistingAsync(
            Uri serviceOrigin,
            string grant,
            string deviceName,
            bool providerBridge,
            CancellationToken cancellationToken = default) =>
            Task.FromException<PersonalCloudConnectionProfile>(new NotSupportedException());

        public async Task<PersonalCloudSyncResult> SyncAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(250, cancellationToken);
            CurrentConnection = null;
            throw new IOException("This desktop is not paired with Personal Cloud.");
        }

        public Task<CloudflareD1Export> ExportAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<CloudflareD1Export>(new NotSupportedException());

        public Task<PersonalCloudUpgradeResult> UpgradeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PersonalCloudUpgradeResult("1.0.0"));

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public Task Calendar_schedule_refresh_cancels_an_active_block_gesture() => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var day = DateOnly.FromDateTime(DateTime.Today);
        var startLocal = day.ToDateTime(new TimeOnly(11, 30));
        var start = new DateTimeOffset(startLocal, TimeZoneInfo.Local.GetUtcOffset(startLocal));
        var task = existing with
        {
            Id = "calendar-schedule-refresh-gesture-task",
            Title = "Calendar schedule refresh gesture task",
            ParentId = null,
            PlannedOn = day,
            ScheduledStart = start,
            ScheduledEnd = start.AddMinutes(90),
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.CalendarDay.SetSelectedDate(day);
        context.Vm.SetCalendarDayView(true);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();

        var dayView = context.Window.FindControl<CalendarDayView>("CalendarDayView")!;
        var canvas = dayView.FindControl<Canvas>("TimelineCanvas")!;
        var block = Assert.Single(dayView.GetVisualDescendants().OfType<Border>(), border =>
            border.Classes.Contains("calendar-day-block") &&
            border.DataContext is CalendarDayBlockViewModel blockViewModel && blockViewModel.Task.Id == task.Id);
        var pointer = new Pointer(4, PointerType.Mouse, isPrimary: true);
        var pressPoint = block.TranslatePoint(new Point(block.Bounds.Width / 2, block.Bounds.Height / 2), context.Window)!.Value;
        var movedPoint = new Point(pressPoint.X, pressPoint.Y + 30);
        var activeBlockBorder = typeof(CalendarDayView).GetField(
            "_activeBlockBorder",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        Assert.True(RaisePointerPressed(block, context.Window, pointer, pressPoint).Handled);
        RaisePointerMoved(block, context.Window, pointer, movedPoint);
        Assert.Same(block, activeBlockBorder.GetValue(dayView));

        var refreshedStart = start.AddHours(2);
        var refreshedEnd = refreshedStart.AddMinutes(90);
        await context.Store.UpsertTaskAsync(task with { ScheduledStart = refreshedStart, ScheduledEnd = refreshedEnd });
        await context.Vm.ApplySearchAsync();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(context.Vm.CalendarDay.Blocks, refreshedBlock =>
            refreshedBlock.Task.Id == task.Id && refreshedBlock.Start == refreshedStart && refreshedBlock.End == refreshedEnd);
        Assert.Null(activeBlockBorder.GetValue(dayView));
        Assert.DoesNotContain(block, canvas.Children);
        RaisePointerMoved(block, context.Window, pointer, movedPoint);
        RaisePointerReleasedAt(block, context.Window, pointer, movedPoint);

        await UntilAsync(async () =>
        {
            var saved = await context.Store.GetTaskAsync(task.Id);
            return saved?.ScheduledStart?.ToUnixTimeMilliseconds() == refreshedStart.ToUnixTimeMilliseconds() &&
                saved.ScheduledEnd?.ToUnixTimeMilliseconds() == refreshedEnd.ToUnixTimeMilliseconds();
        });
        Assert.DoesNotContain(block, canvas.Children);
    });

    [Fact]
    public Task Calendar_previous_day_change_cancels_an_active_block_gesture() => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var day = DateOnly.FromDateTime(DateTime.Today);
        var startLocal = day.ToDateTime(new TimeOnly(11, 30));
        var start = new DateTimeOffset(startLocal, TimeZoneInfo.Local.GetUtcOffset(startLocal));
        var task = existing with
        {
            Id = "calendar-date-change-gesture-task",
            Title = "Calendar date-change gesture task",
            ParentId = null,
            PlannedOn = day,
            ScheduledStart = start,
            ScheduledEnd = start.AddMinutes(90),
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.CalendarDay.SetSelectedDate(day);
        context.Vm.SetCalendarDayView(true);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();

        var dayView = context.Window.FindControl<CalendarDayView>("CalendarDayView")!;
        var block = Assert.Single(dayView.GetVisualDescendants().OfType<Border>(), border =>
            border.Classes.Contains("calendar-day-block") &&
            border.DataContext is CalendarDayBlockViewModel blockViewModel && blockViewModel.Task.Id == task.Id);
        var pointer = new Pointer(5, PointerType.Mouse, isPrimary: true);
        var pressPoint = block.TranslatePoint(new Point(block.Bounds.Width / 2, block.Bounds.Height / 2), context.Window)!.Value;
        var movedPoint = new Point(pressPoint.X, pressPoint.Y + 30);
        var canvas = dayView.FindControl<Canvas>("TimelineCanvas")!;
        var activeBlockBorder = typeof(CalendarDayView).GetField(
            "_activeBlockBorder",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        Assert.True(RaisePointerPressed(block, context.Window, pointer, pressPoint).Handled);
        RaisePointerMoved(block, context.Window, pointer, movedPoint);
        Assert.Same(block, activeBlockBorder.GetValue(dayView));
        var previousDayButton = Assert.Single(dayView.GetVisualDescendants().OfType<Button>(), button =>
            AutomationProperties.GetName(button) == "Previous day");
        previousDayButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(day.AddDays(-1), context.Vm.CalendarDay.SelectedDate);
        Assert.Null(activeBlockBorder.GetValue(dayView));
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(block, canvas.Children);

        RaisePointerMoved(block, context.Window, pointer, movedPoint);
        RaisePointerReleasedAt(block, context.Window, pointer, movedPoint);

        await UntilAsync(async () =>
        {
            var saved = await context.Store.GetTaskAsync(task.Id);
            return saved?.ScheduledStart?.ToUnixTimeMilliseconds() == start.ToUnixTimeMilliseconds() &&
                saved.ScheduledEnd?.ToUnixTimeMilliseconds() == task.ScheduledEnd?.ToUnixTimeMilliseconds();
        });
    });

    [Fact]
    public Task Saving_a_today_task_hidden_by_list_filters_keeps_its_editor_open() => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var now = DateTimeOffset.Now;
        var task = existing with
        {
            Id = "calendar-filtered-today-task",
            Title = "Filtered Today task",
            ParentId = null,
            PlannedOn = DateOnly.FromDateTime(now.LocalDateTime),
            ScheduledStart = now.AddMinutes(30),
            ScheduledEnd = now.AddHours(1),
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.SetCalendarDayView(true);
        context.Vm.SearchText = "not this task";
        await context.Vm.ApplySearchAsync();

        var todayTask = Assert.Single(context.Vm.CalendarDay.FilteredTodayTasks, item => item.Task.Id == task.Id);
        await context.Vm.SelectTaskAsync(todayTask);
        context.Vm.DetailTitle = "Saved from Today";
        Assert.True(await context.Vm.SaveSelectedAsync());

        Assert.Equal(task.Id, context.Vm.SelectedTask?.Task.Id);
        Assert.Equal("Saved from Today", context.Vm.DetailTitle);
        Assert.Equal("Saved from Today", (await context.Store.GetTaskAsync(task.Id))?.Title);
    });

    [Fact]
    public Task Completing_a_selected_today_task_saves_its_detail_edits_first() => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var now = DateTimeOffset.Now;
        var task = existing with
        {
            Id = "calendar-completion-edit-task",
            Title = "Before completion",
            ParentId = null,
            PlannedOn = DateOnly.FromDateTime(now.LocalDateTime),
            ScheduledStart = now.AddMinutes(30),
            ScheduledEnd = now.AddHours(1),
            CompletionState = TaskCompletionState.Open,
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.SetCalendarDayView(true);
        context.Vm.ToggleCalendarTodayPanel();
        context.Window.UpdateLayout();
        var todayTask = Assert.Single(context.Vm.CalendarDay.FilteredTodayTasks, item => item.Task.Id == task.Id);
        await context.Vm.SelectTaskAsync(todayTask);
        context.Vm.DetailTitle = "Saved before completion";
        var completion = context.Window.GetVisualDescendants().OfType<CheckBox>()
            .Single(box => AutomationProperties.GetName(box) == "Complete Before completion");

        completion.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !context.Vm.CalendarDay.FilteredTodayTasks.Any(item => item.Task.Id == task.Id));

        var saved = (await context.Store.GetTaskAsync(task.Id))!;
        Assert.Equal("Saved before completion", saved.Title);
        Assert.True(saved.IsCompleted);
    });

    [Fact]
    public Task Today_completion_checkbox_reverts_when_saving_the_editor_is_rejected() => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var now = DateTimeOffset.Now;
        var task = existing with
        {
            Id = "calendar-rejected-completion-task",
            Title = "Rejected completion",
            ParentId = null,
            PlannedOn = DateOnly.FromDateTime(now.LocalDateTime),
            ScheduledStart = now.AddMinutes(30),
            ScheduledEnd = now.AddHours(1),
            CompletionState = TaskCompletionState.Open,
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.SetCalendarDayView(true);
        context.Vm.ToggleCalendarTodayPanel();
        context.Window.UpdateLayout();
        var todayTask = Assert.Single(context.Vm.CalendarDay.FilteredTodayTasks, item => item.Task.Id == task.Id);
        await context.Vm.SelectTaskAsync(todayTask);
        context.Vm.DetailTitle = "   ";
        var completion = context.Window.GetVisualDescendants().OfType<CheckBox>()
            .Single(box => AutomationProperties.GetName(box) == "Complete Rejected completion");
        completion.IsChecked = true;

        completion.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !context.Vm.IsBusy && completion.IsChecked == false);

        Assert.False((await context.Store.GetTaskAsync(task.Id))!.IsCompleted);
    });

    [Fact]
    public Task Calendar_today_task_can_be_opened_with_the_keyboard() => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var task = existing with
        {
            Id = "keyboard-today-task",
            Title = "Keyboard accessible Today row",
            ParentId = null,
            PlannedOn = DateOnly.FromDateTime(DateTime.Today),
            CompletionState = TaskCompletionState.Open,
        };
        await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.SetCalendarDayView(true);
        context.Vm.ToggleCalendarTodayPanel();
        context.Window.UpdateLayout();

        var rowButton = context.Window.GetVisualDescendants().OfType<Button>()
            .Single(button => (AutomationProperties.GetName(button) ?? string.Empty).StartsWith(task.Title, StringComparison.Ordinal));
        Assert.Equal(task.Title, ((TaskListItemViewModel)rowButton.DataContext!).Title);
        Assert.Contains(context.Window.GetVisualDescendants().OfType<CheckBox>(), box =>
            AutomationProperties.GetName(box) == $"Complete {task.Title}");
        var search = context.Window.GetVisualDescendants().OfType<TextBox>()
            .Single(box => AutomationProperties.GetName(box) == "Search Today tasks");
        Assert.True(search.Focus());
        // Headless input may refresh row controls; tab to the live task button before Enter.
        var tabLimit = context.Window.GetVisualDescendants().OfType<Control>()
            .Count(control => control.Focusable && control.IsEnabled && control.IsVisible);
        var taskButtonFocused = false;
        for (var tab = 0; tab < tabLimit && !taskButtonFocused; tab++)
        {
            context.Window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            context.Window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            taskButtonFocused = context.Window.FocusManager.GetFocusedElement() is Button focusedButton &&
                focusedButton.GetType() == typeof(Button) &&
                focusedButton.DataContext is TaskListItemViewModel focusedTask && focusedTask.Task.Id == task.Id;
        }
        Assert.True(taskButtonFocused, $"Tab did not focus the Today task row after {tabLimit} keyboard steps.");
        var focusedTaskButton = Assert.IsType<Button>(context.Window.FocusManager.GetFocusedElement());
        Assert.Equal(task.Id, ((TaskListItemViewModel)focusedTaskButton.DataContext!).Task.Id);
        var clickEvents = 0;
        focusedTaskButton.Click += (_, _) => clickEvents++;
        context.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        context.Window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.True(clickEvents > 0,
            $"Enter did not activate the focused Today row. click={clickEvents}; focused={context.Window.FocusManager.GetFocusedElement()?.GetType().Name ?? "<none>"}");
        await Until(() => context.Vm.SelectedTask?.Task.Id == task.Id);
    });

    [Fact]
    public Task Pointer_completion_after_dirty_title_save_completes_in_one_click() => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var task = existing with
        {
            Title = "Focus task",
            ParentId = null,
            PlannedOn = today,
            CompletionState = TaskCompletionState.Open,
        };
        await context.Store.UpsertTaskAsync(task);
        var yesterdayTask = existing with
        {
            Id = "calendar-yesterday",
            Title = "Yesterday only task",
            ParentId = null,
            PlannedOn = today.AddDays(-1),
            CompletionState = TaskCompletionState.Open,
        };
        await context.Store.UpsertTaskAsync(yesterdayTask);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.SearchText = "Yesterday";
        await context.Vm.ApplySearchAsync();
        Assert.Contains(context.Vm.Tasks, item => item.Task.Id == yesterdayTask.Id);
        Assert.DoesNotContain(context.Vm.Tasks, item => item.Task.Id == task.Id);
        context.Vm.SetCalendarDayView(true);
        context.Vm.ToggleCalendarTodayPanel();
        context.Vm.CalendarDay.TodaySearchText = "Focus";
        context.Window.UpdateLayout();

        var todayTask = Assert.Single(context.Vm.CalendarDay.FilteredTodayTasks, item => item.Task.Id == task.Id);
        await context.Vm.SelectTaskAsync(todayTask);
        await Until(() => context.Vm.SelectedTask?.Task.Id == task.Id && !context.Vm.IsUpdatingDetails && !context.Vm.IsBusy);
        var title = context.Window.GetVisualDescendants().OfType<TextBox>()
            .Single(box => AutomationProperties.GetName(box) == "Task title");
        Assert.True(title.Focus());
        title.Text = "Focus saved after canceled click";

        var completion = context.Window.GetVisualDescendants().OfType<CheckBox>()
            .Single(box => box.DataContext is TaskListItemViewModel item && item.Task.Id == task.Id);
        var pointer = new Pointer(1, PointerType.Mouse, isPrimary: true);
        RaisePointerPressed(completion, context.Window, pointer);
        Assert.True(completion.Focus());
        Assert.False(title.IsFocused);
        RaisePointerCaptureLost(completion, pointer);
        await UntilAsync(async () =>
        {
            var saved = await context.Store.GetTaskAsync(task.Id);
            return saved?.Title == "Focus saved after canceled click" && !saved.IsCompleted;
        });
        await Until(() => !context.Vm.IsBusy && !context.Vm.IsUpdatingDetails &&
            context.Vm.CalendarDay.FilteredTodayTasks.Any(item => item.Task.Id == task.Id));
        context.Window.UpdateLayout();

        title = context.Window.GetVisualDescendants().OfType<TextBox>()
            .Single(box => AutomationProperties.GetName(box) == "Task title");
        Assert.True(title.IsFocused || title.Focus());
        title.Text = "Focus completed after one click";
        completion = context.Window.GetVisualDescendants().OfType<CheckBox>()
            .Single(box => box.DataContext is TaskListItemViewModel item && item.Task.Id == task.Id);
        RaisePointerPressed(completion, context.Window, pointer);
        Assert.True(completion.Focus());
        RaisePointerCaptureLost(completion, pointer);
        RaisePointerReleased(completion, context.Window, pointer, handled: true);
        completion.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(async () =>
        {
            var saved = await context.Store.GetTaskAsync(task.Id);
            return saved?.Title == "Focus completed after one click" && saved.IsCompleted;
        });
    });

    [Fact]
    public Task Pointer_completion_canceled_handled_release_saves_deferred_title() => Run(async context =>
    {
        var existing = (await context.Store.GetTaskAsync("task-0"))!;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var task = existing with
        {
            Title = "Focus task",
            ParentId = null,
            PlannedOn = today,
            CompletionState = TaskCompletionState.Open,
        };
        await context.Store.UpsertTaskAsync(task);
        var yesterdayTask = existing with
        {
            Id = "calendar-yesterday",
            Title = "Yesterday only task",
            ParentId = null,
            PlannedOn = today.AddDays(-1),
            CompletionState = TaskCompletionState.Open,
        };
        await context.Store.UpsertTaskAsync(yesterdayTask);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Calendar));
        context.Vm.SearchText = "Yesterday";
        await context.Vm.ApplySearchAsync();
        context.Vm.SetCalendarDayView(true);
        context.Vm.ToggleCalendarTodayPanel();
        context.Vm.CalendarDay.TodaySearchText = "Focus";
        context.Window.UpdateLayout();

        var todayTask = Assert.Single(context.Vm.CalendarDay.FilteredTodayTasks, item => item.Task.Id == task.Id);
        await context.Vm.SelectTaskAsync(todayTask);
        await Until(() => context.Vm.SelectedTask?.Task.Id == task.Id && !context.Vm.IsUpdatingDetails && !context.Vm.IsBusy);
        var title = context.Window.GetVisualDescendants().OfType<TextBox>()
            .Single(box => AutomationProperties.GetName(box) == "Task title");
        Assert.True(title.Focus());
        title.Text = "Focus saved after handled release";

        var completion = context.Window.GetVisualDescendants().OfType<CheckBox>()
            .Single(box => box.DataContext is TaskListItemViewModel item && item.Task.Id == task.Id);
        var pointer = new Pointer(1, PointerType.Mouse, isPrimary: true);
        RaisePointerPressed(completion, context.Window, pointer);
        Assert.True(completion.Focus());
        Assert.False(title.IsFocused);
        RaisePointerReleased(context.Window, context.Window, pointer, handled: true);
        await UntilAsync(async () =>
        {
            var saved = await context.Store.GetTaskAsync(task.Id);
            return saved?.Title == "Focus saved after handled release" && !saved.IsCompleted;
        });
        await Until(() => !context.Vm.IsBusy && !context.Vm.IsUpdatingDetails);
    });

    private static PointerPressedEventArgs RaisePointerPressed(InputElement source, Window window, IPointer pointer, Point? rootPosition = null)
    {
        var args = new PointerPressedEventArgs(
            source,
            pointer,
            window,
            rootPosition ?? new Point(),
            1,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None,
            1)
        {
            RoutedEvent = InputElement.PointerPressedEvent,
        };
        source.RaiseEvent(args);
        return args;
    }

    private static void RaisePointerMoved(InputElement source, Window window, IPointer pointer, Point rootPosition) =>
        source.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent,
            source,
            pointer,
            window,
            rootPosition,
            2,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None)
        {
            RoutedEvent = InputElement.PointerMovedEvent,
        });

    private static void RaisePointerWheelChanged(InputElement source, Window window, IPointer pointer, Point rootPosition, Vector delta) =>
        source.RaiseEvent(new PointerWheelEventArgs(
            source,
            pointer,
            window,
            rootPosition,
            4,
            new PointerPointProperties(),
            KeyModifiers.None,
            delta)
        {
            RoutedEvent = InputElement.PointerWheelChangedEvent,
        });

    private static void RaisePointerReleasedAt(InputElement source, Window window, IPointer pointer, Point rootPosition) =>
        source.RaiseEvent(new PointerReleasedEventArgs(
            source,
            pointer,
            window,
            rootPosition,
            3,
            new PointerPointProperties(),
            KeyModifiers.None,
            MouseButton.Left)
        {
            RoutedEvent = InputElement.PointerReleasedEvent,
        });

    private static void RaisePointerCaptureLost(InputElement source, IPointer pointer) =>
        source.RaiseEvent(new PointerCaptureLostEventArgs(source, pointer)
        {
            RoutedEvent = InputElement.PointerCaptureLostEvent,
        });

    private static void RaisePointerReleased(InputElement source, MainWindow window, IPointer pointer, bool handled) =>
        source.RaiseEvent(new PointerReleasedEventArgs(
            source,
            pointer,
            window,
            new Point(),
            2,
            new PointerPointProperties(),
            KeyModifiers.None,
            MouseButton.Left)
        {
            RoutedEvent = InputElement.PointerReleasedEvent,
            Handled = handled,
        });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Create_and_open_preserves_saved_filters_through_shell_events_and_restart(bool completed, bool changeView) => Run(async context =>
    {
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.NextActions));
        var key = $"{context.Vm.SelectedSpace?.SpaceId ?? "all"}|{(completed ? "completed" : "tasks")}|all";
        var label = context.Vm.LabelFilterOptions.First(item => item.LabelId is not null);
        await context.Preferences.UpdateAsync(preferences =>
        {
            preferences.TaskViewSettings[key] = new() { PriorityFilterIndex = 1, RepeatFilterIndex = 2, LabelFilterId = label.LabelId };
            return preferences;
        });
        if (!changeView)
        {
            await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item =>
                item.Kind == (completed ? TaskListKind.Completed : TaskListKind.Open)));
        }
        await Until(() => ListOptionsIdle(context.Window) && !context.Vm.IsBusy);
        var trigger = context.Window.FindControl<WrapPanel>("TaskFilterCommands")!.Children.OfType<Button>().Last();
        var flyout = (Flyout)trigger.Flyout!;
        flyout.ShowAt(trigger);
        context.Window.UpdateLayout();
        var filters = ((StackPanel)flyout.Content!).Children.OfType<ComboBox>().ToArray();
        var selectionEvents = 0;
        foreach (var filter in filters) filter.SelectionChanged += (_, _) => selectionEvents++;

        await context.Vm.CreateTaskAsync(new("Reveal outside current list", "", null, completed ? 4 : 0, 2, null, "", true));
        await Until(() => ListOptionsIdle(context.Window) && !context.Vm.IsBusy);
        Assert.True(selectionEvents >= 3, "Bound controls must exercise the coalesced shell event path.");
        Assert.Equal("Reveal outside current list", context.Vm.SelectedTask!.Task.Title);
        Assert.Equal(0, context.Vm.PriorityFilterIndex);
        Assert.Equal(0, context.Vm.RepeatFilterIndex);
        var saved = context.Preferences.Load().TaskViewSettings[key];
        Assert.Equal(1, saved.PriorityFilterIndex);
        Assert.Equal(2, saved.RepeatFilterIndex);
        Assert.Equal(label.LabelId, saved.LabelFilterId);
        flyout.Hide();
        await Until(() => ListOptionsIdle(context.Window) && !context.Vm.IsBusy);
        Assert.Equal(1, context.Preferences.Load().TaskViewSettings[key].PriorityFilterIndex);
        context.Window.Close();
        await Until(() => !context.Window.IsVisible);
        Assert.Equal(1, context.Preferences.Load().TaskViewSettings[key].PriorityFilterIndex);

        var restarted = new MainWindowViewModel(context.Store, new InMemoryCredentialStore(), preferencesStore: context.Preferences);
        var window = new MainWindow(restarted) { Width = 1440, Height = 900 };
        try
        {
            window.Show();
            await Until(() => restarted.StatusMessage.StartsWith("Local data") && !restarted.IsBusy && ListOptionsIdle(window));
            Assert.Equal(1, restarted.PriorityFilterIndex);
            Assert.Equal(2, restarted.RepeatFilterIndex);
            Assert.Equal(label.LabelId, restarted.SelectedLabelFilter?.LabelId);
            var button = window.FindControl<WrapPanel>("TaskFilterCommands")!.Children.OfType<Button>().Last();
            var picker = (Flyout)button.Flyout!;
            picker.ShowAt(button);
            window.UpdateLayout();
            var choices = ((StackPanel)picker.Content!).Children.OfType<ComboBox>().ToArray();
            // Ordinary rapid user selections still coalesce and persist after the reveal.
            choices[0].SelectedIndex = 2;
            choices[1].SelectedIndex = 1;
            choices[2].SelectedItem = restarted.LabelFilterOptions.First(item => item.LabelId is null);
            await Until(() => ListOptionsIdle(window) && !restarted.IsBusy);
            var updated = context.Preferences.Load().TaskViewSettings[key];
            Assert.Equal(2, updated.PriorityFilterIndex);
            Assert.Equal(1, updated.RepeatFilterIndex);
            Assert.Null(updated.LabelFilterId);
            var nextRestart = new MainWindowViewModel(context.Store, new InMemoryCredentialStore(), preferencesStore: context.Preferences);
            await nextRestart.InitializeAsync();
            Assert.Equal(2, nextRestart.PriorityFilterIndex);
            Assert.Equal(1, nextRestart.RepeatFilterIndex);
            Assert.Null(nextRestart.SelectedLabelFilter?.LabelId);
            picker.Hide();
            await Until(() => ListOptionsIdle(window) && !restarted.IsBusy);
            Assert.Equal(2, context.Preferences.Load().TaskViewSettings[key].PriorityFilterIndex);
            Assert.Equal(1, context.Preferences.Load().TaskViewSettings[key].RepeatFilterIndex);
        }
        finally { window.Close(); }
    });

    private static bool ListOptionsIdle(MainWindow window) =>
        new[] { "_applyingListOptions", "_changingNavigation" }.All(field =>
            !(bool)typeof(MainWindow).GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!);

    [Fact]
    public Task Getting_started_commands_sync_feedback_and_narrow_project_toggle_are_reachable() => Run(async context =>
    {
        await context.Preferences.UpdateAsync(value => value with { ShowGetStarted = true });
        await context.Vm.CreateSpaceAsync("Empty workspace");
        await context.Vm.SelectSpaceAsync(context.Vm.EditableSpaceItems.Single(item => item.Title == "Empty workspace"));
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Inbox));
        context.Window.UpdateLayout();
        var banner = context.Window.FindControl<Border>("GetStartedPanel")!;
        Assert.True(banner.IsVisible);
        Click(banner, "Sync");
        await Until(() => context.Vm.StatusMessage == "Connect an app in Settings before syncing." && !context.Vm.IsSyncing);
        Click(banner, "Add task");
        await Until(() => context.Window.OwnedWindows.OfType<AddTaskWindow>().Any());
        Click(context.Window.OwnedWindows.OfType<AddTaskWindow>().Single(), "Cancel");
        Click(banner, "Dismiss");
        await Until(() => !banner.IsVisible);
        Assert.False(context.Preferences.Load().ShowGetStarted);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        context.Window.Width = 920; await Task.Delay(100); context.Window.UpdateLayout();
        var projects = context.Window.FindControl<Border>("ProjectsPane")!;
        var command = context.Window.FindControl<Button>("ProjectsCommand")!;
        Assert.True(command.IsVisible, $"Window={context.Window.Bounds}; Workbench={context.Window.FindControl<Grid>("WorkbenchGrid")!.Bounds}; navigation={context.Vm.SelectedNavigation?.Kind}; selected={context.Vm.SelectedTask?.Task.Id}");
        var before = projects.IsVisible;
        command.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.NotEqual(before, projects.IsVisible);
        command.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(before, projects.IsVisible);
        context.Window.FindControl<Button>("SyncNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Window.FindControl<ScrollViewer>("SyncWorkspace")!.IsVisible);
        context.Window.UpdateLayout(); context.Vm.ReportStatus(""); Click(context.Window.FindControl<ScrollViewer>("SyncWorkspace")!, "Sync now");
        await Until(() => context.Vm.StatusMessage == "Connect an app in Settings before syncing." && !context.Vm.IsSyncing);
    });

    [Fact]
    public Task Shell_launchers_search_empty_state_and_compact_space_selection_work() => Run(async context =>
    {
        context.Window.GetVisualDescendants().OfType<Button>().First(button => AutomationProperties.GetName(button) == "Add task")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Window.OwnedWindows.OfType<AddTaskWindow>().Any());
        Click(context.Window.OwnedWindows.OfType<AddTaskWindow>().Single(), "Cancel");
        context.Window.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Search tasks and projects")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Window.OwnedWindows.OfType<GlobalSearchWindow>().Any());
        context.Window.OwnedWindows.OfType<GlobalSearchWindow>().Single().Close();
        var search = context.Window.FindControl<TextBox>("SearchBox")!;
        search.Text = "missing title";
        search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await Until(() => context.Vm.Tasks.Count == 0 && !context.Vm.IsBusy);
        context.Window.UpdateLayout();
        Click(context.Window, context.Vm.EmptyStateActionText);
        await Until(() => context.Vm.Tasks.Count == 4 && !context.Vm.IsBusy);
        Assert.Equal("", search.Text);
        await context.Vm.CreateSpaceAsync("Compact destination");
        context.Window.FindControl<Button>("NavigationCollapseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var trigger = context.Window.FindControl<Button>("CompactSpacePickerButton")!;
        trigger.Flyout!.ShowAt(trigger); context.Window.UpdateLayout();
        var content = (ItemsControl)((Flyout)trigger.Flyout).Content!;
        content.GetVisualDescendants().OfType<Button>().Single(button => button.DataContext is SpaceNavigationItemViewModel { Title: "Compact destination" })
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Vm.SelectedSpace?.Title == "Compact destination" && !context.Vm.IsBusy);
        Assert.False(trigger.Flyout.IsOpen);
        context.Window.FindControl<Button>("SyncNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Window.FindControl<ScrollViewer>("SyncWorkspace")!.IsVisible);
        Assert.Equal("Sync", context.Preferences.Load().LastView);
    });

    [Fact]
    public Task Project_navigation_group_collapse_and_row_completion_preserve_the_selected_task() => Run(async context =>
    {
        var projectList = context.Window.FindControl<ListBox>("ProjectList")!;
        projectList.SelectedItem = context.Vm.ProjectItems.Single(item => item.Project.Id == "project");
        await Until(() => context.Vm.SelectedProject?.Project.Id == "project" && !context.Vm.IsBusy);
        Assert.Equal(4, context.Vm.Tasks.Count);
        context.Vm.GroupIndex = 1;
        await context.Vm.ApplyListOptionsAsync(); context.Window.UpdateLayout();
        var list = context.Window.FindControl<ListBox>("TaskList")!;
        var header = list.GetVisualDescendants().OfType<Button>().First(button => button.DataContext is TaskListEntryViewModel { IsHeader: true });
        var before = context.Vm.TaskEntries.Count;
        header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(context.Vm.TaskEntries.Count < before);
        context.Window.UpdateLayout();
        list.GetVisualDescendants().OfType<Button>().First(button => button.DataContext is TaskListEntryViewModel { IsHeader: true })
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(before, context.Vm.TaskEntries.Count);
        context.Window.UpdateLayout();
        list.GetVisualDescendants().OfType<CheckBox>().Single(box => AutomationProperties.GetName(box) == "Complete Control test 0")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(async () => (await context.Store.GetTaskAsync("task-0"))!.IsCompleted);
    });

    [Fact]
    public Task Inspector_status_project_move_and_delete_controls_persist_changes() => Run(async context =>
    {
        foreach (var (index, expected) in new[] { (0, TaskItemStatus.Inbox), (1, TaskItemStatus.Next), (2, TaskItemStatus.Waiting),
            (3, TaskItemStatus.Someday), (4, TaskItemStatus.Completed), (5, TaskItemStatus.None) })
        {
            var task = (await context.Store.GetTaskAsync("task-0"))!;
            await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == (task.IsCompleted ? TaskListKind.Completed : TaskListKind.Open)));
            await context.Vm.SelectTaskAsync(context.Vm.Tasks.Single(item => item.Task.Id == "task-0"));
            await Task.Delay(60); context.Window.UpdateLayout();
            context.Window.FindControl<Border>("DetailsPane")!.GetVisualDescendants().OfType<ComboBox>()
                .Single(box => AutomationProperties.GetName(box) == "Task status").SelectedIndex = index;
            await UntilAsync(async () => (await context.Store.GetTaskAsync("task-0"))!.Status == expected);
            await Until(() => !context.Vm.IsBusy && !context.Vm.IsUpdatingDetails);
        }
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await context.Vm.SelectTaskAsync(context.Vm.Tasks.Single(item => item.Task.Id == "task-0"));
        context.Window.UpdateLayout();
        var pane = context.Window.FindControl<Border>("DetailsPane")!;
        pane.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Find or create project")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Window.OwnedWindows.OfType<ProjectPickerWindow>().Any());
        var picker = context.Window.OwnedWindows.OfType<ProjectPickerWindow>().Single();
        picker.FindControl<ListBox>("ProjectsList")!.SelectedIndex = 0;
        Click(picker, "Choose");
        await UntilAsync(async () => (await context.Store.GetTaskAsync("task-0"))!.ProjectId is null);
        await context.Vm.CreateSpaceAsync("Move destination");
        var trigger = FindButton(pane, "Move to space");
        trigger.Flyout!.ShowAt(trigger); context.Window.UpdateLayout();
        var content = (ItemsControl)((Flyout)trigger.Flyout).Content!;
        content.GetVisualDescendants().OfType<Button>().Single(button => button.DataContext is SpaceNavigationItemViewModel { Title: "Move destination" })
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var destination = context.Vm.EditableSpaceItems.Single(item => item.Title == "Move destination");
        await UntilAsync(async () => (await context.Store.GetTaskAsync("task-0"))!.SpaceId == destination.SpaceId);
        trigger.Flyout.Hide(); await Until(() => !context.Vm.IsBusy);
        await context.Vm.SelectSpaceAsync(destination);
        await context.Vm.SelectTaskAsync(context.Vm.Tasks.Single(item => item.Task.Id == "task-0"));
        Click(pane, "Delete");
        await Until(() => context.Window.OwnedWindows.OfType<ConfirmWindow>().Any());
        Click(context.Window.OwnedWindows.OfType<ConfirmWindow>().Single(), "Cancel");
        Assert.NotNull(await context.Store.GetTaskAsync("task-0"));
        Click(pane, "Delete");
        await Until(() => context.Window.OwnedWindows.OfType<ConfirmWindow>().Any());
        Click(context.Window.OwnedWindows.OfType<ConfirmWindow>().Single(), "Delete");
        await UntilAsync(async () => await context.Store.GetTaskAsync("task-0") is null);
    });

    [Fact]
    public Task Routing_rule_settings_add_edit_cancel_and_delete_update_the_list() => Run(async context =>
    {
        context.Window.FindControl<Button>("SettingsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var settings = context.Window.FindControl<ScrollViewer>("SettingsWorkspace")!;
        await Until(() => settings.IsVisible); context.Window.UpdateLayout();
        foreach (var expander in settings.GetVisualDescendants().OfType<Expander>()) expander.IsExpanded = true;
        context.Window.UpdateLayout();
        Click(settings, "Add rule");
        await Until(() => context.Window.OwnedWindows.OfType<TodoistRoutingRuleWindow>().Any());
        var dialog = context.Window.OwnedWindows.OfType<TodoistRoutingRuleWindow>().Single();
        dialog.FindControl<TextBox>("LabelBox")!.Text = "review";
        Click(dialog, "Save");
        await Until(() => context.Vm.TodoistRoutingRules.Count == 1 && !context.Vm.IsBusy);
        context.Window.UpdateLayout(); Click(settings, "Edit");
        await Until(() => context.Window.OwnedWindows.OfType<TodoistRoutingRuleWindow>().Any());
        dialog = context.Window.OwnedWindows.OfType<TodoistRoutingRuleWindow>().Single();
        Assert.Equal("review", dialog.FindControl<TextBox>("LabelBox")!.Text);
        dialog.FindControl<TextBox>("LabelBox")!.Text = "release"; Click(dialog, "Save");
        await Until(() => context.Vm.TodoistRoutingRules.Single().LabelText.Contains("release") && !context.Vm.IsBusy);
        context.Window.UpdateLayout(); Click(settings, "Delete");
        await Until(() => context.Window.OwnedWindows.OfType<ConfirmWindow>().Any());
        Click(context.Window.OwnedWindows.OfType<ConfirmWindow>().Single(), "Cancel");
        Assert.Single(context.Vm.TodoistRoutingRules);
        Click(settings, "Delete");
        await Until(() => context.Window.OwnedWindows.OfType<ConfirmWindow>().Any());
        Click(context.Window.OwnedWindows.OfType<ConfirmWindow>().Single(), "Delete");
        await Until(() => context.Vm.TodoistRoutingRules.Count == 0);
    });

    [Fact]
    public Task Disconnected_cloud_backup_controls_recover_from_error_and_cancel() => Run(async context =>
    {
        context.Window.FindControl<Button>("SettingsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var settings = context.Window.FindControl<ScrollViewer>("SettingsWorkspace")!;
        await Until(() => settings.IsVisible); context.Window.UpdateLayout();
        var automatic = settings.GetVisualDescendants().OfType<ToggleSwitch>().Single(toggle => Equals(toggle.Content, "Automatic OneDrive backup"));
        automatic.IsChecked = true;
        await Until(() => automatic.IsChecked == false && context.Vm.StatusMessage.Contains("Connect a OneDrive account first"));
        Assert.False(context.Preferences.Load().OneDriveEnabled);
        Assert.False(FindButton(settings, "Back up now").IsEnabled);
        Assert.False(FindButton(settings, "Restore selected cloud backup").IsEnabled);
        Click(settings, "Refresh backups");
        await Until(() => context.Vm.CloudStatus.StartsWith("Cloud backup failed:"));
        Assert.True(context.Vm.IsStatusMessagePersistent);
        var encryption = settings.GetVisualDescendants().OfType<ToggleSwitch>().Single(toggle => Equals(toggle.Content, "Encrypt backups with a passphrase"));
        encryption.IsChecked = true;
        await Until(() => context.Window.OwnedWindows.Any(window => window.Title == "Backup passphrase"));
        Click(context.Window.OwnedWindows.Single(window => window.Title == "Backup passphrase"), "Cancel");
        await Until(() => encryption.IsChecked == false);
        Assert.False(context.Vm.OneDriveEncrypted);
    });

    [Theory]
    [InlineData("Done")]
    [InlineData("Open issue")]
    [InlineData("Replace link")]
    [InlineData("Remove link")]
    public Task GitHub_link_dialog_actions_preserve_tasks_and_report_missing_connection(string action) => Run(async context =>
    {
        await context.Store.UpsertTaskExternalLinkAsync(new TaskExternalLinkInfo
        {
            Id = "link", TaskId = "task-0", IntegrationId = IntegrationIds.GitHub, ExternalId = "invalid-link",
            Kind = TaskExternalLinkKinds.Issue, DisplayName = "example/repo#1", Url = "http://invalid.example/",
        });
        await context.Vm.SelectTaskAsync(context.Vm.Tasks.Single(item => item.Task.Id == "task-0"));
        context.Window.UpdateLayout();
        Click(context.Window.FindControl<Border>("DetailsPane")!, context.Vm.GitHubActionText);
        await Until(() => context.Window.OwnedWindows.Any(window => window.Title == "GitHub issue"));
        var dialog = context.Window.OwnedWindows.Single(window => window.Title == "GitHub issue");
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text!.Contains("cannot access this issue"));
        Click(dialog, action);
        await Until(() => !dialog.IsVisible);
        if (action == "Remove link") await UntilAsync(async () => (await context.Store.GetTaskExternalLinksAsync("task-0")).Count == 0);
        else Assert.Single(await context.Store.GetTaskExternalLinksAsync("task-0"));
        if (action == "Replace link") await UntilAsync(
            () => Task.FromResult(context.Vm.StatusMessage == "Connect GitHub in Settings first."),
            () => $"Replace link feedback: {context.Vm.StatusMessage}; selected task: {context.Vm.SelectedTask?.Task.Id}; busy: {context.Vm.IsBusy}");
        Assert.NotNull(await context.Store.GetTaskAsync("task-0"));
    });

    [Fact]
    public Task Legacy_settings_space_and_provider_buttons_validate_and_preserve_local_tasks() => Run(async context =>
    {
        var settings = new Desktop.Settings.SettingsWindow(context.Vm); settings.Show(context.Window);
        await Task.Delay(50); settings.UpdateLayout();
        var input = settings.FindControl<TextBox>("NewSpaceBox")!;
        input.Text = "Legacy destination";
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await Until(() => context.Vm.EditableSpaceItems.Any(item => item.Title == "Legacy destination") && !context.Vm.IsBusy);
        Assert.Equal("", input.Text);
        input.Text = "Button destination"; Click(settings, "Add space");
        await Until(() => context.Vm.EditableSpaceItems.Any(item => item.Title == "Button destination") && !context.Vm.IsBusy);
        settings.UpdateLayout();
        settings.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Rename") &&
            button.DataContext is SpaceNavigationItemViewModel { Title: "Legacy destination" }).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => settings.OwnedWindows.OfType<TextPromptWindow>().Any());
        var prompt = settings.OwnedWindows.OfType<TextPromptWindow>().Single();
        prompt.FindControl<TextBox>("ValueBox")!.Text = "Legacy renamed"; Click(prompt, "Save");
        await Until(() => context.Vm.EditableSpaceItems.Any(item => item.Title == "Legacy renamed") && !context.Vm.IsBusy);
        settings.UpdateLayout();
        settings.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Archive") &&
            button.DataContext is SpaceNavigationItemViewModel { Title: "Legacy renamed" }).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => settings.OwnedWindows.OfType<ConfirmWindow>().Any());
        Click(settings.OwnedWindows.OfType<ConfirmWindow>().Single(), "Archive");
        await Until(() => !context.Vm.EditableSpaceItems.Any(item => item.Title == "Legacy renamed") && !context.Vm.IsBusy);
        foreach (var button in settings.GetVisualDescendants().OfType<Button>().Where(button => Equals(button.Content, "Connect")).ToArray())
        {
            context.Vm.ReportStatus(""); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => context.Vm.StatusMessage.Contains("token", StringComparison.OrdinalIgnoreCase));
        }
        foreach (var button in settings.GetVisualDescendants().OfType<Button>().Where(button => Equals(button.Content, "Disconnect")).ToArray())
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => settings.OwnedWindows.OfType<ConfirmWindow>().Any());
            Click(settings.OwnedWindows.OfType<ConfirmWindow>().Single(), "Disconnect");
            await Until(() => !context.Vm.IsBusy);
        }
        var repo = settings.GetVisualDescendants().OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "Default GitHub repository");
        repo.Text = "invalid"; Click(settings, "Save repository");
        await Until(() => context.Vm.IsStatusMessagePersistent);
        Assert.NotNull(await context.Store.GetTaskAsync("task-0"));
        Click(settings, "Close");
    });
}
