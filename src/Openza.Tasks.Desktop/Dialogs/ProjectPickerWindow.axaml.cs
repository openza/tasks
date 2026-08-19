using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Openza.Tasks.Desktop.Dialogs;

public sealed partial class ProjectPickerWindow : Window
{
    private readonly IReadOnlyList<PickerOption> _projects;
    private readonly string _selectedProjectId;

    public ProjectPickerWindow()
        : this([], null)
    {
    }

    public ProjectPickerWindow(IReadOnlyList<PickerOption> projects, string? selectedProjectId)
    {
        InitializeComponent();
        _projects = projects;
        _selectedProjectId = selectedProjectId ?? string.Empty;
        RefreshProjects();
    }

    private void OnOpened(object? sender, EventArgs e) => SearchBox.Focus();

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e) => RefreshProjects();

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CompleteFromSearch();
            e.Handled = true;
        }
    }

    private void OnProjectDoubleTapped(object? sender, TappedEventArgs e) => ChooseSelectedProject();

    private void OnCreateProjectClicked(object? sender, RoutedEventArgs e) => CreateProject();

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);

    private void OnChooseClicked(object? sender, RoutedEventArgs e) => ChooseSelectedProject();

    private void RefreshProjects()
    {
        var selectedId = (ProjectsList.SelectedItem as PickerOption)?.Id ?? _selectedProjectId;
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var filtered = _projects
            .Where(project => string.IsNullOrWhiteSpace(query) ||
                project.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(project => string.IsNullOrWhiteSpace(project.Id) ? 0 : 1)
            .ThenBy(project => project.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(30)
            .ToArray();
        ProjectsList.ItemsSource = filtered;
        ProjectsList.SelectedItem = filtered.FirstOrDefault(project =>
                string.Equals(project.Id, selectedId, StringComparison.Ordinal))
            ?? filtered.FirstOrDefault();

        var canCreate = !string.IsNullOrWhiteSpace(query) &&
            !_projects.Any(project => string.Equals(project.Title, query, StringComparison.CurrentCultureIgnoreCase));
        CreateProjectButton.IsVisible = canCreate;
        CreateProjectButton.Content = canCreate ? $"Create project \"{query}\"" : "Create project";
    }

    private void CompleteFromSearch()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var exact = _projects.FirstOrDefault(project =>
            string.Equals(project.Title, query, StringComparison.CurrentCultureIgnoreCase));
        if (exact is not null)
        {
            Close(ProjectPickerResult.Select(exact.Id));
            return;
        }

        if (ProjectsList.SelectedItem is PickerOption)
        {
            ChooseSelectedProject();
            return;
        }

        CreateProject();
    }

    private void ChooseSelectedProject()
    {
        if (ProjectsList.SelectedItem is PickerOption project)
        {
            Close(ProjectPickerResult.Select(project.Id));
        }
    }

    private void CreateProject()
    {
        var name = SearchBox.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(name))
        {
            Close(ProjectPickerResult.Create(name));
        }
    }
}

public sealed record ProjectPickerResult(string? ProjectId, string? NewProjectName)
{
    public static ProjectPickerResult Select(string projectId) => new(projectId, null);
    public static ProjectPickerResult Create(string name) => new(null, name);
}
