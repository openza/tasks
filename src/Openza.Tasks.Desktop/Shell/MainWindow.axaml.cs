using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using System.ComponentModel;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Desktop.Dialogs;
using Openza.Tasks.Desktop.Connected;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.Settings;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Desktop.Shell;

public sealed partial class MainWindow : Window
{
    private const double ExpandedNavigationWidth = 220;
    private const double CompactNavigationWidth = 64;
    private bool _initialized;
    private bool _changingNavigation;
    private NavigationItemViewModel? _pendingNavigationItem;
    private bool _changingTaskSelection;
    private readonly SemaphoreSlim _taskSelectionGate = new(1, 1);
    private bool _connectedPaneOpen;
    private bool _navigationCollapsed;
    private bool _automaticSyncRunning;
    private bool _closingAfterSave;
    private readonly DesktopPreferencesStore _preferencesStore = new();
    private readonly DispatcherTimer _automaticSyncTimer = new()
    {
        Interval = TimeSpan.FromMinutes(5),
    };
    private static readonly IBrush LightHoverBrush = new SolidColorBrush(Color.Parse("#E2E8F0"));
    private static readonly IBrush LightPressedBrush = new SolidColorBrush(Color.Parse("#D3DCE8"));
    private static readonly IBrush DarkHoverBrush = new SolidColorBrush(Color.Parse("#344052"));
    private static readonly IBrush DarkPressedBrush = new SolidColorBrush(Color.Parse("#3F4C60"));

    public MainWindow()
        : this(new MainWindowViewModel(new SqliteTaskStore(DesktopDataPaths.DatabasePath)))
    {
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        DetailLabelsBox.TextFilter = FilterLabelSuggestion;
        DetailLabelsBox.TextSelector = SelectLabelSuggestion;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _automaticSyncTimer.Tick += OnAutomaticSyncTick;
    }

    public MainWindowViewModel ViewModel { get; }

