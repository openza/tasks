using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Pages;
using Openza.Tasks.ViewModels;

namespace Openza.Tasks.Shell;

public sealed partial class AppShell
{
    private async Task LoadProjectsAsync()
    {
        _allProjects.Clear();
        _allProjects.AddRange(await _store.GetProjectsAsync().ConfigureAwait(true));
        TasksPage.SetProjectOptions(_allProjects);
        await RefreshProjectListAsync().ConfigureAwait(true);
    }

    private async Task RefreshProjectListAsync()
    {
        var counts = await _store.GetTaskCountsAsync().ConfigureAwait(true);
        UpdateNavigationCounts(counts);
        TasksPage.ViewModel.ProjectGroups.Clear();
        _projectIdByName.Clear();

        var search = TasksPage.ProjectSearchText;
        foreach (var group in BuildProjectGroups(counts, search))
        {
            TasksPage.ViewModel.ProjectGroups.Add(group);
        }
    }

    private IEnumerable<ProjectGroupViewModel> BuildProjectGroups(TaskCountSummary counts, string search)
    {
        var groups = _allProjects
            .Where(project => string.IsNullOrWhiteSpace(search) || project.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .Where(project => project.IntegrationId != IntegrationIds.Obsidian)
            .GroupBy(project => project.IntegrationId)
            .OrderBy(group => group.Key switch
            {
                IntegrationIds.Local => 0,
                IntegrationIds.Todoist => 1,
                IntegrationIds.MicrosoftToDo => 2,
                _ => 9,
            });

        foreach (var group in groups)
        {
            var projects = group
                .OrderByDescending(project => project.IsFavorite)
                .ThenBy(project => project.SortOrder)
                .ThenBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(project =>
                {
                    counts.ActiveByProject.TryGetValue(project.Id, out var count);
                    _projectIdByName[project.Name] = project.Id;
                    return ProjectListItemViewModel.FromProject(project, count, string.Equals(project.Id, _selectedProjectId, StringComparison.Ordinal));
                })
                .ToList();

            if (projects.Count == 0)
            {
                continue;
            }

            var groupViewModel = new ProjectGroupViewModel
            {
                Id = group.Key,
                Name = SourceName(group.Key),
                Glyph = group.Key switch
                {
                    _ => "\uE8B7",
                },
                Count = projects.Count,
                IsExpanded = true,
            };

            foreach (var project in projects)
            {
                groupViewModel.Projects.Add(project);
            }

            yield return groupViewModel;
        }
    }

    private async void OnProjectSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_uiReady)
        {
            return;
        }

