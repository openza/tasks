using System.Collections.ObjectModel;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Desktop.ViewModels.Navigation;

namespace Openza.Tasks.Desktop.ViewModels;

public sealed class CalendarDayViewModel : ObservableObject
{
    public const int HoursPerDay = 24;
    public const double HourHeight = 64;
    internal const double TimelineHeightValue = HoursPerDay * HourHeight;

    private readonly List<TaskListItemViewModel> _scheduledTasks = [];
    private readonly List<TaskListItemViewModel> _blockPickerTasks = [];
    private readonly List<TaskListItemViewModel> _todayTaskSource = [];
    private readonly List<TaskListItemViewModel> _todayTasks = [];
    private readonly TimeZoneInfo _timeZone;
    private TaskViewContext? _dayTaskContext;
    private DateOnly _selectedDate = DateOnly.FromDateTime(DateTime.Now);
    private DateTimeOffset _now = DateTimeOffset.Now;
    private string? _todaySpaceId;
    private bool _isDayView;
    private bool _isTodayPanelOpen;
    private bool _hasSetDayTasks;
    private string _todaySearchText = string.Empty;

    public CalendarDayViewModel(TimeZoneInfo? timeZone = null)
    {
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    public ObservableCollection<CalendarDayBlockViewModel> Blocks { get; } = [];
    public ObservableCollection<TaskListItemViewModel> FilteredTodayTasks { get; } = [];
    public IReadOnlyList<TaskListItemViewModel> BlockPickerTasks => _blockPickerTasks;
    public IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, HoursPerDay).ToArray();
    public double TimelineHeight => TimelineHeightValue;

    public DateOnly SelectedDate
    {
        get => _selectedDate;
        private set
        {
            if (!SetProperty(ref _selectedDate, value)) return;
            OnPropertyChanged(nameof(SelectedDateText));
            OnPropertyChanged(nameof(IsSelectedDayToday));
            OnPropertyChanged(nameof(NowTop));
            RebuildBlocks();
        }
    }

    public DateTimeOffset Now => _now;
    public bool IsDayView => _isDayView;
    public bool IsTodayPanelOpen => _isTodayPanelOpen;
    public bool IsSelectedDayToday => SelectedDate == DateOnly.FromDateTime(Now.LocalDateTime);
    public string SelectedDateText => SelectedDate.ToDateTime(TimeOnly.MinValue).ToString("dddd, d MMMM");
    public string TodaySummaryText => $"{Now.LocalDateTime:dddd, d MMMM} · {_todayTasks.Count} task{(_todayTasks.Count == 1 ? string.Empty : "s")}";
    public string NowText => $"Now · {Now.LocalDateTime:HH:mm}";
    public double NowTop => Now.LocalDateTime.TimeOfDay.TotalMinutes * HourHeight / 60;
    public string TodayEmptyText => string.IsNullOrWhiteSpace(TodaySearchText) ? "Nothing for today" : "No matching tasks";
    public bool HasNoTodayTasks => FilteredTodayTasks.Count == 0;

    public string TodaySearchText
    {
        get => _todaySearchText;
        set
        {
            if (SetProperty(ref _todaySearchText, value ?? string.Empty))
            {
                RebuildFilteredTodayTasks();
            }
        }
    }

    public void SetDayView(bool isDayView)
    {
        if (_isDayView == isDayView) return;
        _isDayView = isDayView;
        OnPropertyChanged(nameof(IsDayView));
    }

    public void ToggleTodayPanel() => SetTodayPanelOpen(!IsTodayPanelOpen);

    public void CloseTodayPanel() => SetTodayPanelOpen(false);

    public void SetTodayPanelOpen(bool isOpen)
    {
        if (_isTodayPanelOpen == isOpen) return;
        _isTodayPanelOpen = isOpen;
        OnPropertyChanged(nameof(IsTodayPanelOpen));
    }

    public void PreviousDay() => SetSelectedDate(SelectedDate.AddDays(-1));

    public void NextDay() => SetSelectedDate(SelectedDate.AddDays(1));

    public void GoToToday() => SetSelectedDate(DateOnly.FromDateTime(Now.LocalDateTime));

    public void SetSelectedDate(DateOnly date) => SelectedDate = date;

