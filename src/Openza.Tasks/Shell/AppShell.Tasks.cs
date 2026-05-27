using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Openza.Tasks.Controls;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Services;
using Openza.Tasks.Pages;
using Openza.Tasks.Services;
using Openza.Tasks.ViewModels;
using Windows.System;

namespace Openza.Tasks.Shell;

public sealed partial class AppShell
{
    private string CurrentTaskListSelectionKey() =>
        string.Equals(_currentView, "tasks", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(_selectedProjectId)
            ? $"tasks/project/{_selectedProjectId}"
            : _currentView;

    private void RememberCurrentTaskListSelection()
    {
        if (!IsTaskView(_currentView))
        {
            return;
        }

        var key = CurrentTaskListSelectionKey();
        if (TasksPage.IsDetailsPaneOpen && !string.IsNullOrWhiteSpace(_selectedTaskId))
        {
            _selectedTaskIdByList[key] = _selectedTaskId;
        }
        else
        {
            _selectedTaskIdByList.Remove(key);
        }
    }

    private void RestoreTaskListSelection()
    {
        _selectedTaskId = IsTaskView(_currentView) &&
            _selectedTaskIdByList.TryGetValue(CurrentTaskListSelectionKey(), out var selectedTaskId)
                ? selectedTaskId
                : null;
    }

    private void ForgetCurrentTaskListSelection()
    {
        if (IsTaskView(_currentView))
        {
            _selectedTaskIdByList.Remove(CurrentTaskListSelectionKey());
        }
    }

    private async Task LoadLabelsAsync()
    {
        _allLabels.Clear();
        _allLabels.AddRange(await _store.GetLabelsAsync().ConfigureAwait(true));
        TasksPage.RefreshLabelFilter(_allLabels, _labelFilterId);
        TasksPage.SetLabelOptions(_allLabels);
    }

    private async Task RefreshTasksAsync()
    {
        if (!_uiReady)
        {
            return;
        }

        var refreshVersion = ++_taskRefreshVersion;
        var refreshView = _currentView;
        var refreshSpaceId = _currentSpaceId;
        var refreshProjectId = _selectedProjectId;
        var selectedTaskBeforeRefresh = _selectedTaskId;
        var viewportBeforeRefresh = TasksPage.CaptureTaskListViewport();
        var projects = _allProjects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var selectedProject = refreshView == "tasks" ? GetSelectedProject(refreshProjectId) : null;
        var query = new TaskQuery
        {
            SpaceId = refreshSpaceId,
            Kind = refreshView switch
            {
                "inbox" => TaskListKind.Inbox,
                "today" => TaskListKind.Today,
                "calendar" => TaskListKind.Calendar,
                "overdue" => TaskListKind.Overdue,
                "waiting" => TaskListKind.Waiting,
                "someday" => TaskListKind.Someday,
                "completed" => TaskListKind.Completed,
                "tasks" => TaskListKind.Open,
                _ => TaskListKind.NextActions,
            },
            ProjectId = selectedProject?.Id,
            LabelId = _labelFilterId,
            DateScope = _dateScopeFilter,
            RepeatScope = _repeatScopeFilter,
            SearchText = TasksPage.SearchText,
            SortMode = _sortMode,
            SortDirection = _sortDirection,
            Priority = _priorityFilter,
        };

        var snapshotTask = _store.GetTaskListRefreshSnapshotAsync(query);
        var sourceItemsTask = _store.GetProviderSourceItemsAsync(spaceId: refreshSpaceId, includeAdopted: false, includeIgnored: true);

        await Task.WhenAll(snapshotTask, sourceItemsTask).ConfigureAwait(true);

        if (refreshVersion != _taskRefreshVersion ||
            !string.Equals(refreshView, _currentView, StringComparison.Ordinal) ||
            !string.Equals(refreshSpaceId, _currentSpaceId, StringComparison.Ordinal) ||
            !string.Equals(refreshProjectId, _selectedProjectId, StringComparison.Ordinal))
        {
            return;
        }

        var snapshot = await snapshotTask.ConfigureAwait(true);
        var tasks = snapshot.VisibleTasks;
        var allSpaceTasks = snapshot.AllSpaceTasks;
        _lastTaskCounts = snapshot.Counts;
        var subtaskProgress = BuildSubtaskProgress(allSpaceTasks);
        var matchingSubtasks = BuildMatchingSubtaskText(tasks, query.SearchText);
        var taskItems = tasks
            .Where(task => string.IsNullOrWhiteSpace(task.ParentId))
            .Select(task =>
            {
                projects.TryGetValue(task.ProjectId ?? string.Empty, out var project);
                return new TaskListItemViewModel(
                    task,
                    project,
                    refreshView,
                    selectedProject is not null,
                    0,
                    subtaskProgress.TryGetValue(task.Id, out var progress) ? progress : string.Empty,
                    matchingSubtasks.TryGetValue(task.Id, out var matchingSubtask) ? matchingSubtask : string.Empty);
            })
            .ToList();

        TasksPage.ViewModel.SetTasks(taskItems, _groupMode);

        if (_selectedTaskId is null)
        {
            _loadedDetailsTaskId = null;
            TasksPage.HideDetailsPane();
            TasksPage.ClearTaskSelection();
            TasksPage.DetailsPanel.ClearForNewTask(selectedProject, 3, DefaultStatusForCurrentView());
        }
        else if (tasks.All(task => task.Id != _selectedTaskId))
        {
            if (string.Equals(refreshView, "inbox", StringComparison.Ordinal) &&
                TasksPage.IsDetailsPaneOpen &&
                taskItems.FirstOrDefault(task => !task.IsSubtask) is { } nextInboxTask)
            {
                await LoadTaskDetailsAsync(nextInboxTask.Id).ConfigureAwait(true);
            }
            else
            {
                ForgetCurrentTaskListSelection();
                _selectedTaskId = null;
                _loadedDetailsTaskId = null;
                TasksPage.HideDetailsPane();
                TasksPage.ClearTaskSelection();
                TasksPage.DetailsPanel.ClearForNewTask(selectedProject, 3, DefaultStatusForCurrentView());
            }
        }
        else
        {
            var selectedTaskId = _selectedTaskId;
            if (!TasksPage.IsDetailsPaneOpen ||
                !string.Equals(_loadedDetailsTaskId, selectedTaskId, StringComparison.Ordinal))
            {
                await LoadTaskDetailsAsync(selectedTaskId).ConfigureAwait(true);
            }
            else
            {
                TasksPage.SelectTask(selectedTaskId);
            }
        }

        if (_selectedTaskId is not null && string.Equals(_selectedTaskId, selectedTaskBeforeRefresh, StringComparison.Ordinal))
        {
            TasksPage.RestoreTaskListViewport(viewportBeforeRefresh, _selectedTaskId);
        }

        var title = selectedProject?.Name ?? refreshView switch
        {
            "inbox" => "Inbox",
            "today" => "Today",
            "calendar" => "Calendar",
            "overdue" => "Overdue",
            "waiting" => "Waiting For",
            "someday" => "Someday",
            "completed" => "Completed",
            "tasks" => "Tasks",
            _ => "Next Actions",
        };
        var taskCount = taskItems.Count(task => !task.IsSubtask);
        var subtitle = $"{taskCount} task{(taskCount == 1 ? string.Empty : "s")}";
        if (selectedProject is not null)
        {
            subtitle = $"{subtitle} · {SourceName(selectedProject.IntegrationId)}";
        }

        TasksPage.ViewModel.SelectedProject = selectedProject;
        TasksPage.SetHeader(title, subtitle, selectedProject is not null);
        var sourceItems = await sourceItemsTask.ConfigureAwait(true);
        var counts = snapshot.Counts;
        UpdateNavigationCounts(counts);
        TasksPage.ViewModel.SetProjectGroups(BuildProjectGroups(counts, TasksPage.ProjectSearchText));
        TasksPage.SetGetStartedVisible(_settings.Settings.ShowGetStarted &&
            string.Equals(refreshView, "inbox", StringComparison.Ordinal) &&
            counts.All == 0 &&
            sourceItems.Count == 0 &&
            TasksPage.ViewModel.IsEmpty &&
            selectedProject is null &&
            string.IsNullOrWhiteSpace(TasksPage.SearchText) &&
            _priorityFilter is null &&
            _dateScopeFilter == TaskDateScope.All &&
            _repeatScopeFilter == TaskRepeatScope.Include &&
            _labelFilterId is null);
        ApplySourceItems(sourceItems);
    }

    private async void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_uiReady)
        {
            return;
        }

        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ScheduleTaskSearchRefresh();
            return;
        }

        await RefreshTasksAsync().ConfigureAwait(true);
    }

    private void ScheduleTaskSearchRefresh()
    {
        if (_taskSearchRefreshTimer is null)
        {
            _taskSearchRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            _taskSearchRefreshTimer.Tick += async (_, _) =>
            {
                _taskSearchRefreshTimer?.Stop();
                await RefreshTasksAsync().ConfigureAwait(true);
            };
        }

        _taskSearchRefreshTimer.Stop();
        _taskSearchRefreshTimer.Start();
    }

    private async void OnSortChanged(object sender, EventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        ApplyViewControlsFromPage();
        await SaveTaskViewPreferencesAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
    }

    private async void OnGroupChanged(object sender, EventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        ApplyViewControlsFromPage();
        await SaveTaskViewPreferencesAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
    }

    private async void OnPriorityChanged(object sender, EventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        ApplyViewControlsFromPage();
        await SaveTaskViewPreferencesAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
    }

    private async void OnRepeatScopeChanged(object sender, EventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        ApplyViewControlsFromPage();
        await SaveTaskViewPreferencesAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
    }

    private async void OnLabelChanged(object sender, EventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        ApplyViewControlsFromPage();
        await SaveTaskViewPreferencesAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
    }

    private async void OnTaskSelected(TasksPage sender, TaskListItemViewModel item)
    {
        if (!await SavePendingTaskDetailsAsync().ConfigureAwait(true))
        {
            if (_selectedTaskId is null)
            {
                TasksPage.ClearTaskSelection();
            }
            else
            {
                TasksPage.SelectTask(_selectedTaskId);
            }

            return;
        }

        await LoadTaskDetailsAsync(item.Id).ConfigureAwait(true);
    }

    private async Task LoadTaskDetailsAsync(string taskId)
    {
        var task = await _store.GetTaskAsync(taskId).ConfigureAwait(true);
        if (task is null)
        {
            return;
        }

        _selectedTaskId = task.Id;
        _loadedDetailsTaskId = task.Id;
        RememberCurrentTaskListSelection();
        var projects = _allProjects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var childTasks = await _store.GetTasksAsync(new TaskQuery
        {
            SpaceId = task.SpaceId,
            ParentId = task.Id,
            Kind = TaskListKind.All,
            IncludeSubtasks = true,
        }).ConfigureAwait(true);
        var childItems = childTasks
            .Select(child =>
            {
                projects.TryGetValue(child.ProjectId ?? string.Empty, out var childProject);
                return new TaskListItemViewModel(child, childProject, _currentView, GetSelectedProject() is not null, 1);
            })
            .ToList();
        TasksPage.DetailsPanel.LoadTask(task, GetProject(task.ProjectId), childItems);
        var links = await _store.GetTaskExternalLinksAsync(task.Id).ConfigureAwait(true);
        var githubIssue = links.FirstOrDefault(link =>
            link.IntegrationId == IntegrationIds.GitHub &&
            string.Equals(link.Kind, TaskExternalLinkKinds.Issue, StringComparison.Ordinal));
        TasksPage.DetailsPanel.SetGitHubLink(githubIssue, await IsGitHubConnectedAsync().ConfigureAwait(true));
        TasksPage.SelectTask(task.Id);
        TasksPage.ShowDetailsPane();
    }

    private async Task<bool> IsGitHubConnectedAsync() =>
        !string.IsNullOrWhiteSpace(await _credentials.GetAsync(GitHubIssueService.TokenKey).ConfigureAwait(true));

    private async void OnDetailsManageGitHubIssueRequested(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedTaskId))
        {
            return;
        }

        if (!await SavePendingTaskDetailsAsync().ConfigureAwait(true))
        {
            return;
        }

        var task = await _store.GetTaskAsync(_selectedTaskId).ConfigureAwait(true);
        if (task is null)
        {
            return;
        }

        var link = (await _store.GetTaskExternalLinksAsync(task.Id).ConfigureAwait(true))
            .FirstOrDefault(item => item.IntegrationId == IntegrationIds.GitHub && item.Kind == TaskExternalLinkKinds.Issue);
        if (link is null)
        {
            await CreateGitHubIssueForTaskAsync(task).ConfigureAwait(true);
            return;
        }

        var status = await CheckGitHubIssueStatusAsync(link).ConfigureAwait(true);
        await ShowManageGitHubIssueDialogAsync(task, link, status).ConfigureAwait(true);
    }

    private async Task<GitHubIssueStatusResult?> CheckGitHubIssueStatusAsync(TaskExternalLinkInfo link)
    {
        var token = await _credentials.GetAsync(GitHubIssueService.TokenKey).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(token) || !TryParseGitHubIssueExternalId(link.ExternalId, out var owner, out var repository, out var number))
        {
            return null;
        }

        try
        {
            var status = await _gitHubIssueService.GetIssueStatusAsync(token, owner, repository, number).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(status.Error))
            {
                ShowInfo("GitHub issue not checked", status.Error, InfoBarSeverity.Warning);
            }

            return status;
        }
        catch (Exception exception)
        {
            ShowInfo("GitHub issue not checked", exception.Message, InfoBarSeverity.Warning);
            return null;
        }
    }

    private async Task ShowManageGitHubIssueDialogAsync(TaskItem task, TaskExternalLinkInfo link, GitHubIssueStatusResult? status)
    {
        var issueUnavailable = status?.Unavailable == true;
        var removeButton = new Button
        {
            Content = "Remove link",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        ContentDialog? dialog = null;
        removeButton.Click += async (_, _) =>
        {
            await _store.DeleteTaskExternalLinkAsync(link.Id).ConfigureAwait(true);
            TasksPage.DetailsPanel.SetGitHubLink(null, await IsGitHubConnectedAsync().ConfigureAwait(true));
            ShowInfo("GitHub link removed", link.DisplayName, InfoBarSeverity.Informational);
            dialog?.Hide();
        };
        var panel = new StackPanel
        {
            Spacing = 12,
            Width = 500,
            Children =
            {
                new TextBlock
                {
                    Text = issueUnavailable
                        ? "Openza cannot access this GitHub issue. It may be deleted, private, or no longer authorized."
                        : "This task is linked to a GitHub issue.",
                    TextWrapping = TextWrapping.Wrap,
                },
                new Border
                {
                    Padding = new Thickness(12),
                    CornerRadius = new CornerRadius(6),
                    BorderBrush = (Brush)Application.Current.Resources["OpenzaBorderBrush"],
                    BorderThickness = new Thickness(1),
                    Child = new StackPanel
                    {
                        Spacing = 4,
                        Children =
                        {
                            new TextBlock { Text = link.DisplayName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                            new TextBlock
                            {
                                Text = link.Url,
                                Style = (Style)Application.Current.Resources["OpenzaCaptionTextBlockStyle"],
                                TextWrapping = TextWrapping.Wrap,
                            },
                        },
                    },
                },
                removeButton,
            },
        };

        dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "GitHub issue",
            PrimaryButtonText = issueUnavailable ? "Create issue" : "Open issue",
            SecondaryButtonText = issueUnavailable ? string.Empty : "Replace link",
            CloseButtonText = "Done",
            DefaultButton = ContentDialogButton.Primary,
            Content = panel,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            if (issueUnavailable)
            {
                await CreateGitHubIssueForTaskAsync(task, replaceExistingLink: link).ConfigureAwait(true);
                return;
            }

            if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri))
            {
                await Launcher.LaunchUriAsync(uri);
            }

            return;
        }

        if (result == ContentDialogResult.Secondary)
        {
            await CreateGitHubIssueForTaskAsync(task, replaceExistingLink: link).ConfigureAwait(true);
        }
    }

    private async Task CreateGitHubIssueForTaskAsync(TaskItem task, TaskExternalLinkInfo? replaceExistingLink = null)
    {
        var token = await _credentials.GetAsync(GitHubIssueService.TokenKey).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(token))
        {
            SelectNavigation("settings");
            ShowInfo("Connect GitHub", "Connect GitHub in Settings before creating an issue.", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            var project = GetProject(task.ProjectId);
            var request = await ShowCreateGitHubIssueDialogAsync(token, task, project).ConfigureAwait(true);
            if (request is null)
            {
                return;
            }

            var result = await _gitHubIssueService.CreateIssueAsync(token, request).ConfigureAwait(true);
            if (replaceExistingLink is not null)
            {
                await _store.DeleteTaskExternalLinkAsync(replaceExistingLink.Id).ConfigureAwait(true);
            }

            var metadata = JsonSerializer.Serialize(new
            {
                result.Owner,
                Repository = result.Repository,
                result.Number,
                result.State,
                result.Labels,
                result.Assignees,
            });
            var link = new TaskExternalLinkInfo
            {
                Id = $"github_{Guid.NewGuid():N}",
                TaskId = task.Id,
                IntegrationId = IntegrationIds.GitHub,
                ConnectionId = GitHubIssueService.DefaultConnectionId,
                ExternalId = result.ExternalId,
                Kind = TaskExternalLinkKinds.Issue,
                DisplayName = result.DisplayName,
                Url = result.Url,
                MetadataJson = metadata,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await _store.UpsertTaskExternalLinkAsync(link).ConfigureAwait(true);
            await SaveGitHubDefaultRepositoryAsync($"{request.Owner}/{request.Repository}").ConfigureAwait(true);
            TasksPage.DetailsPanel.SetGitHubLink(link, true);
            ShowInfo("GitHub issue created", $"{result.DisplayName} is linked to this task.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowInfo("GitHub issue failed", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async Task<GitHubIssueCreateRequest?> ShowCreateGitHubIssueDialogAsync(string token, TaskItem task, ProjectItem? project)
    {
        var connections = await _store.GetProviderConnectionsAsync().ConfigureAwait(true);
        var settings = GitHubIssueService.ReadSettings(connections.FirstOrDefault(connection => connection.IntegrationId == IntegrationIds.GitHub)?.SettingsJson);
        var repositories = new List<GitHubRepositoryInfo>();
        var repositorySuggestions = new ObservableCollection<string>();

        var repositoryBox = new AutoSuggestBox
        {
            Header = "Repository",
            PlaceholderText = "Search repositories",
            QueryIcon = new SymbolIcon(Symbol.Find),
            ItemsSource = repositorySuggestions,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var repositoryStatusText = new TextBlock
        {
            Text = "Loading repositories from your GitHub account.",
            Style = (Style)Application.Current.Resources["OpenzaCaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
        GitHubRepositoryInfo? selectedRepository = null;

        void RefreshRepositorySuggestions(IEnumerable<GitHubRepositoryInfo> source)
        {
            repositorySuggestions.Clear();
            foreach (var repository in source.Select(repository => repository.FullName).Take(50))
            {
                repositorySuggestions.Add(repository);
            }
        }

        async Task SearchRepositoriesAsync()
        {
            var text = repositoryBox.Text.Trim();
            var localMatches = FilterRepositories(repositories, text);
            RefreshRepositorySuggestions(localMatches);
        }

        repositoryBox.TextChanged += async (_, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                await SearchRepositoriesAsync().ConfigureAwait(true);
            }
        };
        repositoryBox.SuggestionChosen += (_, args) =>
        {
            if (args.SelectedItem is string fullName &&
                repositories.FirstOrDefault(repository => string.Equals(repository.FullName, fullName, StringComparison.OrdinalIgnoreCase)) is { } repository)
            {
                selectedRepository = repository;
                repositoryBox.Text = repository.FullName;
            }
        };
        repositoryBox.QuerySubmitted += (_, args) =>
        {
            selectedRepository = (args.ChosenSuggestion is string fullName
                    ? repositories.FirstOrDefault(repository => string.Equals(repository.FullName, fullName, StringComparison.OrdinalIgnoreCase))
                    : null)
                ?? repositories.FirstOrDefault(repository => string.Equals(repository.FullName, repositoryBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? selectedRepository;
            if (selectedRepository is not null)
            {
                repositoryBox.Text = selectedRepository.FullName;
            }
        };
        var titleBox = new TextBox
        {
            Header = "Issue title",
            Text = task.Title,
            AcceptsReturn = true,
            MinHeight = 72,
            MaxHeight = 120,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var bodyBox = new TextBox
        {
            Header = "Description",
            Text = GitHubIssueService.BuildIssueBody(task, project),
            AcceptsReturn = true,
            MinHeight = 180,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var labelBox = new ComboBox
        {
            Header = "Label",
            PlaceholderText = "No label",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        async Task LoadLabelsAsync()
        {
            labelBox.ItemsSource = null;
            labelBox.SelectedItem = null;
            if (selectedRepository is null)
            {
                labelBox.ItemsSource = new[] { "No label" };
                labelBox.SelectedIndex = 0;
                return;
            }

            var labels = await _gitHubIssueService.GetLabelsAsync(token, selectedRepository.Owner, selectedRepository.Name).ConfigureAwait(true);
            labelBox.ItemsSource = labels.Select(label => label.Name).Prepend("No label").ToList();
            labelBox.SelectedIndex = 0;
        }

        repositoryBox.QuerySubmitted += async (_, _) =>
        {
            try
            {
                await LoadLabelsAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                AppLog.Write(exception);
            }
        };
        await LoadLabelsAsync().ConfigureAwait(true);

        var panel = new StackPanel
        {
            Spacing = 12,
            Width = 560,
            Children =
            {
                new TextBlock
                {
                    Text = "Create a GitHub issue and keep this Openza task open.",
                    TextWrapping = TextWrapping.Wrap,
                },
                repositoryBox,
                repositoryStatusText,
                titleBox,
                bodyBox,
                labelBox,
            },
        };

        _ = LoadRepositoriesForGitHubDialogAsync(
            token,
            settings.DefaultRepositoryFullName,
            repositories,
            repositoryStatusText,
            () => RefreshRepositorySuggestions(FilterRepositories(repositories, repositoryBox.Text)));

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Create GitHub issue",
            PrimaryButtonText = "Create issue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = panel,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            string.IsNullOrWhiteSpace(titleBox.Text))
        {
            return null;
        }

        selectedRepository = repositories.FirstOrDefault(repository =>
                string.Equals(repository.FullName, repositoryBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? selectedRepository;
        if (selectedRepository is null)
        {
            ShowInfo("Choose a repository", "Choose a repository returned by GitHub for this account.", InfoBarSeverity.Warning);
            return null;
        }

        var selectedLabel = labelBox.SelectedItem?.ToString();
        var labels = string.IsNullOrWhiteSpace(selectedLabel) || selectedLabel == "No label"
            ? []
            : new[] { selectedLabel };
        return new GitHubIssueCreateRequest(
            selectedRepository.Owner,
            selectedRepository.Name,
            titleBox.Text.Trim(),
            bodyBox.Text.Trim(),
            labels,
            []);
    }

    private async Task LoadRepositoriesForGitHubDialogAsync(
        string token,
        string defaultRepositoryFullName,
        List<GitHubRepositoryInfo> repositories,
        TextBlock statusText,
        Action refreshSuggestions)
    {
        try
        {
            var loaded = await _gitHubIssueService.GetRepositoriesAsync(token).ConfigureAwait(true);
            repositories.Clear();
            repositories.AddRange(loaded);
            refreshSuggestions();
            var owners = repositories.Select(repository => repository.Owner).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            statusText.Text = repositories.Count == 0
                ? "No repositories found for this GitHub account."
                : $"{repositories.Count} repositories loaded from {owners.Count} owner{(owners.Count == 1 ? string.Empty : "s")}. Search by owner or repo name.";
            if (owners.Count == 1)
            {
                statusText.Text += " If an organization is missing, approve Openza Tasks for that organization in GitHub, then reconnect.";
            }

            if (!string.IsNullOrWhiteSpace(defaultRepositoryFullName) &&
                repositories.Any(repository => string.Equals(repository.FullName, defaultRepositoryFullName, StringComparison.OrdinalIgnoreCase)))
            {
                statusText.Text += $" Last used: {defaultRepositoryFullName}.";
            }
        }
        catch (Exception exception)
        {
            statusText.Text = "Could not load repositories from GitHub.";
            AppLog.Write(exception);
        }
    }

    private static IReadOnlyList<GitHubRepositoryInfo> FilterRepositories(IReadOnlyList<GitHubRepositoryInfo> repositories, string text)
    {
        var tokens = text
            .Split(' ', '/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return repositories
            .Where(repository => tokens.Length == 0 || tokens.All(token => repository.FullName.Contains(token, StringComparison.CurrentCultureIgnoreCase)))
            .Take(50)
            .ToList();
    }

    private static bool TryParseGitHubIssueExternalId(string externalId, out string owner, out string repository, out int number)
    {
        owner = string.Empty;
        repository = string.Empty;
        number = 0;
        var hashIndex = externalId.LastIndexOf('#');
        var slashIndex = externalId.IndexOf('/');
        if (hashIndex <= 0 || slashIndex <= 0 || slashIndex >= hashIndex || !int.TryParse(externalId[(hashIndex + 1)..], out number))
        {
            return false;
        }

        owner = externalId[..slashIndex];
        repository = externalId[(slashIndex + 1)..hashIndex];
        return !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repository);
    }

    private async Task SaveGitHubDefaultRepositoryAsync(string fullName)
    {
        var connections = await _store.GetProviderConnectionsAsync().ConfigureAwait(true);
        var connection = connections.FirstOrDefault(item => item.IntegrationId == IntegrationIds.GitHub);
        var settings = GitHubIssueService.ReadSettings(connection?.SettingsJson) with
        {
            DefaultRepositoryFullName = fullName,
        };
        await _store.UpsertProviderConnectionAsync((connection ?? new ProviderConnectionInfo
        {
            Id = GitHubIssueService.DefaultConnectionId,
            IntegrationId = IntegrationIds.GitHub,
            DisplayName = "GitHub",
            Status = "connected",
        }) with
        {
            SettingsJson = GitHubIssueService.WriteSettings(settings),
            UpdatedAt = DateTimeOffset.UtcNow,
        }).ConfigureAwait(true);
    }

    private async void OnQuickAddClicked(object sender, RoutedEventArgs e)
    {
        var quickAdd = await TasksPage.ShowQuickAddAsync(DefaultProjectForQuickAdd(), DefaultStatusForCurrentView(), DefaultDateForCurrentView()).ConfigureAwait(true);
        if (quickAdd is null)
        {
            return;
        }

        var task = new TaskItem
        {
            Id = $"local_{Guid.NewGuid():N}",
            SpaceId = _currentSpaceId,
            IntegrationId = IntegrationIds.Local,
            Title = quickAdd.Title,
            Notes = EmptyToNull(quickAdd.Notes),
            ProjectId = quickAdd.ProjectId,
            Priority = quickAdd.Priority,
            PlannedOn = quickAdd.PlannedOn,
            Status = quickAdd.Status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Labels = quickAdd.BuildLabels(_allLabels, IntegrationIds.Local),
        };

        await _store.UpsertTaskAsync(task).ConfigureAwait(true);
        await LoadLabelsAsync().ConfigureAwait(true);
        await LoadProjectsAsync(refreshList: false).ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
        ShowInfo("Task created", task.Title, InfoBarSeverity.Success);

        if (quickAdd.OpenAfterCreate)
        {
            await LoadTaskDetailsAsync(task.Id).ConfigureAwait(true);
        }
    }

    private void OnConnectProvidersClicked(object sender, RoutedEventArgs e)
    {
        SelectNavigation("settings");
    }

    private void OnExploreSyncClicked(object sender, RoutedEventArgs e)
    {
        SelectNavigation("sync");
    }

    private async void OnDismissGetStartedClicked(object sender, RoutedEventArgs e)
    {
        _settings.Settings.ShowGetStarted = false;
        await _settings.SaveAsync().ConfigureAwait(true);
        TasksPage.SetGetStartedVisible(false);
    }

    private readonly record struct TaskDetailsDraft(
        string Snapshot,
        string Title,
        string Notes,
        string LabelsText,
        DateOnly? PlannedOn,
        DateOnly? DeadlineOn,
        string? ProjectId,
        TaskItemStatus Status,
        int Priority,
        string? SourceDateMismatchAcknowledgementKey);

    private TaskDetailsDraft CaptureTaskDetailsDraft()
    {
        var panel = TasksPage.DetailsPanel;
        return new TaskDetailsDraft(
            panel.Snapshot,
            panel.TitleText,
            panel.NotesText,
            panel.LabelsText,
            panel.SelectedPlannedOn,
            panel.SelectedDeadlineOn,
            panel.SelectedProject?.Id,
            panel.SelectedStatus,
            panel.SelectedPriority,
            panel.SourceDateMismatchAcknowledgementKey);
    }

    private async void OnDetailsAutoSaveRequested(object sender, RoutedEventArgs e)
    {
        await SaveTaskDetailsIfNeededAsync(showFeedback: false).ConfigureAwait(true);
    }

    private async Task<bool> SaveTaskDetailsIfNeededAsync(bool showFeedback)
    {
        if (_taskDetailsAutoSaveTask is { IsCompleted: false } currentSave)
        {
            _taskDetailsAutoSaveQueued = TasksPage.IsDetailsPaneOpen && TasksPage.DetailsPanel.HasUnsavedChanges;
            return await currentSave.ConfigureAwait(true);
        }

        if (!TasksPage.IsDetailsPaneOpen || !TasksPage.DetailsPanel.HasUnsavedChanges)
        {
            return true;
        }

        _taskDetailsAutoSaveTask = SaveTaskDetailsLoopAsync(showFeedback);
        try
        {
            return await _taskDetailsAutoSaveTask.ConfigureAwait(true);
        }
        finally
        {
            _taskDetailsAutoSaveTask = null;
        }
    }

    private async Task<bool> SaveTaskDetailsLoopAsync(bool showFeedback)
    {
        do
        {
            _taskDetailsAutoSaveQueued = false;
            if (!TasksPage.IsDetailsPaneOpen || !TasksPage.DetailsPanel.HasUnsavedChanges)
            {
                return true;
            }

            try
            {
                if (!await SaveTaskDetailsAsync(showFeedback).ConfigureAwait(true))
                {
                    return false;
                }
            }
            catch (Exception exception)
            {
                TasksPage.DetailsPanel.SetAutoSaveState("Could not save");
                ShowInfo("Could not save task", exception.Message, InfoBarSeverity.Error);
                return false;
            }

            showFeedback = false;
        }
        while (_taskDetailsAutoSaveQueued || TasksPage.DetailsPanel.HasUnsavedChanges);

        return true;
    }

    private async Task<bool> SaveTaskDetailsAsync(bool showFeedback)
    {
        var draft = CaptureTaskDetailsDraft();
        TaskItem? existing = _selectedTaskId is null ? null : await _store.GetTaskAsync(_selectedTaskId).ConfigureAwait(true);
        if (existing is null && draft.Title.Length == 0)
        {
            TasksPage.DetailsPanel.SetAutoSaveState("Add a title to save");
            if (showFeedback)
            {
                ShowInfo("Title required", "Add a task title before saving.", InfoBarSeverity.Warning);
            }

            return false;
        }

        var integrationId = existing?.IntegrationId ?? IntegrationIds.Local;
        var isLinkedProviderTask = existing?.HasProviderSource == true || existing?.IsProviderTask == true;
        var labelsIntegration = isLinkedProviderTask ? IntegrationIds.Local : integrationId;
        var task = existing is not null
            ? existing with
            {
                Title = existing.IsProviderTask && !existing.HasProviderSource ? existing.Title : draft.Title,
                Status = existing.IsCompleted ? existing.Status : draft.Status,
                ProjectId = draft.ProjectId,
                Priority = draft.Priority,
                PlannedOn = draft.PlannedOn,
                PlannedAt = PreserveExactTime(draft.PlannedOn, existing.PlannedOn, existing.PlannedAt),
                DeadlineOn = draft.DeadlineOn,
                DeadlineAt = PreserveExactTime(draft.DeadlineOn, existing.DeadlineOn, existing.DeadlineAt),
                Notes = EmptyToNull(draft.Notes),
                LocalMetadataJson = WithSourceDateMismatchAcknowledgement(existing.LocalMetadataJson, draft.SourceDateMismatchAcknowledgementKey),
                UpdatedAt = DateTimeOffset.UtcNow,
                Labels = BuildLabels(draft.LabelsText, labelsIntegration),
            }
            : new TaskItem
            {
                Id = existing?.Id ?? $"local_{Guid.NewGuid():N}",
                ExternalId = existing?.ExternalId,
                SpaceId = existing?.SpaceId ?? _currentSpaceId,
                IntegrationId = integrationId,
                Title = draft.Title,
                ProjectId = draft.ProjectId,
                ParentId = existing?.ParentId,
                Priority = draft.Priority,
                Status = existing?.IsCompleted == true ? existing.Status : draft.Status,
                PlannedOn = draft.PlannedOn,
                DeadlineOn = draft.DeadlineOn,
                Notes = EmptyToNull(draft.Notes),
                ProviderMetadataJson = existing?.ProviderMetadataJson,
                LocalMetadataJson = WithSourceDateMismatchAcknowledgement(existing?.LocalMetadataJson, draft.SourceDateMismatchAcknowledgementKey),
                CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                CompletedAt = existing?.CompletedAt,
                Labels = BuildLabels(draft.LabelsText, labelsIntegration),
            };

        TasksPage.DetailsPanel.SetAutoSaveState("Saving...");
        await _store.UpsertTaskAsync(task).ConfigureAwait(true);
        _selectedTaskId = task.Id;
        if (!TasksPage.DetailsPanel.MarkSaved(draft.Snapshot))
        {
            _taskDetailsAutoSaveQueued = true;
        }

        await LoadLabelsAsync().ConfigureAwait(true);
        await LoadProjectsAsync(refreshList: false).ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
        TasksPage.DetailsPanel.SetAutoSaveState("Saved", autoDismiss: true);
        if (showFeedback)
        {
            ShowInfo("Task saved", task.Title, InfoBarSeverity.Success);
        }

        return true;
    }

    private static DateTimeOffset? PreserveExactTime(DateOnly? selectedDate, DateOnly? existingDate, DateTimeOffset? existingDateTime)
    {
        if (selectedDate is null || existingDateTime is null)
        {
            return null;
        }

        var exactDate = TaskDateValues.FromDateTimeOffset(existingDateTime);
        return existingDate == selectedDate && exactDate == selectedDate ? existingDateTime : null;
    }

    private static string? WithSourceDateMismatchAcknowledgement(string? localMetadataJson, string? acknowledgementKey)
    {
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(localMetadataJson)
                ? new JsonObject()
                : JsonNode.Parse(localMetadataJson)?.AsObject() ?? new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }

        var openza = root["openza"] as JsonObject;
        if (openza is null)
        {
            openza = new JsonObject();
            root["openza"] = openza;
        }

        if (string.IsNullOrWhiteSpace(acknowledgementKey))
        {
            openza.Remove("sourceDateMismatchAcknowledgementKey");
        }
        else
        {
            openza["sourceDateMismatchAcknowledgementKey"] = acknowledgementKey;
        }

        if (openza.Count == 0)
        {
            root.Remove("openza");
        }

        return root.Count == 0 ? null : root.ToJsonString();
    }

    private static Dictionary<string, string> BuildSubtaskProgress(IReadOnlyList<TaskItem> tasks)
    {
        return tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.ParentId))
            .GroupBy(task => task.ParentId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var total = group.Count();
                    var completed = group.Count(task => task.IsCompleted);
                    return $"{completed}/{total} subtasks";
                },
                StringComparer.Ordinal);
    }

    private static Dictionary<string, string> BuildMatchingSubtaskText(IReadOnlyList<TaskItem> visibleTasks, string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return [];
        }

        return visibleTasks
            .Where(task => !string.IsNullOrWhiteSpace(task.ParentId))
            .GroupBy(task => task.ParentId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var matches = group
                        .Select(task => task.Title)
                        .Where(title => !string.IsNullOrWhiteSpace(title))
                        .Distinct(StringComparer.CurrentCultureIgnoreCase)
                        .Take(2)
                        .ToList();
                    if (matches.Count == 0)
                    {
                        return string.Empty;
                    }

                    var remaining = Math.Max(0, group.Count() - matches.Count);
                    var suffix = remaining > 0 ? $" +{remaining}" : string.Empty;
                    var prefix = matches.Count == 1 && remaining == 0 ? "Matching subtask" : "Matching subtasks";
                    return $"{prefix}: {string.Join(", ", matches)}{suffix}";
                },
                StringComparer.Ordinal);
    }

    private async void OnToggleCompleteClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id)
        {
            return;
        }

        var showFeedback = true;
        if (TryFindTaskRow(sender as DependencyObject, out var row) &&
            row is not null &&
            row.DataContext is TaskListItemViewModel { IsCompleted: false })
        {
            showFeedback = false;
        }

        await ToggleTaskCompletionAsync(id, showFeedback).ConfigureAwait(true);
    }

    private async void OnTaskRowActionRequested(TasksPage sender, TaskRowActionRequestedEventArgs args)
    {
        if (string.Equals(args.TaskId, _selectedTaskId, StringComparison.Ordinal) &&
            !await SavePendingTaskDetailsAsync().ConfigureAwait(true))
        {
            return;
        }

        switch (args.Action)
        {
            case TaskRowActionKind.SetDate when args.Date is { } date:
                await ApplyRowTaskUpdateAsync(args.TaskId, task => task with
                {
                    PlannedOn = date,
                    PlannedAt = PreserveExactTime(date, task.PlannedOn, task.PlannedAt),
                }).ConfigureAwait(true);
                break;
            case TaskRowActionKind.ClearDate:
                await ApplyRowTaskUpdateAsync(args.TaskId, task => task with
                {
                    PlannedOn = null,
                    PlannedAt = null,
                }).ConfigureAwait(true);
                break;
            case TaskRowActionKind.ChangeProject:
                await ChangeRowTaskProjectAsync(args.TaskId).ConfigureAwait(true);
                break;
            case TaskRowActionKind.ChangeLabels:
                await ChangeRowTaskLabelsAsync(args.TaskId).ConfigureAwait(true);
                break;
            case TaskRowActionKind.SetStatus when args.Status is { } status:
                await ApplyRowTaskUpdateAsync(args.TaskId, task => task.IsCompleted ? task : task with { Status = status }).ConfigureAwait(true);
                break;
            case TaskRowActionKind.SetPriority when args.Priority is { } priority:
                await ApplyRowTaskUpdateAsync(args.TaskId, task => task with { Priority = priority }).ConfigureAwait(true);
                break;
            case TaskRowActionKind.MoveToSpace:
                await MoveTaskToSpaceAsync(args.TaskId).ConfigureAwait(true);
                break;
            case TaskRowActionKind.Delete:
                await DeleteTaskAsync(args.TaskId).ConfigureAwait(true);
                break;
        }
    }

    private async void OnDetailsToggleCompleteClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTaskId is null)
        {
            return;
        }

        await ToggleTaskCompletionAsync(_selectedTaskId, showFeedback: true).ConfigureAwait(true);
    }

    private async Task ToggleTaskCompletionAsync(string id, bool showFeedback)
    {
        var task = await _store.GetTaskAsync(id).ConfigureAwait(true);
        if (task is null)
        {
            return;
        }

        var completed = !task.IsCompleted;
        if ((task.HasProviderSource || task.IsProviderTask) &&
            (!string.IsNullOrWhiteSpace(task.SourceProviderTaskId) || !string.IsNullOrWhiteSpace(task.ExternalId)))
        {
            await _store.QueueCompletionAsync(new PendingCompletion
            {
                Id = $"completion_{task.Id}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
                TaskId = task.Id,
                Provider = task.SourceIntegrationId ?? task.IntegrationId,
                ProviderTaskId = BuildProviderTaskId(task),
                Completed = completed,
                CompletedAt = completed ? DateTimeOffset.UtcNow : null,
                CreatedAt = DateTimeOffset.UtcNow,
            }).ConfigureAwait(true);
        }

        if (completed)
        {
            await _store.CompleteTaskAsync(task.Id).ConfigureAwait(true);
        }
        else
        {
            await _store.ReopenTaskAsync(task.Id).ConfigureAwait(true);
        }

        await LoadProjectsAsync(refreshList: false).ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
        if (string.Equals(_selectedTaskId, id, StringComparison.Ordinal))
        {
            await LoadTaskDetailsAsync(id).ConfigureAwait(true);
        }

        if (showFeedback)
        {
            ShowInfo(
                completed ? "Task completed" : "Task reopened",
                task.Title,
                completed ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
        }
    }

    private static bool TryFindTaskRow(DependencyObject? element, out TaskRowControl? row)
    {
        while (element is not null)
        {
            if (element is TaskRowControl found)
            {
                row = found;
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        row = null;
        return false;
    }

    private async void OnDetailsDeleteTaskClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTaskId is null)
        {
            return;
        }

        await DeleteTaskAsync(_selectedTaskId).ConfigureAwait(true);
    }

    private async void OnDetailsMoveToSpaceClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTaskId is null)
        {
            return;
        }

        if (!await SavePendingTaskDetailsAsync().ConfigureAwait(true))
        {
            return;
        }

        await MoveTaskToSpaceAsync(_selectedTaskId).ConfigureAwait(true);
    }

    private async Task MoveTaskToSpaceAsync(string taskId)
    {
        var task = await _store.GetTaskAsync(taskId).ConfigureAwait(true);
        if (task is null)
        {
            ShowInfo("Task not found", "The selected task no longer exists.", InfoBarSeverity.Warning);
            return;
        }

        var targetSpaces = _spaces
            .Where(space => !string.Equals(space.Id, _currentSpaceId, StringComparison.Ordinal))
            .OrderBy(space => space.SortOrder)
            .ThenBy(space => space.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(space => new MoveSpaceOption(space.Id, space.Name))
            .ToList();
        if (targetSpaces.Count == 0)
        {
            ShowInfo("No other Space", "Create another Space before moving this task.", InfoBarSeverity.Informational);
            return;
        }

        var subtasks = await _store.GetTasksAsync(new TaskQuery
        {
            ParentId = task.Id,
            IncludeSubtasks = true,
            Kind = TaskListKind.All,
        }).ConfigureAwait(true);

        var spacePicker = new ComboBox
        {
            Header = "Move to",
            DisplayMemberPath = nameof(MoveSpaceOption.Name),
            ItemsSource = targetSpaces,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var notes = new List<string>
        {
            "The task will move out of this Space.",
        };
        if (subtasks.Count > 0)
        {
            notes.Add($"{subtasks.Count} subtask{(subtasks.Count == 1 ? string.Empty : "s")} will move with it.");
        }

        if (!string.IsNullOrWhiteSpace(task.ProjectId))
        {
            notes.Add("Its project will be cleared because projects belong to one Space.");
        }

        if (!string.IsNullOrWhiteSpace(task.ParentId))
        {
            notes.Add("Its parent link will be cleared because the parent is staying in this Space.");
        }

        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = string.Join(" ", notes),
                    TextWrapping = TextWrapping.Wrap,
                },
                spacePicker,
            },
        };

        var dialog = new ContentDialog
        {
            Title = "Move to Space",
            Content = content,
            PrimaryButtonText = "Move",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            spacePicker.SelectedItem is not MoveSpaceOption targetSpace)
        {
            return;
        }

        try
        {
            await _store.MoveTaskToSpaceAsync(task.Id, targetSpace.Id).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowInfo("Could not move task", exception.Message, InfoBarSeverity.Error);
            return;
        }

        _selectedTaskId = null;
        _loadedDetailsTaskId = null;
        TasksPage.HideDetailsPane();
        TasksPage.ClearTaskSelection();
        TasksPage.DetailsPanel.ClearForNewTask(GetProject(_selectedProjectId), 3, DefaultStatusForCurrentView());
        await LoadProjectsAsync(refreshList: false).ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
        await RefreshSourceItemsAsync().ConfigureAwait(true);
        await RefreshSettingsSpacesAsync().ConfigureAwait(true);
        ShowInfo("Task moved", $"Moved to {targetSpace.Name}.", InfoBarSeverity.Success);
    }

    private async Task ApplyRowTaskUpdateAsync(string taskId, Func<TaskItem, TaskItem> update)
    {
        var existing = await _store.GetTaskAsync(taskId).ConfigureAwait(true);
        if (existing is null)
        {
            ShowInfo("Task not found", "The selected task no longer exists.", InfoBarSeverity.Warning);
            return;
        }

        var updated = update(existing) with { UpdatedAt = DateTimeOffset.UtcNow };
        await _store.UpsertTaskAsync(updated).ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
        if (string.Equals(_selectedTaskId, taskId, StringComparison.Ordinal))
        {
            await LoadTaskDetailsAsync(taskId).ConfigureAwait(true);
        }
    }

    private async Task ChangeRowTaskProjectAsync(string taskId)
    {
        var task = await _store.GetTaskAsync(taskId).ConfigureAwait(true);
        if (task is null)
        {
            ShowInfo("Task not found", "The selected task no longer exists.", InfoBarSeverity.Warning);
            return;
        }

        var inboxProject = new ProjectItem
        {
            Id = string.Empty,
            Name = "No project",
            IntegrationId = IntegrationIds.Local,
        };
        var projectOptions = new List<ProjectItem> { inboxProject };
        projectOptions.AddRange(_allProjects.Where(project => project.EffectiveStatus != ProjectLifecycleStates.Archived));
        var projectBox = new ComboBox
        {
            Header = "Project",
            DisplayMemberPath = "Name",
            ItemsSource = projectOptions,
            SelectedItem = projectOptions.FirstOrDefault(project => string.Equals(project.Id, task.ProjectId ?? string.Empty, StringComparison.Ordinal)) ?? inboxProject,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var dialog = new ContentDialog
        {
            Title = "Change project",
            Content = projectBox,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            projectBox.SelectedItem is not ProjectItem selectedProject)
        {
            return;
        }

        await ApplyRowTaskUpdateAsync(taskId, item => item with
        {
            ProjectId = string.IsNullOrWhiteSpace(selectedProject.Id) ? null : selectedProject.Id,
        }).ConfigureAwait(true);
    }

    private async Task ChangeRowTaskLabelsAsync(string taskId)
    {
        var task = await _store.GetTaskAsync(taskId).ConfigureAwait(true);
        if (task is null)
        {
            ShowInfo("Task not found", "The selected task no longer exists.", InfoBarSeverity.Warning);
            return;
        }

        var selectedIds = task.Labels.Select(label => label.Id).ToHashSet(StringComparer.Ordinal);
        var labelChecks = _allLabels
            .OrderBy(label => label.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(label => new CheckBox
            {
                Content = label.Name,
                Tag = label,
                IsChecked = selectedIds.Contains(label.Id),
            })
            .ToList();
        var panel = new StackPanel { Spacing = 6 };
        foreach (var check in labelChecks)
        {
            panel.Children.Add(check);
        }

        var scrollViewer = new ScrollViewer
        {
            Content = labelChecks.Count == 0
                ? new TextBlock { Text = "No labels exist yet. Create labels from the task details pane first.", TextWrapping = TextWrapping.Wrap }
                : panel,
            MaxHeight = 360,
        };
        var dialog = new ContentDialog
        {
            Title = "Change labels",
            Content = scrollViewer,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            IsPrimaryButtonEnabled = labelChecks.Count > 0,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var labels = labelChecks
            .Where(check => check.IsChecked == true)
            .Select(check => (LabelItem)check.Tag)
            .ToList();
        await LoadLabelsAsync().ConfigureAwait(true);
        await ApplyRowTaskUpdateAsync(taskId, item => item with { Labels = labels }).ConfigureAwait(true);
    }

    private async Task DeleteTaskAsync(string id)
    {
        var task = await _store.GetTaskAsync(id).ConfigureAwait(true);
        if (task is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Delete task",
            Content = task.Title,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _store.DeleteTaskAsync(id).ConfigureAwait(true);
        }
        catch (ProviderLinkedTaskDeleteException exception)
        {
            ShowInfo(
                "Task is linked",
                exception.Message,
                InfoBarSeverity.Warning);
            return;
        }
        catch (Exception exception)
        {
            ShowInfo(
                "Could not delete task",
                exception.Message,
                InfoBarSeverity.Error);
            return;
        }

        if (_selectedTaskId == id)
        {
            _selectedTaskId = null;
            _loadedDetailsTaskId = null;
            TasksPage.HideDetailsPane();
            TasksPage.ClearTaskSelection();
            TasksPage.DetailsPanel.ClearForNewTask(null, 3, DefaultStatusForCurrentView());
        }

        await LoadProjectsAsync(refreshList: false).ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
    }

    private async void OnDetailsCancelEditClicked(object sender, RoutedEventArgs e)
    {
        if (!await SavePendingTaskDetailsAsync().ConfigureAwait(true))
        {
            return;
        }

        _selectedTaskId = null;
        _loadedDetailsTaskId = null;
        TasksPage.HideDetailsPane();
        TasksPage.ClearTaskSelection();
        TasksPage.DetailsPanel.ClearForNewTask(null, 3, DefaultStatusForCurrentView());
    }

    private ProjectItem? DefaultProjectForQuickAdd() =>
        string.Equals(_currentView, "tasks", StringComparison.Ordinal)
            ? GetProject(_selectedProjectId)
            : null;

    private TaskItemStatus DefaultStatusForCurrentView() => _currentView switch
    {
        "next" => TaskItemStatus.Next,
        "waiting" => TaskItemStatus.Waiting,
        "someday" => TaskItemStatus.Someday,
        _ => TaskItemStatus.Inbox,
    };

    private DateTimeOffset? DefaultDateForCurrentView() =>
        string.Equals(_currentView, "today", StringComparison.Ordinal)
            ? DateTimeOffset.Now.Date
            : null;

    private sealed record MoveSpaceOption(string Id, string Name);

    private async Task<bool> SavePendingTaskDetailsAsync()
    {
        if (!TasksPage.IsDetailsPaneOpen)
        {
            return true;
        }

        TasksPage.DetailsPanel.StopPendingAutoSave();
        return await SaveTaskDetailsIfNeededAsync(showFeedback: false).ConfigureAwait(true);
    }

    private IReadOnlyList<LabelItem> BuildLabels(string labelText, string integrationId)
    {
        return labelText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Select(name =>
                _allLabels.FirstOrDefault(label =>
                    string.Equals(label.Name, name, StringComparison.CurrentCultureIgnoreCase) &&
                    string.Equals(label.IntegrationId, integrationId, StringComparison.Ordinal)) ??
                _allLabels.FirstOrDefault(label =>
                    string.Equals(label.Name, name, StringComparison.CurrentCultureIgnoreCase)) ??
                new LabelItem
                {
                    Id = $"label_{Guid.NewGuid():N}",
                    IntegrationId = integrationId,
                    Name = name,
                    Color = "#808080",
                    CreatedAt = DateTimeOffset.UtcNow,
                })
            .ToList();
    }

    private static string BuildProviderTaskId(TaskItem task)
    {
        if (!string.IsNullOrWhiteSpace(task.SourceProviderTaskId))
        {
            return task.SourceProviderTaskId;
        }

        if (task.IntegrationId == IntegrationIds.MicrosoftToDo && task.ProjectId?.StartsWith("mstodo_", StringComparison.Ordinal) == true)
        {
            return $"{task.ProjectId["mstodo_".Length..]}|{task.ExternalId}";
        }

        return task.ExternalId ?? task.Id;
    }
}
