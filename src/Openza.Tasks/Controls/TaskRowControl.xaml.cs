using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Openza.Tasks.ViewModels;

namespace Openza.Tasks.Controls;

public sealed partial class TaskRowControl : UserControl
{
    private readonly TranslateTransform _completionTransform = new();
    private bool _completionInProgress;

    public event RoutedEventHandler? ToggleCompleteClicked;
    public event RoutedEventHandler? DeleteTaskClicked;

    public TaskRowControl()
    {
        InitializeComponent();
        RowSurface.RenderTransform = _completionTransform;
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

        if (!item.IsCompleted)
        {
            await PlayCompletionMotionAsync().ConfigureAwait(true);
        }

        ToggleCompleteClicked?.Invoke(sender, e);
        _ = ResetIfStillVisibleAsync(item.Id);
    }

    private void OnDeleteTaskClicked(object sender, RoutedEventArgs e) => DeleteTaskClicked?.Invoke(sender, e);

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

    private async Task PlayCompletionMotionAsync()
    {
        const int steps = 6;
        for (var index = 1; index <= steps; index++)
        {
            RowSurface.Opacity = 1 - (0.64 * index / steps);
            _completionTransform.X = 14d * index / steps;
            await Task.Delay(24).ConfigureAwait(true);
        }
    }

    private async Task ResetIfStillVisibleAsync(string id)
    {
        await Task.Delay(1200).ConfigureAwait(true);
        if (!_completionInProgress ||
            DataContext is not TaskListItemViewModel item ||
            !string.Equals(item.Id, id, StringComparison.Ordinal))
        {
            return;
        }

        _completionInProgress = false;
        SyncRowState();
        ResetRowVisualState();
    }

    private void ResetRowVisualState()
    {
        RowSurface.Opacity = 1;
        _completionTransform.X = 0;
        RowSurface.Background = ResourceBrush("OpenzaRowBackgroundBrush");
    }

    private static Brush ResourceBrush(string key) => (Brush)Application.Current.Resources[key];
}
