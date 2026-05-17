using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Services;
using Openza.Tasks.Core.Sync;
using Openza.Tasks.Pages;
using Openza.Tasks.Services;
using Openza.Tasks.ViewModels;
using Windows.Foundation;
using Windows.System;

namespace Openza.Tasks.Shell;

public sealed partial class AppShell : UserControl
{
    private const string TodoistTokenKey = "todoist.accessToken";
    private const int DefaultAutoSyncIntervalMinutes = 5;

    private readonly ITaskStore _store;
    private readonly TaskSyncEngine _syncEngine;
    private readonly ICredentialStore _credentials;
    private readonly BackupService _backupService;
    private readonly AppSettingsService _settings;
    private readonly MicrosoftToDoAuthService _microsoftAuth;
    private readonly Window _ownerWindow;
    private readonly HttpClient _httpClient = new();
    private readonly List<SpaceItem> _spaces = [];
    private readonly List<ProjectItem> _allProjects = [];
    private readonly List<LabelItem> _allLabels = [];
    private readonly Dictionary<string, string> _projectIdByName = new(StringComparer.Ordinal);
    private string _currentView = "inbox";
    private string _currentSpaceId = SpaceIds.Default;
    private string? _selectedTaskId;
    private string? _selectedProjectId;
    private string? _pendingProjectActionId;
    private string _projectFilter = "all";
    private TaskSortMode _sortMode = TaskSortMode.PriorityThenDate;
    private TaskGroupMode _groupMode = TaskGroupMode.None;
    private int? _priorityFilter;
    private TaskDateScope _dateScopeFilter = TaskDateScope.All;
    private TaskRepeatScope _repeatScopeFilter = TaskRepeatScope.Include;
    private string? _labelFilterId;
    private DispatcherTimer? _statusInfoTimer;
    private DispatcherTimer? _autoSyncTimer;
    private bool _syncInProgress;
    private bool _uiReady;
    private bool _suppressNavigationSelection;
    private bool _suppressSettingsEvents;
    private bool _deferNavigationCountUpdates;
    private bool _taskDetailsAutoSaveQueued;
    private Task<bool>? _taskDetailsAutoSaveTask;

    public string CurrentView => _currentView;

    public AppShell(
        Window ownerWindow,
        ITaskStore store,
        TaskSyncEngine syncEngine,
        ICredentialStore credentials,
        BackupService backupService,
        AppSettingsService settings,
        MicrosoftToDoAuthService microsoftAuth)
    {
        _ownerWindow = ownerWindow;
        _store = store;
        _syncEngine = syncEngine;
        _credentials = credentials;
        _backupService = backupService;
        _settings = settings;
        _microsoftAuth = microsoftAuth;
        InitializeComponent();
        ShellNav.ItemInvoked += OnNavigationItemInvoked;
        AddKeyboardShortcuts();
        _uiReady = true;
        UpdatePageWorkspaceWidths();
    }

    public async Task InitializeAsync()
    {
        _suppressSettingsEvents = true;
        SettingsPage.SelectTheme(_settings.Settings.Theme);
        SettingsPage.AutoBackupEnabled = _settings.Settings.AutoBackupEnabled;
        SettingsPage.AutoSyncEnabled = _settings.Settings.AutoSyncEnabled;
        SettingsPage.SetMicrosoftConfig(GetMicrosoftClientId(), GetMicrosoftTenantId());
        _suppressSettingsEvents = false;
        ApplyTheme(_settings.Settings.Theme);
        if (_settings.Settings.AutoBackupEnabled)
        {
            await TryCreateStartupBackupAsync().ConfigureAwait(true);
        }

        _deferNavigationCountUpdates = _settings.Settings.AutoSyncEnabled;
        try
        {
            await LoadSpacesAsync().ConfigureAwait(true);
            await LoadProjectsAsync().ConfigureAwait(true);
            await LoadLabelsAsync().ConfigureAwait(true);
            await RefreshSettingsStateAsync().ConfigureAwait(true);
            if (_settings.Settings.AutoSyncEnabled)
            {
                await RunAutomaticSyncAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            _deferNavigationCountUpdates = false;
        }

        await RefreshProjectListAsync().ConfigureAwait(true);
        var counts = await _store.GetTaskCountsAsync(_currentSpaceId).ConfigureAwait(true);
        var startView = _settings.Settings.ShowGetStarted && counts.All == 0
            ? "inbox"
            : _settings.Settings.LastView;
        SelectNavigation(startView);
        if (IsTaskView(_currentView))
        {
            await RefreshTasksAsync().ConfigureAwait(true);
        }

        StartAutoSyncTimer();
    }

    private void SelectNavigation(string tag)
    {
        foreach (var item in GetNavigationItems())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                ShellNav.SelectedItem = item;
                _currentView = tag;
                ApplyTaskViewPreferences();
                ApplyWorkspaceMode();
                return;
            }
        }

