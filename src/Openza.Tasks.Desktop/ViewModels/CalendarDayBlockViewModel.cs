using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Desktop.ViewModels;

public sealed class CalendarDayBlockViewModel : ObservableObject
{
    private const double MinimumHeight = 32;
    private readonly DateTime _visibleEnd;
    private DateTimeOffset _now;
    private int _overlapColumn;
    private int _overlapColumnCount = 1;

    internal CalendarDayBlockViewModel(
        TaskListItemViewModel task,
        DateTimeOffset start,
        DateTimeOffset end,
        DateTime localStart,
        DateTime visibleEnd,
        DateTimeOffset now)
    {
        Item = task;
        Start = start;
        End = end;
        LocalStart = localStart;
        _visibleEnd = visibleEnd;
        _now = now;
    }

    public TaskListItemViewModel Item { get; }
    public TaskItem Task => Item.Task;
    public string Title => Task.Title;
    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }
    public DateTime LocalStart { get; }
    public DateTime LocalEnd => _visibleEnd;
    public double Top => LocalStart.TimeOfDay.TotalMinutes * CalendarDayViewModel.HourHeight / 60;
    public double Height => Math.Max(MinimumHeight, (LocalEnd - LocalStart).TotalMinutes * CalendarDayViewModel.HourHeight / 60);
    public string TimeText => $"{LocalStart:HH:mm} – {(LocalEnd.Date > LocalStart.Date ? "24:00" : LocalEnd.ToString("HH:mm"))}";
    public bool IsCompact => Height < 48;
    public bool IsCompleted => Task.IsCompleted;
    public bool IsNotDone => Task.IsOpen && End <= _now;
    public string StateText => IsCompleted ? "Done" : IsNotDone ? "Not done" : string.Empty;
    public double Opacity => IsNotDone ? 0.55 : IsCompleted ? 0.72 : 1;
    public double LeftFraction => (double)_overlapColumn / _overlapColumnCount;
    public double WidthFraction => 1d / _overlapColumnCount;
    public string AccessibleName => string.Join(", ", new[] { Title, TimeText, StateText }.Where(value => value.Length > 0));

    internal void SetOverlapColumn(int column, int columnCount)
    {
        _overlapColumn = column;
        _overlapColumnCount = columnCount;
        OnPropertyChanged(nameof(LeftFraction));
        OnPropertyChanged(nameof(WidthFraction));
    }

    internal void SetNow(DateTimeOffset now)
    {
        if (_now == now) return;
        _now = now;
        OnPropertyChanged(nameof(IsNotDone));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Opacity));
        OnPropertyChanged(nameof(AccessibleName));
    }
}
