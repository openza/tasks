using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Openza.Tasks.Desktop.Dialogs;

public sealed partial class LabelPickerWindow : Window
{
    private readonly IReadOnlyList<LabelChoice> _allChoices;

    public LabelPickerWindow()
        : this([], [])
    {
    }

    public LabelPickerWindow(IEnumerable<string> labels, IEnumerable<string> selectedLabels)
    {
        InitializeComponent();
        var selected = selectedLabels.ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        _allChoices = labels
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(label => label, StringComparer.CurrentCultureIgnoreCase)
            .Select(label => new LabelChoice(label, selected.Contains(label)))
            .ToList();
        LabelsList.ItemsSource = FilteredChoices;
        RefreshChoices();
    }

    private ObservableCollection<LabelChoice> FilteredChoices { get; } = [];

    private void OnOpened(object? sender, EventArgs e) => SearchBox.Focus();

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => RefreshChoices();

    private void RefreshChoices()
    {
        var search = SearchBox.Text?.Trim() ?? string.Empty;
        FilteredChoices.Clear();
        foreach (var choice in _allChoices.Where(choice => choice.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)))
        {
            FilteredChoices.Add(choice);
        }
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);

    private void OnApplyClicked(object? sender, RoutedEventArgs e)
    {
        var labels = _allChoices.Where(choice => choice.IsSelected).Select(choice => choice.Name)
            .Concat((NewLabelsBox.Text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.CurrentCultureIgnoreCase);
        Close(string.Join(", ", labels));
    }
}

public sealed class LabelChoice(string name, bool isSelected)
{
    public string Name { get; } = name;
    public bool IsSelected { get; set; } = isSelected;
}
