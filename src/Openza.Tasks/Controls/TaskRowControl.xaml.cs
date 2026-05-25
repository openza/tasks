using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Openza.Tasks.Core.Models;
using Openza.Tasks.ViewModels;
using Windows.Foundation;

namespace Openza.Tasks.Controls;

public sealed partial class TaskRowControl : UserControl
{
    private bool _completionInProgress;

    public event RoutedEventHandler? ToggleCompleteClicked;
    public event TypedEventHandler<TaskRowControl, TaskListItemViewModel>? TaskInvoked;
    public event TypedEventHandler<TaskRowControl, TaskRowActionRequestedEventArgs>? TaskRowActionRequested;

    public TaskRowControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private async void OnToggleCompleteClicked(object sender, RoutedEventArgs e)
    {
        if (_completionInProgress)
        {
            return;
        }

        if (DataContext is not TaskListItemViewModel item)
        {
            SyncRowState();
            ToggleCompleteClicked?.Invoke(sender, e);
            return;
        }

        _completionInProgress = true;
        CompleteCheckBox.IsEnabled = false;
        CompleteCheckBox.IsChecked = !item.IsCompleted;

        ToggleCompleteClicked?.Invoke(sender, e);
        await ResetIfStillVisibleAsync(item.Id).ConfigureAwait(true);
    }

    private void OnDeleteButtonClicked(object sender, RoutedEventArgs e)
    {
        if (TaskId(sender) is { } id)
        {
            TaskRowActionRequested?.Invoke(this, new TaskRowActionRequestedEventArgs(id, TaskRowActionKind.Delete));
        }
    }

    private void OnRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsInteractiveElement(e.OriginalSource as DependencyObject) ||
            DataContext is not TaskListItemViewModel item)
        {
            return;
        }

        TaskInvoked?.Invoke(this, item);
    }

    private void OnRowDateSelected(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
    {
        if (args.AddedDates.Count == 0 || DataContext is not TaskListItemViewModel item)
        {
            return;
        }

        TaskRowActionRequested?.Invoke(this, new TaskRowActionRequestedEventArgs(item.Id, TaskRowActionKind.SetDate)
        {
            Date = DateOnly.FromDateTime(args.AddedDates[0].LocalDateTime),
        });
    }

    private void OnClearDateClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is TaskListItemViewModel item)
        {
            TaskRowActionRequested?.Invoke(this, new TaskRowActionRequestedEventArgs(item.Id, TaskRowActionKind.ClearDate));
        }
    }

    private void OnChangeProjectClicked(object sender, RoutedEventArgs e)
    {
        if (TaskId(sender) is { } id)
        {
            TaskRowActionRequested?.Invoke(this, new TaskRowActionRequestedEventArgs(id, TaskRowActionKind.ChangeProject));
        }
    }

    private void OnChangeLabelsClicked(object sender, RoutedEventArgs e)
    {
        if (TaskId(sender) is { } id)
        {
            TaskRowActionRequested?.Invoke(this, new TaskRowActionRequestedEventArgs(id, TaskRowActionKind.ChangeLabels));
        }
    }

    private void OnMoveToSpaceClicked(object sender, RoutedEventArgs e)
    {
        if (TaskId(sender) is { } id)
        {
            TaskRowActionRequested?.Invoke(this, new TaskRowActionRequestedEventArgs(id, TaskRowActionKind.MoveToSpace));
        }
    }

    private void OnStatusMenuItemClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag?.ToString() is not { } tag ||
            DataContext is not TaskListItemViewModel item)
        {
            return;
        }

        var status = tag switch
        {
            "next" => TaskItemStatus.Next,
            "waiting" => TaskItemStatus.Waiting,
            "someday" => TaskItemStatus.Someday,
            _ => TaskItemStatus.Inbox,
        };
        TaskRowActionRequested?.Invoke(this, new TaskRowActionRequestedEventArgs(item.Id, TaskRowActionKind.SetStatus)
        {
            Status = status,
        });
    }

    private void OnPriorityMenuItemClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag?.ToString() is not { } tag ||
            !int.TryParse(tag, out var priority) ||
            DataContext is not TaskListItemViewModel item)
        {
            return;
        }

        TaskRowActionRequested?.Invoke(this, new TaskRowActionRequestedEventArgs(item.Id, TaskRowActionKind.SetPriority)
        {
            Priority = priority,
        });
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ActionsBar.Opacity = 1;
        RowSurface.Background = ResourceBrush("OpenzaRowHoverBrush");
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        ActionsBar.Opacity = ActionsBar.FocusState == FocusState.Unfocused ? 0 : 1;
        RowSurface.Background = ResourceBrush("OpenzaRowBackgroundBrush");
    }

    private void OnActionsGotFocus(object sender, RoutedEventArgs e)
    {
        ActionsBar.Opacity = 1;
    }

    private void OnActionsLostFocus(object sender, RoutedEventArgs e)
    {
        ActionsBar.Opacity = 0;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        _completionInProgress = false;
        SyncRowState();
        ActionsBar.Opacity = 0;
        ResetRowVisualState();
    }

    private void SyncRowState()
    {
        if (DataContext is not TaskListItemViewModel item)
        {
            CompleteCheckBox.IsChecked = false;
            CompleteCheckBox.Tag = null;
            return;
        }

        CompleteCheckBox.IsChecked = item.IsCompleted;
        CompleteCheckBox.Tag = item.Id;
        CompleteCheckBox.IsEnabled = true;
    }

    private async Task ResetIfStillVisibleAsync(string id)
    {
        await Task.Delay(350).ConfigureAwait(true);
        if (!_completionInProgress ||
            DataContext is not TaskListItemViewModel item ||
            !string.Equals(item.Id, id, StringComparison.Ordinal))
        {
            return;
        }

        _completionInProgress = false;
        CompleteCheckBox.IsEnabled = true;
        ResetRowVisualState();
    }

    private void ResetRowVisualState()
    {
        RowSurface.Opacity = 1;
        RowSurface.Background = ResourceBrush("OpenzaRowBackgroundBrush");
    }

    private static Brush ResourceBrush(string key) => (Brush)Application.Current.Resources[key];

    private static bool IsInteractiveElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase or CheckBox or TextBlock { IsTextSelectionEnabled: true })
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private static string? TaskId(object sender) => (sender as FrameworkElement)?.Tag as string;
}
