using System.Collections.ObjectModel;
using System.Globalization;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Desktop.ViewModels;

public sealed class CalendarBlockPickerViewModel : ObservableObject
{
    private readonly IReadOnlyList<TaskListItemViewModel> _tasks;
    private readonly CalendarDayViewModel _calendarDay;
    private readonly DateOnly _startDate;
    private readonly DateTimeOffset _initialStart;
    private readonly bool _isEditing;
    private string _searchText = string.Empty;
    private string _startTimeText;
    private TaskListItemViewModel? _selectedTask;
    private decimal? _durationMinutes = 60;

    public CalendarBlockPickerViewModel(
        CalendarDayViewModel calendarDay,
        DateTimeOffset start,
        TaskListItemViewModel? task = null,
        int? durationMinutes = null)
    {
        _calendarDay = calendarDay;
        _initialStart = start;
        var localStart = calendarDay.LocalDateTimeFor(start);
        _startDate = DateOnly.FromDateTime(localStart);
        _startTimeText = localStart.ToString("HH:mm", CultureInfo.InvariantCulture);
        _isEditing = task is not null;
        _tasks = calendarDay.BlockPickerTasks
            .Where(item => item.Task.IsOpen && string.IsNullOrWhiteSpace(item.Task.ParentId))
            .OrderBy(item => item.Task.WorkflowStatus == TaskWorkflowStatus.Next ? 0 : 1)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        _selectedTask = task;
        _durationMinutes = durationMinutes ?? 60;
        RebuildFilteredTasks();
    }

    public ObservableCollection<TaskListItemViewModel> FilteredTasks { get; } = [];
    public DateTimeOffset Start => TryGetStart(out var start) ? start : _initialStart;
    public DateTimeOffset End => _calendarDay.AddTimelineDuration(Start, TimeSpan.FromMinutes((double)(_durationMinutes ?? 60)));
    public string StartDateText => _startDate.ToDateTime(TimeOnly.MinValue).ToString("dddd, d MMMM");
    public string EndText => _calendarDay.LocalDateTimeFor(End).ToString("dddd, d MMM · HH:mm");
    public bool IsEditing => _isEditing;
    public bool IsTaskSelectionVisible => !IsEditing;
    public bool HasNoTasks => !IsEditing && FilteredTasks.Count == 0;
    public bool HasSelectedTaskSchedule => SelectedTaskSchedule is not null;
    public string ExistingScheduleText => SelectedTaskSchedule is { } schedule
        ? $"Existing interval: {_calendarDay.LocalDateTimeFor(schedule.Start):ddd, d MMM · HH:mm} – {_calendarDay.LocalDateTimeFor(schedule.End):ddd, d MMM · HH:mm}"
        : string.Empty;
    public string ReplacementNoticeText => HasSelectedTaskSchedule
        ? "This task already has a scheduled interval. Adding it here will replace that interval."
        : string.Empty;
    public string AddActionText => HasSelectedTaskSchedule ? "Replace scheduled interval" : "Add block";
    public bool HasInvalidStartTime => !TryGetStart(out _);
    public bool CanSchedule => _selectedTask is not null && !HasInvalidStartTime && _durationMinutes is > 0 and <= int.MaxValue;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                RebuildFilteredTasks();
            }
        }
    }

    public string StartTimeText
    {
        get => _startTimeText;
        set
        {
            if (!SetProperty(ref _startTimeText, value ?? string.Empty)) return;
            OnPropertyChanged(nameof(Start));
            OnPropertyChanged(nameof(End));
            OnPropertyChanged(nameof(EndText));
            OnPropertyChanged(nameof(HasInvalidStartTime));
            OnPropertyChanged(nameof(CanSchedule));
        }
    }

    public TaskListItemViewModel? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (SetProperty(ref _selectedTask, value))
            {
                OnPropertyChanged(nameof(HasSelectedTaskSchedule));
                OnPropertyChanged(nameof(ExistingScheduleText));
                OnPropertyChanged(nameof(ReplacementNoticeText));
                OnPropertyChanged(nameof(AddActionText));
                OnPropertyChanged(nameof(CanSchedule));
            }
        }
    }

    public decimal? DurationMinutes
    {
        get => _durationMinutes;
        set
        {
            if (!SetProperty(ref _durationMinutes, value)) return;
            OnPropertyChanged(nameof(End));
            OnPropertyChanged(nameof(EndText));
            OnPropertyChanged(nameof(CanSchedule));
        }
    }

    public CalendarBlockPickerResult? CreateResult() => CanSchedule && SelectedTask is { } task
        ? new CalendarBlockPickerResult(task, Start, End)
        : null;

    private bool TryGetStart(out DateTimeOffset start)
    {
        if (TimeOnly.TryParseExact(StartTimeText, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            var localStart = _startDate.ToDateTime(time);
            if (!_calendarDay.IsInvalidLocalTime(localStart))
            {
                start = _calendarDay.ResolveLocalTime(localStart, _calendarDay.LocalOffsetFor(_initialStart));
                return true;
            }
        }

        start = _initialStart;
        return false;
    }

    private (DateTimeOffset Start, DateTimeOffset End)? SelectedTaskSchedule
    {
        get
        {
            if (SelectedTask?.Task.ScheduledStart is not { } start) return null;
            return (start, SelectedTask.Task.ScheduledEnd ?? start.AddHours(1));
        }
    }

    private void RebuildFilteredTasks()
    {
        FilteredTasks.Clear();
        var search = SearchText.Trim();
        foreach (var task in _tasks.Where(item => search.Length == 0 ||
                     item.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                     item.ProjectName.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                     item.Status.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                     item.SourceText.Contains(search, StringComparison.CurrentCultureIgnoreCase)))
        {
            FilteredTasks.Add(task);
        }

        OnPropertyChanged(nameof(HasNoTasks));
    }
}

public sealed record CalendarBlockPickerResult(
    TaskListItemViewModel Item,
    DateTimeOffset Start,
    DateTimeOffset End);
