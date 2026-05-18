using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.ViewModels;
using Windows.System;

namespace Openza.Tasks.Shell;

public sealed partial class AppShell
{
    private void OnGlobalSearchClicked(object sender, RoutedEventArgs e)
    {
        OpenGlobalSearchOverlay();
    }

    private void OpenGlobalSearchOverlay()
    {
        GlobalSearchOverlay.Visibility = Visibility.Visible;
        GlobalSearchScopeSelector.SelectedIndex = 0;
        GlobalSearchIncludeCompletedBox.IsChecked = false;
        GlobalSearchBox.Text = string.Empty;
        UpdateGlobalSearchState(showPrompt: true);
        FocusGlobalSearchBox();
    }

    private void OnGlobalSearchCloseClicked(object sender, RoutedEventArgs e)
    {
        CloseGlobalSearchOverlay();
    }

    private void CloseGlobalSearchOverlay()
    {
        GlobalSearchOverlay.Visibility = Visibility.Collapsed;
        GlobalSearchBox.Text = string.Empty;
        _globalTaskSearchResults.Clear();
        _globalProjectSearchResults.Clear();
        UpdateGlobalSearchState(showPrompt: true);
    }

    private void FocusGlobalSearchBox()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (GlobalSearchOverlay.Visibility == Visibility.Visible)
            {
                GlobalSearchBox.Focus(FocusState.Programmatic);
            }
        });
    }

    private void OnGlobalSearchPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && GlobalSearchOverlay.Visibility == Visibility.Visible)
        {
            CloseGlobalSearchOverlay();
            e.Handled = true;
        }
    }

    private async void OnGlobalSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_uiReady || GlobalSearchOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        await RefreshGlobalSearchAsync().ConfigureAwait(true);
    }

    private async void OnGlobalSearchScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || GlobalSearchOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        await RefreshGlobalSearchAsync().ConfigureAwait(true);
    }

    private async void OnGlobalSearchIncludeCompletedChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || GlobalSearchOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        await RefreshGlobalSearchAsync().ConfigureAwait(true);
    }

    private async void OnGlobalSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var first = _globalTaskSearchResults.FirstOrDefault() ?? _globalProjectSearchResults.FirstOrDefault();
        if (first is not null)
        {
            await NavigateToGlobalSearchResultAsync(first.Result).ConfigureAwait(true);
        }
    }

    private async void OnGlobalSearchResultClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GlobalSearchResultViewModel item)
        {
            await NavigateToGlobalSearchResultAsync(item.Result).ConfigureAwait(true);
        }
    }

    private async Task RefreshGlobalSearchAsync()
    {
        var searchText = GlobalSearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(searchText))
        {
            _globalTaskSearchResults.Clear();
            _globalProjectSearchResults.Clear();
            UpdateGlobalSearchState(showPrompt: true);
            return;
        }

        var includeAllSpaces = IsAllSpacesGlobalSearch();
        var results = await _store.SearchAsync(new GlobalSearchQuery
        {
            SearchText = searchText,
            SpaceId = _currentSpaceId,
            IncludeAllSpaces = includeAllSpaces,
            IncludeCompletedTasks = GlobalSearchIncludeCompletedBox.IsChecked == true,
            Limit = 25,
        }).ConfigureAwait(true);

        var spacesById = _spaces.ToDictionary(space => space.Id, StringComparer.Ordinal);
        _globalTaskSearchResults.Clear();
        _globalProjectSearchResults.Clear();
        foreach (var result in results)
        {
            spacesById.TryGetValue(result.SpaceId, out var space);
            var item = new GlobalSearchResultViewModel(result, space, includeAllSpaces);
            if (result.Kind == GlobalSearchResultKind.Task)
            {
                _globalTaskSearchResults.Add(item);
            }
            else
            {
                _globalProjectSearchResults.Add(item);
            }
        }

        UpdateGlobalSearchState(showPrompt: false);
    }

    private void UpdateGlobalSearchState(bool showPrompt)
    {
        var hasTasks = _globalTaskSearchResults.Count > 0;
        var hasProjects = _globalProjectSearchResults.Count > 0;
        GlobalSearchPromptText.Visibility = showPrompt ? Visibility.Visible : Visibility.Collapsed;
        GlobalSearchTasksSection.Visibility = hasTasks ? Visibility.Visible : Visibility.Collapsed;
        GlobalSearchProjectsSection.Visibility = hasProjects ? Visibility.Visible : Visibility.Collapsed;
        GlobalSearchNoResultsText.Visibility = !showPrompt && !hasTasks && !hasProjects ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool IsAllSpacesGlobalSearch() =>
        (GlobalSearchScopeSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "all";

    private async Task NavigateToGlobalSearchResultAsync(GlobalSearchResult result)
    {
        if (!await SavePendingTaskDetailsAsync().ConfigureAwait(true))
        {
            return;
        }

        CloseGlobalSearchOverlay();
        await SwitchSpaceAsync(result.SpaceId, savePendingEdits: false, refreshTasks: false).ConfigureAwait(true);
        if (result.Kind == GlobalSearchResultKind.Project)
        {
            await SelectProjectAsync(result.Id).ConfigureAwait(true);
            return;
        }

        var task = await _store.GetTaskAsync(result.Id).ConfigureAwait(true);
        if (task is null)
        {
            return;
        }

        if (task.IsCompleted)
        {
            await SelectTaskListForSearchResultAsync("completed", null).ConfigureAwait(true);
        }
        else if (!string.IsNullOrWhiteSpace(task.ProjectId))
        {
            await SelectProjectAsync(task.ProjectId).ConfigureAwait(true);
        }
        else
        {
            await SelectTaskListForSearchResultAsync(TaskViewForSearchResult(task), null).ConfigureAwait(true);
        }

        await LoadTaskDetailsAsync(task.Id).ConfigureAwait(true);
    }

    private async Task SelectTaskListForSearchResultAsync(string view, string? projectId)
    {
        _selectedProjectId = projectId;
        _selectedTaskId = null;
        SelectNavigationSilently(view);
        ApplyTaskViewPreferences();
        TasksPage.HideDetailsPane();
        TasksPage.HideConnectedTasksDrawer();
        TasksPage.ClearTaskSelection();
        TasksPage.DetailsPanel.ClearForNewTask(GetProject(projectId), 3, DefaultStatusForCurrentView());
        await RefreshProjectListAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
    }

    private static string TaskViewForSearchResult(TaskItem task) => task.WorkflowStatus switch
    {
        TaskWorkflowStatus.Inbox => "inbox",
        TaskWorkflowStatus.Next => "next",
        TaskWorkflowStatus.Waiting => "waiting",
        TaskWorkflowStatus.Someday => "someday",
        _ => "tasks",
    };
}
