using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Openza.Tasks.Desktop.Dialogs;

public sealed partial class TextPromptWindow : Window
{
    public TextPromptWindow()
    {
        InitializeComponent();
    }

    public TextPromptWindow(string title, string value)
        : this()
    {
        Title = title;
        TitleText.Text = title;
        ValueBox.Text = value;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ValueBox.Focus();
        ValueBox.SelectAll();
    }

    private void OnValueKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Complete();
        }
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirmClicked(object? sender, RoutedEventArgs e) => Complete();

    private void Complete()
    {
        var value = ValueBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(value))
        {
            Close(value);
        }
    }
}
