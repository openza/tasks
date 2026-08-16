using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Openza.Tasks.Desktop.Dialogs;

public sealed partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        InitializeComponent();
    }

    public ConfirmWindow(string title, string message)
        : this(title, message, "Delete", showCancel: true)
    {
    }

    public ConfirmWindow(string title, string message, string primaryButtonText, bool showCancel)
        : this()
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = primaryButtonText;
        CancelButton.IsVisible = showCancel;
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);

    private void OnConfirmClicked(object? sender, RoutedEventArgs e) => Close(true);
}
