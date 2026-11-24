using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Desktop.Views;

public sealed partial class CalendarDayView : UserControl
{
    private CalendarDayViewModel? _viewModel;
    private bool _timelineRenderQueued;
    private Border? _activeBlockBorder;
    private CalendarDayBlockViewModel? _activeBlock;
    private Point _pointerOrigin;
    private double _blockTopAtStart;
    private double _blockHeightAtStart;
    private bool _resizeGesture;
    private bool _blockWasDragged;

    public CalendarDayView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    public event EventHandler<TaskListItemViewModel>? TaskSelected;
    public event Func<DateTimeOffset, Task>? BlockPickerRequested;
    public event Func<TaskListItemViewModel, DateTimeOffset?, DateTimeOffset?, Task>? BlockScheduleChangeRequested;
    public event Func<TaskListItemViewModel, DateTimeOffset, DateTimeOffset, Task>? BlockEditRequested;
    public event Func<TaskListItemViewModel, Task<bool>>? TodayTaskCompletionRequested;

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
                IsHitTestVisible = false,
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
            IsHitTestVisible = false,
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
            Padding = new Thickness(0),
            Opacity = block.Opacity,
            Focusable = true,
            Classes = { "calendar-day-block" },
            Child = BlockContent(block),
        };
        AutomationProperties.SetName(border, block.AccessibleName);
        ToolTip.SetTip(border, block.CanResizeEnd
            ? $"{block.AccessibleName}. Click for actions; drag to move; drag the bottom edge to resize."
            : $"{block.AccessibleName}. Click for actions or drag to move.");
        border.PointerPressed += OnBlockPointerPressed;
        border.PointerMoved += OnBlockPointerMoved;
        border.PointerReleased += OnBlockPointerReleased;
        border.PointerCaptureLost += OnBlockPointerCaptureLost;
        border.KeyDown += OnBlockKeyDown;
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
            return WithResizeHandle(content, block);
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
        return WithResizeHandle(content, block);
    }

    private static Control WithResizeHandle(Control content, CalendarDayBlockViewModel block)
    {
        var container = new Grid();
        container.Children.Add(new Border { Padding = new Thickness(6, 3), Child = content });
        if (!block.CanResizeEnd) return container;
        var handleHeight = Math.Min(3, Math.Max(0, (block.Height - 2) / 2));
        if (handleHeight <= 0) return container;
        var handle = new Border
        {
            Height = handleHeight,
            Margin = new Thickness(5, 0, 5, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Classes = { "calendar-block-resize-handle" },
        };
        container.Children.Add(handle);
        return container;
    }

    private async void OnTimelinePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null || !e.GetCurrentPoint(TimelineCanvas).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        var position = e.GetPosition(TimelineCanvas);
        var start = _viewModel.TimeAtTimelinePosition(position.Y);
        if (BlockPickerRequested is { } handler) await handler(start);
    }

    private void OnBlockPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: CalendarDayBlockViewModel block } border ||
            !e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _activeBlock = block;
        _activeBlockBorder = border;
        _pointerOrigin = e.GetPosition(TimelineCanvas);
        _blockTopAtStart = Canvas.GetTop(border);
        _blockHeightAtStart = border.Bounds.Height;
        _resizeGesture = IsPointerOnResizeHandle(border, e.GetPosition(border));
        _blockWasDragged = false;
        border.Focus();
        e.Pointer.Capture(border);
        e.Handled = true;
    }

    private void OnBlockPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Border border || !ReferenceEquals(border, _activeBlockBorder) || _viewModel is null) return;
        var delta = e.GetPosition(TimelineCanvas).Y - _pointerOrigin.Y;
        if (!_blockWasDragged && Math.Abs(delta) < 4) return;
        _blockWasDragged = true;
        var schedule = GetDragSchedule(_activeBlock!, delta, _resizeGesture);
        UpdateBlockPreview(border, schedule.Start, schedule.End);
        e.Handled = true;
    }

    private async void OnBlockPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Border border || !ReferenceEquals(border, _activeBlockBorder) || _activeBlock is null) return;
        var block = _activeBlock;
        var wasDragged = _blockWasDragged;
        var resizeGesture = _resizeGesture;
        var delta = e.GetPosition(TimelineCanvas).Y - _pointerOrigin.Y;
        var startTop = _blockTopAtStart;
        var startHeight = _blockHeightAtStart;
        _activeBlockBorder = null;
        _activeBlock = null;
        _blockWasDragged = false;
        _resizeGesture = false;
        e.Pointer.Capture(null);
        e.Handled = true;

        if (!wasDragged)
        {
            border.Height = startHeight;
            Canvas.SetTop(border, startTop);
            border.IsVisible = true;
            ShowBlockFlyout(border, block);
            return;
        }

        if (_viewModel is null || BlockScheduleChangeRequested is not { } handler)
        {
            border.Height = startHeight;
            Canvas.SetTop(border, startTop);
            border.IsVisible = true;
            return;
        }

        var schedule = GetDragSchedule(block, delta, resizeGesture);
        UpdateBlockPreview(border, schedule.Start, schedule.End);
        await handler(block.Item, schedule.Start, schedule.End);
        QueueTimelineRebuild();
    }

    private void OnBlockPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (sender is not Border border || !ReferenceEquals(border, _activeBlockBorder)) return;
        border.Height = _blockHeightAtStart;
        Canvas.SetTop(border, _blockTopAtStart);
        border.IsVisible = true;
        _activeBlockBorder = null;
        _activeBlock = null;
        _blockWasDragged = false;
        _resizeGesture = false;
    }

    private (DateTimeOffset Start, DateTimeOffset End) GetDragSchedule(CalendarDayBlockViewModel block, double delta, bool resizeGesture) =>
        resizeGesture
            ? _viewModel!.ResizeBlockEndBy(block, delta)
            : _viewModel!.MoveBlockBy(block, delta);

    private static bool IsPointerOnResizeHandle(Border block, Point pointerPosition)
    {
        var handle = block.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(child => child.Classes.Contains("calendar-block-resize-handle"));
        if (handle is null || handle.TranslatePoint(default, block) is not { } origin) return false;
        return pointerPosition.X >= origin.X && pointerPosition.X < origin.X + handle.Bounds.Width &&
            pointerPosition.Y >= origin.Y && pointerPosition.Y < origin.Y + handle.Bounds.Height;
    }

    private void UpdateBlockPreview(Border border, DateTimeOffset start, DateTimeOffset end)
    {
        if (_viewModel?.TimelineLayoutFor(start, end) is not { } layout)
        {
            border.IsVisible = false;
            return;
        }

        border.IsVisible = true;
        Canvas.SetTop(border, layout.Top);
        border.Height = layout.Height;
    }

    private void OnBlockKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) ||
            sender is not Border { DataContext: CalendarDayBlockViewModel block } border)
        {
            return;
        }

        ShowBlockFlyout(border, block);
        e.Handled = true;
    }

    private void ShowBlockFlyout(Border border, CalendarDayBlockViewModel block)
    {
        var content = new StackPanel { MinWidth = 240, Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = block.Title,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        var start = _viewModel?.LocalDateTimeFor(block.Start).ToString("dddd, d MMMM · HH:mm") ?? block.TimeText;
        var end = _viewModel?.LocalDateTimeFor(block.End).ToString("dddd, d MMMM · HH:mm") ?? block.TimeText;
        content.Children.Add(new TextBlock
        {
            Text = $"{start} – {end}",
            TextWrapping = TextWrapping.Wrap,
        });

        var flyout = new Flyout { Content = content };
        var openButton = new Button { Content = "Open task", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        openButton.Click += (_, _) =>
        {
            flyout.Hide();
            TaskSelected?.Invoke(this, block.Item);
        };
        content.Children.Add(openButton);

        var editButton = new Button { Content = "Edit time", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        editButton.Click += async (_, _) =>
        {
            flyout.Hide();
            if (BlockEditRequested is { } edit) await edit(block.Item, block.Start, block.End);
        };
        content.Children.Add(editButton);

        var removeButton = new Button { Content = "Remove from timeline", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        removeButton.Click += async (_, _) =>
        {
            flyout.Hide();
            if (BlockScheduleChangeRequested is { } remove) await remove(block.Item, null, null);
        };
        content.Children.Add(removeButton);
        flyout.ShowAt(border);
    }

    private void OnPreviousDayClicked(object? sender, RoutedEventArgs e) => _viewModel?.PreviousDay();

    private void OnNextDayClicked(object? sender, RoutedEventArgs e) => _viewModel?.NextDay();

    private void OnGoToTodayClicked(object? sender, RoutedEventArgs e)
    {
        _viewModel?.GoToToday();
        ScrollToCurrentTime();
    }

    private async void OnAddBlockClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || BlockPickerRequested is not { } handler) return;
        var top = _viewModel.IsSelectedDayToday ? _viewModel.NowTop : 9 * CalendarDayViewModel.HourHeight;
        await handler(_viewModel.TimeAtTimelinePosition(top));
    }

    private void OnCloseTodayPanelClicked(object? sender, RoutedEventArgs e) => _viewModel?.CloseTodayPanel();

    private void OnTodayTaskClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TaskListItemViewModel task }) return;
        TaskSelected?.Invoke(this, task);
    }

    private async void OnTodayTaskCompletionClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is CheckBox { DataContext: TaskListItemViewModel task } checkBox)
        {
            var isCompleted = task.Task.IsCompleted;
            checkBox.IsEnabled = false;
            try
            {
                if (TodayTaskCompletionRequested is { } completionRequested)
                {
                    isCompleted = await completionRequested(task);
                }
            }
            finally
            {
                checkBox.IsChecked = isCompleted;
                checkBox.IsEnabled = true;
            }
        }
    }
}
