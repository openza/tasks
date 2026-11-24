using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Desktop.Views;

public sealed partial class CalendarDayView : UserControl
{
    private CalendarDayViewModel? _viewModel;
    private bool _timelineRenderQueued;

    public CalendarDayView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    public event EventHandler<TaskListItemViewModel>? TodayTaskSelected;
    public event EventHandler<TaskListItemViewModel>? TodayTaskCompletionRequested;

    public void ScrollToCurrentTime()
    {
        if (_viewModel is null) return;
        var hour = _viewModel.IsSelectedDayToday ? _viewModel.Now.LocalDateTime.Hour : 8;
        var offset = Math.Max(0, hour * CalendarDayViewModel.HourHeight - CalendarDayViewModel.HourHeight);
        Dispatcher.UIThread.Post(() => TimelineScrollViewer.Offset = new Vector(0, offset), DispatcherPriority.Loaded);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.Blocks.CollectionChanged -= OnBlocksChanged;
            _viewModel.PropertyChanged -= OnCalendarDayPropertyChanged;
        }

        _viewModel = DataContext as CalendarDayViewModel;
        if (_viewModel is not null)
        {
            _viewModel.Blocks.CollectionChanged += OnBlocksChanged;
            _viewModel.PropertyChanged += OnCalendarDayPropertyChanged;
        }

        RebuildTimeline();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e) => ScrollToCurrentTime();

    private void OnTimelineSizeChanged(object? sender, SizeChangedEventArgs e) => RebuildTimeline();

    private void OnBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueTimelineRebuild();

    private void OnCalendarDayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CalendarDayViewModel.Now) or nameof(CalendarDayViewModel.SelectedDate) or nameof(CalendarDayViewModel.IsSelectedDayToday))
        {
            QueueTimelineRebuild();
        }
    }

    private void QueueTimelineRebuild()
    {
        if (_timelineRenderQueued) return;
        _timelineRenderQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _timelineRenderQueued = false;
            RebuildTimeline();
        }, DispatcherPriority.Background);
    }

    private void RebuildTimeline()
    {
        HourLabelsCanvas.Children.Clear();
        TimelineCanvas.Children.Clear();
        if (_viewModel is null) return;

        var height = CalendarDayViewModel.HourHeight;
        var width = TimelineCanvas.Bounds.Width;
        for (var hour = 0; hour < CalendarDayViewModel.HoursPerDay; hour++)
        {
            var label = new TextBlock
            {
                Text = $"{hour:00}:00",
                Classes = { "calendar-hour-label" },
            };
            Canvas.SetTop(label, hour * height + 2);
            HourLabelsCanvas.Children.Add(label);

            AddRule(hour * height, width, "calendar-hour-rule");
            if (hour < CalendarDayViewModel.HoursPerDay - 1)
            {
                AddRule(hour * height + height / 2, width, "calendar-half-hour-rule");
            }
        }

        AddRule(CalendarDayViewModel.HoursPerDay * height, width, "calendar-hour-rule");
        foreach (var block in _viewModel.Blocks)
        {
            AddBlock(block, width);
        }

        if (_viewModel.IsSelectedDayToday)
        {
            var nowLine = new Border
            {
                Height = 2,
                Width = width,
                Classes = { "calendar-now-rule" },
            };
            Canvas.SetTop(nowLine, _viewModel.NowTop);
            TimelineCanvas.Children.Add(nowLine);
        }
    }

    private void AddRule(double top, double width, string styleClass)
    {
        var rule = new Border
        {
            Height = 1,
            Width = width,
            Classes = { styleClass },
        };
        Canvas.SetTop(rule, top);
        TimelineCanvas.Children.Add(rule);
    }

    private void AddBlock(CalendarDayBlockViewModel block, double canvasWidth)
    {
        var laneWidth = canvasWidth * block.WidthFraction;
        var border = new Border
        {
            DataContext = block,
            Width = Math.Max(1, laneWidth - 8),
            Height = block.Height,
            Opacity = block.Opacity,
            Classes = { "calendar-day-block" },
            Child = BlockContent(block),
        };
        AutomationProperties.SetName(border, block.AccessibleName);
        ToolTip.SetTip(border, block.AccessibleName);
        Canvas.SetLeft(border, canvasWidth * block.LeftFraction + 4);
        Canvas.SetTop(border, block.Top);
        TimelineCanvas.Children.Add(border);
    }

    private static Control BlockContent(CalendarDayBlockViewModel block)
    {
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("3,*"), ColumnSpacing = 0 };
        var accent = new Border { Classes = { "calendar-block-accent" }, CornerRadius = new CornerRadius(3) };
        content.Children.Add(accent);

        var text = new Grid
        {
            Margin = new Thickness(8, 0, 0, 0),
            RowDefinitions = new RowDefinitions(block.IsCompact ? "Auto" : "Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowSpacing = block.IsCompact ? 0 : 1,
        };
        Grid.SetColumn(text, 1);

        var title = new TextBlock
        {
            Text = block.Title,
            Classes = { "calendar-block-title" },
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            TextDecorations = block.IsCompleted ? TextDecorations.Strikethrough : null,
        };
        text.Children.Add(title);

        var state = new TextBlock
        {
            Text = block.StateText,
            Classes = { "calendar-block-state" },
            IsVisible = block.StateText.Length > 0,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        Grid.SetColumn(state, 1);
        text.Children.Add(state);

        if (block.IsCompact)
        {
            content.Children.Add(text);
            return content;
        }

        var time = new TextBlock
        {
            Text = block.TimeText,
            Classes = { "calendar-block-time" },
        };
        Grid.SetRow(time, 1);
        Grid.SetColumnSpan(time, 2);
        text.Children.Add(time);
        content.Children.Add(text);
        return content;
    }

    private void OnPreviousDayClicked(object? sender, RoutedEventArgs e) => _viewModel?.PreviousDay();

    private void OnNextDayClicked(object? sender, RoutedEventArgs e) => _viewModel?.NextDay();

    private void OnGoToTodayClicked(object? sender, RoutedEventArgs e)
    {
        _viewModel?.GoToToday();
        ScrollToCurrentTime();
    }

    private void OnCloseTodayPanelClicked(object? sender, RoutedEventArgs e) => _viewModel?.CloseTodayPanel();

    private void OnTodayTaskClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TaskListItemViewModel task }) return;
        TodayTaskSelected?.Invoke(this, task);
    }

    private void OnTodayTaskCompletionClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is CheckBox { DataContext: TaskListItemViewModel task })
        {
            TodayTaskCompletionRequested?.Invoke(this, task);
        }
    }
}
