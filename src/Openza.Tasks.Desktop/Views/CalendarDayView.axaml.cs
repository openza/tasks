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
    private bool _clockVisualUpdateQueued;
    private Border? _activeBlockBorder;
    private IPointer? _activeBlockPointer;
    private CalendarDayBlockViewModel? _activeBlock;
    private Flyout? _activeBlockFlyout;
    private Visual? _gestureCoordinateRoot;

    private Point _pointerOrigin;
    private double _explicitScrollOffsetDelta;
    private double _wheelScrollOffsetAtStart;
    private double _pendingWheelScrollOffsetDelta;
    private bool _wheelInputInProgress;
    private long _scrollChangedGeneration;
    private long _wheelInputStartGeneration;
    private double _wheelInputScrollOffsetDelta;
    private bool _wheelInputHasLayoutChange;
    private double _blockTopAtStart;
    private double _blockHeightAtStart;
    private bool _resizeGesture;
    private bool _blockWasDragged;
    private bool _blockPickerRequestInProgress;

    public CalendarDayView()
    {
        InitializeComponent();
        TimelineScrollViewer.ScrollChanged += OnTimelineScrollChanged;
        TimelineScrollViewer.AddHandler(InputElement.PointerWheelChangedEvent, OnTimelineWheelTunnel, RoutingStrategies.Tunnel);
        TimelineScrollViewer.AddHandler(InputElement.PointerWheelChangedEvent, OnTimelineWheelBubble, RoutingStrategies.Bubble, handledEventsToo: true);
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && !IsVisible)
        {
            CancelActiveBlockGesture();
        }
    }

    public event EventHandler<TaskListItemViewModel>? TaskSelected;
    public event Func<DateTimeOffset, Task>? BlockPickerRequested;
    public event Action<Exception>? BlockPickerRequestFailed;
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
        CancelActiveBlockGesture();
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

    private void OnTimelineSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        CancelActiveBlockGesture();
        RebuildTimeline();
    }

    private void OnTimelineScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_activeBlockBorder is null) return;
        _scrollChangedGeneration++;
        var hasLayoutChange = e.ViewportDelta.Y != 0 || e.ExtentDelta.Y != 0;
        if (_wheelInputInProgress)
        {
            _wheelInputScrollOffsetDelta += e.OffsetDelta.Y;
            _wheelInputHasLayoutChange |= hasLayoutChange;
            return;
        }

        if (_pendingWheelScrollOffsetDelta != 0)
        {
            if (e.OffsetDelta.Y != 0 && !hasLayoutChange)
            {
                _explicitScrollOffsetDelta += e.OffsetDelta.Y - _pendingWheelScrollOffsetDelta;
            }
            _pendingWheelScrollOffsetDelta = 0;
            return;
        }

        if (e.OffsetDelta.Y != 0 && !hasLayoutChange)
        {
            _explicitScrollOffsetDelta += e.OffsetDelta.Y;
        }
    }

    private void OnTimelineWheelTunnel(object? sender, PointerWheelEventArgs e)
    {
        if (_activeBlockBorder is null) return;
        _wheelInputInProgress = true;
        _wheelInputStartGeneration = _scrollChangedGeneration;
        _wheelInputScrollOffsetDelta = 0;
        _wheelInputHasLayoutChange = false;
        _wheelScrollOffsetAtStart = TimelineScrollViewer.Offset.Y;
    }

    private void OnTimelineWheelBubble(object? sender, PointerWheelEventArgs e)
    {
        if (!_wheelInputInProgress) return;
        var wheelOffsetDelta = TimelineScrollViewer.Offset.Y - _wheelScrollOffsetAtStart;
        var scrollChangedDuringInput = _scrollChangedGeneration != _wheelInputStartGeneration;
        var scrollOffsetDelta = _wheelInputScrollOffsetDelta;
        var hasLayoutChange = _wheelInputHasLayoutChange;
        _wheelInputInProgress = false;
        _wheelInputStartGeneration = 0;
        _wheelInputScrollOffsetDelta = 0;
        _wheelInputHasLayoutChange = false;

        if (wheelOffsetDelta != 0)
        {
            _explicitScrollOffsetDelta += wheelOffsetDelta;
        }

        if (scrollChangedDuringInput)
        {
            if (hasLayoutChange)
            {
                _pendingWheelScrollOffsetDelta = 0;
            }
            else if (scrollOffsetDelta == 0)
            {
                _pendingWheelScrollOffsetDelta += wheelOffsetDelta;
            }
            else
            {
                _explicitScrollOffsetDelta += scrollOffsetDelta - _pendingWheelScrollOffsetDelta - wheelOffsetDelta;
                _pendingWheelScrollOffsetDelta = 0;
            }
        }
        else
        {
            _pendingWheelScrollOffsetDelta += wheelOffsetDelta;
        }
    }

    private void OnBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        QueueTimelineRebuild();
    }

    private void OnCalendarDayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarDayViewModel.SelectedDate))
        {
            QueueTimelineRebuild();
        }
        else if (e.PropertyName is nameof(CalendarDayViewModel.Now) or nameof(CalendarDayViewModel.IsSelectedDayToday))
        {
            QueueClockVisualUpdate();
        }
    }

    private void QueueTimelineRebuild()
    {
        CancelActiveBlockGesture();
        if (_timelineRenderQueued) return;

        _timelineRenderQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _timelineRenderQueued = false;
            CancelActiveBlockGesture();
            RebuildTimeline();
        }, DispatcherPriority.Background);
    }

    private void QueueClockVisualUpdate()
    {
        if (_clockVisualUpdateQueued) return;
        _clockVisualUpdateQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _clockVisualUpdateQueued = false;
            if (_viewModel is null) return;

            var nowLine = TimelineCanvas.Children.OfType<Border>()
                .FirstOrDefault(border => border.Classes.Contains("calendar-now-rule"));
            if (_viewModel.IsSelectedDayToday)
            {
                if (nowLine is null)
                {
                    nowLine = new Border
                    {
                        Height = 2,
                        Width = TimelineCanvas.Bounds.Width,
                        IsHitTestVisible = false,
                        Classes = { "calendar-now-rule" },
                    };
                    TimelineCanvas.Children.Add(nowLine);
                }

                Canvas.SetTop(nowLine, _viewModel.NowTop);
            }
            else if (nowLine is not null)
            {
                TimelineCanvas.Children.Remove(nowLine);
            }

            foreach (var border in TimelineCanvas.Children.OfType<Border>()
                         .Where(border => border.DataContext is CalendarDayBlockViewModel))
            {
                var block = (CalendarDayBlockViewModel)border.DataContext!;
                if (border.Opacity != block.Opacity) border.Opacity = block.Opacity;
                AutomationProperties.SetName(border, block.AccessibleName);
                var tooltip = block.CanResizeEnd
                    ? $"{block.AccessibleName}. Click for actions; drag to move; drag the bottom edge to resize."
                    : $"{block.AccessibleName}. Click for actions or drag to move.";
                if (!Equals(ToolTip.GetTip(border), tooltip)) ToolTip.SetTip(border, tooltip);

                var state = border.GetVisualDescendants().OfType<TextBlock>()
                    .FirstOrDefault(text => text.Classes.Contains("calendar-block-state"));
                if (state is not null)
                {
                    if (state.Text != block.StateText) state.Text = block.StateText;
                    var stateIsVisible = block.StateText.Length > 0;
                    if (state.IsVisible != stateIsVisible) state.IsVisible = stateIsVisible;
                }
            }
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
        var start = _viewModel.GetTimelineClickStart(position.Y);
        await RequestBlockPickerAsync(start);
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
        _activeBlockPointer = e.Pointer;
        _gestureCoordinateRoot = (Visual?)TopLevel.GetTopLevel(this) ?? this;
        _pointerOrigin = e.GetPosition(_gestureCoordinateRoot);
        _explicitScrollOffsetDelta = 0;
        _pendingWheelScrollOffsetDelta = 0;
        _wheelInputInProgress = false;
        _wheelInputStartGeneration = 0;
        _wheelInputScrollOffsetDelta = 0;
        _wheelInputHasLayoutChange = false;
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
        var delta = GetGestureDelta(e);
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
        var delta = GetGestureDelta(e);
        var startTop = _blockTopAtStart;
        var startHeight = _blockHeightAtStart;
        _activeBlockBorder = null;
        _activeBlockPointer = null;
        _activeBlock = null;
        _blockWasDragged = false;
        _resizeGesture = false;
        _gestureCoordinateRoot = null;
        _explicitScrollOffsetDelta = 0;
        _pendingWheelScrollOffsetDelta = 0;
        _wheelInputInProgress = false;
        _wheelInputStartGeneration = 0;
        _wheelInputScrollOffsetDelta = 0;
        _wheelInputHasLayoutChange = false;
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
        _activeBlockPointer = null;
        _activeBlock = null;
        _blockWasDragged = false;
        _resizeGesture = false;
        _gestureCoordinateRoot = null;
        _explicitScrollOffsetDelta = 0;
        _pendingWheelScrollOffsetDelta = 0;
        _wheelInputInProgress = false;
        _wheelInputStartGeneration = 0;
        _wheelInputScrollOffsetDelta = 0;
        _wheelInputHasLayoutChange = false;
    }

    private void CancelActiveBlockGesture()
    {
        if (_activeBlockBorder is not { } border) return;
        var pointer = _activeBlockPointer;
        border.Height = _blockHeightAtStart;
        Canvas.SetTop(border, _blockTopAtStart);
        border.IsVisible = true;
        _activeBlockBorder = null;
        _activeBlockPointer = null;
        _activeBlock = null;
        _blockWasDragged = false;
        _resizeGesture = false;
        _gestureCoordinateRoot = null;
        _explicitScrollOffsetDelta = 0;
        _pendingWheelScrollOffsetDelta = 0;
        _wheelInputInProgress = false;
        _wheelInputStartGeneration = 0;
        _wheelInputScrollOffsetDelta = 0;
        _wheelInputHasLayoutChange = false;
        pointer?.Capture(null);
    }

    private double GetGestureDelta(PointerEventArgs e) =>
        e.GetPosition(_gestureCoordinateRoot ?? this).Y - _pointerOrigin.Y + _explicitScrollOffsetDelta;

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
        _activeBlockFlyout = flyout;
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeBlockFlyout, flyout)) _activeBlockFlyout = null;
        };
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
        if (_viewModel is null) return;
        await RequestBlockPickerAsync(_viewModel.GetAddBlockStart(_viewModel.Now));
    }

    private async Task RequestBlockPickerAsync(DateTimeOffset start)
    {
        if (_blockPickerRequestInProgress || BlockPickerRequested is not { } handler) return;
        _blockPickerRequestInProgress = true;
        try
        {
            await handler(start);
        }
        catch (Exception exception)
        {
            BlockPickerRequestFailed?.Invoke(exception);
        }
        finally
        {
            _blockPickerRequestInProgress = false;
        }
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
