using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Openza.Tasks.Core.Data;

namespace Openza.Tasks.Desktop.Dialogs;

public sealed partial class GlobalSearchWindow : Window
{
    private readonly Func<string, Task<IReadOnlyList<GlobalSearchResult>>> _search;

    public GlobalSearchWindow()
        : this(_ => Task.FromResult<IReadOnlyList<GlobalSearchResult>>([]))
    {
    }

    public GlobalSearchWindow(Func<string, Task<IReadOnlyList<GlobalSearchResult>>> search)
    {
        InitializeComponent();
        _search = search;
        ResultsList.ItemsSource = Results;
    }

    private ObservableCollection<GlobalSearchResult> Results { get; } = [];

    private void OnOpened(object? sender, EventArgs e) => SearchBox.Focus();

    private async void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await SearchAsync();
    }

    private async void OnSearchClicked(object? sender, RoutedEventArgs e) => await SearchAsync();

    private async Task SearchAsync()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            ResultStatus.Text = "Enter a search term.";
            return;
        }

        ResultStatus.Text = "Searching…";
        IReadOnlyList<GlobalSearchResult> results;
        try
        {
            results = await _search(query);
        }
        catch (Exception exception)
        {
            ResultStatus.Text = $"Search failed: {exception.Message}";
            return;
        }
        Results.Clear();
        foreach (var result in results)
        {
            Results.Add(result);
        }
        ResultsList.SelectedIndex = Results.Count > 0 ? 0 : -1;
        ResultStatus.Text = Results.Count == 0
            ? "No matching tasks or projects."
            : $"{Results.Count} result{(Results.Count == 1 ? string.Empty : "s")}";
    }

    private void OnResultsDoubleTapped(object? sender, TappedEventArgs e) => Complete();

    private void OnOpenClicked(object? sender, RoutedEventArgs e) => Complete();

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);

    private void Complete()
    {
        if (ResultsList.SelectedItem is GlobalSearchResult result)
        {
            Close(result);
        }
    }
}