    private static bool FilterLabelSuggestion(string? search, string? suggestion)
    {
        if (string.IsNullOrWhiteSpace(suggestion))
        {
            return false;
        }

        search ??= string.Empty;
        var segments = search.Split(',', StringSplitOptions.TrimEntries);
        var currentSearch = segments.LastOrDefault() ?? string.Empty;
        var alreadySelected = segments
            .Take(Math.Max(0, segments.Length - 1))
            .Any(label => string.Equals(label, suggestion, StringComparison.CurrentCultureIgnoreCase));

        return !alreadySelected &&
            suggestion.Contains(currentSearch, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string SelectLabelSuggestion(string? search, string? suggestion)
    {
        search ??= string.Empty;
        suggestion ??= string.Empty;
        var separatorIndex = search.LastIndexOf(',');
        if (separatorIndex < 0)
        {
            return suggestion;
        }

        var existingLabels = search[..separatorIndex].Trim();
        return existingLabels.Length == 0
            ? suggestion
            : $"{existingLabels}, {suggestion}";
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        await ViewModel.InitializeAsync();
        _initialized = true;
        AutomaticSyncToggle.IsChecked = ViewModel.AutomaticSyncEnabled;
        UpdateAutomaticSyncTimer();
        UpdateWorkbenchLayout();
    }

    private async void OnAutomaticSyncTick(object? sender, EventArgs e)
    {
        if (_automaticSyncRunning)
        {
            return;
        }

        _automaticSyncRunning = true;
        try
        {
            await ViewModel.RunAutomaticTodoistSyncAsync();
        }
        finally
        {
            _automaticSyncRunning = false;
        }
    }

    private async void OnAutomaticSyncChanged(object? sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        var enabled = AutomaticSyncToggle.IsChecked == true;
        ViewModel.SetAutomaticSyncEnabled(enabled);
        var preferences = _preferencesStore.Load();
        await _preferencesStore.SaveAsync(preferences with { AutomaticSyncEnabled = enabled });
        UpdateAutomaticSyncTimer();
    }

    private void UpdateAutomaticSyncTimer()
    {
        if (ViewModel.AutomaticSyncEnabled)
        {
            _automaticSyncTimer.Start();
        }
        else
        {
            _automaticSyncTimer.Stop();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _automaticSyncTimer.Stop();
        _automaticSyncTimer.Tick -= OnAutomaticSyncTick;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingAfterSave || !_initialized)
        {
            return;
        }

        e.Cancel = true;
        if (!await ViewModel.SaveSelectedAsync())
        {
            return;
        }

        _closingAfterSave = true;
        Close();
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateWorkbenchLayout();

    private void OnInteractiveSurfacePointerEntered(object? sender, PointerEventArgs e) =>
        SetInteractiveSurface(sender, IsDarkTheme ? DarkHoverBrush : LightHoverBrush);

    private void OnInteractiveSurfacePointerExited(object? sender, PointerEventArgs e) =>
        SetInteractiveSurface(sender, Brushes.Transparent);

    private void OnInteractiveSurfacePointerPressed(object? sender, PointerPressedEventArgs e) =>
        SetInteractiveSurface(sender, IsDarkTheme ? DarkPressedBrush : LightPressedBrush);

    private void OnInteractiveSurfacePointerReleased(object? sender, PointerReleasedEventArgs e) =>
        SetInteractiveSurface(sender, IsDarkTheme ? DarkHoverBrush : LightHoverBrush);

    private bool IsDarkTheme => ActualThemeVariant == ThemeVariant.Dark;

    private static void SetInteractiveSurface(object? sender, IBrush brush)
    {
        switch (sender)
        {
            case Border border:
                border.Background = brush;
                break;
            case Button button:
                button.Background = brush;
                break;
        }
    }

    private void OnNavigationCollapseClicked(object? sender, RoutedEventArgs e)
    {
        _navigationCollapsed = !_navigationCollapsed;
        ShellGrid.ColumnDefinitions[0].Width = new GridLength(
            _navigationCollapsed ? CompactNavigationWidth : ExpandedNavigationWidth);
        ToolTip.SetTip(
            NavigationCollapseButton,
            _navigationCollapsed ? "Expand navigation" : "Collapse navigation");
        UpdateWorkbenchLayout();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedNavigation) or
            nameof(MainWindowViewModel.SelectedProject) or
            nameof(MainWindowViewModel.SelectedTask))
        {
            UpdateWorkbenchLayout();
        }
    }

    private void UpdateWorkbenchLayout()
    {
        var navigationWidth = _navigationCollapsed ? CompactNavigationWidth : ExpandedNavigationWidth;
        var width = Math.Max(0, ClientSize.Width - navigationWidth);
        if (width <= 0)
        {
            return;
        }

        var showProjects = TaskWorkspace.IsVisible &&
            (ViewModel.SelectedProject is not null || ViewModel.SelectedNavigation?.Kind == TaskListKind.Open);
        var showDetails = TaskWorkspace.IsVisible && ViewModel.HasSelectedTask && !_connectedPaneOpen;
        var narrow = width < 900;

        if (narrow && showDetails)
        {
            WorkbenchGrid.ColumnDefinitions[0].Width = new GridLength(0);
            WorkbenchGrid.ColumnDefinitions[1].Width = new GridLength(0);
            WorkbenchGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            WorkbenchGrid.ColumnDefinitions[3].Width = new GridLength(0);
            ProjectsPane.IsVisible = false;
            TaskListPane.IsVisible = false;
            DetailsPane.IsVisible = true;
            ConnectedPane.IsVisible = false;
            return;
        }

        WorkbenchGrid.ColumnDefinitions[0].Width = showProjects
            ? new GridLength(width < 1250 ? 300 : 360)
            : new GridLength(0);
        WorkbenchGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        WorkbenchGrid.ColumnDefinitions[2].Width = showDetails
            ? new GridLength(Math.Clamp(width * 0.55, 520, 850))
            : new GridLength(0);
        WorkbenchGrid.ColumnDefinitions[3].Width = _connectedPaneOpen
            ? new GridLength(width < 1250 ? 420 : 520)
            : new GridLength(0);
        ProjectsPane.IsVisible = showProjects;
        TaskListPane.IsVisible = true;
        DetailsPane.IsVisible = showDetails;
        ConnectedPane.IsVisible = _connectedPaneOpen;
    }

    private async void OnNavigationSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NavigationList.SelectedItem is not NavigationItemViewModel item)
        {
            return;
        }