    public void SetNow(DateTimeOffset now)
    {
        if (_now == now) return;
        var previousDate = DateOnly.FromDateTime(_now.LocalDateTime);
        _now = now;
        OnPropertyChanged(nameof(Now));
        OnPropertyChanged(nameof(IsSelectedDayToday));
        OnPropertyChanged(nameof(NowText));
        OnPropertyChanged(nameof(NowTop));
        if (previousDate != DateOnly.FromDateTime(now.LocalDateTime))
        {
            RebuildTodayTasks();
        }
        else
        {
            OnPropertyChanged(nameof(TodaySummaryText));
        }
        foreach (var block in Blocks)
        {
            block.SetNow(now);
        }
    }

    public void SetScheduledTasks(IEnumerable<TaskListItemViewModel> tasks)
    {
        _scheduledTasks.Clear();
        _scheduledTasks.AddRange(tasks);
        RebuildBlocks();
    }

    public void SetDayTasks(IEnumerable<TaskListItemViewModel> tasks, TaskViewContext? context = null)
    {
        var dayTasks = tasks
            .Where(item => item.Task.IsOpen || item.Task.IsCompleted)
            .ToArray();
        if (_hasSetDayTasks && _dayTaskContext == context && HasSameDayTasks(_scheduledTasks, dayTasks)) return;

        _hasSetDayTasks = true;
        _dayTaskContext = context;
        _scheduledTasks.Clear();
        _scheduledTasks.AddRange(dayTasks);
        _blockPickerTasks.Clear();
        _blockPickerTasks.AddRange(dayTasks
            .Where(item => item.Task.IsOpen && string.IsNullOrWhiteSpace(item.Task.ParentId))
            .OrderBy(item => item.Task.WorkflowStatus == TaskWorkflowStatus.Next ? 0 : 1)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase));
        RebuildBlocks();
    }

    private static bool HasSameDayTasks(IReadOnlyList<TaskListItemViewModel> current, IReadOnlyList<TaskListItemViewModel> incoming)
    {
        if (current.Count != incoming.Count) return false;

        var currentById = new Dictionary<string, TaskListItemViewModel>(StringComparer.Ordinal);
        foreach (var item in current)
        {
            if (!currentById.TryAdd(item.Task.Id, item)) return false;
        }

        foreach (var right in incoming)
        {
            if (!currentById.TryGetValue(right.Task.Id, out var left) ||
                !HasSameTask(left.Task, right.Task) ||
                left.ProjectName != right.ProjectName ||
                left.ViewKind != right.ViewKind ||
                left.IsProjectView != right.IsProjectView ||
                left.SubtaskProgressText != right.SubtaskProgressText ||
                left.MatchingSubtaskText != right.MatchingSubtaskText)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasSameTask(TaskItem left, TaskItem right) =>
        left with { Labels = right.Labels } == right &&
        left.Labels.OrderBy(label => label.Id, StringComparer.Ordinal)
            .SequenceEqual(right.Labels.OrderBy(label => label.Id, StringComparer.Ordinal));

    public DateTimeOffset TimeAtTimelinePosition(double top, bool snapToQuarterHour = false, TimeSpan? preferredOffset = null)
    {
        var minutes = Math.Clamp(top * 60 / HourHeight, 0, HoursPerDay * 60 - 1);
        var precision = snapToQuarterHour ? 15 : 1;
        var roundedMinutes = Math.Round(minutes / precision, MidpointRounding.AwayFromZero) * precision;
        var localTime = SelectedDate.ToDateTime(TimeOnly.MinValue).AddMinutes(roundedMinutes);
        return ResolveLocalTime(localTime, preferredOffset);
    }

    public (DateTimeOffset Start, DateTimeOffset End) MoveBlockTo(CalendarDayBlockViewModel block, double top)
    {
        var maxTop = TimelineHeight - HourHeight / 4;
        var start = TimeAtTimelinePosition(Math.Clamp(top, 0, maxTop), snapToQuarterHour: true, preferredOffset: LocalOffsetFor(block.Start));
        return (start, AddTimelineDuration(start, block.End - block.Start));
    }

    public (DateTimeOffset Start, DateTimeOffset End) MoveBlockBy(CalendarDayBlockViewModel block, double verticalDelta)
    {
        var localStart = TimeZoneInfo.ConvertTime(block.Start, _timeZone).DateTime;
        if (DateOnly.FromDateTime(localStart) >= SelectedDate)
        {
            return MoveBlockTo(block, block.Top + verticalDelta);
        }

        var adjustedLocalStart = localStart.AddMinutes(verticalDelta * 60 / HourHeight);
        var start = SnapLocalTime(adjustedLocalStart, LocalOffsetFor(block.Start));
        return (start, AddTimelineDuration(start, block.End - block.Start));
    }

    public DateTimeOffset AddTimelineDuration(DateTimeOffset start, TimeSpan duration) =>
        TimeZoneInfo.ConvertTime(start.Add(duration), _timeZone);

    public (double Top, double Height)? TimelineLayoutFor(DateTimeOffset start, DateTimeOffset end)
    {
        var dayStartInstant = ResolveLocalDayBoundary(SelectedDate);
        var dayEndInstant = ResolveLocalDayBoundary(SelectedDate.AddDays(1));
        if (end <= start || end <= dayStartInstant || start >= dayEndInstant) return null;

        var visibleStart = start < dayStartInstant ? dayStartInstant : start;
        var visibleEnd = end > dayEndInstant ? dayEndInstant : end;
        if (visibleEnd <= visibleStart) return null;

        var localStart = TimeZoneInfo.ConvertTime(visibleStart, _timeZone).DateTime;
        var localEnd = TimeZoneInfo.ConvertTime(visibleEnd, _timeZone).DateTime;
        var top = localStart.TimeOfDay.TotalMinutes * HourHeight / 60;
        var height = CalendarDayBlockViewModel.HeightFor(localStart, localEnd, visibleEnd - visibleStart);
        return (top, height);
    }

    public (DateTimeOffset Start, DateTimeOffset End) ResizeBlockEndBy(CalendarDayBlockViewModel block, double verticalDelta)
    {
        var localEnd = TimeZoneInfo.ConvertTime(block.End, _timeZone).DateTime;
        var adjustedLocalEnd = localEnd.AddMinutes(verticalDelta * 60 / HourHeight);
        var end = SnapLocalTime(adjustedLocalEnd, LocalOffsetFor(block.End));
        var minimumEnd = block.Start.AddMinutes(15);
        if (end < minimumEnd)
        {
            var minimumLocal = TimeZoneInfo.ConvertTime(minimumEnd, _timeZone).DateTime;
            var roundedUpMinutes = Math.Ceiling(minimumLocal.TimeOfDay.TotalMinutes / 15) * 15;
            end = ResolveLocalTime(minimumLocal.Date.AddMinutes(roundedUpMinutes), LocalOffsetFor(minimumEnd));
        }
        return (block.Start, end);
    }

    public void SetTodayTaskSource(IEnumerable<TaskListItemViewModel> tasks, string? spaceId)
    {
        _todayTaskSource.Clear();
        _todayTaskSource.AddRange(tasks);
        _todaySpaceId = spaceId;
        RebuildTodayTasks();
    }

    private void RebuildTodayTasks()
    {
        var today = DateOnly.FromDateTime(Now.LocalDateTime);
        var sourceById = _todayTaskSource.ToDictionary(item => item.Task.Id, StringComparer.Ordinal);
        var visibleTasks = TaskListProjection.Build(
            _todayTaskSource.Select(item => item.Task).ToArray(),
            new TaskQuery { SpaceId = _todaySpaceId, Kind = TaskListKind.Today },
            today);

        _todayTasks.Clear();
        foreach (var task in visibleTasks.Where(task => string.IsNullOrWhiteSpace(task.ParentId)))
        {
            if (sourceById.TryGetValue(task.Id, out var item)) _todayTasks.Add(item);
        }

        OnPropertyChanged(nameof(TodaySummaryText));
        RebuildFilteredTodayTasks();
    }

    private void RebuildFilteredTodayTasks()
    {
        FilteredTodayTasks.Clear();
        var search = TodaySearchText.Trim();
        foreach (var task in _todayTasks.Where(task => string.IsNullOrWhiteSpace(search) ||
                     task.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                     task.CalendarPanelMetadataText.Contains(search, StringComparison.CurrentCultureIgnoreCase)))
        {
            FilteredTodayTasks.Add(task);
        }
        OnPropertyChanged(nameof(HasNoTodayTasks));
        OnPropertyChanged(nameof(TodayEmptyText));
    }

    private void RebuildBlocks()
    {
        Blocks.Clear();
        var dayStartInstant = ResolveLocalDayBoundary(SelectedDate);
        var dayEndInstant = ResolveLocalDayBoundary(SelectedDate.AddDays(1));
        foreach (var task in _scheduledTasks)
        {
            if (task.Task.ScheduledStart is not { } start ||
                (!task.Task.IsOpen && !task.Task.IsCompleted))
            {
                continue;
            }

            var end = task.Task.ScheduledEnd ?? start.AddHours(1);
            if (end <= start) continue;

            if (end <= dayStartInstant || start >= dayEndInstant) continue;

            var clippedStart = start < dayStartInstant ? dayStartInstant : start;
            var clippedEnd = end > dayEndInstant ? dayEndInstant : end;
            if (clippedEnd <= clippedStart) continue;

            var localStart = TimeZoneInfo.ConvertTime(clippedStart, _timeZone).DateTime;
            var localEnd = TimeZoneInfo.ConvertTime(clippedEnd, _timeZone).DateTime;
            Blocks.Add(new CalendarDayBlockViewModel(
                task, start, end, clippedStart, clippedEnd, localStart, localEnd, Now));
        }

        AssignOverlapColumns(Blocks);
    }

    private DateTimeOffset ResolveLocalDayBoundary(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        for (var minute = 0; minute < 48 * 60; minute++)
        {
            if (!_timeZone.IsInvalidTime(local))
            {
                var offsets = _timeZone.IsAmbiguousTime(local)
                    ? _timeZone.GetAmbiguousTimeOffsets(local)
                    : [_timeZone.GetUtcOffset(local)];
                return new DateTimeOffset(local, offsets.Max());
            }

            local = local.AddMinutes(1);
        }

        throw new InvalidOperationException($"No valid local time found for {date} in {_timeZone.Id}.");
    }

    private DateTimeOffset SnapLocalTime(DateTime localTime, TimeSpan? preferredOffset = null)
    {
        var minutes = Math.Round(localTime.TimeOfDay.TotalMinutes / 15, MidpointRounding.AwayFromZero) * 15;
        return ResolveLocalTime(localTime.Date.AddMinutes(minutes), preferredOffset);
    }

    public DateTime LocalDateTimeFor(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, _timeZone).DateTime;

    public TimeSpan LocalOffsetFor(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, _timeZone).Offset;

    public bool IsInvalidLocalTime(DateTime localTime) => _timeZone.IsInvalidTime(DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified));

    public DateTimeOffset ResolveLocalTime(DateTime localTime, TimeSpan? preferredOffset = null)
    {
        localTime = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        for (var minute = 0; minute < 48 * 60; minute++)
        {
            if (!_timeZone.IsInvalidTime(localTime))
            {
                var offsets = _timeZone.IsAmbiguousTime(localTime) ? _timeZone.GetAmbiguousTimeOffsets(localTime) : null;
                var offset = offsets is not null && preferredOffset is { } preferred && offsets.Contains(preferred)
                    ? preferred
                    : offsets?.Max() ?? _timeZone.GetUtcOffset(localTime);
                return new DateTimeOffset(localTime, offset);
            }

            localTime = localTime.AddMinutes(1);
        }

        throw new InvalidOperationException($"No valid local time was found near {localTime}.");
    }

    private static void AssignOverlapColumns(IReadOnlyList<CalendarDayBlockViewModel> blocks)
    {
        var cluster = new List<CalendarDayBlockViewModel>();
        var clusterEnd = DateTime.MinValue;

        foreach (var block in blocks.OrderBy(block => block.LocalStart).ThenBy(block => block.LayoutEnd))
        {
            if (cluster.Count > 0 && block.LocalStart >= clusterEnd)
            {
                AssignClusterColumns(cluster);
                cluster.Clear();
                clusterEnd = DateTime.MinValue;
            }

            cluster.Add(block);
            if (block.LayoutEnd > clusterEnd) clusterEnd = block.LayoutEnd;
        }

        if (cluster.Count > 0) AssignClusterColumns(cluster);
    }

    private static void AssignClusterColumns(IReadOnlyList<CalendarDayBlockViewModel> cluster)
    {
        var active = new List<(int Column, DateTime End)>();
        var maxColumns = 1;
        var assignments = new List<(CalendarDayBlockViewModel Block, int Column)>();

        foreach (var block in cluster.OrderBy(block => block.LocalStart).ThenBy(block => block.LayoutEnd))
        {
            active.RemoveAll(item => item.End <= block.LocalStart);
            var column = 0;
            while (active.Any(item => item.Column == column)) column++;
            active.Add((column, block.LayoutEnd));
            assignments.Add((block, column));
            maxColumns = Math.Max(maxColumns, active.Count);
        }

        foreach (var (block, column) in assignments)
        {
            block.SetOverlapColumn(column, maxColumns);
        }
    }
}