        await RefreshProjectListAsync().ConfigureAwait(true);
    }

    private async void OnProjectSelected(TasksPage sender, string? id)
    {
        if (!_uiReady)
        {
            return;
        }

        if (!await ConfirmDiscardTaskEditsAsync().ConfigureAwait(true))
        {
            return;
        }

        _selectedProjectId = string.IsNullOrWhiteSpace(id) ? null : id;
        if (!string.Equals(_currentView, "tasks", StringComparison.Ordinal))
        {
            SelectNavigationSilently("tasks");
        }

        _selectedTaskId = null;
        TasksPage.HideDetailsPane();
        TasksPage.ClearTaskSelection();
        TasksPage.DetailsPanel.ClearForNewTask(GetProject(_selectedProjectId), 3, TaskItemStatus.None);
        await RefreshProjectListAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
    }

    private async void OnClearProjectClicked(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || string.IsNullOrWhiteSpace(_selectedProjectId))
        {
            return;
        }

        if (!await ConfirmDiscardTaskEditsAsync().ConfigureAwait(true))
        {
            return;
        }

        _selectedProjectId = null;
        _selectedTaskId = null;
        TasksPage.HideDetailsPane();
        TasksPage.ClearTaskSelection();
        TasksPage.DetailsPanel.ClearForNewTask(null, 3);
        await RefreshProjectListAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
    }

    private async void OnAddProjectClicked(object sender, RoutedEventArgs e)
    {
        var textBox = new TextBox { Header = "Project name", PlaceholderText = "New project" };
        var dialog = new ContentDialog
        {
            Title = "Create project",
            Content = textBox,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };

        dialog.Opened += (_, _) => textBox.Focus(FocusState.Programmatic);
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(textBox.Text))
        {
            await _store.UpsertProjectAsync(new ProjectItem
            {
                Id = $"proj_{Guid.NewGuid():N}",
                Name = textBox.Text.Trim(),
                IntegrationId = IntegrationIds.Local,
                CreatedAt = DateTimeOffset.UtcNow,
            }).ConfigureAwait(true);
            await LoadProjectsAsync().ConfigureAwait(true);
            await RefreshTasksAsync().ConfigureAwait(true);
        }
    }

    private async void OnEditProjectClicked(TasksPage sender, string id)
    {
        _pendingProjectActionId = id;
        await EditSelectedProjectAsync().ConfigureAwait(true);
    }

    private async Task EditSelectedProjectAsync()
    {
        var project = GetSelectedProject(_pendingProjectActionId);
        if (project is null)
        {
            ShowInfo("Select a project", "Choose a local project to edit.", InfoBarSeverity.Warning);
            return;
        }

        if (project.IntegrationId != IntegrationIds.Local)
        {
            ShowInfo("Project is managed", "Only local projects can be edited here.", InfoBarSeverity.Informational);
            return;
        }

        var nameBox = new TextBox { Header = "Project name", Text = project.Name };
        var colorBox = new TextBox { Header = "Color", Text = project.Color, PlaceholderText = "#808080" };
        var favoriteBox = new CheckBox { Content = "Favorite project", IsChecked = project.IsFavorite };
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(nameBox);
        stack.Children.Add(colorBox);
        stack.Children.Add(favoriteBox);

        var dialog = new ContentDialog
        {
            Title = "Edit project",
            Content = stack,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text))
        {
            return;
        }

        await _store.UpsertProjectAsync(project with
        {
            Name = nameBox.Text.Trim(),
            Color = NormalizeColor(colorBox.Text),
            IsFavorite = favoriteBox.IsChecked == true,
            UpdatedAt = DateTimeOffset.UtcNow,
        }).ConfigureAwait(true);
        await LoadProjectsAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
        ShowInfo("Project updated", nameBox.Text.Trim(), InfoBarSeverity.Success);
    }

    private async void OnDeleteProjectClicked(TasksPage sender, string id)
    {
        _pendingProjectActionId = id;
        await DeleteSelectedProjectAsync().ConfigureAwait(true);
    }

    private async Task DeleteSelectedProjectAsync()
    {
        var project = GetSelectedProject(_pendingProjectActionId);
        if (project is null)
        {
            ShowInfo("Select a project", "Choose a local project to delete.", InfoBarSeverity.Warning);
            return;
        }

        if (project.IntegrationId != IntegrationIds.Local)
        {
            ShowInfo("Project is managed", "Only local projects can be deleted here.", InfoBarSeverity.Informational);
            return;
        }

        var taskCount = (await _store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All, ProjectId = project.Id }).ConfigureAwait(true)).Count;
        var dialog = new ContentDialog
        {
            Title = "Delete project",
            Content = taskCount == 0
                ? $"Delete {project.Name}?"
                : $"{project.Name} contains {taskCount} task{(taskCount == 1 ? string.Empty : "s")}. Move them to Inbox, or delete them with the project.",
            PrimaryButtonText = taskCount == 0 ? "Delete" : "Move tasks to Inbox",
            SecondaryButtonText = taskCount == 0 ? string.Empty : "Delete tasks",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None)
        {
            return;
        }

        await _store.DeleteProjectAsync(project.Id, moveTasksToInbox: result == ContentDialogResult.Primary && taskCount > 0).ConfigureAwait(true);
        if (string.Equals(_selectedProjectId, project.Id, StringComparison.Ordinal))
        {
            _selectedProjectId = null;
        }

        await LoadProjectsAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
        ShowInfo("Project deleted", project.Name, InfoBarSeverity.Success);
    }

    private ProjectItem? GetSelectedProject(string? projectId = null) =>
        string.IsNullOrWhiteSpace(projectId ?? _selectedProjectId)
            ? null
            : _allProjects.FirstOrDefault(project => project.Id == (projectId ?? _selectedProjectId));

    private ProjectItem? GetProject(string? projectId) =>
        _allProjects.FirstOrDefault(project => project.Id == projectId);

    private static string NormalizeColor(string color)
    {
        var value = string.IsNullOrWhiteSpace(color) ? "#808080" : color.Trim();
        if (!value.StartsWith('#'))
        {
            value = $"#{value}";
        }

        return value.Length == 7 && value.Skip(1).All(Uri.IsHexDigit)
            ? value
            : "#808080";
    }

    private void UpdateNavigationCounts(TaskCountSummary counts)
    {
        SetBadge(InboxBadge, counts.Inbox);
        SetBadge(NextBadge, counts.NextActions);
        SetBadge(WaitingBadge, counts.Waiting);
        SetBadge(SomedayBadge, counts.Someday);
        SetBadge(TodayBadge, counts.Today);
        SetBadge(OverdueBadge, counts.Overdue);
        SetBadge(TasksBadge, counts.Open);
        SetBadge(CompletedBadge, counts.Completed);
    }

    private static void SetBadge(InfoBadge badge, int count)
    {
        badge.Value = count;
        badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
