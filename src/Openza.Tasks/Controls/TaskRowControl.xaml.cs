using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Openza.Tasks.Controls;

public sealed partial class TaskRowControl : UserControl
{
    public event RoutedEventHandler? ToggleCompleteClicked;
    public event RoutedEventHandler? CopyTaskIdClicked;
    public event RoutedEventHandler? DeleteTaskClicked;

    public TaskRowControl()
    {
        InitializeComponent();
    }

    private void OnToggleCompleteClicked(object sender, RoutedEventArgs e) => ToggleCompleteClicked?.Invoke(sender, e);

    private void OnCopyTaskIdClicked(object sender, RoutedEventArgs e) => CopyTaskIdClicked?.Invoke(sender, e);

    private void OnDeleteTaskClicked(object sender, RoutedEventArgs e) => DeleteTaskClicked?.Invoke(sender, e);

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ActionsBar.Opacity = 1;
        RowSurface.Background = (Brush)Application.Current.Resources["OpenzaQuietHoverBrush"];
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        ActionsBar.Opacity = ActionsBar.FocusState == FocusState.Unfocused ? 0 : 1;
        RowSurface.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void OnActionsGotFocus(object sender, RoutedEventArgs e)
    {
        ActionsBar.Opacity = 1;
    }

    private void OnActionsLostFocus(object sender, RoutedEventArgs e)
    {
        ActionsBar.Opacity = 0;
    }
}
