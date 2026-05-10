using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;

namespace Openza.Tasks.Controls;

public sealed partial class EmptyStateControl : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(EmptyStateControl),
        new PropertyMetadata("Nothing here"));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message),
        typeof(string),
        typeof(EmptyStateControl),
        new PropertyMetadata("Add a task or change your filters."));

    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(
        nameof(ActionText),
        typeof(string),
        typeof(EmptyStateControl),
        new PropertyMetadata("Add task"));

    public static readonly DependencyProperty ActionVisibilityProperty = DependencyProperty.Register(
        nameof(ActionVisibility),
        typeof(Visibility),
        typeof(EmptyStateControl),
        new PropertyMetadata(Visibility.Visible));

    public event RoutedEventHandler? ActionClicked;

    public EmptyStateControl()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string ActionText
    {
        get => (string)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    public Visibility ActionVisibility
    {
        get => (Visibility)GetValue(ActionVisibilityProperty);
        set => SetValue(ActionVisibilityProperty, value);
    }

    private void OnActionClicked(object sender, RoutedEventArgs e) => ActionClicked?.Invoke(sender, e);
}