        if (_changingNavigation)
        {
            _pendingNavigationItem = item;
            return;
        }

        await NavigateToAsync(item);
    }

    private async void OnNavigationItemTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: NavigationItemViewModel item } ||
            !ReferenceEquals(NavigationList.SelectedItem, item))
        {
            return;
        }

        if (_changingNavigation)
        {
            _pendingNavigationItem = item;
            return;
        }

        if (TaskWorkspace.IsVisible && ReferenceEquals(ViewModel.SelectedNavigation, item))
        {
            return;
        }

        await NavigateToAsync(item);
    }

    private async Task NavigateToAsync(NavigationItemViewModel item)
    {
        if (_changingNavigation)
        {
            _pendingNavigationItem = item;
            return;
        }

        _changingNavigation = true;
        try
        {
            if (!await ViewModel.SaveSelectedAsync())
            {
                NavigationList.SelectedItem = ViewModel.SelectedNavigation;
                return;
            }

            ShowTaskWorkspace();
            ProjectList.SelectedItem = null;
            NavigationList.SelectedItem = item;
            await ViewModel.SelectNavigationAsync(item);
        }
        finally
        {
            _changingNavigation = false;
        }

        var pendingItem = _pendingNavigationItem;
        _pendingNavigationItem = null;
        if (pendingItem is not null &&
            (!TaskWorkspace.IsVisible || !ReferenceEquals(ViewModel.SelectedNavigation, pendingItem)))
        {
            await NavigateToAsync(pendingItem);
        }
    }

    private async void OnSpaceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _changingNavigation || SpacePicker.SelectedItem is not SpaceNavigationItemViewModel item)
        {
            return;
        }

        _changingNavigation = true;
        if (!await ViewModel.SaveSelectedAsync())
        {
            SpacePicker.SelectedItem = ViewModel.SelectedSpace;
            _changingNavigation = false;
            return;
        }

        ProjectList.SelectedItem = null;
        await ViewModel.SelectSpaceAsync(item);
        var preferences = _preferencesStore.Load();
        await _preferencesStore.SaveAsync(preferences with { SelectedSpaceId = item.SpaceId });
        _changingNavigation = false;
    }

    private async void OnProjectSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_changingNavigation || ProjectList.SelectedItem is not ProjectNavigationItemViewModel item)
        {
            return;
        }

        _changingNavigation = true;
        if (!await ViewModel.SaveSelectedAsync())
        {
            ProjectList.SelectedItem = ViewModel.SelectedProject;
            _changingNavigation = false;
            return;
        }

        NavigationList.SelectedItem = null;
        await ViewModel.SelectProjectAsync(item);
        _changingNavigation = false;
    }

    private void OnProjectSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_initialized)
        {
            ViewModel.ApplyProjectFilter();
        }
    }

    private void OnProjectFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initialized)
        {
            ViewModel.ApplyProjectFilter();
        }
    }

    private async void OnAddTaskClicked(object? sender, RoutedEventArgs e)
    {
        var dialog = new AddTaskWindow(ViewModel);
        var draft = await dialog.ShowDialog<AddTaskDraft?>(this);
        if (draft is not null)
        {
            await ViewModel.CreateTaskAsync(draft);
        }
    }

    private async void OnImportClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Markdown tasks",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Markdown and text")
                {
                    Patterns = ["*.md", "*.markdown", "*.txt"],
                },
            ],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await ViewModel.ImportMarkdownAsync(path);
        }
    }

    private async void OnExportClicked(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Openza tasks",
            SuggestedFileName = $"openza-tasks-{DateTime.Now:yyyy-MM-dd}.md",
            DefaultExtension = "md",
            FileTypeChoices =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
            ],
        });
        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await ViewModel.ExportMarkdownAsync(path);
        }
    }

    private async void OnBackupClicked(object? sender, RoutedEventArgs e)
    {
        await ViewModel.CreateRestorePointAsync();
    }

    private async void OnSyncClicked(object? sender, RoutedEventArgs e)
    {
        await ViewModel.RunTodoistSyncAsync();
    }

    private async void OnSettingsClicked(object? sender, RoutedEventArgs e)
    {
        TaskWorkspace.IsVisible = false;
        SyncWorkspace.IsVisible = false;
        SettingsWorkspace.IsVisible = true;
        _connectedPaneOpen = false;
        ViewModel.LoadRestorePoints();
        var preferences = _preferencesStore.Load();
        ShellThemePicker.SelectedIndex = preferences.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0,
        };
        await ViewModel.RefreshTodoistConnectionAsync();
        await ViewModel.RefreshTodoistRoutingRulesAsync();
        await ViewModel.RefreshGitHubConnectionAsync();
    }

    private void OnSyncNavigationClicked(object? sender, RoutedEventArgs e)
    {
        TaskWorkspace.IsVisible = false;
        SettingsWorkspace.IsVisible = false;
        SyncWorkspace.IsVisible = true;
        _connectedPaneOpen = false;
    }

    private async void OnConnectedTasksClicked(object? sender, RoutedEventArgs e)
    {
        ShowTaskWorkspace();
        _connectedPaneOpen = true;
        ViewModel.SelectedTask = null;
        await ViewModel.LoadConnectedTasksAsync();
        UpdateWorkbenchLayout();
    }

    private async void OnCreateProjectClicked(object? sender, RoutedEventArgs e)
    {
        var prompt = new TextPromptWindow("Create project", string.Empty);
        var name = await prompt.ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        ViewModel.NewProjectName = name;
        await ViewModel.CreateProjectAsync();
    }

    private async void OnRenameProjectClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProject is null)
        {
            return;
        }

        var prompt = new TextPromptWindow("Rename project", ViewModel.SelectedProject.Title);
        var name = await prompt.ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await ViewModel.RenameSelectedProjectAsync(name);
        }
    }

    private async void OnDeleteProjectClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProject is null)
        {
            return;
        }

        var dialog = new ConfirmWindow(
            "Delete project?",
            $"{ViewModel.SelectedProject.Title} will be deleted. Its tasks will be preserved and moved to Inbox.");
        if (await dialog.ShowDialog<bool>(this))
        {
            await ViewModel.DeleteSelectedProjectAsync();
        }
    }

    private async void OnListOptionsChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        await ViewModel.ApplyListOptionsAsync();
    }

    private async void OnMoveTaskToSpaceClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SpaceNavigationItemViewModel targetSpace })
        {
            await ViewModel.MoveSelectedTaskToSpaceAsync(targetSpace);
        }
    }

    private async void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await ViewModel.ApplySearchAsync();
    }

    private async void OnTaskCompletionClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: TaskListEntryViewModel { Task: { } item } })
        {
            return;
        }

        if (!await SelectTaskAfterSavingAsync(item))
        {
            return;
        }

        await ViewModel.ToggleSelectedCompletionAsync();
        e.Handled = true;
    }

    private async void OnTaskEntrySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_changingTaskSelection && sender is ListBox { SelectedItem: TaskListEntryViewModel { Task: { } task } })
        {
            await SelectTaskAfterSavingAsync(task);
        }
    }

    private async void OnTaskRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { DataContext: TaskListEntryViewModel { Task: { } task } entry })
        {
            _changingTaskSelection = true;
            TaskList.SelectedItem = entry;
            _changingTaskSelection = false;
            await SelectTaskAfterSavingAsync(task);
        }
    }

    private async Task<bool> SelectTaskAfterSavingAsync(TaskListItemViewModel task)
    {
        await _taskSelectionGate.WaitAsync();
        try
        {
            if (string.Equals(ViewModel.SelectedTask?.Task.Id, task.Task.Id, StringComparison.Ordinal))
            {
                return true;
            }

            var previousId = ViewModel.SelectedTask?.Task.Id;
            if (!await ViewModel.SaveSelectedAsync())
            {
                _changingTaskSelection = true;
                TaskList.SelectedItem = TaskEntriesItem(previousId);
                _changingTaskSelection = false;
                return false;
            }

            await ViewModel.SelectTaskAsync(task);
            return true;
        }
        finally
        {
            _taskSelectionGate.Release();
        }
    }

    private TaskListEntryViewModel? TaskEntriesItem(string? taskId) =>
        ViewModel.TaskEntries.FirstOrDefault(entry => string.Equals(entry.Task?.Task.Id, taskId, StringComparison.Ordinal));

    private async void OnCloseDetailsClicked(object? sender, RoutedEventArgs e)
    {
        if (!await ViewModel.SaveSelectedAsync())
        {
            return;
        }

        TaskList.SelectedItem = null;
        ViewModel.SelectedTask = null;
        UpdateWorkbenchLayout();
    }

    private async void OnSubtaskCompletionClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: TaskListItemViewModel item })
        {
            await ViewModel.ToggleSubtaskCompletionAsync(item);
            e.Handled = true;
        }
    }

    private async void OnToggleCompletionClicked(object? sender, RoutedEventArgs e)
    {
        await ViewModel.ToggleSelectedCompletionAsync();
    }

    private async void OnSaveTaskClicked(object? sender, RoutedEventArgs e)
    {
        await ViewModel.SaveSelectedAsync();
    }

    private async void OnDetailEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_initialized && ViewModel.HasSelectedTask && !ViewModel.IsUpdatingDetails)
        {
            await ViewModel.SaveSelectedAsync();
        }
    }

    private async void OnDetailEditorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initialized && ViewModel.HasSelectedTask && !ViewModel.IsUpdatingDetails)
        {
            await ViewModel.SaveSelectedAsync();
        }
    }

    private async void OnDetailDateChanged(object? sender, DatePickerSelectedValueChangedEventArgs e)
    {
        if (_initialized && ViewModel.HasSelectedTask && !ViewModel.IsUpdatingDetails)
        {
            await ViewModel.SaveSelectedAsync();
        }
    }

    private async void OnDeleteTaskClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTask is null)
        {
            return;
        }

        var dialog = new ConfirmWindow(
            "Delete task?",
            $"“{ViewModel.SelectedTask.Title}” will be permanently removed from this local database.");
        if (await dialog.ShowDialog<bool>(this))
        {
            try
            {
                await ViewModel.DeleteSelectedAsync();
            }
            catch (ProviderLinkedTaskDeleteException exception)
            {
                var alert = new ConfirmWindow(
                    "Task is linked",
                    exception.Message,
                    "Close",
                    showCancel: false);
                await alert.ShowDialog<bool>(this);
            }
            catch (Exception exception)
            {
                var alert = new ConfirmWindow(
                    "Could not delete task",
                    exception.Message,
                    "Close",
                    showCancel: false);
                await alert.ShowDialog<bool>(this);
            }
        }
    }

    private async void OnGitHubActionClicked(object? sender, RoutedEventArgs e)
    {
        var url = await ViewModel.RunGitHubActionAsync();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            await Launcher.LaunchUriAsync(uri);
        }
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.K)
        {
            await OpenGlobalSearchAsync();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.N)
        {
            OnAddTaskClicked(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.S)
        {
            await ViewModel.SaveSelectedAsync();
            e.Handled = true;
        }
    }

    private void ShowTaskWorkspace()
    {
        TaskWorkspace.IsVisible = true;
        SyncWorkspace.IsVisible = false;
        SettingsWorkspace.IsVisible = false;
        UpdateWorkbenchLayout();
    }

    private async void OnSearchCommandClicked(object? sender, RoutedEventArgs e) => await OpenGlobalSearchAsync();

    private async Task OpenGlobalSearchAsync()
    {
        if (!await ViewModel.SaveSelectedAsync())
        {
            return;
        }

        var dialog = new GlobalSearchWindow(ViewModel.SearchGloballyAsync);
        var result = await dialog.ShowDialog<GlobalSearchResult?>(this);
        if (result is null)
        {
            return;
        }

        ShowTaskWorkspace();
        await ViewModel.OpenGlobalSearchResultAsync(result);
        UpdateWorkbenchLayout();
    }

    private void OnFilterClicked(object? sender, RoutedEventArgs e)
    {
        // The compact command matches WinUI; detailed filters remain keyboard-accessible
        // through the existing persisted list options while the flyout is implemented.
        SearchBox.Focus();
    }

    private void OnCloseConnectedTasksClicked(object? sender, RoutedEventArgs e)
    {
        _connectedPaneOpen = false;
        UpdateWorkbenchLayout();
    }

    private void OnConnectedSearchChanged(object? sender, TextChangedEventArgs e) =>
        ViewModel.FilterConnectedTasks(ConnectedSearchBox.Text ?? string.Empty);

    private void OnConnectedFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initialized)
        {
            ViewModel.ApplyConnectedFilters();
        }
    }

    private void OnShowSkippedChanged(object? sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            ViewModel.ApplyConnectedFilters();
        }
    }

    private async void OnConnectedPrimaryClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ConnectedTaskViewModel item })
        {
            if (item.Source.IsSkipped)
            {
                await ViewModel.UnskipConnectedTaskAsync(item);
            }
            else
            {
                await ViewModel.AdoptConnectedTaskAsync(item);
            }
        }
    }

    private async void OnConnectedSkipClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ConnectedTaskViewModel item })
        {
            await ViewModel.SkipConnectedTaskAsync(item);
        }
    }

    private async void OnAddAllConnectedClicked(object? sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.FilteredConnectedTasks.Where(item => !item.Source.IsSkipped).ToArray())
        {
            await ViewModel.AdoptConnectedTaskAsync(item);
        }
    }

    private async void OnSyncNowClicked(object? sender, RoutedEventArgs e) => await ViewModel.RunTodoistSyncAsync();

    private async void OnOpenDataFolderFromShellClicked(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(DesktopDataPaths.DataDirectory);
        await Launcher.LaunchUriAsync(new Uri(DesktopDataPaths.DataDirectory));
    }

    private async void OnShellThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || Avalonia.Application.Current is null)
        {
            return;
        }

        var theme = ShellThemePicker.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System",
        };
        Avalonia.Application.Current.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        var preferences = _preferencesStore.Load();
        await _preferencesStore.SaveAsync(preferences with { Theme = theme });
    }

    private async void OnExportDatabaseFromShellClicked(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Openza Tasks backup",
            SuggestedFileName = $"openza-tasks-backup-{DateTime.Now:yyyy-MM-dd}.db",
            DefaultExtension = "db",
            FileTypeChoices = [new FilePickerFileType("SQLite database") { Patterns = ["*.db"] }],
        });
        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await ViewModel.ExportDatabaseAsync(path);
        }
    }

    private async void OnRestoreDatabaseFromShellClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Restore Openza Tasks backup",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("SQLite database") { Patterns = ["*.db"] }],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var dialog = new ConfirmWindow(
            "Restore this database?",
            "The current database will be replaced after Openza creates a safety restore point.");
        if (await dialog.ShowDialog<bool>(this))
        {
            await ViewModel.RestoreDatabaseAsync(path);
        }
    }

    private async void OnRestoreSelectedPointFromShellClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedRestorePoint is null)
        {
            return;
        }
        var dialog = new ConfirmWindow(
            "Restore selected point?",
            "The current database will be replaced after Openza creates a new safety restore point.");
        if (await dialog.ShowDialog<bool>(this))
        {
            await ViewModel.RestoreSelectedPointAsync();
        }
    }

    private async void OnDeleteSelectedPointFromShellClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedRestorePoint is null)
        {
            return;
        }
        var dialog = new ConfirmWindow(
            "Delete restore point?",
            "This restore point and its metadata will be permanently removed.");
        if (await dialog.ShowDialog<bool>(this))
        {
            await ViewModel.DeleteSelectedRestorePointAsync();
        }
    }

    private async void OnConnectTodoistFromShellClicked(object? sender, RoutedEventArgs e) =>
        await ViewModel.ConnectTodoistAsync();

    private async void OnDisconnectTodoistFromShellClicked(object? sender, RoutedEventArgs e)
    {
        var dialog = new ConfirmWindow(
            "Disconnect Todoist?",
            "The secure token will be removed from this Linux installation. Existing local tasks remain available.");
        if (await dialog.ShowDialog<bool>(this))
        {
            await ViewModel.DisconnectTodoistAsync();
        }
    }

    private async void OnAddTodoistRuleClicked(object? sender, RoutedEventArgs e) =>
        await ShowTodoistRuleDialogAsync(null);

    private async void OnEditTodoistRuleClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TodoistRoutingRuleViewModel rule })
        {
            await ShowTodoistRuleDialogAsync(rule);
        }
    }

    private async Task ShowTodoistRuleDialogAsync(TodoistRoutingRuleViewModel? existing)
    {
        var dialog = new TodoistRoutingRuleWindow(
            existing,
            ViewModel.TodoistRoutingLabelChoices,
            ViewModel.TodoistRoutingSpaceChoices,
            ViewModel.TodoistRoutingProjectChoices);
        var draft = await dialog.ShowDialog<Openza.Tasks.Core.Sync.TodoistRoutingRuleDraft?>(this);
        if (draft is not null)
        {
            await ViewModel.SaveTodoistRoutingRuleAsync(draft);
        }
    }

    private async void OnDeleteTodoistRuleClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TodoistRoutingRuleViewModel rule })
        {
            return;
        }

        var dialog = new ConfirmWindow(
            "Delete Todoist rule?",
            $"{rule.LabelText} will no longer route new Todoist tasks to {rule.SpaceName}.");
        if (await dialog.ShowDialog<bool>(this))
        {
            await ViewModel.DeleteTodoistRoutingRuleAsync(rule.Id);
        }
    }

    private async void OnConnectGitHubFromShellClicked(object? sender, RoutedEventArgs e) =>
        await ViewModel.ConnectGitHubAsync();

    private async void OnDisconnectGitHubFromShellClicked(object? sender, RoutedEventArgs e)
    {
        var dialog = new ConfirmWindow(
            "Disconnect GitHub?",
            "The secure token will be removed from this Linux installation. Existing task-to-issue links remain.");
        if (await dialog.ShowDialog<bool>(this))
        {
            await ViewModel.DisconnectGitHubAsync();
        }
    }

    private async void OnSaveGitHubRepositoryFromShellClicked(object? sender, RoutedEventArgs e) =>
        await ViewModel.SaveGitHubDefaultRepositoryAsync();

    private async void OnAddSpaceFromShellClicked(object? sender, RoutedEventArgs e)
    {
        await ViewModel.CreateSpaceAsync(ShellNewSpaceBox.Text ?? string.Empty);
        ShellNewSpaceBox.Clear();
    }

    private async void OnRenameSpaceFromShellClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SpaceNavigationItemViewModel item })
        {
            return;
        }

        var prompt = new TextPromptWindow("Rename space", item.Title);
        var name = await prompt.ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await ViewModel.RenameSpaceAsync(item, name);
        }
    }

    private async void OnArchiveSpaceFromShellClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SpaceNavigationItemViewModel item })
        {
            return;
        }

        var dialog = new ConfirmWindow(
            "Archive space?",
            $"{item.Title} will be hidden from normal navigation. Its tasks and projects remain in the database.");
        if (await dialog.ShowDialog<bool>(this))
        {
            await ViewModel.ArchiveSpaceAsync(item);
        }
    }
}
