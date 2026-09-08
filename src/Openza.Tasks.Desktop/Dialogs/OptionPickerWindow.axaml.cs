using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Openza.Tasks.Desktop.Dialogs;

public sealed partial class OptionPickerWindow : Window
{
    public OptionPickerWindow()
        : this("Choose", string.Empty, [], null)
    {
    }

    public OptionPickerWindow(string title, string description, IReadOnlyList<PickerOption> options, string? selectedId)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        DescriptionText.Text = description;
        OptionsList.ItemsSource = options;
        OptionsList.SelectedItem = options.FirstOrDefault(option => string.Equals(option.Id, selectedId, StringComparison.Ordinal))
            ?? options.FirstOrDefault();
    }

    private void OnOpened(object? sender, EventArgs e) => OptionsList.Focus();

    private void OnOptionDoubleTapped(object? sender, TappedEventArgs e) => Complete();

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);

    private void OnChooseClicked(object? sender, RoutedEventArgs e) => Complete();

    private void Complete()
    {
        if (OptionsList.SelectedItem is PickerOption option)
        {
            Close(option.Id);
        }
    }
}

public sealed record PickerOption(string Id, string Title, string Subtitle = "");
