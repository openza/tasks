using System.Collections.ObjectModel;
using System.Text.Json;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Services;
using Openza.Tasks.Core.Sync;
using Openza.Tasks.Desktop.Services;
using FluentIcons.Common;

namespace Openza.Tasks.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private const string TodoistTokenKey = "todoist-token";
    private readonly ITaskStore _store;
    private readonly ICredentialStore _credentials;
    private readonly HttpClient _httpClient = new();
    private readonly TaskSyncEngine _syncEngine;
    private readonly Func<string, string, ITaskProjectMoveProvider> _todoistMoveProviderFactory;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private int _detailUpdateDepth;
    private readonly GitHubIssueService _gitHubIssueService = new(new HttpClient());
    private readonly DesktopPreferencesStore _preferencesStore = new();
    private readonly Lazy<BackupService?> _backupService;
    private readonly List<ProjectItem> _projects = [];
    private readonly List<LabelItem> _labels = [];
    private readonly Dictionary<string, int> _projectCounts = new(StringComparer.Ordinal);
    private string _defaultSpaceId = SpaceIds.Default;
    private string? _currentSpaceId;
    private SpaceNavigationItemViewModel? _selectedSpace;
    private NavigationItemViewModel? _selectedNavigation;
    private ProjectNavigationItemViewModel? _selectedProject;
    private TaskListItemViewModel? _selectedTask;
    private string _quickAddTitle = string.Empty;
    private string _searchText = string.Empty;
    private string _projectSearchText = string.Empty;
    private int _projectFilterIndex;
    private string _pageTitle = "Inbox";
    private string _pageSubtitle = "Capture first. Organize when it helps.";
    private string _statusMessage = "Ready";
    private bool _isBusy;
    private string _detailTitle = string.Empty;
    private string _detailNotes = string.Empty;
    private int _detailStatusIndex;
    private int _detailPriorityIndex = 1;
    private DateTimeOffset? _detailDate;
    private DateTimeOffset? _detailDeadline;
    private string _detailLabels = string.Empty;
    private ProjectOptionViewModel? _detailProject;
    private string _newProjectName = string.Empty;
    private int _sortIndex;
    private int _sortDirectionIndex;
    private readonly List<ConnectedTaskViewModel> _allConnectedTasks = [];
    private string _todoistToken = string.Empty;
    private string _todoistConnectionText = "Token required on this Linux installation.";
    private int _priorityFilterIndex;
    private int _repeatFilterIndex;
    private int _groupIndex;
    private int _connectedSourceFilterIndex;
    private int _connectedProjectFilterIndex;
    private bool _showSkippedConnectedTasks;
    private string _connectedSearchText = string.Empty;
    private LabelOptionViewModel? _selectedLabelFilter;
    private RestorePointViewModel? _selectedRestorePoint;
    private string _gitHubToken = string.Empty;
    private string _gitHubConnectionText = "Token required on this Linux installation.";
    private string _gitHubDefaultRepository = string.Empty;
    private string _gitHubActionText = "Create GitHub issue";
    private TaskExternalLinkInfo? _selectedGitHubLink;
    private bool _automaticSyncEnabled = true;

    public MainWindowViewModel(ITaskStore store)
        : this(store, new SecretToolCredentialStore())
    {
    }

    public MainWindowViewModel(
        ITaskStore store,
        ICredentialStore credentials,
        Func<string, string, ITaskProjectMoveProvider>? todoistMoveProviderFactory = null)
    {
        _store = store;
        _credentials = credentials;
        _syncEngine = new TaskSyncEngine(store);
        _todoistMoveProviderFactory = todoistMoveProviderFactory ??
            ((token, connectionId) => new TodoistProvider(_httpClient, token, connectionId));
        _backupService = new Lazy<BackupService?>(() => store is SqliteTaskStore sqliteStore
            ? new BackupService(
                sqliteStore.DatabasePath,
                DesktopDataPaths.RestorePointDirectory,
                context: new BackupContext("Openza.Tasks.Desktop", "desktop", CurrentAppVersion))
            : null);
    }

    private static string CurrentAppVersion =>
        typeof(MainWindowViewModel).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion.Split('+')[0]
        ?? typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3)
        ?? "unknown";

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; } =
    [
        new("Inbox", TaskListKind.Inbox, Icon.MailInbox),
        new("Next Actions", TaskListKind.NextActions, Icon.StarArrowRight),
        new("Today", TaskListKind.Today, Icon.CalendarToday),
        new("Calendar", TaskListKind.Calendar, Icon.Calendar),
        new("Overdue", TaskListKind.Overdue, Icon.ClockWarning),
        new("Waiting For", TaskListKind.Waiting, Icon.ClockPause),
        new("Someday", TaskListKind.Someday, Icon.ClockAlarm),
        new("Tasks", TaskListKind.Open, Icon.TaskListSquare),
        new("Completed", TaskListKind.Completed, Icon.CheckmarkCircle),
    ];

    public ObservableCollection<ProjectNavigationItemViewModel> ProjectItems { get; } = [];
    public ObservableCollection<ProjectOptionViewModel> ProjectOptions { get; } = [];
    public ObservableCollection<SpaceNavigationItemViewModel> SpaceItems { get; } = [];
    public ObservableCollection<SpaceNavigationItemViewModel> EditableSpaceItems { get; } = [];
    public ObservableCollection<TaskListItemViewModel> Tasks { get; } = [];
    public ObservableCollection<TaskListEntryViewModel> TaskEntries { get; } = [];
    public ObservableCollection<LabelOptionViewModel> LabelFilterOptions { get; } = [];
    public ObservableCollection<string> LabelSuggestions { get; } = [];
    public ObservableCollection<ConnectedTaskViewModel> FilteredConnectedTasks { get; } = [];
    public ObservableCollection<string> ConnectedSourceOptions { get; } = ["All sources"];
    public ObservableCollection<string> ConnectedProjectOptions { get; } = ["All lists"];
    public ObservableCollection<TaskListItemViewModel> Subtasks { get; } = [];
    public ObservableCollection<RestorePointViewModel> RestorePoints { get; } = [];
    public ObservableCollection<TodoistRoutingRuleViewModel> TodoistRoutingRules { get; } = [];
    public ObservableCollection<TodoistRoutingChoiceViewModel> TodoistRoutingLabelChoices { get; } = [];
    public ObservableCollection<TodoistRoutingChoiceViewModel> TodoistRoutingSpaceChoices { get; } = [];
    public ObservableCollection<TodoistRoutingChoiceViewModel> TodoistRoutingProjectChoices { get; } = [];

    public SpaceNavigationItemViewModel? SelectedSpace
    {
        get => _selectedSpace;
        set => SetProperty(ref _selectedSpace, value);
    }

    public NavigationItemViewModel? SelectedNavigation
    {
        get => _selectedNavigation;
        set => SetProperty(ref _selectedNavigation, value);
    }

    public ProjectNavigationItemViewModel? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (SetProperty(ref _selectedProject, value))
            {
                OnPropertyChanged(nameof(HasSelectedProject));
            }
        }
    }

    public bool HasSelectedProject => SelectedProject is not null;

    public TaskListItemViewModel? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (SetProperty(ref _selectedTask, value))
            {
                using var detailUpdate = BeginDetailUpdate();
                LoadDetails(value?.Task);
                OnPropertyChanged(nameof(HasSelectedTask));
                OnPropertyChanged(nameof(HasNoSelectedTask));
                OnPropertyChanged(nameof(CompletionActionText));
            }
        }
    }

    public bool HasSelectedTask => SelectedTask is not null;
    public bool HasNoSelectedTask => !HasSelectedTask;
    public string CompletionActionText => SelectedTask?.Task.IsCompleted == true ? "Reopen" : "Complete";
    public bool HasNoTasks => Tasks.Count == 0;
    public string DatabasePath => (_store as SqliteTaskStore)?.DatabasePath ?? "Custom data store";
    public bool HasNoConnectedTasks => FilteredConnectedTasks.Count == 0;
    public bool HasSubtasks => Subtasks.Count > 0;
    public bool HasNoTodoistRoutingRules => TodoistRoutingRules.Count == 0;
    public bool IsUpdatingDetails => _detailUpdateDepth > 0;

    public bool AutomaticSyncEnabled
    {
        get => _automaticSyncEnabled;
        private set => SetProperty(ref _automaticSyncEnabled, value);
    }

    public int ConnectedSourceFilterIndex
    {
        get => _connectedSourceFilterIndex;
        set => SetProperty(ref _connectedSourceFilterIndex, value);
    }

    public int ConnectedProjectFilterIndex
    {
        get => _connectedProjectFilterIndex;
        set => SetProperty(ref _connectedProjectFilterIndex, value);
    }

    public bool ShowSkippedConnectedTasks
    {
        get => _showSkippedConnectedTasks;
        set => SetProperty(ref _showSkippedConnectedTasks, value);
    }

    public RestorePointViewModel? SelectedRestorePoint
    {
        get => _selectedRestorePoint;
        set => SetProperty(ref _selectedRestorePoint, value);
    }

    public string GitHubToken
    {
        get => _gitHubToken;
        set => SetProperty(ref _gitHubToken, value);
    }

    public string GitHubConnectionText
    {
        get => _gitHubConnectionText;
        private set => SetProperty(ref _gitHubConnectionText, value);
    }

    public string GitHubDefaultRepository
    {
        get => _gitHubDefaultRepository;
        set => SetProperty(ref _gitHubDefaultRepository, value);
    }

    public string GitHubActionText
    {
        get => _gitHubActionText;
        private set => SetProperty(ref _gitHubActionText, value);
    }

    public string TodoistToken
    {
        get => _todoistToken;
        set => SetProperty(ref _todoistToken, value);
    }

    public string TodoistConnectionText
    {
        get => _todoistConnectionText;
        private set => SetProperty(ref _todoistConnectionText, value);
    }

    public int PriorityFilterIndex
    {
        get => _priorityFilterIndex;
        set => SetProperty(ref _priorityFilterIndex, value);
    }

    public int RepeatFilterIndex
    {
        get => _repeatFilterIndex;
        set => SetProperty(ref _repeatFilterIndex, value);
    }

    public int GroupIndex
    {
        get => _groupIndex;
        set => SetProperty(ref _groupIndex, value);
    }

    public LabelOptionViewModel? SelectedLabelFilter
    {
        get => _selectedLabelFilter;
        set => SetProperty(ref _selectedLabelFilter, value);
    }

    public string QuickAddTitle
    {
        get => _quickAddTitle;
        set => SetProperty(ref _quickAddTitle, value);
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string ProjectSearchText
    {
        get => _projectSearchText;
        set => SetProperty(ref _projectSearchText, value);
    }

    public int ProjectFilterIndex
    {
        get => _projectFilterIndex;
        set => SetProperty(ref _projectFilterIndex, value);
    }

    public string PageTitle
    {
        get => _pageTitle;
        private set => SetProperty(ref _pageTitle, value);
    }

    public string PageSubtitle
    {
        get => _pageSubtitle;
        private set => SetProperty(ref _pageSubtitle, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string DetailTitle
    {
        get => _detailTitle;
        set => SetProperty(ref _detailTitle, value);
    }

    public string DetailNotes
    {
        get => _detailNotes;
        set => SetProperty(ref _detailNotes, value);
    }

    public int DetailStatusIndex
    {
        get => _detailStatusIndex;
        set => SetProperty(ref _detailStatusIndex, value);
    }

    public int DetailPriorityIndex
    {
        get => _detailPriorityIndex;
        set => SetProperty(ref _detailPriorityIndex, value);
    }

    public DateTimeOffset? DetailDate
    {
        get => _detailDate;
        set => SetProperty(ref _detailDate, value);
    }

    public DateTimeOffset? DetailDeadline
    {
        get => _detailDeadline;
        set => SetProperty(ref _detailDeadline, value);
    }

    public string DetailLabels
    {
        get => _detailLabels;
        set => SetProperty(ref _detailLabels, value);
    }

    public ProjectOptionViewModel? DetailProject
    {
        get => _detailProject;
        set => SetProperty(ref _detailProject, value);
    }

    public string NewProjectName
    {
        get => _newProjectName;
        set => SetProperty(ref _newProjectName, value);
    }

    public int SortIndex
    {
        get => _sortIndex;
        set => SetProperty(ref _sortIndex, value);
    }

    public int SortDirectionIndex
    {
        get => _sortDirectionIndex;
        set => SetProperty(ref _sortDirectionIndex, value);
    }

    public async Task InitializeAsync()
    {
        await RunBusyAsync(async () =>
        {
            await _store.InitializeAsync();
            await EnsureDailyRestorePointAsync();
            var spaces = await _store.GetSpacesAsync();
            _defaultSpaceId = spaces.FirstOrDefault()?.Id ?? SpaceIds.Default;
            LoadSpaceCollections(spaces);
            var preferences = _preferencesStore.Load();
            AutomaticSyncEnabled = preferences.AutomaticSyncEnabled;
            var preferredSpaceId = preferences.SelectedSpaceId;
            var initialSpace = SpaceItems.FirstOrDefault(item => item.SpaceId == preferredSpaceId && item.Space is not null);
            if (initialSpace is null && spaces.Count > 0)
            {
                var rankedSpaces = new List<(string SpaceId, int OpenTasks)>();
                foreach (var space in spaces)
                {
                    var openTasks = await _store.GetTasksAsync(new TaskQuery
                    {
                        SpaceId = space.Id,
                        Kind = TaskListKind.Open,
                    });
                    rankedSpaces.Add((space.Id, openTasks.Count));
                }
                initialSpace = SpaceItems.FirstOrDefault(item =>
                    item.SpaceId == rankedSpaces.OrderByDescending(entry => entry.OpenTasks).First().SpaceId);
            }
            SelectedSpace = initialSpace ?? SpaceItems[0];
            _currentSpaceId = SelectedSpace.SpaceId;
            SelectedNavigation = NavigationItems[0];
            await RefreshAsyncCore();
            StatusMessage = $"Local data · {_store.GetType().Name.Replace("TaskStore", string.Empty, StringComparison.Ordinal)}";
        });
    }

    public async Task SelectNavigationAsync(NavigationItemViewModel item)
    {
        SelectedNavigation = item;
        SelectedProject = null;
        PageTitle = item.Title;
        PageSubtitle = SubtitleFor(item.Kind);
        await RefreshAsync();
    }

    public async Task SelectSpaceAsync(SpaceNavigationItemViewModel item)
    {
        SelectedSpace = item;
        _currentSpaceId = item.SpaceId;
        SelectedProject = null;
        PageSubtitle = item.Space is null
            ? "Tasks from every space."
            : $"Tasks in {item.Title}.";
        await RefreshAsync();
    }

    public Task<IReadOnlyList<GlobalSearchResult>> SearchGloballyAsync(string searchText) =>
        _store.SearchAsync(new GlobalSearchQuery
        {
            SearchText = searchText,
            IncludeAllSpaces = true,
            IncludeCompletedTasks = true,
            Limit = 50,
        });

    public async Task OpenGlobalSearchResultAsync(GlobalSearchResult result)
    {
        var resultTask = result.Kind == GlobalSearchResultKind.Task
            ? await _store.GetTaskAsync(result.Id)
            : null;
        var space = SpaceItems.FirstOrDefault(item => item.SpaceId == result.SpaceId && item.Space is not null);
        if (space is not null)
        {
            SelectedSpace = space;
            _currentSpaceId = space.SpaceId;
        }

        SelectedProject = null;
        SelectedNavigation = NavigationItems.First(item => item.Kind == (resultTask?.IsCompleted == true
            ? TaskListKind.Completed
            : TaskListKind.Open));
        PageTitle = SelectedNavigation.Title;
        PageSubtitle = SubtitleFor(SelectedNavigation.Kind);
        await RefreshAsyncCore(result.Kind == GlobalSearchResultKind.Task ? result.Id : null);

        if (result.Kind == GlobalSearchResultKind.Project)
        {
            var project = ProjectItems.FirstOrDefault(item => item.Project.Id == result.Id);
            if (project is not null)
            {
                SelectedProject = project;
                SelectedNavigation = null;
                PageTitle = project.Title;
                PageSubtitle = "Project tasks";
                await RefreshAsyncCore();
            }
        }
    }

    public async Task CreateSpaceAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            StatusMessage = "Enter a space name.";
            return;
        }

        await RunBusyAsync(async () =>
        {
            var spaces = await _store.GetSpacesAsync(includeArchived: true);
            if (spaces.Any(space => string.Equals(space.Name, name, StringComparison.CurrentCultureIgnoreCase) && !space.IsArchived))
            {
                StatusMessage = "A space with that name already exists.";
                return;
            }

            await _store.UpsertSpaceAsync(new SpaceItem
            {
                Id = $"space_{Guid.NewGuid():N}",
                Name = name,
                Color = "#6B8AFD",
                SortOrder = spaces.Count,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await ReloadSpacesAsync();
            StatusMessage = "Space created";
        });
    }

    public async Task ArchiveSpaceAsync(SpaceNavigationItemViewModel item)
    {
        if (item.Space is null)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var activeSpaces = await _store.GetSpacesAsync();
            if (activeSpaces.Count <= 1)
            {
                StatusMessage = "The last active space cannot be archived.";
                return;
            }

            await _store.UpsertSpaceAsync(item.Space with
            {
                IsArchived = true,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            _currentSpaceId = null;
            await ReloadSpacesAsync();
            SelectedSpace = SpaceItems[0];
            StatusMessage = "Space archived";
            await RefreshAsyncCore();
        });
    }

    public async Task RenameSpaceAsync(SpaceNavigationItemViewModel item, string name)
    {
        if (item.Space is null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            await _store.UpsertSpaceAsync(item.Space with
            {
                Name = name.Trim(),
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await ReloadSpacesAsync();
            SelectedSpace = SpaceItems.FirstOrDefault(space => space.SpaceId == _currentSpaceId) ?? SpaceItems[0];
            StatusMessage = "Space renamed";
        });
    }

    public async Task SelectProjectAsync(ProjectNavigationItemViewModel item)
    {
        SelectedProject = item;
        SelectedNavigation = null;
        PageTitle = item.Title;
        PageSubtitle = "Project tasks";
        await RefreshAsync();
    }

    public Task ApplySearchAsync() => RefreshAsync();

    public Task ApplyListOptionsAsync() => RefreshAsync();

    public void ApplyProjectFilter() => RebuildProjectItems();

    public async Task ImportMarkdownAsync(string path)
    {
        await RunBusyAsync(async () =>
        {
            var markdown = await File.ReadAllTextAsync(path);
            var parsedTasks = MarkdownTaskParser.Parse(markdown);
            if (parsedTasks.Count == 0)
            {
                StatusMessage = "No Markdown checkbox tasks were found.";
                return;
            }

            if (_backupService.Value is { } backupService)
            {
                await backupService.CreateBackupAsync(BackupReasons.PreImport);
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var parsed in parsedTasks)
            {
                await _store.UpsertTaskAsync(new TaskItem
                {
                    Id = $"local_{Guid.NewGuid():N}",
                    SpaceId = _currentSpaceId ?? _defaultSpaceId,
                    IntegrationId = IntegrationIds.Local,
                    Title = parsed.Title,
                    Status = parsed.IsCompleted ? TaskItemStatus.Completed : TaskItemStatus.Inbox,
                    CompletedAt = parsed.IsCompleted ? now : null,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            StatusMessage = $"Imported {parsedTasks.Count} task{(parsedTasks.Count == 1 ? string.Empty : "s")}";
            await RefreshAsyncCore();
        });
    }

    public async Task ExportMarkdownAsync(string path)
    {
        await RunBusyAsync(async () =>
        {
            var tasks = await _store.GetTasksAsync(new TaskQuery
            {
                SpaceId = _currentSpaceId,
                Kind = TaskListKind.All,
                IncludeSubtasks = true,
            });
            var projects = await _store.GetProjectsAsync(_currentSpaceId, includeArchived: true);
            var labels = await _store.GetLabelsAsync();
            await File.WriteAllTextAsync(path, MarkdownExporter.Export(tasks, projects, labels));
            StatusMessage = $"Exported {tasks.Count} tasks";
        });
    }

    public async Task CreateRestorePointAsync()
    {
        if (_backupService.Value is not { } backupService)
        {
            StatusMessage = "Restore points are unavailable for this data store.";
            return;
        }

        await RunBusyAsync(async () =>
        {
            var path = await backupService.CreateBackupAsync();
            StatusMessage = $"Restore point created · {Path.GetFileName(path)}";
            LoadRestorePointsCore();
        });
    }

    public void LoadRestorePoints()
    {
        LoadRestorePointsCore();
    }

    public async Task RestoreSelectedPointAsync()
    {
        if (SelectedRestorePoint is null)
        {
            return;
        }

        await RestoreDatabaseAsync(SelectedRestorePoint.Backup.Path);
        LoadRestorePointsCore();
    }

    public async Task DeleteSelectedRestorePointAsync()
    {
        if (_backupService.Value is not { } backupService || SelectedRestorePoint is null)
        {
            return;
        }

        var path = SelectedRestorePoint.Backup.Path;
        await RunBusyAsync(async () =>
        {
            await backupService.DeleteBackupAsync(path);
            StatusMessage = "Restore point deleted";
            LoadRestorePointsCore();
        });
    }

    public async Task ExportDatabaseAsync(string path)
    {
        if (_backupService.Value is not { } backupService)
        {
            StatusMessage = "Database export is unavailable for this data store.";
            return;
        }

        await RunBusyAsync(async () =>
        {
            await backupService.ExportDatabaseAsync(path);
            StatusMessage = "Backup file exported";
        });
    }

    public async Task RestoreDatabaseAsync(string path)
    {
        if (_backupService.Value is not { } backupService)
        {
            StatusMessage = "Database restore is unavailable for this data store.";
            return;
        }

        await RunBusyAsync(async () =>
        {
            await backupService.RestoreBackupAsync(path);
            await _store.InitializeAsync();
            var spaces = await _store.GetSpacesAsync();
            _defaultSpaceId = spaces.FirstOrDefault()?.Id ?? SpaceIds.Default;
            LoadSpaceCollections(spaces);
            var preferredSpaceId = _preferencesStore.Load().SelectedSpaceId;
            SelectedSpace = SpaceItems.FirstOrDefault(item =>
                    item.Space is not null && item.SpaceId == preferredSpaceId)
                ?? SpaceItems.FirstOrDefault(item => item.Space is not null)
                ?? SpaceItems[0];
            _currentSpaceId = SelectedSpace.SpaceId;
            SelectedProject = null;
            SelectedTask = null;
            SelectedNavigation = NavigationItems[0];
            PageTitle = SelectedNavigation.Title;
            PageSubtitle = SubtitleFor(SelectedNavigation.Kind);
            StatusMessage = "Database restored; a safety restore point was created";
            await RefreshAsyncCore();
        });
    }

    public async Task LoadConnectedTasksAsync()
    {
        await RunBusyAsync(async () =>
        {
            var items = await _store.GetProviderSourceItemsAsync(
                spaceId: _currentSpaceId,
                includeAdopted: false,
                includeIgnored: true);
            _allConnectedTasks.Clear();
            _allConnectedTasks.AddRange(items.Select(item => new ConnectedTaskViewModel(item)));
            RebuildConnectedFilterOptions();
            FilterConnectedTasks(string.Empty);
            StatusMessage = $"{items.Count} connected task{(items.Count == 1 ? string.Empty : "s")} waiting";
        });
    }

    public async Task RefreshTodoistConnectionAsync()
    {
        try
        {
            var token = await _credentials.GetAsync(TodoistTokenKey);
            TodoistConnectionText = string.IsNullOrWhiteSpace(token)
                ? "Token required on this Linux installation. Windows Credential Locker secrets are not copied."
                : "Connected securely through the desktop Secret Service.";
        }
        catch (Exception exception)
        {
            TodoistConnectionText = exception.Message;
        }
    }

    public async Task RefreshTodoistRoutingRulesAsync() =>
        await RunBusyAsync(LoadTodoistRoutingRulesCoreAsync);

    public async Task SaveTodoistRoutingRuleAsync(TodoistRoutingRuleDraft draft)
    {
        var normalizedLabel = TodoistRoutingRuleCodec.NormalizeLabel(draft.Label);
        if (!draft.MatchNoLabels && string.IsNullOrWhiteSpace(normalizedLabel))
        {
            StatusMessage = "Choose the Todoist label this rule should match.";
            return;
        }

        await RunBusyAsync(async () =>
        {
            var spaces = await _store.GetSpacesAsync();
            if (spaces.All(space => !string.Equals(space.Id, draft.SpaceId, StringComparison.Ordinal)))
            {
                StatusMessage = "Choose where matching Todoist tasks should go.";
                return;
            }

            var routes = await _store.GetSyncRoutesAsync();
            var existingRoute = routes.FirstOrDefault(route =>
                string.Equals(route.Id, TodoistRoutingRuleCodec.RouteId, StringComparison.Ordinal));
            var settings = TodoistRoutingRuleCodec.Read(existingRoute?.SettingsJson);
            var postImport = string.IsNullOrWhiteSpace(draft.MoveToProjectId)
                ? null
                : new TodoistRoutingPostImport(draft.MoveToProjectId);
            var labelRoutes = settings.LabelRoutes;
            var unlabeledRoute = settings.UnlabeledRoute;

            if (draft.MatchNoLabels)
            {
                labelRoutes = labelRoutes
                    .Where(rule => !string.Equals(rule.Id, draft.Id, StringComparison.Ordinal))
                    .ToList();
                unlabeledRoute = new TodoistRoutingRule(
                    TodoistRoutingRuleCodec.UnlabeledRuleId,
                    string.Empty,
                    draft.SpaceId,
                    postImport);
            }
            else
            {
                var id = string.IsNullOrWhiteSpace(draft.Id)
                    ? $"todoist_rule_{Guid.NewGuid():N}"
                    : draft.Id;
                if (string.Equals(id, TodoistRoutingRuleCodec.UnlabeledRuleId, StringComparison.Ordinal))
                {
                    id = $"todoist_rule_{Guid.NewGuid():N}";
                    unlabeledRoute = null;
                }

                var nextRule = new TodoistRoutingRule(id, normalizedLabel, draft.SpaceId, postImport);
                labelRoutes = labelRoutes
                    .Where(rule => !string.Equals(rule.Id, id, StringComparison.Ordinal) &&
                        !string.Equals(rule.Label, normalizedLabel, StringComparison.OrdinalIgnoreCase))
                    .Append(nextRule)
                    .OrderBy(rule => rule.Label, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }

            var nextSettings = new TodoistRoutingRuleSettings(labelRoutes, unlabeledRoute);
            var now = DateTimeOffset.UtcNow;
            await _store.UpsertSyncRouteAsync(new SyncRouteInfo
            {
                Id = TodoistRoutingRuleCodec.RouteId,
                Name = "Todoist label rules",
                SourceConnectionId = "todoist_default",
                Mode = "one_way",
                Visibility = "optional",
                IsEnabled = nextSettings.LabelRoutes.Count > 0 || nextSettings.UnlabeledRoute is not null,
                SettingsJson = TodoistRoutingRuleCodec.Write(nextSettings),
                CreatedAt = existingRoute?.CreatedAt ?? now,
                UpdatedAt = now,
            });
            await LoadTodoistRoutingRulesCoreAsync();
            StatusMessage = draft.MatchNoLabels
                ? "Todoist rule saved · tasks with no labels will use the selected Space"
                : $"Todoist rule saved · @{normalizedLabel} will use the selected Space";
        });
    }

    public async Task DeleteTodoistRoutingRuleAsync(string id)
    {
        await RunBusyAsync(async () =>
        {
            var routes = await _store.GetSyncRoutesAsync();
            var existingRoute = routes.FirstOrDefault(route =>
                string.Equals(route.Id, TodoistRoutingRuleCodec.RouteId, StringComparison.Ordinal));
            var settings = TodoistRoutingRuleCodec.Read(existingRoute?.SettingsJson);
            var labelRoutes = settings.LabelRoutes
                .Where(rule => !string.Equals(rule.Id, id, StringComparison.Ordinal))
                .ToList();
            var unlabeledRoute = string.Equals(settings.UnlabeledRoute?.Id, id, StringComparison.Ordinal)
                ? null
                : settings.UnlabeledRoute;
            var nextSettings = new TodoistRoutingRuleSettings(labelRoutes, unlabeledRoute);
            var now = DateTimeOffset.UtcNow;
            await _store.UpsertSyncRouteAsync(new SyncRouteInfo
            {
                Id = TodoistRoutingRuleCodec.RouteId,
                Name = "Todoist label rules",
                SourceConnectionId = "todoist_default",
                Mode = "one_way",
                Visibility = "optional",
                IsEnabled = nextSettings.LabelRoutes.Count > 0 || nextSettings.UnlabeledRoute is not null,
                SettingsJson = TodoistRoutingRuleCodec.Write(nextSettings),
                CreatedAt = existingRoute?.CreatedAt ?? now,
                UpdatedAt = now,
            });
            await LoadTodoistRoutingRulesCoreAsync();
            StatusMessage = "Todoist rule deleted";
        });
    }

    private async Task LoadTodoistRoutingRulesCoreAsync()
    {
        var routes = await _store.GetSyncRoutesAsync();
        var route = routes.FirstOrDefault(item =>
            string.Equals(item.Id, TodoistRoutingRuleCodec.RouteId, StringComparison.Ordinal));
        var settings = TodoistRoutingRuleCodec.Read(route?.SettingsJson);
        var spaces = await _store.GetSpacesAsync();
        var providerProjects = await _store.GetProviderProjectsAsync(IntegrationIds.Todoist);
        var providerLabels = await _store.GetProviderLabelsAsync(IntegrationIds.Todoist);

        ReplaceItems(
            TodoistRoutingLabelChoices,
            providerLabels
                .OrderBy(label => label.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(label => new TodoistRoutingChoiceViewModel(label.Name, $"@{label.Name}")));
        ReplaceItems(
            TodoistRoutingSpaceChoices,
            spaces
                .OrderBy(space => space.SortOrder)
                .ThenBy(space => space.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(space => new TodoistRoutingChoiceViewModel(space.Id, space.Name)));
        ReplaceItems(
            TodoistRoutingProjectChoices,
            providerProjects
                .Where(project => !string.IsNullOrWhiteSpace(project.ExternalId))
                .OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(project => new TodoistRoutingChoiceViewModel(project.ExternalId!, project.Name)));

        var spaceNames = TodoistRoutingSpaceChoices.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal);
        var projectNames = TodoistRoutingProjectChoices.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal);
        var items = settings.LabelRoutes
            .Select(rule => BuildTodoistRoutingRuleViewModel(rule, false, spaceNames, projectNames))
            .ToList();
        if (settings.UnlabeledRoute is { } unlabeledRoute)
        {
            items.Insert(0, BuildTodoistRoutingRuleViewModel(unlabeledRoute, true, spaceNames, projectNames));
        }

        ReplaceItems(TodoistRoutingRules, items);
        OnPropertyChanged(nameof(HasNoTodoistRoutingRules));
    }

    private static TodoistRoutingRuleViewModel BuildTodoistRoutingRuleViewModel(
        TodoistRoutingRule rule,
        bool matchNoLabels,
        IReadOnlyDictionary<string, string> spaceNames,
        IReadOnlyDictionary<string, string> projectNames) => new()
    {
        Id = rule.Id,
        Label = rule.Label,
        SpaceId = rule.SpaceId,
        SpaceName = spaceNames.GetValueOrDefault(rule.SpaceId, "Unknown Space"),
        MoveToProjectId = rule.PostImport?.MoveToProjectId,
        MoveToProjectName = rule.PostImport?.MoveToProjectId is { } projectId
            ? projectNames.GetValueOrDefault(projectId, "Todoist project")
            : null,
        MatchNoLabels = matchNoLabels,
    };

    private static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    public async Task RefreshGitHubConnectionAsync()
    {
        try
        {
            var token = await _credentials.GetAsync(GitHubIssueService.TokenKey);
            var connections = await _store.GetProviderConnectionsAsync();
            var connection = connections.FirstOrDefault(item => item.IntegrationId == IntegrationIds.GitHub);
            var settings = GitHubIssueService.ReadSettings(connection?.SettingsJson);
            GitHubDefaultRepository = settings.DefaultRepositoryFullName;
            GitHubConnectionText = string.IsNullOrWhiteSpace(token)
                ? "Token required on this Linux installation. Existing issue links remain available."
                : string.IsNullOrWhiteSpace(settings.Username)
                    ? "Connected securely through the desktop Secret Service."
                    : $"Connected as {settings.Username}.";
        }
        catch (Exception exception)
        {
            GitHubConnectionText = exception.Message;
        }
    }

    public async Task ConnectGitHubAsync()
    {
        var token = GitHubToken.Trim();
        if (token.Length == 0)
        {
            StatusMessage = "Paste a GitHub token first.";
            return;
        }

        await RunBusyAsync(async () =>
        {
            var validation = await _gitHubIssueService.ValidateTokenAsync(token);
            if (!validation.Success)
            {
                StatusMessage = validation.Error ?? "GitHub rejected the token.";
                return;
            }

            await _credentials.SaveAsync(GitHubIssueService.TokenKey, token);
            var settings = new GitHubConnectionSettings
            {
                Username = validation.Username ?? string.Empty,
                DefaultRepositoryFullName = GitHubDefaultRepository.Trim(),
                ConnectedAt = DateTimeOffset.UtcNow,
                LastStatus = "Connected",
            };
            await _store.UpsertProviderConnectionAsync(new ProviderConnectionInfo
            {
                Id = GitHubIssueService.DefaultConnectionId,
                IntegrationId = IntegrationIds.GitHub,
                DisplayName = "GitHub",
                AccountKey = validation.Username,
                Status = "connected",
                SettingsJson = GitHubIssueService.WriteSettings(settings),
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await _store.SetIntegrationConfiguredAsync(IntegrationIds.GitHub, true);
            GitHubToken = string.Empty;
            GitHubConnectionText = string.IsNullOrWhiteSpace(validation.Username)
                ? "GitHub connected."
                : $"Connected as {validation.Username}.";
            StatusMessage = "GitHub connected";
        });
    }

    public async Task DisconnectGitHubAsync()
    {
        await RunBusyAsync(async () =>
        {
            await _credentials.RemoveAsync(GitHubIssueService.TokenKey);
            await _store.UpsertProviderConnectionAsync(new ProviderConnectionInfo
            {
                Id = GitHubIssueService.DefaultConnectionId,
                IntegrationId = IntegrationIds.GitHub,
                DisplayName = "GitHub",
                Status = "disconnected",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await _store.SetIntegrationConfiguredAsync(IntegrationIds.GitHub, false);
            GitHubConnectionText = "Not connected on this Linux installation.";
            StatusMessage = "GitHub disconnected; existing issue links remain";
        });
    }

    public async Task SaveGitHubDefaultRepositoryAsync()
    {
        var value = GitHubDefaultRepository.Trim();
        if (!TryParseRepository(value, out _, out _))
        {
            StatusMessage = "Use the GitHub repository format owner/name.";
            return;
        }

        await RunBusyAsync(async () =>
        {
            var connections = await _store.GetProviderConnectionsAsync();
            var connection = connections.FirstOrDefault(item => item.IntegrationId == IntegrationIds.GitHub);
            var settings = GitHubIssueService.ReadSettings(connection?.SettingsJson) with
            {
                DefaultRepositoryFullName = value,
            };
            await _store.UpsertProviderConnectionAsync((connection ?? new ProviderConnectionInfo
            {
                Id = GitHubIssueService.DefaultConnectionId,
                IntegrationId = IntegrationIds.GitHub,
                DisplayName = "GitHub",
            }) with
            {
                SettingsJson = GitHubIssueService.WriteSettings(settings),
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            StatusMessage = "Default GitHub repository saved";
        });
    }

    public async Task ConnectTodoistAsync()
    {
        var token = TodoistToken.Trim();
        if (token.Length == 0)
        {
            StatusMessage = "Paste a Todoist API token first.";
            return;
        }

        await RunBusyAsync(async () =>
        {
            try
            {
                await new TodoistProvider(_httpClient, token).ValidateAccessAsync();
            }
            catch (HttpRequestException exception)
            {
                StatusMessage = exception.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    ? "Todoist rejected that token. Check it and try again."
                    : $"Could not validate Todoist: {exception.Message}";
                return;
            }

            await _credentials.SaveAsync(TodoistTokenKey, token);
            await _store.SetIntegrationConfiguredAsync(IntegrationIds.Todoist, true);
            await _store.SetIntegrationActiveAsync(IntegrationIds.Todoist, true);
            TodoistToken = string.Empty;
            TodoistConnectionText = "Connected securely through the desktop Secret Service.";
            StatusMessage = "Todoist connected";
        });
    }

    public async Task DisconnectTodoistAsync()
    {
        await RunBusyAsync(async () =>
        {
            await _credentials.RemoveAsync(TodoistTokenKey);
            await _store.SetIntegrationConfiguredAsync(IntegrationIds.Todoist, false);
            await _store.SetIntegrationActiveAsync(IntegrationIds.Todoist, false);
            TodoistConnectionText = "Not connected on this Linux installation.";
            StatusMessage = "Todoist disconnected; imported tasks remain local";
        });
    }

    public async Task RunTodoistSyncAsync()
    {
        await RunTodoistSyncAsync(showMissingConnection: true);
    }

    public async Task RunAutomaticTodoistSyncAsync()
    {
        if (!AutomaticSyncEnabled)
        {
            return;
        }

        if (!await SaveSelectedAsync())
        {
            return;
        }

        await RunTodoistSyncAsync(showMissingConnection: false);
    }

    public void SetAutomaticSyncEnabled(bool enabled) => AutomaticSyncEnabled = enabled;

    private async Task RunTodoistSyncAsync(bool showMissingConnection)
    {
        await RunBusyAsync(async () =>
        {
            var token = await _credentials.GetAsync(TodoistTokenKey);
            if (string.IsNullOrWhiteSpace(token))
            {
                if (showMissingConnection)
                {
                    StatusMessage = "Connect Todoist in Settings before syncing.";
                }
                return;
            }

            StatusMessage = "Syncing Todoist…";
            var summary = await _syncEngine.SyncAsync(new TodoistProvider(_httpClient, token));
            if (!summary.Success)
            {
                StatusMessage = $"Todoist sync failed: {summary.Error}";
                return;
            }

            await LoadConnectedTasksCoreAsync();
            await RefreshAsyncCore(SelectedTask?.Task.Id);
            await LoadTodoistRoutingRulesCoreAsync();
            StatusMessage = $"Todoist synced · {summary.TasksAdded} new, {summary.TasksUpdated} updated, {summary.CompletionsSynced} completions, {summary.DateUpdatesSynced} date changes";
        });
    }

    public void FilterConnectedTasks(string searchText)
    {
        _connectedSearchText = searchText.Trim();
        var source = ConnectedSourceFilterIndex > 0 && ConnectedSourceFilterIndex < ConnectedSourceOptions.Count
            ? ConnectedSourceOptions[ConnectedSourceFilterIndex]
            : null;
        var project = ConnectedProjectFilterIndex > 0 && ConnectedProjectFilterIndex < ConnectedProjectOptions.Count
            ? ConnectedProjectOptions[ConnectedProjectFilterIndex]
            : null;
        FilteredConnectedTasks.Clear();
        foreach (var item in _allConnectedTasks.Where(item =>
                     (ShowSkippedConnectedTasks || !item.Source.IsSkipped) &&
                     (source is null || string.Equals(item.Source.SourceName, source, StringComparison.CurrentCultureIgnoreCase)) &&
                     (project is null || string.Equals(item.Source.SourceProjectName, project, StringComparison.CurrentCultureIgnoreCase)) &&
                     (_connectedSearchText.Length == 0 ||
                      item.Title.Contains(_connectedSearchText, StringComparison.CurrentCultureIgnoreCase) ||
                      item.SourceText.Contains(_connectedSearchText, StringComparison.CurrentCultureIgnoreCase))))
        {
            FilteredConnectedTasks.Add(item);
        }
        OnPropertyChanged(nameof(HasNoConnectedTasks));
    }

    public void ApplyConnectedFilters() => FilterConnectedTasks(_connectedSearchText);

    public async Task AdoptConnectedTaskAsync(ConnectedTaskViewModel item)
    {
        await RunBusyAsync(async () =>
        {
            var targetSpaceId = _currentSpaceId ?? item.Source.SuggestedSpaceId ?? _defaultSpaceId;
            var task = await _store.AdoptProviderSourceItemAsync(item.Source.Id, targetSpaceId);
            if (task is null)
            {
                StatusMessage = "That connected task is no longer available.";
            }
            else
            {
                var filing = await TryApplyTodoistPostImportFilingAsync(item.Source);
                StatusMessage = filing switch
                {
                    { Applied: true } => "Task added to Openza and filed in Todoist",
                    { Error.Length: > 0 } => $"Task added to Openza, but Todoist filing failed: {filing.Error}",
                    _ => "Task added to Openza",
                };
            }
            await LoadConnectedTasksCoreAsync();
            await RefreshAsyncCore(task?.Id);
        });
    }

    private async Task<TodoistFilingResult> TryApplyTodoistPostImportFilingAsync(ProviderSourceItem source)
    {
        if (!string.Equals(source.IntegrationId, IntegrationIds.Todoist, StringComparison.Ordinal))
        {
            return TodoistFilingResult.NotApplicable;
        }

        var routes = await _store.GetSyncRoutesAsync();
        var routingPolicy = ProviderSourceRoutingPolicy.FromRoutes(
            routes,
            source.ProviderConnectionId,
            source.IntegrationId);
        var action = routingPolicy.Match(source).PostImportAction;
        if (action is null || string.IsNullOrWhiteSpace(action.MoveToProjectId))
        {
            return TodoistFilingResult.NotApplicable;
        }

        try
        {
            var token = await _credentials.GetAsync(TodoistTokenKey);
            if (string.IsNullOrWhiteSpace(token))
            {
                return new TodoistFilingResult(false, "Reconnect Todoist in Settings and try again.");
            }

            var provider = _todoistMoveProviderFactory(token, source.ProviderConnectionId);
            await provider.MoveTaskAsync(source.ProviderTaskId, action.MoveToProjectId);
            return new TodoistFilingResult(true, string.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new TodoistFilingResult(false, exception.Message);
        }
    }

    private sealed record TodoistFilingResult(bool Applied, string Error)
    {
        public static TodoistFilingResult NotApplicable { get; } = new(false, string.Empty);
    }

    public async Task SkipConnectedTaskAsync(ConnectedTaskViewModel item)
    {
        await RunBusyAsync(async () =>
        {
            await _store.SkipProviderSourceItemAsync(item.Source.Id);
            StatusMessage = "Connected task skipped";
            await LoadConnectedTasksCoreAsync();
        });
    }

    public async Task UnskipConnectedTaskAsync(ConnectedTaskViewModel item)
    {
        await RunBusyAsync(async () =>
        {
            await _store.UnskipProviderSourceItemAsync(item.Source.Id);
            StatusMessage = "Connected task returned to intake";
            await LoadConnectedTasksCoreAsync();
        });
    }

    public async Task CreateProjectAsync()
    {
        var name = NewProjectName.Trim();
        if (name.Length == 0)
        {
            StatusMessage = "Enter a project name.";
            return;
        }

        if (_currentSpaceId is null)
        {
            StatusMessage = "Choose a specific space before creating a project.";
            return;
        }

        var project = new ProjectItem
        {
            Id = $"project_{Guid.NewGuid():N}",
            SpaceId = _currentSpaceId,
            IntegrationId = IntegrationIds.Local,
            Name = name,
            Color = "#6B8AFD",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await RunBusyAsync(async () =>
        {
            await _store.UpsertProjectAsync(project);
            NewProjectName = string.Empty;
            StatusMessage = "Project created";
            await RefreshAsyncCore();
            var created = ProjectItems.FirstOrDefault(item => item.Project.Id == project.Id);
            if (created is not null)
            {
                SelectedProject = created;
                SelectedNavigation = null;
                PageTitle = created.Title;
                PageSubtitle = "Project tasks";
                await RefreshAsyncCore();
            }
        });
    }

    public async Task RenameSelectedProjectAsync(string name)
    {
        if (SelectedProject is null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var project = SelectedProject.Project;
        await RunBusyAsync(async () =>
        {
            await _store.UpsertProjectAsync(project with
            {
                Name = name.Trim(),
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            PageTitle = name.Trim();
            StatusMessage = "Project renamed";
            await RefreshAsyncCore();
            SelectedProject = ProjectItems.FirstOrDefault(item => item.Project.Id == project.Id);
        });
    }

    public async Task DeleteSelectedProjectAsync()
    {
        if (SelectedProject is null)
        {
            return;
        }

        var projectId = SelectedProject.Project.Id;
        await RunBusyAsync(async () =>
        {
            await _store.DeleteProjectAsync(projectId, moveTasksToInbox: true);
            SelectedProject = null;
            SelectedNavigation = NavigationItems[0];
            PageTitle = "Inbox";
            PageSubtitle = SubtitleFor(TaskListKind.Inbox);
            StatusMessage = "Project deleted; its tasks were moved to Inbox";
            await RefreshAsyncCore();
        });
    }

    public async Task AddTaskAsync()
    {
        var title = QuickAddTitle.Trim();
        if (title.Length == 0)
        {
            StatusMessage = "Type a task before adding it.";
            return;
        }

        await CreateTaskAsync(new AddTaskDraft(
            title,
            string.Empty,
            ProjectOptions.FirstOrDefault(option => option.ProjectId == SelectedProject?.Project.Id),
            StatusIndex(DefaultStatus()),
            1,
            SelectedNavigation?.Kind == TaskListKind.Today ? DateTimeOffset.Now : null,
            string.Empty,
            true));
        QuickAddTitle = string.Empty;
    }

    public async Task CreateTaskAsync(AddTaskDraft draft)
    {
        var title = draft.Title.Trim();
        if (title.Length == 0)
        {
            StatusMessage = "Type a task before adding it.";
            return;
        }

        var task = new TaskItem
        {
            Id = $"local_{Guid.NewGuid():N}",
            SpaceId = _currentSpaceId ?? _defaultSpaceId,
            IntegrationId = IntegrationIds.Local,
            Title = title,
            Notes = string.IsNullOrWhiteSpace(draft.Notes) ? null : draft.Notes.Trim(),
            ProjectId = draft.Project?.ProjectId ?? SelectedProject?.Project.Id,
            Status = StatusFromIndex(draft.StatusIndex),
            Priority = Math.Clamp(draft.PriorityIndex + 1, 1, 4),
            PlannedOn = draft.PlannedDate is { } plannedDate
                ? DateOnly.FromDateTime(plannedDate.LocalDateTime)
                : null,
            Labels = ParseLabels(draft.LabelsText),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await RunBusyAsync(async () =>
        {
            await _store.UpsertTaskAsync(task);
            StatusMessage = "Task added";
            await RefreshAsyncCore(draft.OpenAfterCreate ? task.Id : null);
        });
    }

    public async Task ToggleSelectedCompletionAsync()
    {
        if (SelectedTask is null)
        {
            return;
        }

        var task = SelectedTask.Task;
        await RunBusyAsync(async () =>
        {
            await SetTaskCompletionAsync(task, !task.IsCompleted);
            StatusMessage = task.IsCompleted ? "Task reopened" : "Task completed";

            await RefreshAsyncCore();
        });
    }

    public async Task<bool> SaveSelectedAsync()
    {
        if (SelectedTask is null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(DetailTitle))
        {
            StatusMessage = "A task title is required.";
            return false;
        }

        var originalTaskId = SelectedTask.Task.Id;

        return await RunBusyAsync(async () =>
        {
            if (SelectedTask is null || !string.Equals(SelectedTask.Task.Id, originalTaskId, StringComparison.Ordinal))
            {
                return;
            }

            var original = SelectedTask.Task;
            var status = StatusFromIndex(DetailStatusIndex);
            var taskLabels = ParseLabels(DetailLabels);
            DateOnly? plannedOn = DetailDate is null ? null : DateOnly.FromDateTime(DetailDate.Value.LocalDateTime);
            DateOnly? deadlineOn = DetailDeadline is null ? null : DateOnly.FromDateTime(DetailDeadline.Value.LocalDateTime);
            var updated = original with
            {
                Title = DetailTitle.Trim(),
                Notes = NullIfEmpty(DetailNotes),
                Status = status,
                Priority = Math.Clamp(DetailPriorityIndex + 1, 1, 4),
                ProjectId = DetailProject?.ProjectId,
                PlannedOn = plannedOn,
                PlannedAt = ProviderWriteBackPlanner.PreserveExactTime(plannedOn, original.PlannedOn, original.PlannedAt),
                DeadlineOn = deadlineOn,
                DeadlineAt = ProviderWriteBackPlanner.PreserveExactTime(deadlineOn, original.DeadlineOn, original.DeadlineAt),
                CompletedAt = status == TaskItemStatus.Completed
                    ? original.CompletedAt ?? DateTimeOffset.UtcNow
                    : null,
                UpdatedAt = DateTimeOffset.UtcNow,
                Labels = taskLabels,
            };

            PendingCompletion? pendingCompletion = null;
            if (original.IsCompleted != updated.IsCompleted)
            {
                pendingCompletion = ProviderWriteBackPlanner.CreateCompletion(
                    original,
                    updated.IsCompleted,
                    DateTimeOffset.UtcNow);
            }

            var pendingDateUpdate = ProviderWriteBackPlanner.CreateTodoistDateUpdate(
                original,
                updated,
                DateTimeOffset.UtcNow);
            await _store.UpsertTaskWithPendingUpdatesAsync(updated, pendingCompletion, pendingDateUpdate);

            StatusMessage = "Changes saved";
            var selectedId = SelectedTask?.Task.Id;
            await RefreshAsyncCore(string.Equals(selectedId, original.Id, StringComparison.Ordinal)
                ? updated.Id
                : selectedId);
        });
    }

    public async Task DeleteSelectedAsync()
    {
        if (SelectedTask is null)
        {
            return;
        }

        var taskId = SelectedTask.Task.Id;
        await RunBusyAsync(async () =>
        {
            await _store.DeleteTaskAsync(taskId);
            StatusMessage = "Task deleted";
            await RefreshAsyncCore();
        });
    }

    public async Task MoveSelectedTaskToSpaceAsync(SpaceNavigationItemViewModel targetSpace)
    {
        if (SelectedTask is null || targetSpace.Space is null)
        {
            return;
        }

        if (string.Equals(SelectedTask.Task.SpaceId, targetSpace.SpaceId, StringComparison.Ordinal))
        {
            StatusMessage = $"Task is already in {targetSpace.Title}.";
            return;
        }

        var taskId = SelectedTask.Task.Id;
        await RunBusyAsync(async () =>
        {
            await _store.MoveTaskToSpaceAsync(taskId, targetSpace.SpaceId!);
            StatusMessage = $"Task moved to {targetSpace.Title}";
            await RefreshAsyncCore();
        });
    }

    public async Task SelectTaskAsync(TaskListItemViewModel task)
    {
        SelectedTask = task;
        await LoadSubtasksAsync();
        await LoadSelectedGitHubLinkAsync();
    }

    public async Task<string?> RunGitHubActionAsync()
    {
        if (SelectedTask is null)
        {
            return null;
        }

        if (_selectedGitHubLink is not null)
        {
            return _selectedGitHubLink.Url;
        }

        string? resultUrl = null;
        await RunBusyAsync(async () =>
        {
            var token = await _credentials.GetAsync(GitHubIssueService.TokenKey);
            if (string.IsNullOrWhiteSpace(token))
            {
                StatusMessage = "Connect GitHub in Settings first.";
                return;
            }

            var connections = await _store.GetProviderConnectionsAsync();
            var connection = connections.FirstOrDefault(item => item.IntegrationId == IntegrationIds.GitHub);
            var settings = GitHubIssueService.ReadSettings(connection?.SettingsJson);
            if (!TryParseRepository(settings.DefaultRepositoryFullName, out var owner, out var repository))
            {
                StatusMessage = "Set a default GitHub repository in Settings first.";
                return;
            }

            var task = SelectedTask.Task;
            var project = _projects.FirstOrDefault(item => item.Id == task.ProjectId);
            var result = await _gitHubIssueService.CreateIssueAsync(token, new GitHubIssueCreateRequest(
                owner,
                repository,
                task.Title,
                GitHubIssueService.BuildIssueBody(task, project),
                [],
                []));
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
                MetadataJson = JsonSerializer.Serialize(new { result.Owner, result.Repository, result.Number, result.State }),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await _store.UpsertTaskExternalLinkAsync(link);
            _selectedGitHubLink = link;
            GitHubActionText = "Open GitHub issue";
            StatusMessage = $"GitHub issue {result.DisplayName} created";
            resultUrl = result.Url;
        });
        return resultUrl;
    }

    public async Task ToggleSubtaskCompletionAsync(TaskListItemViewModel subtask)
    {
        var parentId = SelectedTask?.Task.Id;
        await RunBusyAsync(async () =>
        {
            await SetTaskCompletionAsync(subtask.Task, !subtask.Task.IsCompleted);
            StatusMessage = subtask.Task.IsCompleted ? "Subtask reopened" : "Subtask completed";
            await RefreshAsyncCore(parentId);
        });
    }

    private async Task SetTaskCompletionAsync(TaskItem task, bool completed)
    {
        var pendingCompletion = ProviderWriteBackPlanner.CreateCompletion(task, completed, DateTimeOffset.UtcNow);
        await _store.SetTaskCompletionWithPendingUpdateAsync(task.Id, completed, pendingCompletion);
    }

    private async Task RefreshAsync()
    {
        await RunBusyAsync(() => RefreshAsyncCore(SelectedTask?.Task.Id));
    }

    private async Task RefreshAsyncCore(string? selectTaskId = null)
    {
        using var detailUpdate = BeginDetailUpdate();
        var snapshot = await _store.GetTaskListRefreshSnapshotAsync(new TaskQuery
        {
            SpaceId = _currentSpaceId,
            ProjectId = SelectedProject?.Project.Id,
            Kind = SelectedProject is null ? SelectedNavigation?.Kind ?? TaskListKind.Inbox : TaskListKind.Open,
            SearchText = NullIfEmpty(SearchText),
            SortMode = SortModeFromIndex(SortIndex),
            SortDirection = SortDirectionIndex == 1
                ? TaskSortDirection.Descending
                : TaskSortDirection.Ascending,
            Priority = PriorityFilterIndex == 0 ? null : PriorityFilterIndex,
            RepeatScope = RepeatFilterIndex switch
            {
                1 => TaskRepeatScope.Exclude,
                2 => TaskRepeatScope.Only,
                _ => TaskRepeatScope.Include,
            },
            LabelId = SelectedLabelFilter?.LabelId,
        });
        var projects = await _store.GetProjectsAsync(_currentSpaceId, includeArchived: true);
        var labels = await _store.GetLabelsAsync();
        _projects.Clear();
        _projects.AddRange(projects);
        _labels.Clear();
        _labels.AddRange(labels);
        var projectNames = projects.ToDictionary(project => project.Id, project => project.Name, StringComparer.Ordinal);

        LabelSuggestions.Clear();
        foreach (var labelName in labels
                     .Select(label => label.Name)
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Distinct(StringComparer.CurrentCultureIgnoreCase)
                     .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
        {
            LabelSuggestions.Add(labelName);
        }

        ProjectOptions.Clear();
        ProjectOptions.Add(new ProjectOptionViewModel(null));
        foreach (var project in projects.Where(project => project.IsActive))
        {
            ProjectOptions.Add(new ProjectOptionViewModel(project));
        }

        var selectedLabelId = SelectedLabelFilter?.LabelId;
        LabelFilterOptions.Clear();
        LabelFilterOptions.Add(new LabelOptionViewModel(null));
        foreach (var label in labels.OrderBy(label => label.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            LabelFilterOptions.Add(new LabelOptionViewModel(label));
        }
        SelectedLabelFilter = LabelFilterOptions.FirstOrDefault(option => option.LabelId == selectedLabelId)
            ?? LabelFilterOptions[0];

        Tasks.Clear();
        foreach (var task in snapshot.VisibleTasks.Where(task => string.IsNullOrWhiteSpace(task.ParentId)))
        {
            projectNames.TryGetValue(task.ProjectId ?? string.Empty, out var projectName);
            Tasks.Add(new TaskListItemViewModel(task, projectName));
        }
        BuildTaskEntries(projects);
        OnPropertyChanged(nameof(HasNoTasks));
        PageSubtitle = $"{Tasks.Count} task{(Tasks.Count == 1 ? string.Empty : "s")}";

        UpdateNavigationCounts(snapshot.Counts);
        _projectCounts.Clear();
        foreach (var (projectId, count) in snapshot.Counts.ActiveByProject)
        {
            _projectCounts[projectId] = count;
        }
        RebuildProjectItems();

        SelectedTask = selectTaskId is null
            ? null
            : Tasks.FirstOrDefault(item => item.Task.Id == selectTaskId);
        await LoadSubtasksAsync();
        await LoadSelectedGitHubLinkAsync();
        OnPropertyChanged(nameof(HasNoSelectedTask));
    }

    private async Task LoadSubtasksAsync()
    {
        Subtasks.Clear();
        if (SelectedTask is null)
        {
            OnPropertyChanged(nameof(HasSubtasks));
            return;
        }

        var items = await _store.GetTasksAsync(new TaskQuery
        {
            SpaceId = SelectedTask.Task.SpaceId,
            ParentId = SelectedTask.Task.Id,
            Kind = TaskListKind.All,
            IncludeSubtasks = true,
        });
        foreach (var task in items)
        {
            var projectName = _projects.FirstOrDefault(project => project.Id == task.ProjectId)?.Name;
            Subtasks.Add(new TaskListItemViewModel(task, projectName));
        }

        OnPropertyChanged(nameof(HasSubtasks));
    }

    private void LoadRestorePointsCore()
    {
        RestorePoints.Clear();
        if (_backupService.Value is not { } backupService)
        {
            return;
        }

        foreach (var backup in backupService.ListBackupInfo())
        {
            RestorePoints.Add(new RestorePointViewModel(backup));
        }
        SelectedRestorePoint = RestorePoints.FirstOrDefault();
    }

    private async Task EnsureDailyRestorePointAsync()
    {
        if (_backupService.Value is not { } backupService)
        {
            return;
        }

        var today = DateTime.Today;
        var alreadyCreated = backupService.ListBackupInfo().Any(backup =>
            backup.Reason == BackupReasons.Daily && backup.CreatedAt.Date == today);
        if (!alreadyCreated)
        {
            await backupService.CreateBackupAsync(BackupReasons.Daily);
        }
    }

    private async Task LoadSelectedGitHubLinkAsync()
    {
        _selectedGitHubLink = null;
        GitHubActionText = "Create GitHub issue";
        if (SelectedTask is null)
        {
            return;
        }

        _selectedGitHubLink = (await _store.GetTaskExternalLinksAsync(SelectedTask.Task.Id))
            .FirstOrDefault(link => link.IntegrationId == IntegrationIds.GitHub && link.Kind == TaskExternalLinkKinds.Issue);
        GitHubActionText = _selectedGitHubLink is null ? "Create GitHub issue" : "Open GitHub issue";
    }

    private static bool TryParseRepository(string? value, out string owner, out string repository)
    {
        var parts = value?.Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        owner = parts.Length == 2 ? parts[0] : string.Empty;
        repository = parts.Length == 2 ? parts[1] : string.Empty;
        return owner.Length > 0 && repository.Length > 0;
    }

    private async Task LoadConnectedTasksCoreAsync()
    {
        var items = await _store.GetProviderSourceItemsAsync(
            spaceId: _currentSpaceId,
            includeAdopted: false,
            includeIgnored: true);
        _allConnectedTasks.Clear();
        _allConnectedTasks.AddRange(items.Select(item => new ConnectedTaskViewModel(item)));
        RebuildConnectedFilterOptions();
        FilterConnectedTasks(string.Empty);
    }

    private void RebuildConnectedFilterOptions()
    {
        var selectedSource = ConnectedSourceFilterIndex < ConnectedSourceOptions.Count
            ? ConnectedSourceOptions[ConnectedSourceFilterIndex]
            : null;
        var selectedProject = ConnectedProjectFilterIndex < ConnectedProjectOptions.Count
            ? ConnectedProjectOptions[ConnectedProjectFilterIndex]
            : null;

        ConnectedSourceOptions.Clear();
        ConnectedSourceOptions.Add("All sources");
        foreach (var source in _allConnectedTasks.Select(item => item.Source.SourceName).Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase))
        {
            ConnectedSourceOptions.Add(source);
        }

        ConnectedProjectOptions.Clear();
        ConnectedProjectOptions.Add("All lists");
        foreach (var project in _allConnectedTasks.Select(item => item.Source.SourceProjectName).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase))
        {
            ConnectedProjectOptions.Add(project);
        }

        ConnectedSourceFilterIndex = Math.Max(0, selectedSource is null ? 0 : ConnectedSourceOptions.IndexOf(selectedSource));
        ConnectedProjectFilterIndex = Math.Max(0, selectedProject is null ? 0 : ConnectedProjectOptions.IndexOf(selectedProject));
    }

    private async Task ReloadSpacesAsync()
    {
        var spaces = await _store.GetSpacesAsync();
        _defaultSpaceId = spaces.FirstOrDefault()?.Id ?? SpaceIds.Default;
        LoadSpaceCollections(spaces);
    }

    private void LoadSpaceCollections(IReadOnlyList<SpaceItem> spaces)
    {
        SpaceItems.Clear();
        EditableSpaceItems.Clear();
        SpaceItems.Add(new SpaceNavigationItemViewModel(null));
        foreach (var space in spaces)
        {
            var item = new SpaceNavigationItemViewModel(space);
            SpaceItems.Add(item);
            EditableSpaceItems.Add(item);
        }
    }

    private void RebuildProjectItems()
    {
        var search = ProjectSearchText.Trim();
        var projects = _projects.Where(project => ProjectFilterIndex switch
        {
            1 => true,
            2 => project.IsCompleted,
            3 => project.EffectiveStatus == ProjectLifecycleStates.Archived,
            _ => project.IsActive,
        });
        if (search.Length > 0)
        {
            projects = projects.Where(project => project.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        }

        ProjectItems.Clear();
        foreach (var project in projects.OrderByDescending(project => project.IsFavorite).ThenBy(project => project.SortOrder).ThenBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _projectCounts.TryGetValue(project.Id, out var count);
            ProjectItems.Add(new ProjectNavigationItemViewModel(project, count));
        }
    }

    private void UpdateNavigationCounts(TaskCountSummary counts)
    {
        foreach (var item in NavigationItems)
        {
            item.Count = item.Kind switch
            {
                TaskListKind.Inbox => counts.Inbox,
                TaskListKind.Today => counts.Today,
                TaskListKind.Calendar => counts.Calendar,
                TaskListKind.Overdue => counts.Overdue,
                TaskListKind.NextActions => counts.NextActions,
                TaskListKind.Waiting => counts.Waiting,
                TaskListKind.Someday => counts.Someday,
                TaskListKind.Open => counts.Open,
                TaskListKind.Completed => counts.Completed,
                _ => 0,
            };
        }
    }

    private void LoadDetails(TaskItem? task)
    {
        DetailTitle = task?.Title ?? string.Empty;
        DetailNotes = task?.Notes ?? string.Empty;
        DetailStatusIndex = StatusIndex(task?.Status ?? TaskItemStatus.Inbox);
        DetailPriorityIndex = Math.Clamp((task?.Priority ?? 2) - 1, 0, 3);
        DetailDate = task?.PlannedOn is { } date
            ? ToLocalDateTimeOffset(date)
            : null;
        DetailDeadline = task?.DeadlineOn is { } deadline
            ? ToLocalDateTimeOffset(deadline)
            : null;
        DetailLabels = task is null ? string.Empty : string.Join(", ", task.Labels.Select(label => label.Name));
        DetailProject = ProjectOptions.FirstOrDefault(option => option.ProjectId == task?.ProjectId)
            ?? ProjectOptions.FirstOrDefault();
    }

    private IDisposable BeginDetailUpdate()
    {
        _detailUpdateDepth++;
        return new DetailUpdateScope(this);
    }

    private sealed class DetailUpdateScope(MainWindowViewModel owner) : IDisposable
    {
        private MainWindowViewModel? _owner = owner;

        public void Dispose()
        {
            if (_owner is null)
            {
                return;
            }

            _owner._detailUpdateDepth--;
            _owner = null;
        }
    }

    private TaskItemStatus DefaultStatus() => SelectedNavigation?.Kind switch
    {
        TaskListKind.NextActions => TaskItemStatus.Next,
        TaskListKind.Waiting => TaskItemStatus.Waiting,
        TaskListKind.Someday => TaskItemStatus.Someday,
        _ => TaskItemStatus.Inbox,
    };

    private static int StatusIndex(TaskItemStatus status) => status switch
    {
        TaskItemStatus.Next => 1,
        TaskItemStatus.Waiting => 2,
        TaskItemStatus.Someday => 3,
        TaskItemStatus.Completed => 4,
        _ => 0,
    };

    private static TaskItemStatus StatusFromIndex(int index) => index switch
    {
        1 => TaskItemStatus.Next,
        2 => TaskItemStatus.Waiting,
        3 => TaskItemStatus.Someday,
        4 => TaskItemStatus.Completed,
        _ => TaskItemStatus.Inbox,
    };

    private static string SubtitleFor(TaskListKind kind) => kind switch
    {
        TaskListKind.Today => "Everything that needs your attention today.",
        TaskListKind.Calendar => "Open tasks with dates and deadlines.",
        TaskListKind.Overdue => "Open tasks whose dates have passed.",
        TaskListKind.NextActions => "Clear, available next steps.",
        TaskListKind.Waiting => "Things you are waiting on.",
        TaskListKind.Someday => "Ideas and possibilities for later.",
        TaskListKind.Open => "All open tasks in this space.",
        TaskListKind.Completed => "A record of finished work.",
        _ => "Capture first. Organize when it helps.",
    };

    private IReadOnlyList<LabelItem> ParseLabels(string labelsText)
    {
        return labelsText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Select(name => _labels.FirstOrDefault(label =>
                    string.Equals(label.Name, name, StringComparison.CurrentCultureIgnoreCase))
                ?? new LabelItem
                {
                    Id = $"label_{Guid.NewGuid():N}",
                    IntegrationId = IntegrationIds.Local,
                    Name = name,
                    CreatedAt = DateTimeOffset.UtcNow,
                })
            .ToList();
    }

    private static TaskSortMode SortModeFromIndex(int index) => index switch
    {
        1 => TaskSortMode.Date,
        2 => TaskSortMode.CreatedNewest,
        3 => TaskSortMode.Title,
        4 => TaskSortMode.Project,
        _ => TaskSortMode.PriorityThenDate,
    };

    private void BuildTaskEntries(IReadOnlyList<ProjectItem> projects)
    {
        TaskEntries.Clear();
        var groupMode = GroupModeFromIndex(GroupIndex);
        if (groupMode == TaskGroupMode.None)
        {
            foreach (var task in Tasks)
            {
                TaskEntries.Add(TaskListEntryViewModel.Item(task));
            }
            return;
        }

        var projectById = projects.ToDictionary(project => project.Id, StringComparer.Ordinal);
        var groups = new Dictionary<string, (TaskGroupAssignment Assignment, List<TaskListItemViewModel> Tasks)>(StringComparer.Ordinal);
        foreach (var task in Tasks)
        {
            projectById.TryGetValue(task.Task.ProjectId ?? string.Empty, out var project);
            foreach (var assignment in TaskGroupBuilder.GetAssignments(task.Task, project, groupMode))
            {
                if (!groups.TryGetValue(assignment.Key, out var group))
                {
                    group = (assignment, []);
                    groups.Add(assignment.Key, group);
                }
                group.Tasks.Add(task);
            }
        }

        foreach (var group in groups.Values.OrderBy(group => group.Assignment.SortKey, StringComparer.Ordinal).ThenBy(group => group.Assignment.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            TaskEntries.Add(TaskListEntryViewModel.Header(group.Assignment.Title, group.Tasks.Count));
            foreach (var task in group.Tasks)
            {
                TaskEntries.Add(TaskListEntryViewModel.Item(task));
            }
        }
    }

    private static TaskGroupMode GroupModeFromIndex(int index) => index switch
    {
        1 => TaskGroupMode.Date,
        2 => TaskGroupMode.Project,
        3 => TaskGroupMode.Status,
        4 => TaskGroupMode.Priority,
        5 => TaskGroupMode.Label,
        6 => TaskGroupMode.Source,
        7 => TaskGroupMode.Repeating,
        8 => TaskGroupMode.CreatedDate,
        9 => TaskGroupMode.CompletedDate,
        _ => TaskGroupMode.None,
    };

    private static DateTimeOffset ToLocalDateTimeOffset(DateOnly date)
    {
        var value = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(value, TimeZoneInfo.Local.GetUtcOffset(value));
    }

    private async Task<bool> RunBusyAsync(Func<Task> action)
    {
        await _operationGate.WaitAsync();
        IsBusy = true;
        try
        {
            await action();
            return true;
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not complete that action: {exception.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
            _operationGate.Release();
        }
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
