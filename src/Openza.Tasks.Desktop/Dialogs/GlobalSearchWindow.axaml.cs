using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Openza.Tasks.Core.Data;

namespace Openza.Tasks.Desktop.Dialogs;

public sealed partial class GlobalSearchWindow : Window
{
    private readonly Func<string, bool, bool, Task<IReadOnlyList<GlobalSearchResult>>> _search;
    private bool _ready;
    private int _searchGeneration;

    public GlobalSearchWindow()
        : this((_, _, _) => Task.FromResult<IReadOnlyList<GlobalSearchResult>>([]))
    {
    }

    public GlobalSearchWindow(Func<string, bool, bool, Task<IReadOnlyList<GlobalSearchResult>>> search)
    {
        _search = search;
        InitializeComponent();
        TaskResultsList.ItemsSource = TaskResults;
        ProjectResultsList.ItemsSource = ProjectResults;
        ScopeSelector.SelectedIndex = 0;
        _ready = true;
    }

    private ObservableCollection<GlobalSearchResult> TaskResults { get; } = [];

    private ObservableCollection<GlobalSearchResult> ProjectResults { get; } = [];

    private void OnOpened(object? sender, EventArgs e) => SearchBox.Focus();

    private async void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(null);
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (SelectedResult() is not null)
        {
            Complete();
        }
        else
        {
            await SearchAsync();
        }
    }

    private async void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_ready)
        {
            await SearchAsync();
        }
    }

    private async void OnSearchOptionsChanged(object? sender, RoutedEventArgs e)
    {
        if (_ready)
        {
            await SearchAsync();
        }
    }

    private async void OnSearchClicked(object? sender, RoutedEventArgs e) => await SearchAsync();

    private async Task SearchAsync()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            _searchGeneration++;
            ClearResults();
            ResultStatus.Text = "Enter a search term.";
            return;
        }

        var generation = ++_searchGeneration;
        ResultStatus.Text = "Searching…";
        IReadOnlyList<GlobalSearchResult> results;
        try
        {
            results = await _search(
                query,
                ScopeSelector.SelectedIndex == 1,
                IncludeCompletedBox.IsChecked == true);
        }
        catch (Exception exception)
        {
            if (generation == _searchGeneration)
            {
                ResultStatus.Text = $"Search failed: {exception.Message}";
            }
            return;
        }
        if (generation != _searchGeneration)
        {
            return;
        }

        ClearResults();
        foreach (var result in results)
        {
            if (result.Kind == GlobalSearchResultKind.Task)
            {
                TaskResults.Add(result);
            }
            else
            {
                ProjectResults.Add(result);
            }
        }
        TaskResultsSection.IsVisible = TaskResults.Count > 0;
        ProjectResultsSection.IsVisible = ProjectResults.Count > 0;
        if (TaskResults.Count > 0)
        {
            TaskResultsList.SelectedIndex = 0;
        }
        else if (ProjectResults.Count > 0)
        {
            ProjectResultsList.SelectedIndex = 0;
        }
        ResultStatus.Text = results.Count == 0
            ? "No matching tasks or projects."
            : $"{results.Count} result{(results.Count == 1 ? string.Empty : "s")}";
    }

    private void ClearResults()
    {
        TaskResults.Clear();
        ProjectResults.Clear();
        TaskResultsList.SelectedIndex = -1;
        ProjectResultsList.SelectedIndex = -1;
        TaskResultsSection.IsVisible = false;
        ProjectResultsSection.IsVisible = false;
    }

    private void OnTaskSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TaskResultsList.SelectedItem is not null)
        {
            ProjectResultsList.SelectedIndex = -1;
        }
    }

    private void OnProjectSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ProjectResultsList.SelectedItem is not null)
        {
            TaskResultsList.SelectedIndex = -1;
        }
    }

    private void OnResultsDoubleTapped(object? sender, TappedEventArgs e) => Complete();

    private void OnOpenClicked(object? sender, RoutedEventArgs e) => Complete();

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);

    private void Complete()
    {
        if (SelectedResult() is { } result)
        {
            Close(result);
        }
    }

    private GlobalSearchResult? SelectedResult() =>
        TaskResultsList.SelectedItem as GlobalSearchResult
        ?? ProjectResultsList.SelectedItem as GlobalSearchResult
        ?? TaskResults.FirstOrDefault()
        ?? ProjectResults.FirstOrDefault();
}