        ShellNav.SelectedItem = ShellNav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault();
        ApplyTaskViewPreferences();
        ApplyWorkspaceMode();
    }

    private void SelectNavigationSilently(string tag)
    {
        _suppressNavigationSelection = true;
        SelectNavigation(tag);
        _suppressNavigationSelection = false;
    }

    private async void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_uiReady || _suppressNavigationSelection)
        {
            return;
        }

        if (args.SelectedItem is not NavigationViewItem item || item.Tag is null)
        {
            return;
        }

        var nextView = item.Tag.ToString() ?? "next";
        if (string.Equals(nextView, "add-task", StringComparison.Ordinal))
        {
            var targetView = IsTaskView(_currentView) ? _currentView : "inbox";
            SelectNavigationSilently(targetView);
            OnQuickAddClicked(sender, new RoutedEventArgs());
            return;
        }

        if (!string.Equals(nextView, _currentView, StringComparison.Ordinal) &&
            !await SavePendingTaskDetailsAsync().ConfigureAwait(true))
        {
            SelectNavigationSilently(_currentView);
            return;
        }

        if (!string.Equals(nextView, _currentView, StringComparison.Ordinal) &&
            (TasksPage.IsDetailsPaneOpen || TasksPage.IsConnectedTasksDrawerOpen))
        {
            _selectedTaskId = null;
            TasksPage.HideDetailsPane();
            TasksPage.HideConnectedTasksDrawer();
            TasksPage.ClearTaskSelection();
            TasksPage.DetailsPanel.ClearForNewTask(null, 3, DefaultStatusForCurrentView());
        }

        var projectSelectionChanged = false;
        if (!string.Equals(nextView, "tasks", StringComparison.Ordinal) && _selectedProjectId is not null)
        {
            _selectedProjectId = null;
            projectSelectionChanged = true;
        }

        _currentView = nextView;
        ApplyTaskViewPreferences();
        if (!IsTaskView(_currentView))
        {
            _selectedTaskId = null;
            TasksPage.HideDetailsPane();
            TasksPage.HideConnectedTasksDrawer();
            TasksPage.ClearTaskSelection();
            TasksPage.DetailsPanel.ClearForNewTask(null, 3, DefaultStatusForCurrentView());
        }

        ApplyWorkspaceMode();
        if (projectSelectionChanged)
        {
            await RefreshProjectListAsync().ConfigureAwait(true);
        }

        if (IsTaskView(_currentView))
        {
            await RefreshTasksAsync().ConfigureAwait(true);
        }
    }

    private void OnNavigationItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem { Tag: "add-task" })
        {
            return;
        }

        var targetView = IsTaskView(_currentView) ? _currentView : "inbox";
        SelectNavigationSilently(targetView);
        OnQuickAddClicked(sender, new RoutedEventArgs());
    }

    private IEnumerable<NavigationViewItem> GetNavigationItems() =>
        ShellNav.MenuItems.OfType<NavigationViewItem>()
            .Concat(ShellNav.FooterMenuItems.OfType<NavigationViewItem>());

    private void ApplyWorkspaceMode()
    {
        var settingsVisible = _currentView == "settings";
        var syncVisible = _currentView == "sync";
        var taskVisible = IsTaskView(_currentView);
        TasksPage.SetProjectsPaneEnabled(_currentView == "tasks");
        TasksPage.Visibility = taskVisible ? Visibility.Visible : Visibility.Collapsed;
        SettingsWorkspace.Visibility = settingsVisible ? Visibility.Visible : Visibility.Collapsed;
        SyncWorkspace.Visibility = syncVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsTaskView(string view) => view is "inbox" or "next" or "today" or "calendar" or "overdue" or "waiting" or "someday" or "tasks" or "completed";

    private void ApplyTaskViewPreferences()
    {
        if (!IsTaskView(_currentView))
        {
            return;
        }

        var viewSettings = ResolveTaskViewSettings();
        _sortMode = ParseEnum(viewSettings.SortMode, TaskSortMode.PriorityThenDate);
        _groupMode = ParseEnum(viewSettings.GroupMode, DefaultGroupModeForView(_currentView));
        _priorityFilter = viewSettings.Priority;
        _dateScopeFilter = TaskDateScope.All;
        _repeatScopeFilter = ParseEnum(viewSettings.RepeatScope, TaskRepeatScope.Include);
        _labelFilterId = string.IsNullOrWhiteSpace(viewSettings.LabelId)
            ? null
            : viewSettings.LabelId;
        if (_labelFilterId is not null &&
            _allLabels.Count > 0 &&
            _allLabels.All(label => !string.Equals(label.Id, _labelFilterId, StringComparison.Ordinal)))
        {
            _labelFilterId = null;
        }

        TasksPage.SetSortMode(_sortMode);
        TasksPage.SetGroupMode(_groupMode);
        TasksPage.SetPriorityFilter(_priorityFilter);
        TasksPage.SetDateScopeFilter(_dateScopeFilter);
        TasksPage.SetRepeatScopeFilter(_repeatScopeFilter);
        TasksPage.SetLabelFilter(_labelFilterId);
    }

    private TaskViewSettings ResolveTaskViewSettings()
    {
        var key = TaskViewSettingsKey();
        if (_settings.Settings.TaskViewSettings.TryGetValue(key, out var stored))
        {
            return stored;
        }

        var groupMode = DefaultGroupModeForView(_currentView);
        if (_settings.Settings.TaskGroupModes.TryGetValue(_currentView, out var oldStoredGroup) &&
            Enum.TryParse<TaskGroupMode>(oldStoredGroup, ignoreCase: true, out var oldGroupMode))
        {
            groupMode = oldGroupMode;
        }

        return new TaskViewSettings
        {
            SortMode = TaskSortMode.PriorityThenDate.ToString(),
            GroupMode = groupMode.ToString(),
            DateScope = TaskDateScope.All.ToString(),
            RepeatScope = TaskRepeatScope.Include.ToString(),
        };
    }

    private void ApplyViewControlsFromPage()
    {
        _sortMode = TasksPage.SortTag switch
        {
            "date" or "due" => TaskSortMode.Date,
            "created" => TaskSortMode.CreatedNewest,
            "title" => TaskSortMode.Title,
            _ => TaskSortMode.PriorityThenDate,
        };
        _groupMode = TasksPage.GroupMode;
        _priorityFilter = TasksPage.PriorityFilter;
        _dateScopeFilter = TasksPage.DateScopeFilter;
        _repeatScopeFilter = TasksPage.RepeatScopeFilter;
        _labelFilterId = TasksPage.LabelFilterId;
    }

    private async Task SaveTaskViewPreferencesAsync()
    {
        if (!IsTaskView(_currentView))
        {
            return;
        }

        _settings.Settings.TaskViewSettings[TaskViewSettingsKey()] = new TaskViewSettings
        {
            SortMode = _sortMode.ToString(),
            GroupMode = _groupMode.ToString(),
            Priority = _priorityFilter,
            DateScope = _dateScopeFilter.ToString(),
            RepeatScope = _repeatScopeFilter.ToString(),
            LabelId = _labelFilterId,
        };
        _settings.Settings.TaskGroupModes[_currentView] = _groupMode.ToString();
        await _settings.SaveAsync().ConfigureAwait(true);
    }

    private string TaskViewSettingsKey()
    {
        var projectPart = string.Equals(_currentView, "tasks", StringComparison.Ordinal)
            ? _selectedProjectId ?? "all"
            : "all";
        return $"{_currentSpaceId}|{_currentView}|{projectPart}";
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct, Enum =>
        !string.IsNullOrWhiteSpace(value) && Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;

    private static TaskGroupMode DefaultGroupModeForView(string view) => view switch
    {
        "overdue" or "calendar" => TaskGroupMode.Date,
        "completed" => TaskGroupMode.CompletedDate,
        "tasks" => TaskGroupMode.Project,
        _ => TaskGroupMode.None,
    };

    private void OnOpenSettingsFromSyncClicked(object sender, RoutedEventArgs e)
    {
        SelectNavigation("settings");
    }

    private void OnOpenInboxFromSyncClicked(object sender, RoutedEventArgs e)
    {
        SelectNavigation("inbox");
    }

    private void OnAddTaskPointerEntered(object sender, PointerRoutedEventArgs e) =>
        SetAddTaskBackground("OpenzaAccentHoverBrush");

    private void OnAddTaskPointerExited(object sender, PointerRoutedEventArgs e) =>
        SetAddTaskBackground("OpenzaAccentBrush");

    private void OnAddTaskPointerPressed(object sender, PointerRoutedEventArgs e) =>
        SetAddTaskBackground("OpenzaAccentPressedBrush");

    private void SetAddTaskBackground(string brushKey)
    {
        if (Application.Current.Resources[brushKey] is Brush brush)
        {
            AddTaskNavItem.Background = brush;
        }
    }

    private void OnPageWorkspaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePageWorkspaceWidths();
    }

    private void UpdatePageWorkspaceWidths()
    {
        const double horizontalPadding = 80;
        var availableWidth = Math.Max(0, ActualWidth - ShellNav.OpenPaneLength - horizontalPadding);
        var pageWidth = Math.Min(1040, availableWidth);

        if (pageWidth <= 0)
        {
            return;
        }

        SettingsPage.Width = pageWidth;
        SyncPage.Width = Math.Min(920, pageWidth);
    }

    private async void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsEvents)
        {
            return;
        }

        var theme = SettingsPage.SelectedTheme;
        _settings.Settings.Theme = theme;
        ApplyTheme(theme);
        await _settings.SaveAsync().ConfigureAwait(true);
    }

    private void ApplyTheme(string theme)
    {
        RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private async Task LoadSpacesAsync(string? preferredSpaceId = null)
    {
        _spaces.Clear();
        _spaces.AddRange(await _store.GetSpacesAsync().ConfigureAwait(true));
        if (_spaces.Count == 0)
        {
            var defaultSpace = new SpaceItem
            {
                Id = SpaceIds.Default,
                Name = "My space",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await _store.UpsertSpaceAsync(defaultSpace).ConfigureAwait(true);
            _spaces.Add(defaultSpace);
        }

        var selected = _spaces.FirstOrDefault(space => space.Id == preferredSpaceId) ??
            _spaces.FirstOrDefault(space => space.Id == _settings.Settings.LastSpaceId) ??
            _spaces.FirstOrDefault(space => space.Id == _currentSpaceId) ??
            _spaces.First();
        _currentSpaceId = selected.Id;
        SpaceSelector.ItemsSource = null;
        SpaceSelector.ItemsSource = _spaces;
        SpaceSelector.SelectedItem = selected;
        await RefreshSettingsSpacesAsync().ConfigureAwait(true);
    }

    private async Task RefreshSettingsSpacesAsync()
    {
        var activeSpaceCount = _spaces.Count;
        var items = new List<SpaceSettingsItemViewModel>();
        foreach (var space in _spaces.OrderBy(item => item.SortOrder).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var taskCount = (await _store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All, SpaceId = space.Id }).ConfigureAwait(true)).Count;
            var projectCount = (await _store.GetProjectsAsync(space.Id, includeArchived: true).ConfigureAwait(true)).Count;
            var isCurrent = string.Equals(space.Id, _currentSpaceId, StringComparison.Ordinal);
            items.Add(new SpaceSettingsItemViewModel
            {
                Id = space.Id,
                Name = space.Name,
                DetailText = $"{taskCount} task{(taskCount == 1 ? string.Empty : "s")} · {projectCount} project{(projectCount == 1 ? string.Empty : "s")}",
                IsCurrent = isCurrent,
                CanArchive = activeSpaceCount > 1,
            });
        }

        SettingsPage.SetSpaces(items);
    }

    private bool ValidateSpaceName(string name, string? currentSpaceId, out string message)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            message = "Enter a space name.";
            return false;
        }

        if (_spaces.Any(space =>
                !string.Equals(space.Id, currentSpaceId, StringComparison.Ordinal) &&
                string.Equals(space.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            message = "A space with this name already exists.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private async void OnAddSpaceClicked(object sender, RoutedEventArgs e)
    {
        var name = SettingsPage.NewSpaceName;
        if (!ValidateSpaceName(name, null, out var message))
        {
            SettingsPage.ShowSpacesMessage("Cannot add space", message, InfoBarSeverity.Warning);
            return;
        }

        await _store.UpsertSpaceAsync(new SpaceItem
        {
            Id = $"space_{Guid.NewGuid():N}",
            Name = name,
            SortOrder = _spaces.Count == 0 ? 0 : _spaces.Max(space => space.SortOrder) + 1,
            CreatedAt = DateTimeOffset.UtcNow,
        }).ConfigureAwait(true);
        SettingsPage.ClearNewSpaceName();
        await LoadSpacesAsync(_currentSpaceId).ConfigureAwait(true);
        SettingsPage.ShowSpacesMessage("Space added", $"{name} is now available in the space picker.", InfoBarSeverity.Success);
    }

    private async void OnRenameSpaceClicked(SettingsPage sender, string id)
    {
        var edited = SettingsPage.GetSpace(id);
        var existing = _spaces.FirstOrDefault(space => string.Equals(space.Id, id, StringComparison.Ordinal));
        if (edited is null || existing is null)
        {
            SettingsPage.ShowSpacesMessage("Cannot rename space", "The selected space no longer exists.", InfoBarSeverity.Warning);
            return;
        }

        var name = edited.Name.Trim();
        if (!ValidateSpaceName(name, id, out var message))
        {
            SettingsPage.ShowSpacesMessage("Cannot rename space", message, InfoBarSeverity.Warning);
            return;
        }

        await _store.UpsertSpaceAsync(existing with
        {
            Name = name,
            UpdatedAt = DateTimeOffset.UtcNow,
        }).ConfigureAwait(true);
        await LoadSpacesAsync(_currentSpaceId).ConfigureAwait(true);
        SettingsPage.ShowSpacesMessage("Space renamed", name, InfoBarSeverity.Success);
    }

    private async void OnArchiveSpaceClicked(SettingsPage sender, string id)
    {
        var space = _spaces.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (space is null)
        {
            SettingsPage.ShowSpacesMessage("Cannot archive space", "The selected space no longer exists.", InfoBarSeverity.Warning);
            return;
        }

        if (_spaces.Count <= 1)
        {
            SettingsPage.ShowSpacesMessage("Cannot archive space", "At least one active space is required.", InfoBarSeverity.Warning);
            return;
        }

        var isCurrent = string.Equals(space.Id, _currentSpaceId, StringComparison.Ordinal);
        if (isCurrent && !await SavePendingTaskDetailsAsync().ConfigureAwait(true))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Archive space",
            Content = $"{space.Name} will be hidden from the space picker. Its tasks and projects stay in the database.",
            PrimaryButtonText = "Archive",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var nextSpaceId = isCurrent
            ? _spaces.First(item => item.Id != space.Id).Id
            : _currentSpaceId;
        await _store.UpsertSpaceAsync(space with
        {
            IsArchived = true,
            UpdatedAt = DateTimeOffset.UtcNow,
        }).ConfigureAwait(true);
        _settings.Settings.LastSpaceId = nextSpaceId;
        await _settings.SaveAsync().ConfigureAwait(true);
        await LoadSpacesAsync(nextSpaceId).ConfigureAwait(true);
        await LoadProjectsAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
        await RefreshSourceItemsAsync().ConfigureAwait(true);
        SettingsPage.ShowSpacesMessage("Space archived", $"{space.Name} is hidden but its data is kept.", InfoBarSeverity.Success);
    }

    private async void OnSpaceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || SpaceSelector.SelectedItem is not SpaceItem space || space.Id == _currentSpaceId)
        {
            return;
        }

        if (!await SavePendingTaskDetailsAsync().ConfigureAwait(true))
        {
            SpaceSelector.SelectedItem = _spaces.FirstOrDefault(item => item.Id == _currentSpaceId);
            return;
        }

        _currentSpaceId = space.Id;
        _settings.Settings.LastSpaceId = space.Id;
        await _settings.SaveAsync().ConfigureAwait(true);
        _selectedTaskId = null;
        _selectedProjectId = null;
        ApplyTaskViewPreferences();
        TasksPage.HideDetailsPane();
        TasksPage.HideConnectedTasksDrawer();
        TasksPage.ClearTaskSelection();
        await LoadProjectsAsync().ConfigureAwait(true);
        await RefreshTasksAsync().ConfigureAwait(true);
        await RefreshSourceItemsAsync().ConfigureAwait(true);
    }

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        _statusInfoTimer?.Stop();
        StatusInfo.Title = title;
        StatusInfo.Message = message;
        StatusInfo.Severity = severity;
        ApplyStatusInfoVisuals(severity);
        StatusInfo.IsOpen = true;

        if (severity is not (InfoBarSeverity.Success or InfoBarSeverity.Informational))
        {
            return;
        }

        _statusInfoTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _statusInfoTimer.Tick += (_, _) =>
        {
            _statusInfoTimer?.Stop();
            StatusInfo.IsOpen = false;
        };
        _statusInfoTimer.Start();
    }

    private void ApplyStatusInfoVisuals(InfoBarSeverity severity)
    {
        var brushKey = severity switch
        {
            InfoBarSeverity.Success => "OpenzaToastSuccessBrush",
            InfoBarSeverity.Warning => "OpenzaToastWarningBrush",
            InfoBarSeverity.Error => "OpenzaToastErrorBrush",
            _ => "OpenzaToastInfoBrush",
        };

        if (Application.Current.Resources[brushKey] is Brush background)
        {
            StatusInfo.Background = background;
            StatusInfo.BorderBrush = background;
        }

        if (Application.Current.Resources["OpenzaToastForegroundBrush"] is Brush foreground)
        {
            StatusInfo.Foreground = foreground;
        }
    }

    private static string? EmptyToNull(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string SourceName(string integrationId) => integrationId switch
    {
        IntegrationIds.Todoist => "Todoist",
        IntegrationIds.MicrosoftToDo => "Microsoft To Do",
        IntegrationIds.Obsidian => "Obsidian",
        _ => "Openza Tasks",
    };

    private string GetMicrosoftClientId() =>
        string.IsNullOrWhiteSpace(_settings.Settings.MicrosoftToDoClientId)
            ? MicrosoftToDoAuthService.ResolveDefaultClientId()
            : _settings.Settings.MicrosoftToDoClientId.Trim();

    private string GetMicrosoftTenantId() =>
        string.IsNullOrWhiteSpace(_settings.Settings.MicrosoftToDoTenantId)
            ? MicrosoftToDoAuthService.ResolveDefaultTenantId()
            : _settings.Settings.MicrosoftToDoTenantId.Trim();

    private void AddKeyboardShortcuts()
    {
        AddKeyboardShortcut(VirtualKey.N, VirtualKeyModifiers.Control, (_, args) =>
        {
            if (!IsTaskView(_currentView))
            {
                SelectNavigation("inbox");
            }

            OnQuickAddClicked(this, new RoutedEventArgs());
            args.Handled = true;
        });
        AddKeyboardShortcut(VirtualKey.F, VirtualKeyModifiers.Control, (_, args) =>
        {
            if (IsTaskView(_currentView))
            {
                TasksPage.FocusSearch();
            }

            args.Handled = true;
        });
        AddKeyboardShortcut(VirtualKey.S, VirtualKeyModifiers.Control, async (_, args) =>
        {
            if (IsTaskView(_currentView) && TasksPage.IsDetailsPaneOpen)
            {
                TasksPage.DetailsPanel.StopPendingAutoSave();
                await SaveTaskDetailsIfNeededAsync(showFeedback: true).ConfigureAwait(true);
            }
            else
            {
                OnSyncClicked(this, new RoutedEventArgs());
            }

            args.Handled = true;
        });
        AddKeyboardShortcut(VirtualKey.Escape, VirtualKeyModifiers.None, async (_, args) =>
        {
            if (IsTaskView(_currentView))
            {
                if (!await SavePendingTaskDetailsAsync().ConfigureAwait(true))
                {
                    args.Handled = true;
                    return;
                }

                TasksPage.SearchText = string.Empty;
                _selectedTaskId = null;
                TasksPage.HideDetailsPane();
                TasksPage.HideConnectedTasksDrawer();
                TasksPage.ClearTaskSelection();
                TasksPage.DetailsPanel.ClearForNewTask(null, 3, DefaultStatusForCurrentView());
            }

            args.Handled = true;
        });
    }

    private void AddKeyboardShortcut(VirtualKey key, VirtualKeyModifiers modifiers, TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += handler;
        KeyboardAccelerators.Add(accelerator);
    }
}
