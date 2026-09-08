using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private bool _suppressNavigationSelection;
    private NavigationItemViewModel? _suppressedNavigationItem;
    private NavigationItemViewModel? _pendingNavigationItem;
    private bool _changingTaskSelection;
    private readonly SemaphoreSlim _taskSelectionGate = new(1, 1);
    private bool _connectedPaneOpen;
    private bool _navigationCollapsed;
    private bool _narrowProjectsOpen;
    private bool _automaticSyncRunning;
    private bool _taskCompletionInProgress;
    private bool _changingListFilters;
    private bool _applyingListOptions;
    private bool _pendingListOptionsApply;
    private bool _closingAfterSave;
    private readonly DesktopPreferencesStore _preferencesStore = new();
    private readonly DispatcherTimer _automaticSyncTimer = new()
    {
        Interval = TimeSpan.FromMinutes(5),
    };
    private readonly DispatcherTimer _statusHideTimer = new()
    {
        Interval = TimeSpan.FromSeconds(4),
    };
    private readonly DispatcherTimer _taskSearchTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250),
    };
    private static readonly IBrush LightHoverBrush = new SolidColorBrush(Color.Parse("#F0F3F6"));
    private static readonly IBrush LightPressedBrush = new SolidColorBrush(Color.Parse("#DDE3EA"));
    private static readonly IBrush DarkHoverBrush = new SolidColorBrush(Color.Parse("#263447"));
    private static readonly IBrush DarkPressedBrush = new SolidColorBrush(Color.Parse("#334155"));

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
        _statusHideTimer.Tick += OnStatusHideTick;
        _taskSearchTimer.Tick += OnTaskSearchTick;
    }

    public MainWindowViewModel ViewModel { get; }

    private bool FilterLabelSuggestion(string? search, string? suggestion)
    {
        if (string.IsNullOrWhiteSpace(suggestion))
        {
            return false;
        }

        search ??= string.Empty;
        return !ViewModel.DetailLabelItems.Any(label => string.Equals(label, suggestion, StringComparison.CurrentCultureIgnoreCase)) &&
            suggestion.Contains(search, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string SelectLabelSuggestion(string? search, string? suggestion)
        => suggestion ?? string.Empty;

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
        UpdateConnectedPaneForCurrentView(autoOpen: true);
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
        await _preferencesStore.UpdateAsync(preferences => preferences with { AutomaticSyncEnabled = enabled });
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
        _statusHideTimer.Stop();
        _statusHideTimer.Tick -= OnStatusHideTick;
        _taskSearchTimer.Stop();
        _taskSearchTimer.Tick -= OnTaskSearchTick;
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
        ShellGrid.Classes.Set("navigation-compact", _navigationCollapsed);
        ShellGrid.ColumnDefinitions[0].Width = new GridLength(
            _navigationCollapsed ? CompactNavigationWidth : ExpandedNavigationWidth);
        ToolTip.SetTip(
            NavigationCollapseButton,
            _navigationCollapsed ? "Expand navigation" : "Collapse navigation");
        UpdateWorkbenchLayout();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.StatusMessage) or
            nameof(MainWindowViewModel.IsBusy) or
            nameof(MainWindowViewModel.IsStatusMessagePersistent))
        {
            StatusOverlay.IsVisible = ViewModel.IsBusy || !string.IsNullOrWhiteSpace(ViewModel.StatusMessage);
            _statusHideTimer.Stop();
            if (!ViewModel.IsBusy && !ViewModel.IsStatusMessagePersistent)
            {
                _statusHideTimer.Start();
            }
        }

        if (e.PropertyName is nameof(MainWindowViewModel.SelectedNavigation) or
            nameof(MainWindowViewModel.SelectedProject) or
            nameof(MainWindowViewModel.SelectedTask))
        {
            UpdateWorkbenchLayout();
        }

        if (e.PropertyName == nameof(MainWindowViewModel.ConnectedTaskCount))
        {
            UpdateConnectedPaneForCurrentView(autoOpen: true);
            UpdateWorkbenchLayout();
        }
    }

    private void OnStatusHideTick(object? sender, EventArgs e)
    {
        _statusHideTimer.Stop();
        if (!ViewModel.IsBusy)
        {
            StatusOverlay.IsVisible = false;
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
        TaskList.Classes.Set("hide-row-actions", showDetails);
        UpdateTaskFilterLayout(width < 1120 || (width < 1360 && (showDetails || _connectedPaneOpen)));

        ProjectsCommand.IsVisible = narrow && showProjects && !showDetails && !_connectedPaneOpen;

        if (narrow && (showDetails || _connectedPaneOpen))
        {
            WorkbenchGrid.ColumnDefinitions[0].Width = new GridLength(0);
            WorkbenchGrid.ColumnDefinitions[1].Width = new GridLength(0);
            WorkbenchGrid.ColumnDefinitions[2].Width = showDetails
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
            WorkbenchGrid.ColumnDefinitions[3].Width = _connectedPaneOpen
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
            ProjectsPane.IsVisible = false;
            TaskListPane.IsVisible = false;
            DetailsPane.IsVisible = showDetails;
            ConnectedPane.IsVisible = _connectedPaneOpen;
            return;
        }

        if (narrow)
        {
            WorkbenchGrid.ColumnDefinitions[0].Width = showProjects && _narrowProjectsOpen
                ? new GridLength(Math.Min(300, width * 0.42))
                : new GridLength(0);
            WorkbenchGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            WorkbenchGrid.ColumnDefinitions[2].Width = new GridLength(0);
            WorkbenchGrid.ColumnDefinitions[3].Width = new GridLength(0);
            ProjectsPane.IsVisible = showProjects && _narrowProjectsOpen;
            TaskListPane.IsVisible = true;
            DetailsPane.IsVisible = false;
            ConnectedPane.IsVisible = false;
            return;
        }

        _narrowProjectsOpen = false;

        WorkbenchGrid.ColumnDefinitions[0].Width = showProjects
            ? new GridLength(width < 1250 ? 300 : 360)
            : new GridLength(0);
        WorkbenchGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        WorkbenchGrid.ColumnDefinitions[2].Width = showDetails
            ? new GridLength(Math.Clamp(width * 0.42, 460, 620))
            : new GridLength(0);
        WorkbenchGrid.ColumnDefinitions[3].Width = _connectedPaneOpen
            ? new GridLength(width < 1250 ? 420 : 520)
            : new GridLength(0);
        ProjectsPane.IsVisible = showProjects;
        TaskListPane.IsVisible = true;
        DetailsPane.IsVisible = showDetails;
        ConnectedPane.IsVisible = _connectedPaneOpen;
    }

    private void OnProjectsCommandClicked(object? sender, RoutedEventArgs e)
    {
        _narrowProjectsOpen = !_narrowProjectsOpen;
        UpdateWorkbenchLayout();
    }

    private void UpdateTaskFilterLayout(bool compact)
    {
        if (compact)
        {
            SearchBox.Width = double.NaN;
            SearchBox.MaxWidth = double.PositiveInfinity;
            SearchBox.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            Grid.SetRow(SearchBox, 0);
            Grid.SetColumn(SearchBox, 0);
            Grid.SetColumnSpan(SearchBox, 3);
            Grid.SetRow(TaskFilterCommands, 1);
            Grid.SetColumn(TaskFilterCommands, 0);
            Grid.SetColumnSpan(TaskFilterCommands, 3);
            return;
        }

        SearchBox.Width = 460;
        SearchBox.MaxWidth = 480;
        SearchBox.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        Grid.SetRow(SearchBox, 0);
        Grid.SetColumn(SearchBox, 0);
        Grid.SetColumnSpan(SearchBox, 1);
        Grid.SetRow(TaskFilterCommands, 0);
        Grid.SetColumn(TaskFilterCommands, 1);
        Grid.SetColumnSpan(TaskFilterCommands, 1);
    }

    private void UpdateConnectedPaneForCurrentView(bool autoOpen)
    {
        var isInbox = TaskWorkspace.IsVisible &&
            ViewModel.SelectedNavigation?.Kind == TaskListKind.Inbox;
        ConnectedTasksCommand.IsVisible = isInbox && ViewModel.HasConnectedTasks;
        if (!isInbox || !ViewModel.HasConnectedTasks)
        {
            _connectedPaneOpen = false;
            return;
        }

        if (autoOpen && !ViewModel.HasSelectedTask)
        {
            _connectedPaneOpen = true;
        }
    }

    private async void OnNavigationSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NavigationList.SelectedItem is not NavigationItemViewModel item)
        {
            return;
        }

        if (_suppressNavigationSelection && ReferenceEquals(item, _suppressedNavigationItem))
        {
            return;
        }

        if (_changingNavigation)
        {
            if (ReferenceEquals(ViewModel.SelectedNavigation, item))
            {
                return;
            }

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
            UpdateConnectedPaneForCurrentView(autoOpen: true);
            UpdateWorkbenchLayout();
        }
        finally
        {
            _changingNavigation = false;
        }

        await DrainPendingNavigationAsync();
    }

    private async void OnSpaceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _changingNavigation || SpacePicker.SelectedItem is not SpaceNavigationItemViewModel item)
        {
            return;
        }

        _changingNavigation = true;
        try
        {
            if (!await ViewModel.SaveSelectedAsync())
            {
                SpacePicker.SelectedItem = ViewModel.SelectedSpace;
                return;
            }

            _suppressedNavigationItem = ViewModel.SelectedProject is not null
                ? ViewModel.NavigationItems.FirstOrDefault(navigation => navigation.Kind == TaskListKind.Open)
                : null;
            _suppressNavigationSelection = true;
            try
            {
                await ViewModel.SelectSpaceAsync(item);
            }
            finally
            {
                _suppressNavigationSelection = false;
                _suppressedNavigationItem = null;
            }

            UpdateConnectedPaneForCurrentView(autoOpen: true);
            UpdateWorkbenchLayout();
            await _preferencesStore.UpdateAsync(preferences => preferences with { SelectedSpaceId = item.SpaceId });
        }
        finally
        {
            _changingNavigation = false;
        }

        await DrainPendingNavigationAsync();
    }

    private async Task DrainPendingNavigationAsync()
    {
        var pendingItem = _pendingNavigationItem;
        _pendingNavigationItem = null;
        if (pendingItem is not null &&
            (!TaskWorkspace.IsVisible || !ReferenceEquals(ViewModel.SelectedNavigation, pendingItem)))
        {
            await NavigateToAsync(pendingItem);
        }
    }

    private void OnCompactSpaceSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SpaceNavigationItemViewModel item })
        {
            return;
        }

        SpacePicker.SelectedItem = item;
        CompactSpacePickerButton.Flyout?.Hide();
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
        if (ViewModel.SelectedNavigation?.Kind != TaskListKind.Inbox)
        {
            var inbox = ViewModel.NavigationItems.First(item => item.Kind == TaskListKind.Inbox);
            await NavigateToAsync(inbox);
        }

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

    private async void OnEditProjectClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProject is null)
        {
            return;
        }

        var dialog = new ProjectEditorWindow(ViewModel.SelectedProject.Project);
        var draft = await dialog.ShowDialog<ProjectEditDraft?>(this);
        if (draft is not null)
        {
            await ViewModel.UpdateSelectedProjectAsync(draft.Name, draft.Status, draft.IsFavorite);
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
        if (!_initialized || _changingNavigation || _changingListFilters)
        {
            return;
        }

        await ApplyListOptionsCoalescedAsync();
    }

    private async Task ApplyListOptionsCoalescedAsync()
    {
        if (_applyingListOptions)
        {
            _pendingListOptionsApply = true;
            return;
        }

        _applyingListOptions = true;
        try
        {
            do
            {
                _pendingListOptionsApply = false;
                await ViewModel.ApplyListOptionsAsync();
            }
            while (_pendingListOptionsApply);
        }
        finally
        {
            _applyingListOptions = false;
        }
    }

    private async void OnSortMenuItemClicked(object? sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not MenuItem { Tag: string value } || !int.TryParse(value, out var index))
        {
            return;
        }

        ViewModel.SortIndex = index;
        await ApplyListOptionsCoalescedAsync();
    }

    private async void OnSortDirectionMenuItemClicked(object? sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not MenuItem { Tag: string value } || !int.TryParse(value, out var index))
        {
            return;
        }

        ViewModel.SortDirectionIndex = index;
        await ApplyListOptionsCoalescedAsync();
    }

    private async void OnGroupMenuItemClicked(object? sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not MenuItem { Tag: string value } || !int.TryParse(value, out var index))
        {
            return;
        }

        ViewModel.GroupIndex = index;
        await ApplyListOptionsCoalescedAsync();
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
        _taskSearchTimer.Stop();
        ViewModel.SearchText = SearchBox.Text ?? string.Empty;
        await ViewModel.ApplySearchAsync();
    }

    private void OnTaskSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_initialized || _changingListFilters)
        {
            return;
        }

        ViewModel.SearchText = SearchBox.Text ?? string.Empty;
        _taskSearchTimer.Stop();
        _taskSearchTimer.Start();
    }

    private async void OnTaskSearchTick(object? sender, EventArgs e)
    {
        _taskSearchTimer.Stop();
        await ViewModel.ApplySearchAsync();
    }

    private async void OnClearPriorityFilterClicked(object? sender, RoutedEventArgs e) =>
        await ChangeListFiltersAsync(() => ViewModel.PriorityFilterIndex = 0);

    private async void OnClearRepeatFilterClicked(object? sender, RoutedEventArgs e) =>
        await ChangeListFiltersAsync(() => ViewModel.RepeatFilterIndex = 0);

    private async void OnClearLabelFilterClicked(object? sender, RoutedEventArgs e) =>
        await ChangeListFiltersAsync(() => ViewModel.SelectedLabelFilter = ViewModel.LabelFilterOptions.FirstOrDefault());

    private async void OnClearAllFiltersClicked(object? sender, RoutedEventArgs e) =>
        await ChangeListFiltersAsync(() =>
        {
            ViewModel.SearchText = string.Empty;
            ViewModel.PriorityFilterIndex = 0;
            ViewModel.RepeatFilterIndex = 0;
            ViewModel.SelectedLabelFilter = ViewModel.LabelFilterOptions.FirstOrDefault();
        });

    private async Task ChangeListFiltersAsync(Action change)
    {
        _changingListFilters = true;
        try
        {
            change();
        }
        finally
        {
            _changingListFilters = false;
        }

        await ApplyListOptionsCoalescedAsync();
    }

    private async void OnEmptyStateActionClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.HasActiveListFilters)
        {
            await ChangeListFiltersAsync(() =>
            {
                ViewModel.SearchText = string.Empty;
                ViewModel.PriorityFilterIndex = 0;
                ViewModel.RepeatFilterIndex = 0;
                ViewModel.SelectedLabelFilter = ViewModel.LabelFilterOptions.FirstOrDefault();
            });
            return;
        }

        OnAddTaskClicked(sender, e);
    }

    private async void OnTaskCompletionClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_taskCompletionInProgress ||
            sender is not CheckBox { DataContext: TaskListEntryViewModel { Task: { } item } } checkbox)
        {
            return;
        }

        _taskCompletionInProgress = true;
        checkbox.IsEnabled = false;
        try
        {
            if (!await SelectTaskAfterSavingAsync(item))
            {
                return;
            }

            await ViewModel.ToggleSelectedCompletionAsync();
        }
        finally
        {
            checkbox.IsChecked = ViewModel.SelectedTask?.Task.IsCompleted ?? item.Task.IsCompleted;
            checkbox.IsEnabled = true;
            _taskCompletionInProgress = false;
        }
    }

    private async void OnTaskEntrySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_changingTaskSelection && sender is ListBox { SelectedItem: TaskListEntryViewModel { Task: { } task } })
        {
            await SelectTaskAfterSavingAsync(task);
        }
    }

    private void OnTaskGroupHeaderClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: TaskListEntryViewModel { IsHeader: true } header })
        {
            ViewModel.ToggleTaskGroup(header.GroupKey);
        }
    }

    private async void OnTaskRowTapped(object? sender, TappedEventArgs e)
    {
        if (IsInteractiveElement(e.Source) ||
            sender is not Border { DataContext: TaskListEntryViewModel { Task: { } task } entry })
        {
            return;
        }

        _changingTaskSelection = true;
        TaskList.SelectedItem = entry;
        _changingTaskSelection = false;
        await SelectTaskAfterSavingAsync(task);
    }

    private async void OnRowDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is CalendarDatePicker { DataContext: TaskListEntryViewModel { Task: { } item }, SelectedDate: { } selectedDate })
        {
            e.Handled = true;
            await ViewModel.SetTaskDateFromRowAsync(item, DateOnly.FromDateTime(selectedDate));
        }
    }

    private async void OnClearRowDateClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TaskListEntryViewModel { Task: { } item } })
        {
            e.Handled = true;
            await ViewModel.SetTaskDateFromRowAsync(item, null);
        }
    }

    private async void OnRowStatusClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: TaskListEntryViewModel { Task: { } item } } menuItem ||
            !int.TryParse(menuItem.Tag?.ToString(), out var statusIndex))
        {
            return;
        }

        var status = statusIndex switch
        {
            1 => Openza.Tasks.Core.Models.TaskItemStatus.Next,
            2 => Openza.Tasks.Core.Models.TaskItemStatus.Waiting,
            3 => Openza.Tasks.Core.Models.TaskItemStatus.Someday,
            _ => Openza.Tasks.Core.Models.TaskItemStatus.Inbox,
        };
        e.Handled = true;
        await ViewModel.SetTaskStatusFromRowAsync(item, status);
    }

    private async void OnRowPriorityClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: TaskListEntryViewModel { Task: { } item } } menuItem &&
            int.TryParse(menuItem.Tag?.ToString(), out var priority))
        {
            e.Handled = true;
            await ViewModel.SetTaskPriorityFromRowAsync(item, priority);
        }
    }

    private async void OnRowProjectButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TaskListEntryViewModel { Task: { } item } })
        {
            return;
        }

        e.Handled = true;
        var options = ViewModel.GetProjectOptionsForSpace(item.Task.SpaceId)
            .Select(option => new PickerOption(option.ProjectId ?? string.Empty, option.Title))
            .ToList();
        var dialog = new ProjectPickerWindow(options, item.Task.ProjectId);
        var result = await dialog.ShowDialog<ProjectPickerResult?>(this);
        if (!string.IsNullOrWhiteSpace(result?.NewProjectName))
        {
            await ViewModel.CreateProjectForTaskFromRowAsync(item, result.NewProjectName);
        }
        else if (result is not null)
        {
            await ViewModel.SetTaskProjectFromRowAsync(item, result.ProjectId);
        }
    }

    private async void OnRowLabelsButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TaskListEntryViewModel { Task: { } item } })
        {
            return;
        }

        e.Handled = true;
        var dialog = new LabelPickerWindow(ViewModel.LabelSuggestions, item.Task.Labels.Select(label => label.Name));
        var labels = await dialog.ShowDialog<string?>(this);
        if (labels is not null)
        {
            await ViewModel.SetTaskLabelsFromRowAsync(item, labels);
        }
    }

    private async void OnRowMoveSpaceButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TaskListEntryViewModel { Task: { } item } })
        {
            return;
        }

        e.Handled = true;
        var options = ViewModel.EditableSpaceItems
            .Where(space => !string.IsNullOrWhiteSpace(space.SpaceId))
            .Select(space => new PickerOption(space.SpaceId!, space.Title))
            .ToList();
        var dialog = new OptionPickerWindow("Move to Space", "Move this task and its local organization to another Space.", options, item.Task.SpaceId);
        var spaceId = await dialog.ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(spaceId))
        {
            await ViewModel.MoveTaskFromRowAsync(item, spaceId);
        }
    }

    private async void OnRowDeleteClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TaskListEntryViewModel { Task: { } item } })
        {
            return;
        }

        e.Handled = true;
        var dialog = new ConfirmWindow(
            "Delete task?",
            $"Delete “{item.Title}” from Openza? Tasks still linked to a provider must be deleted there first.");
        if (await dialog.ShowDialog<bool>(this))
        {
            try
            {
                await ViewModel.DeleteTaskFromRowAsync(item);
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

    private void OnDismissStatusClicked(object? sender, RoutedEventArgs e)
    {
        ViewModel.DismissStatusMessage();
        StatusOverlay.IsVisible = false;
        e.Handled = true;
    }

    private static bool IsInteractiveElement(object? source)
    {
        if (source is Button or CheckBox or ComboBox or TextBox or SelectableTextBlock or CalendarDatePicker)
        {
            return true;
        }

        return source is Visual visual && visual.GetVisualAncestors().Any(ancestor =>
            ancestor is Button or CheckBox or ComboBox or TextBox or SelectableTextBlock or CalendarDatePicker);
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
        e.Handled = true;
        if (_taskCompletionInProgress || sender is not CheckBox { DataContext: TaskListItemViewModel item } checkbox)
        {
            return;
        }

        _taskCompletionInProgress = true;
        checkbox.IsEnabled = false;
        try
        {
            await ViewModel.ToggleSubtaskCompletionAsync(item);
        }
        finally
        {
            checkbox.IsChecked = ViewModel.VisibleSubtasks.FirstOrDefault(subtask =>
                string.Equals(subtask.Task.Id, item.Task.Id, StringComparison.Ordinal))?.IsCompleted ?? item.IsCompleted;
            checkbox.IsEnabled = true;
            _taskCompletionInProgress = false;
        }
    }

    private void OnToggleSubtasksClicked(object? sender, RoutedEventArgs e) => ViewModel.ToggleSubtasks();

    private async void OnUseSourceDatesClicked(object? sender, RoutedEventArgs e) => await ViewModel.UseSourceDatesAsync();

    private async void OnKeepOpenzaDatesClicked(object? sender, RoutedEventArgs e) => await ViewModel.KeepOpenzaDatesAsync();

    private async void OnDetailProjectPickerClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTask is null)
        {
            return;
        }

        var options = ViewModel.GetProjectOptionsForSpace(ViewModel.SelectedTask.Task.SpaceId)
            .Select(option => new PickerOption(option.ProjectId ?? string.Empty, option.Title))
            .ToList();
        var dialog = new ProjectPickerWindow(options, ViewModel.DetailProject?.ProjectId);
        var result = await dialog.ShowDialog<ProjectPickerResult?>(this);
        if (!string.IsNullOrWhiteSpace(result?.NewProjectName))
        {
            await ViewModel.CreateProjectForSelectedTaskAsync(result.NewProjectName);
            return;
        }

        if (result is null)
        {
            return;
        }

        ViewModel.DetailProject = ViewModel.ProjectOptions.FirstOrDefault(option =>
            string.Equals(option.ProjectId ?? string.Empty, result.ProjectId ?? string.Empty, StringComparison.Ordinal));
        await ViewModel.SaveSelectedAsync();
    }

    private async void OnToggleCompletionClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_taskCompletionInProgress || sender is not CheckBox checkbox)
        {
            return;
        }

        _taskCompletionInProgress = true;
        checkbox.IsEnabled = false;
        try
        {
            await ViewModel.ToggleSelectedCompletionAsync();
        }
        finally
        {
            checkbox.IsChecked = ViewModel.SelectedTask?.Task.IsCompleted;
            checkbox.IsEnabled = true;
            _taskCompletionInProgress = false;
        }
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

    private void OnDetailLabelBoxGotFocus(object? sender, RoutedEventArgs e) => DetailLabelsBox.IsDropDownOpen = true;

    private async void OnDetailLabelSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DetailLabelsBox.SelectedItem is not string label)
        {
            return;
        }

        var changed = ViewModel.AddDetailLabels(label);
        DetailLabelsBox.SelectedItem = null;
        DetailLabelsBox.Text = string.Empty;
        if (changed && _initialized && ViewModel.HasSelectedTask && !ViewModel.IsUpdatingDetails)
        {
            await ViewModel.SaveSelectedAsync();
        }
    }

    private async void OnDetailLabelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.OemComma))
        {
            return;
        }

        var changed = ViewModel.AddDetailLabels(DetailLabelsBox.Text);
        DetailLabelsBox.Text = string.Empty;
        DetailLabelsBox.SelectedItem = null;
        DetailLabelsBox.IsDropDownOpen = false;
        e.Handled = true;
        if (changed && _initialized && ViewModel.HasSelectedTask && !ViewModel.IsUpdatingDetails)
        {
            await ViewModel.SaveSelectedAsync();
        }
    }

    private async void OnRemoveDetailLabelClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: string label } || !ViewModel.RemoveDetailLabel(label))
        {
            return;
        }

        if (_initialized && ViewModel.HasSelectedTask && !ViewModel.IsUpdatingDetails)
        {
            await ViewModel.SaveSelectedAsync();
        }
        DetailLabelsBox.Focus();
    }

    private async void OnDetailEditorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initialized && ViewModel.HasSelectedTask && !ViewModel.IsUpdatingDetails)
        {
            await ViewModel.SaveSelectedAsync();
        }
    }

    private async void OnDetailDateChanged(object? sender, SelectionChangedEventArgs e)
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
            $"Delete “{ViewModel.SelectedTask.Title}” from Openza? Tasks still linked to a provider must be deleted there first.");
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
        if (e.Key == Key.Escape)
        {
            if (TaskWorkspace.IsVisible)
            {
                if (!await ViewModel.SaveSelectedAsync())
                {
                    e.Handled = true;
                    return;
                }

                ViewModel.SearchText = string.Empty;
                _taskSearchTimer.Stop();
                await ViewModel.ApplySearchAsync();
                TaskList.SelectedItem = null;
                ViewModel.SelectedTask = null;
                _connectedPaneOpen = false;
                UpdateWorkbenchLayout();
            }

            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.K)
        {
            await OpenGlobalSearchAsync();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.N)
        {
            if (!TaskWorkspace.IsVisible)
            {
                ShowTaskWorkspace();
                var inbox = ViewModel.NavigationItems.First(item => item.Kind == Openza.Tasks.Core.Data.TaskListKind.Inbox);
                await ViewModel.SelectNavigationAsync(inbox);
            }
            OnAddTaskClicked(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.F)
        {
            if (TaskWorkspace.IsVisible)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
            }
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.S)
        {
            if (TaskWorkspace.IsVisible && ViewModel.HasSelectedTask)
            {
                await ViewModel.SaveSelectedAsync();
            }
            else
            {
                await ViewModel.RunTodoistSyncAsync();
            }
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
        await ViewModel.AdoptAllConnectedTasksAsync();
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
        await _preferencesStore.UpdateAsync(preferences => preferences with { Theme = theme });
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
