using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Migration;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Services;
using Openza.Tasks.Core.Sync;
using Openza.Tasks.Services;
using Windows.Foundation;
using Windows.System;

namespace Openza.Tasks.Shell;

public sealed partial class AppShell : UserControl
{
    private const string TodoistTokenKey = "todoist.accessToken";

    private readonly ITaskStore _store;
    private readonly TaskSyncEngine _syncEngine;
    private readonly ICredentialStore _credentials;
    private readonly BackupService _backupService;
    private readonly AppSettingsService _settings;
    private readonly MicrosoftToDoAuthService _microsoftAuth;
    private readonly MigrationResult _migration;
    private readonly Window _ownerWindow;
    private readonly HttpClient _httpClient = new();
    private readonly List<ProjectItem> _allProjects = [];
    private readonly List<LabelItem> _allLabels = [];
    private readonly Dictionary<string, string> _projectIdByName = new(StringComparer.Ordinal);
    private string _currentView = "inbox";
    private string? _selectedTaskId;
    private string? _selectedProjectId;
    private string? _pendingProjectActionId;
    private TaskSortMode _sortMode = TaskSortMode.PriorityThenDueDate;
    private int? _priorityFilter;
    private string? _labelFilterId;
    private bool _uiReady;
    private bool _suppressNavigationSelection;

    public string CurrentView => _currentView;

    public AppShell(
        Window ownerWindow,
        ITaskStore store,
        TaskSyncEngine syncEngine,
        ICredentialStore credentials,
        BackupService backupService,
        AppSettingsService settings,
        MicrosoftToDoAuthService microsoftAuth,
        MigrationResult migration)
    {
        _ownerWindow = ownerWindow;
        _store = store;
        _syncEngine = syncEngine;
        _credentials = credentials;
        _backupService = backupService;
        _settings = settings;
        _microsoftAuth = microsoftAuth;
        _migration = migration;
        InitializeComponent();
        AddKeyboardShortcuts();
        _uiReady = true;
        UpdatePageWorkspaceWidths();
    }

    public async Task InitializeAsync()
    {
        SettingsPage.SelectTheme(_settings.Settings.Theme);
        SettingsPage.AutoBackupEnabled = _settings.Settings.AutoBackupEnabled;
        SettingsPage.SetMicrosoftConfig(GetMicrosoftClientId(), GetMicrosoftTenantId());
        ApplyTheme(_settings.Settings.Theme);
        if (_settings.Settings.AutoBackupEnabled)
        {
            await TryCreateStartupBackupAsync().ConfigureAwait(true);
        }

        await LoadProjectsAsync().ConfigureAwait(true);
        await LoadLabelsAsync().ConfigureAwait(true);
        await RefreshSettingsStateAsync().ConfigureAwait(true);
        SelectNavigation(_settings.Settings.LastView);
        if (IsTaskView(_currentView))
        {
            await RefreshTasksAsync().ConfigureAwait(true);
        }

        if (_migration.WasMigrated)
        {
            ShowInfo("Data migrated", "Your existing Openza Tasks database was imported. Reconnect providers from Settings.", InfoBarSeverity.Success);
        }
    }

    private void SelectNavigation(string tag)
    {
        foreach (var item in GetNavigationItems())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                ShellNav.SelectedItem = item;
                _currentView = tag;
                ApplyWorkspaceMode();
                return;
            }
        }

        ShellNav.SelectedItem = ShellNav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault();
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
        if (!string.Equals(nextView, _currentView, StringComparison.Ordinal) &&
            !await ConfirmDiscardTaskEditsAsync().ConfigureAwait(true))
        {
            SelectNavigationSilently(_currentView);
            return;
        }

        if (!string.Equals(nextView, _currentView, StringComparison.Ordinal) && TasksPage.IsDetailsPaneOpen)
        {
            _selectedTaskId = null;
            TasksPage.HideDetailsPane();
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
        if (!IsTaskView(_currentView))
        {
            _selectedTaskId = null;
            TasksPage.HideDetailsPane();
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

    private static bool IsTaskView(string view) => view is "inbox" or "next" or "today" or "overdue" or "waiting" or "someday" or "tasks" or "completed";

    private void OnOpenSettingsFromSyncClicked(object sender, RoutedEventArgs e)
    {
        SelectNavigation("settings");
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
        var theme = SettingsPage.SelectedTheme;
        _settings.Settings.Theme = theme;
        ApplyTheme(theme);
        await _settings.SaveAsync().ConfigureAwait(false);
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

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        StatusInfo.Title = title;
        StatusInfo.Message = message;
        StatusInfo.Severity = severity;
        StatusInfo.IsOpen = true;
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
        AddKeyboardShortcut(VirtualKey.S, VirtualKeyModifiers.Control, (_, args) =>
        {
            if (IsTaskView(_currentView) && TasksPage.IsDetailsPaneOpen)
            {
                OnSaveTaskClicked(this, new RoutedEventArgs());
            }
            else
            {
                OnSyncClicked(this, new RoutedEventArgs());
            }

            args.Handled = true;
        });
        AddKeyboardShortcut(VirtualKey.Escape, VirtualKeyModifiers.None, (_, args) =>
        {
            if (IsTaskView(_currentView))
            {
                TasksPage.SearchText = string.Empty;
                _selectedTaskId = null;
                TasksPage.HideDetailsPane();
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
