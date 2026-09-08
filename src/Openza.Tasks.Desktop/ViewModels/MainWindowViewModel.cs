using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Openza.Tasks.Application.Tasks;
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
    private const int SubtaskPreviewLimit = 5;
    private const string TodoistTokenKey = "todoist-token";
    private readonly ITaskStore _store;
    private readonly TaskApplicationService _taskService;
    private readonly ICredentialStore _credentials;
    private readonly HttpClient _httpClient = new();
    private readonly TaskSyncEngine _syncEngine;
    private readonly Func<string, string, ITaskProjectMoveProvider> _todoistMoveProviderFactory;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private long _detailEditVersion;
    private int _detailUpdateDepth;
    private bool _showAllSubtasks;
    private string? _sourceDateMismatchAcknowledgementKey;
    private readonly GitHubIssueService _gitHubIssueService = new(new HttpClient());
    private readonly DesktopPreferencesStore _preferencesStore;
    private readonly Lazy<BackupService?> _backupService;
    private readonly List<ProjectItem> _projects = [];
    private readonly List<LabelItem> _labels = [];
    private readonly Dictionary<string, int> _projectCounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedTaskGroups = new(StringComparer.Ordinal);
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
    private bool _isStatusMessagePersistent;
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
    private string _todoistConnectionText = "Not connected.";
    private int _priorityFilterIndex;
    private int _repeatFilterIndex;
    private int _groupIndex;
    private int _connectedSourceFilterIndex;
    private int _connectedProjectFilterIndex;
    private bool _showSkippedConnectedTasks;
    private string _connectedSearchText = string.Empty;
    private LabelOptionViewModel? _selectedLabelFilter;
    private string? _restoredLabelFilterId;
    private string? _activeTaskViewSettingsKey;
    private RestorePointViewModel? _selectedRestorePoint;
    private string _gitHubToken = string.Empty;
    private string _gitHubConnectionText = "Not connected.";
    private string _gitHubDefaultRepository = string.Empty;
    private string _gitHubActionText = "Create GitHub issue";
    private TaskExternalLinkInfo? _selectedGitHubLink;
    private bool _automaticSyncEnabled = true;

    public MainWindowViewModel(ITaskStore store)
        : this(store, DesktopCredentialStore.Create(DesktopDataPaths.Runtime))
    {
    }

    public MainWindowViewModel(
        ITaskStore store,
        ICredentialStore credentials,
        Func<string, string, ITaskProjectMoveProvider>? todoistMoveProviderFactory = null,
        DesktopPreferencesStore? preferencesStore = null)
    {
        _store = store;
        _taskService = new TaskApplicationService(store);
        _credentials = credentials;
        _preferencesStore = preferencesStore ?? CreatePreferencesStore(store);
        _syncEngine = new TaskSyncEngine(store);
        _todoistMoveProviderFactory = todoistMoveProviderFactory ??
            ((token, connectionId) => new TodoistProvider(_httpClient, token, connectionId));
        _backupService = new Lazy<BackupService?>(() => store is SqliteTaskStore sqliteStore
            ? new BackupService(
                sqliteStore.DatabasePath,
                DesktopDataPaths.RestorePointDirectory,
                context: new BackupContext(
                    "Openza.Tasks.Desktop",
                    DesktopDataPaths.Runtime.Channel.ToString().ToLowerInvariant(),
                    CurrentAppVersion),
                databaseReplacementLeaseFactory: () =>
                    Openza.Tasks.Application.Runtime.ChannelRuntimeLease.AcquireDatabaseReplacement(DesktopDataPaths.Runtime))
            : null);
    }

    private static string CurrentAppVersion =>
        typeof(MainWindowViewModel).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion.Split('+')[0]
        ?? typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3)
        ?? "unknown";

    public string AppVersion => CurrentAppVersion;

    private static DesktopPreferencesStore CreatePreferencesStore(ITaskStore store)
    {
        if (store is SqliteTaskStore sqliteStore &&
            Path.GetDirectoryName(sqliteStore.DatabasePath) is { } dataDirectory)
        {
            return new DesktopPreferencesStore(Path.Combine(dataDirectory, "settings.json"));
        }

        return new DesktopPreferencesStore();
    }

    private static string CredentialStoreDisplayName => OperatingSystem.IsWindows()
        ? "Windows Credential Manager"
        : "the desktop Secret Service";

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
    public ObservableCollection<TaskListItemViewModel> VisibleSubtasks { get; } = [];
    public ObservableCollection<string> DetailLabelItems { get; } = [];
    public ObservableCollection<RestorePointViewModel> RestorePoints { get; } = [];
    public ObservableCollection<TodoistRoutingRuleViewModel> TodoistRoutingRules { get; } = [];
    public ObservableCollection<TodoistRoutingChoiceViewModel> TodoistRoutingLabelChoices { get; } = [];
    public ObservableCollection<TodoistRoutingChoiceViewModel> TodoistRoutingSpaceChoices { get; } = [];
    public ObservableCollection<TodoistRoutingChoiceViewModel> TodoistRoutingProjectChoices { get; } = [];

    public SpaceNavigationItemViewModel? SelectedSpace
    {
        get => _selectedSpace;
        set
        {
            var contextChanged = !string.Equals(_selectedSpace?.SpaceId, value?.SpaceId, StringComparison.Ordinal);
            if (SetProperty(ref _selectedSpace, value) && contextChanged)
            {
                _collapsedTaskGroups.Clear();
            }
        }
    }

    public NavigationItemViewModel? SelectedNavigation
    {
        get => _selectedNavigation;
        set
        {
            var contextChanged = _selectedNavigation?.Kind != value?.Kind;
            if (SetProperty(ref _selectedNavigation, value))
            {
                if (contextChanged)
                {
                    _collapsedTaskGroups.Clear();
                }
                OnPropertyChanged(nameof(EmptyStateTitle));
                OnPropertyChanged(nameof(EmptyStateMessage));
            }
        }
    }

    public ProjectNavigationItemViewModel? SelectedProject
    {
        get => _selectedProject;
        set
        {
            var contextChanged = !string.Equals(_selectedProject?.Project.Id, value?.Project.Id, StringComparison.Ordinal);
            if (SetProperty(ref _selectedProject, value))
            {
                if (contextChanged)
                {
                    _collapsedTaskGroups.Clear();
                }
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
                NotifyTaskMetadataChanged();
            }
        }
    }

    public bool HasSelectedTask => SelectedTask is not null;
    public bool HasNoSelectedTask => !HasSelectedTask;
    public string CompletionActionText => SelectedTask?.Task.IsCompleted == true ? "Reopen" : "Complete";
    public bool HasNoTasks => Tasks.Count == 0;
    public string EmptyStateTitle => HasActiveListFilters ? "No matching tasks" : SelectedNavigation?.Kind switch
    {
        TaskListKind.Inbox => "Inbox is clear",
        TaskListKind.Today => "Nothing for today",
        TaskListKind.Calendar => "No dated tasks",
        TaskListKind.Overdue => "Nothing overdue",
        TaskListKind.Waiting => "Nothing waiting",
        TaskListKind.Someday => "No someday tasks",
        TaskListKind.Completed => "No completed tasks yet",
        _ => "Nothing here",
    };
    public string EmptyStateMessage => HasActiveListFilters
        ? "Change or clear your search and filters to see more tasks."
        : SelectedNavigation?.Kind switch
    {
        TaskListKind.Inbox when HasConnectedTasks => "Clarify captured tasks here, or review tasks waiting from connected apps.",
        TaskListKind.Inbox => "Capture anything on your mind here. Clarify it later when you are ready.",
        TaskListKind.NextActions => "Clarified next actions will appear here.",
        TaskListKind.Today => "Tasks dated, scheduled, or repeating today will appear here.",
        TaskListKind.Calendar => "Dated work will appear here.",
        TaskListKind.Overdue => "Nothing needs recovery right now.",
        TaskListKind.Waiting => "Delegated or blocked tasks you are waiting on will appear here.",
        TaskListKind.Someday => "Ideas you may want later will appear here.",
        TaskListKind.Completed => "Completed tasks will appear here.",
        _ => "Create a task to start filling this list.",
    };
    public string EmptyStateActionText => HasActiveListFilters ? "Clear filters" : "Add task";
    public string DatabasePath => (_store as SqliteTaskStore)?.DatabasePath ?? "Custom data store";
    public bool HasNoConnectedTasks => FilteredConnectedTasks.Count == 0;
    public int ConnectedTaskCount => _allConnectedTasks.Count;
    public int WaitingConnectedTaskCount => _allConnectedTasks.Count(item => !item.Source.IsSkipped);
    public int SkippedConnectedTaskCount => _allConnectedTasks.Count(item => item.Source.IsSkipped);
    public bool HasConnectedTasks => ConnectedTaskCount > 0;
    public bool CanAddAllConnectedTasks => WaitingConnectedTaskCount > 0;
    public string ConnectedTasksSummary => SkippedConnectedTaskCount == 0
        ? WaitingConnectedTaskCount == 1
            ? "1 task is waiting. Add it to Inbox first, then clarify it like any other task."
            : $"{WaitingConnectedTaskCount} tasks are waiting. Add them to Inbox first, then clarify them like any other task."
        : $"{WaitingConnectedTaskCount} waiting, {SkippedConnectedTaskCount} skipped. Skipped tasks stay recoverable here.";
    public bool HasSubtasks => Subtasks.Count > 0;
    public string SubtasksProgressText => $"{Subtasks.Count(subtask => subtask.Task.IsCompleted)}/{Subtasks.Count}";
    public bool CanToggleSubtasks => Subtasks.Count > SubtaskPreviewLimit;
    public string SubtasksToggleText => _showAllSubtasks ? "Show fewer" : $"Show all {Subtasks.Count} subtasks";
    public bool HasSourceTask => SelectedTask?.Task is { } task && (task.IsProviderTask || task.HasProviderSource);
    public bool HasSourceDescription => !string.IsNullOrWhiteSpace(SelectedTask?.Task.SourceDescription);
    public bool HasLocalTaskMetadata => SelectedTask?.Task.IntegrationId == IntegrationIds.Local;
    public string SourceTaskName => IntegrationIds.DisplayName(
        SelectedTask?.Task.SourceIntegrationId ?? SelectedTask?.Task.IntegrationId ?? IntegrationIds.Local);
    public string SourceTaskHeader => $"Source: {SourceTaskName}";
    public string SourceTaskTitle => string.IsNullOrWhiteSpace(SelectedTask?.Task.SourceTitle)
        ? SelectedTask?.Task.Title ?? string.Empty
        : SelectedTask.Task.SourceTitle;
    public string SourceTaskDescription => SelectedTask?.Task.SourceDescription ?? string.Empty;
    public string SourceTaskDescriptionHint => $"From {SourceTaskName}";
    public string SourceTaskProject => string.IsNullOrWhiteSpace(SelectedTask?.Task.SourceProjectName)
        ? "No source project"
        : SelectedTask.Task.SourceProjectName;
    public string SourceTaskDate => FormatTaskDate(SelectedTask?.Task.SourcePlannedMoment);
    public string SourceTaskDeadline => FormatTaskDate(SelectedTask?.Task.SourceDeadlineMoment);
    public string SourceTaskPriority => SelectedTask?.Task.SourcePriority is { } priority
        ? FormatPriority(priority)
        : "No priority";
    public string SourceTaskCreated => SelectedTask?.Task is { } task ? FormatTaskDate(task.CreatedAt) : string.Empty;
    public string SourceTaskRecurrence => string.IsNullOrWhiteSpace(SelectedTask?.Task.RecurrenceRule)
        ? "Not recurring"
        : SelectedTask.Task.RecurrenceRule;
    public string LocalTaskCreated => SelectedTask?.Task is { } localTask ? FormatTaskDateTime(localTask.CreatedAt) : string.Empty;
    public string LocalTaskUpdated => SelectedTask?.Task.UpdatedAt is { } updatedAt ? FormatTaskDateTime(updatedAt) : "Not modified";
    public bool HasSourceDateMismatch => BuildSourceDateMismatch() is not null;
    public string SourceDateMismatchTitle => BuildSourceDateMismatch()?.Title ?? string.Empty;
    public string SourceDateMismatchMessage => BuildSourceDateMismatch()?.Message ?? string.Empty;
    public string UseSourceDatesText => BuildSourceDateMismatch()?.UseSourceText ?? string.Empty;
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
        set
        {
            if (SetProperty(ref _priorityFilterIndex, value))
            {
                NotifyListFilterStateChanged();
            }
        }
    }

    public int RepeatFilterIndex
    {
        get => _repeatFilterIndex;
        set
        {
            if (SetProperty(ref _repeatFilterIndex, value))
            {
                NotifyListFilterStateChanged();
            }
        }
    }

    public int GroupIndex
    {
        get => _groupIndex;
        set
        {
            if (SetProperty(ref _groupIndex, value))
            {
                _collapsedTaskGroups.Clear();
                OnPropertyChanged(nameof(GroupSummary));
            }
        }
    }

    public string GroupSummary => $"Group: {GroupIndex switch
    {
        1 => "Date",
        2 => "Project",
        3 => "Status",
        4 => "Priority",
        5 => "Label",
        6 => "Source",
        7 => "Repeating",
        8 => "Created",
        9 => "Completed",
        _ => "None",
    }}";

    public LabelOptionViewModel? SelectedLabelFilter
    {
        get => _selectedLabelFilter;
        set
        {
            if (SetProperty(ref _selectedLabelFilter, value))
            {
                _restoredLabelFilterId = value?.LabelId;
                NotifyListFilterStateChanged();
            }
        }
    }

    public bool HasPriorityFilter => PriorityFilterIndex > 0;

    public bool HasRepeatFilter => RepeatFilterIndex > 0;

    public bool HasLabelFilter => SelectedLabelFilter?.LabelId is not null;

    public bool HasActiveOptionFilters => ActiveOptionFilterCount > 0;

    public bool HasActiveListFilters => !string.IsNullOrWhiteSpace(SearchText) || HasActiveOptionFilters;

    public string FilterSummary => ActiveOptionFilterCount == 0 ? "Filters" : $"Filters ({ActiveOptionFilterCount})";

    public string FilterAutomationName => ActiveOptionFilterCount == 0
        ? "Filters"
        : $"Filters, {ActiveOptionFilterCount} active";

    public string PriorityFilterChipText => $"Priority: {PriorityFilterIndex switch
    {
        1 => "Urgent",
        2 => "High",
        3 => "Normal",
        4 => "Low",
        _ => "All",
    }}  ×";

    public string RepeatFilterChipText => $"Repeating: {(RepeatFilterIndex == 1 ? "Exclude" : "Only")}  ×";

    public string LabelFilterChipText => $"Label: {SelectedLabelFilter?.Title ?? "Label"}  ×";

    private int ActiveOptionFilterCount =>
        (HasPriorityFilter ? 1 : 0) +
        (HasRepeatFilter ? 1 : 0) +
        (HasLabelFilter ? 1 : 0);

    private void NotifyListFilterStateChanged()
    {
        OnPropertyChanged(nameof(HasPriorityFilter));
        OnPropertyChanged(nameof(HasRepeatFilter));
        OnPropertyChanged(nameof(HasLabelFilter));
        OnPropertyChanged(nameof(HasActiveOptionFilters));
        OnPropertyChanged(nameof(HasActiveListFilters));
        OnPropertyChanged(nameof(FilterSummary));
        OnPropertyChanged(nameof(FilterAutomationName));
        OnPropertyChanged(nameof(PriorityFilterChipText));
        OnPropertyChanged(nameof(RepeatFilterChipText));
        OnPropertyChanged(nameof(LabelFilterChipText));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateMessage));
        OnPropertyChanged(nameof(EmptyStateActionText));
    }

    public string QuickAddTitle
    {
        get => _quickAddTitle;
        set => SetProperty(ref _quickAddTitle, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                OnPropertyChanged(nameof(HasActiveListFilters));
                OnPropertyChanged(nameof(EmptyStateTitle));
                OnPropertyChanged(nameof(EmptyStateMessage));
                OnPropertyChanged(nameof(EmptyStateActionText));
            }
        }
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
        private set
        {
            IsStatusMessagePersistent = false;
            SetProperty(ref _statusMessage, value);
        }
    }

    public bool IsStatusMessagePersistent
    {
        get => _isStatusMessagePersistent;
        private set => SetProperty(ref _isStatusMessagePersistent, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string DetailTitle
    {
        get => _detailTitle;
        set
        {
            if (SetProperty(ref _detailTitle, value))
            {
                MarkDetailEdited();
            }
        }
    }

    public string DetailNotes
    {
        get => _detailNotes;
        set
        {
            if (SetProperty(ref _detailNotes, value))
            {
                MarkDetailEdited();
            }
        }
    }

    public int DetailStatusIndex
    {
        get => _detailStatusIndex;
        set
        {
            if (SetProperty(ref _detailStatusIndex, value))
            {
                MarkDetailEdited();
            }
        }
    }

    public int DetailPriorityIndex
    {
        get => _detailPriorityIndex;
        set
        {
            if (SetProperty(ref _detailPriorityIndex, value))
            {
                MarkDetailEdited();
            }
        }
    }

    public DateTimeOffset? DetailDate
    {
        get => _detailDate;
        set
        {
            if (SetProperty(ref _detailDate, value))
            {
                MarkDetailEdited();
                OnPropertyChanged(nameof(DetailCalendarDate));
                NotifySourceDateMismatchChanged();
            }
        }
    }

    public DateTimeOffset? DetailDeadline
    {
        get => _detailDeadline;
        set
        {
            if (SetProperty(ref _detailDeadline, value))
            {
                MarkDetailEdited();
                OnPropertyChanged(nameof(DetailCalendarDeadline));
                NotifySourceDateMismatchChanged();
            }
        }
    }

    // CalendarDatePicker uses DateTime? while the application layer deliberately
    // keeps a local offset so date-only values survive time-zone conversion.
    public DateTime? DetailCalendarDate
    {
        get => DetailDate?.LocalDateTime.Date;
        set => DetailDate = ToLocalDateTimeOffset(value);
    }

    public DateTime? DetailCalendarDeadline
    {
        get => DetailDeadline?.LocalDateTime.Date;
        set => DetailDeadline = ToLocalDateTimeOffset(value);
    }

    public string DetailLabels
    {
        get => _detailLabels;
        set
        {
            if (SetProperty(ref _detailLabels, value))
            {
                MarkDetailEdited();
            }
        }
    }

    public bool AddDetailLabels(string? labels)
    {
        var changed = false;
        foreach (var label in (labels ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (DetailLabelItems.Any(selected => string.Equals(selected, label, StringComparison.CurrentCultureIgnoreCase)))
            {
                continue;
            }

            DetailLabelItems.Add(label);
            changed = true;
        }

        if (changed)
        {
            SyncDetailLabels();
        }

        return changed;
    }

    public bool RemoveDetailLabel(string label)
    {
        var selected = DetailLabelItems.FirstOrDefault(item => string.Equals(item, label, StringComparison.CurrentCultureIgnoreCase));
        if (selected is null)
        {
            return false;
        }

        DetailLabelItems.Remove(selected);
        SyncDetailLabels();
        return true;
    }

    public ProjectOptionViewModel? DetailProject
    {
        get => _detailProject;
        set
        {
            if (SetProperty(ref _detailProject, value))
            {
                MarkDetailEdited();
            }
        }
    }

    public string NewProjectName
    {
        get => _newProjectName;
        set => SetProperty(ref _newProjectName, value);
    }

    public int SortIndex
    {
        get => _sortIndex;
        set
        {
            if (SetProperty(ref _sortIndex, value))
            {
                OnPropertyChanged(nameof(SortSummary));
            }
        }
    }

    public int SortDirectionIndex
    {
        get => _sortDirectionIndex;
        set
        {
            if (SetProperty(ref _sortDirectionIndex, value))
            {
                OnPropertyChanged(nameof(SortSummary));
            }
        }
    }

    public string SortSummary => $"Sort: {SortIndex switch
    {
        1 => "Date",
        2 => "Newest",
        3 => "Title",
        4 => "Project",
        _ => "Priority",
    }} {(SortDirectionIndex == 1 ? "Desc" : "Asc")}";

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
            await LoadConnectedTasksCoreAsync();
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
        var wasProjectView = SelectedProject is not null;
        SelectedSpace = item;
        _currentSpaceId = item.SpaceId;
        SelectedProject = null;
        if (wasProjectView)
        {
            SelectedNavigation = NavigationItems.Single(navigation => navigation.Kind == TaskListKind.Open);
            PageTitle = SelectedNavigation.Title;
        }
        PageSubtitle = item.Space is null
            ? "Tasks from every space."
            : $"Tasks in {item.Title}.";
        await RefreshAsync();
        await LoadConnectedTasksCoreAsync();
    }

    public Task<IReadOnlyList<GlobalSearchResult>> SearchGloballyAsync(
        string searchText,
        bool includeAllSpaces,
        bool includeCompletedTasks) =>
        _store.SearchAsync(new GlobalSearchQuery
        {
            SearchText = searchText,
            SpaceId = _currentSpaceId,
            IncludeAllSpaces = includeAllSpaces || _currentSpaceId is null,
            IncludeCompletedTasks = includeCompletedTasks,
            Limit = 25,
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

    public async Task ApplyListOptionsAsync()
    {
        await SaveTaskViewPreferencesAsync();
        await RefreshAsync();
    }

    public void ToggleTaskGroup(string groupKey)
    {
        if (string.IsNullOrWhiteSpace(groupKey))
        {
            return;
        }

        if (!_collapsedTaskGroups.Add(groupKey))
        {
            _collapsedTaskGroups.Remove(groupKey);
        }

        BuildTaskEntries(_projects);
    }

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
            await LoadConnectedTasksCoreAsync();
            StatusMessage = WaitingConnectedTaskCount == 1
                ? "1 connected task waiting"
                : $"{WaitingConnectedTaskCount} connected tasks waiting";
        });
    }

    public async Task RefreshTodoistConnectionAsync()
    {
        try
        {
            var token = await _credentials.GetAsync(TodoistTokenKey);
            TodoistConnectionText = string.IsNullOrWhiteSpace(token)
                ? "Not connected."
                : $"Connected securely through {CredentialStoreDisplayName}.";
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
                ? "Not connected. Existing issue links remain available."
                : string.IsNullOrWhiteSpace(settings.Username)
                    ? $"Connected securely through {CredentialStoreDisplayName}."
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
            GitHubConnectionText = "Not connected.";
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
            TodoistConnectionText = $"Connected securely through {CredentialStoreDisplayName}.";
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
            TodoistConnectionText = "Not connected.";
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
            using var syncLease = Openza.Tasks.Application.Runtime.ChannelRuntimeLease.AcquireProviderSync(
                DesktopDataPaths.Runtime,
                IntegrationIds.Todoist);
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
                SetPersistentStatusMessage($"Todoist sync failed: {summary.Error}");
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
        var showSkipped = ShowSkippedConnectedTasks || WaitingConnectedTaskCount == 0;
        var source = ConnectedSourceFilterIndex > 0 && ConnectedSourceFilterIndex < ConnectedSourceOptions.Count
            ? ConnectedSourceOptions[ConnectedSourceFilterIndex]
            : null;
        var project = ConnectedProjectFilterIndex > 0 && ConnectedProjectFilterIndex < ConnectedProjectOptions.Count
            ? ConnectedProjectOptions[ConnectedProjectFilterIndex]
            : null;
        FilteredConnectedTasks.Clear();
        foreach (var item in _allConnectedTasks.Where(item =>
                     (showSkipped || !item.Source.IsSkipped) &&
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

    public IReadOnlyList<ProjectOptionViewModel> GetProjectOptionsForSpace(string spaceId) =>
        ProjectOptions
            .Where(option => option.Project is null || string.Equals(option.Project.SpaceId, spaceId, StringComparison.Ordinal))
            .ToArray();

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
                if (filing.Error.Length > 0)
                {
                    SetPersistentStatusMessage($"Task added to Openza, but Todoist filing failed: {filing.Error}");
                }
                else
                {
                    StatusMessage = filing.Applied
                        ? "Task added to Openza and filed in Todoist"
                        : "Task added to Openza";
                }
            }
            await LoadConnectedTasksCoreAsync();
            await RefreshAsyncCore(task?.Id);
        });
    }

    public async Task AdoptAllConnectedTasksAsync()
    {
        var items = _allConnectedTasks
            .Where(item => !item.Source.IsSkipped)
            .ToArray();
        if (items.Length == 0)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            string? lastTaskId = null;
            var adoptedCount = 0;
            var filingFailureCount = 0;
            foreach (var item in items)
            {
                var targetSpaceId = _currentSpaceId ?? item.Source.SuggestedSpaceId ?? _defaultSpaceId;
                var task = await _store.AdoptProviderSourceItemAsync(item.Source.Id, targetSpaceId);
                if (task is null)
                {
                    continue;
                }

                adoptedCount++;
                lastTaskId = task.Id;
                var filing = await TryApplyTodoistPostImportFilingAsync(item.Source);
                if (filing.Error.Length > 0)
                {
                    filingFailureCount++;
                }
            }

            await LoadConnectedTasksCoreAsync();
            await RefreshAsyncCore(lastTaskId);
            if (filingFailureCount > 0)
            {
                SetPersistentStatusMessage($"Added {adoptedCount} tasks to Openza; {filingFailureCount} Todoist filing actions failed.");
            }
            else
            {
                StatusMessage = $"Added {adoptedCount} tasks to Openza";
            }
        });
    }

    public void DismissStatusMessage()
    {
        IsStatusMessagePersistent = false;
        StatusMessage = string.Empty;
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

    public async Task<bool> CreateProjectForSelectedTaskAsync(string name)
    {
        var trimmedName = name.Trim();
        if (SelectedTask is null || trimmedName.Length == 0)
        {
            return false;
        }

        var existing = FindProjectOption(SelectedTask.Task.SpaceId, trimmedName);
        if (existing is not null)
        {
            DetailProject = existing;
            return await SaveSelectedAsync();
        }

        var project = new ProjectItem
        {
            Id = $"project_{Guid.NewGuid():N}",
            SpaceId = SelectedTask.Task.SpaceId,
            IntegrationId = IntegrationIds.Local,
            Name = trimmedName,
            Color = "#6B8AFD",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        if (!await RunBusyAsync(() => _store.UpsertProjectAsync(project)))
        {
            return false;
        }

        var option = new ProjectOptionViewModel(project);
        ProjectOptions.Add(option);
        DetailProject = option;
        return await SaveSelectedAsync();
    }

    public async Task<bool> CreateProjectForTaskFromRowAsync(TaskListItemViewModel item, string name)
    {
        var trimmedName = name.Trim();
        if (trimmedName.Length == 0)
        {
            return false;
        }

        var existing = FindProjectOption(item.Task.SpaceId, trimmedName);
        if (existing is not null)
        {
            await SetTaskProjectFromRowAsync(item, existing.ProjectId);
            return true;
        }

        var project = new ProjectItem
        {
            Id = $"project_{Guid.NewGuid():N}",
            SpaceId = item.Task.SpaceId,
            IntegrationId = IntegrationIds.Local,
            Name = trimmedName,
            Color = "#6B8AFD",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        if (!await RunBusyAsync(() => _store.UpsertProjectAsync(project)))
        {
            return false;
        }

        await SetTaskProjectFromRowAsync(item, project.Id);
        return true;
    }

    private ProjectOptionViewModel? FindProjectOption(string spaceId, string name) =>
        ProjectOptions.FirstOrDefault(option =>
            option.Project is not null &&
            string.Equals(option.Project.SpaceId, spaceId, StringComparison.Ordinal) &&
            string.Equals(option.Title, name, StringComparison.CurrentCultureIgnoreCase));

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

    public async Task UpdateSelectedProjectAsync(string name, string projectStatus, bool isFavorite)
    {
        if (SelectedProject is null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var project = SelectedProject.Project;
        var status = ProjectLifecycleStates.Normalize(projectStatus);
        await RunBusyAsync(async () =>
        {
            await _store.UpsertProjectAsync(project with
            {
                Name = name.Trim(),
                Status = status,
                IsArchived = status == ProjectLifecycleStates.Archived,
                IsFavorite = isFavorite,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            PageTitle = name.Trim();
            StatusMessage = "Project updated";
            await RefreshAsyncCore();
            SelectedProject = ProjectItems.FirstOrDefault(item => item.Project.Id == project.Id);
            if (SelectedProject is null)
            {
                SelectedNavigation = NavigationItems.First(item => item.Kind == TaskListKind.Open);
                PageTitle = SelectedNavigation.Title;
                PageSubtitle = SubtitleFor(SelectedNavigation.Kind);
                await RefreshAsyncCore();
            }
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
            2,
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

        await RunBusyAsync(async () =>
        {
            var status = StatusFromIndex(draft.StatusIndex);
            var task = await _taskService.CreateTaskAsync(new CreateTaskRequest
            {
                Title = title,
                Notes = draft.Notes,
                Space = _currentSpaceId ?? _defaultSpaceId,
                Project = draft.Project?.ProjectId ?? SelectedProject?.Project.Id,
                Status = status.ToWorkflowStatus(),
                Completed = status == TaskItemStatus.Completed,
                Priority = Math.Clamp(draft.PriorityIndex + 1, 1, 4),
                PlannedOn = draft.PlannedDate is { } plannedDate ? DateOnly.FromDateTime(plannedDate.LocalDateTime) : null,
                Labels = ParseLabels(draft.LabelsText).Select(label => label.Name).ToArray(),
            });
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

    public Task SetTaskDateFromRowAsync(TaskListItemViewModel item, DateOnly? plannedOn) =>
        UpdateTaskFromRowAsync(
            item,
            new UpdateTaskRequest
            {
                TaskId = item.Task.Id,
                PlannedOn = OptionalValue<DateOnly?>.Set(plannedOn),
            },
            plannedOn is null ? "Task date cleared" : "Task date changed");

    public Task SetTaskStatusFromRowAsync(TaskListItemViewModel item, TaskItemStatus status) =>
        item.Task.IsCompleted
            ? Task.CompletedTask
            : UpdateTaskFromRowAsync(
            item,
            new UpdateTaskRequest
            {
                TaskId = item.Task.Id,
                Status = OptionalValue<TaskWorkflowStatus>.Set(status.ToWorkflowStatus()),
            },
            "Task status changed");

    public Task SetTaskPriorityFromRowAsync(TaskListItemViewModel item, int priority) =>
        UpdateTaskFromRowAsync(
            item,
            new UpdateTaskRequest
            {
                TaskId = item.Task.Id,
                Priority = OptionalValue<int>.Set(Math.Clamp(priority, 1, 4)),
            },
            "Task priority changed");

    public Task SetTaskProjectFromRowAsync(TaskListItemViewModel item, string? projectId) =>
        UpdateTaskFromRowAsync(
            item,
            new UpdateTaskRequest
            {
                TaskId = item.Task.Id,
                Project = OptionalValue<string?>.Set(string.IsNullOrWhiteSpace(projectId) ? null : projectId),
            },
            "Task project changed");

    public Task SetTaskLabelsFromRowAsync(TaskListItemViewModel item, string labelsText) =>
        UpdateTaskFromRowAsync(
            item,
            new UpdateTaskRequest
            {
                TaskId = item.Task.Id,
                Labels = OptionalValue<IReadOnlyList<string>>.Set(labelsText
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .ToArray()),
            },
            "Task labels changed");

    public async Task MoveTaskFromRowAsync(TaskListItemViewModel item, string spaceId)
    {
        if (string.Equals(item.Task.SpaceId, spaceId, StringComparison.Ordinal))
        {
            StatusMessage = "Task is already in that space";
            return;
        }

        var selectedId = SelectedTask?.Task.Id;
        await RunBusyAsync(async () =>
        {
            await _store.MoveTaskToSpaceAsync(item.Task.Id, spaceId);
            StatusMessage = "Task moved";
            await RefreshAsyncCore(string.Equals(selectedId, item.Task.Id, StringComparison.Ordinal) ? null : selectedId);
        });
    }

    public async Task DeleteTaskFromRowAsync(TaskListItemViewModel item)
    {
        var selectedId = SelectedTask?.Task.Id;
        await RunBusyAsync(async () =>
        {
            var current = await _store.GetTaskAsync(item.Task.Id);
            if (current is null)
            {
                return;
            }

            await _taskService.DeleteTaskAsync(current.Id, current.Revision);
            StatusMessage = "Task deleted";
            await RefreshAsyncCore(string.Equals(selectedId, current.Id, StringComparison.Ordinal) ? null : selectedId);
        }, rethrow: true, reportError: false);
    }

    private async Task UpdateTaskFromRowAsync(TaskListItemViewModel item, UpdateTaskRequest request, string statusMessage)
    {
        var selectedId = SelectedTask?.Task.Id;
        await RunBusyAsync(async () =>
        {
            var current = await _store.GetTaskAsync(item.Task.Id);
            if (current is null)
            {
                return;
            }

            await _taskService.UpdateTaskAsync(request with { ExpectedRevision = current.Revision });
            StatusMessage = statusMessage;
            await RefreshAsyncCore(selectedId);
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

        var draft = CaptureDetailSaveDraft(SelectedTask.Task.Id);
        var valid = true;
        var succeeded = await RunBusyAsync(async () =>
        {
            while (SelectedTask is not null && string.Equals(SelectedTask.Task.Id, draft.TaskId, StringComparison.Ordinal))
            {
                var original = await _store.GetTaskAsync(draft.TaskId);
                if (original is null)
                {
                    return;
                }

                var completed = draft.Status == TaskItemStatus.Completed;
                var localMetadataJson = WithSourceDateMismatchAcknowledgement(original.LocalMetadataJson, draft.SourceDateMismatchAcknowledgementKey);
                TaskItem? updated = null;
                if (HasDetailChanges(original, draft, completed, localMetadataJson))
                {
                    updated = await _taskService.UpdateTaskAsync(new UpdateTaskRequest
                    {
                        TaskId = original.Id,
                        ExpectedRevision = original.Revision,
                        Title = OptionalValue<string?>.Set(draft.Title),
                        Notes = OptionalValue<string?>.Set(NullIfEmpty(draft.Notes)),
                        Status = completed ? default : OptionalValue<TaskWorkflowStatus>.Set(draft.Status.ToWorkflowStatus()),
                        Completed = OptionalValue<bool>.Set(completed),
                        Priority = OptionalValue<int>.Set(draft.Priority),
                        Project = OptionalValue<string?>.Set(draft.ProjectId),
                        PlannedOn = OptionalValue<DateOnly?>.Set(draft.PlannedOn),
                        DeadlineOn = OptionalValue<DateOnly?>.Set(draft.DeadlineOn),
                        Labels = OptionalValue<IReadOnlyList<string>>.Set(draft.Labels.Select(label => label.Name).ToArray()),
                        LocalMetadataJson = OptionalValue<string?>.Set(localMetadataJson),
                    });
                }

                if (draft.Version != _detailEditVersion)
                {
                    if (string.IsNullOrWhiteSpace(DetailTitle))
                    {
                        StatusMessage = "A task title is required.";
                        valid = false;
                        return;
                    }

                    draft = CaptureDetailSaveDraft(draft.TaskId);
                    continue;
                }

                StatusMessage = "Changes saved";
                var selectedId = SelectedTask?.Task.Id;
                await RefreshAsyncCore(string.Equals(selectedId, original.Id, StringComparison.Ordinal)
                    ? updated?.Id ?? original.Id
                    : selectedId);
                return;
            }
        });
        return succeeded && valid;
    }

    private DetailSaveDraft CaptureDetailSaveDraft(string taskId) => new(
        taskId,
        DetailTitle.Trim(),
        DetailNotes,
        StatusFromIndex(DetailStatusIndex),
        Math.Clamp(DetailPriorityIndex + 1, 1, 4),
        DetailProject?.ProjectId,
        DetailDate is null ? null : DateOnly.FromDateTime(DetailDate.Value.LocalDateTime),
        DetailDeadline is null ? null : DateOnly.FromDateTime(DetailDeadline.Value.LocalDateTime),
        ParseLabels(DetailLabels),
        _sourceDateMismatchAcknowledgementKey,
        _detailEditVersion);

    public async Task DeleteSelectedAsync()
    {
        if (SelectedTask is null)
        {
            return;
        }

        var taskId = SelectedTask.Task.Id;
        var revision = SelectedTask.Task.Revision;
        await RunBusyAsync(async () =>
        {
            await _taskService.DeleteTaskAsync(taskId, revision);
            StatusMessage = "Task deleted";
            await RefreshAsyncCore();
        }, rethrow: true, reportError: false);
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
        _showAllSubtasks = false;
        SelectedTask = task;
        await LoadSubtasksAsync();
        await LoadSelectedGitHubLinkAsync();
    }

    public void ToggleSubtasks()
    {
        if (!CanToggleSubtasks)
        {
            return;
        }

        _showAllSubtasks = !_showAllSubtasks;
        RefreshVisibleSubtasks();
    }

    public async Task UseSourceDatesAsync()
    {
        if (SelectedTask?.Task is not { } task || !HasSourceDateMismatch)
        {
            return;
        }

        using (BeginDetailUpdate())
        {
            DetailDate = TaskDateValues.PreferredMoment(task.SourcePlannedOn, task.SourcePlannedAt);
            DetailDeadline = TaskDateValues.PreferredMoment(task.SourceDeadlineOn, task.SourceDeadlineAt);
            _sourceDateMismatchAcknowledgementKey = null;
        }
        MarkDetailEdited();
        NotifySourceDateMismatchChanged();
        await SaveSelectedAsync();
    }

    public async Task KeepOpenzaDatesAsync()
    {
        if (BuildSourceDateMismatch(ignoreAcknowledgement: true) is null)
        {
            return;
        }

        var previousAcknowledgementKey = _sourceDateMismatchAcknowledgementKey;
        _sourceDateMismatchAcknowledgementKey = BuildSourceDateMismatchKey();
        MarkDetailEdited();
        NotifySourceDateMismatchChanged();
        if (!await SaveSelectedAsync())
        {
            _sourceDateMismatchAcknowledgementKey = previousAcknowledgementKey;
            NotifySourceDateMismatchChanged();
        }
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
        await _taskService.SetCompletedAsync(task.Id, completed, task.Revision);
    }

    private async Task RefreshAsync()
    {
        await RunBusyAsync(() => RefreshAsyncCore(SelectedTask?.Task.Id));
    }

    private async Task RefreshAsyncCore(string? selectTaskId = null)
    {
        using var detailUpdate = BeginDetailUpdate();
        ApplyTaskViewPreferencesForCurrentContext();
        var labels = await _store.GetLabelsAsync();
        await ClearStaleLabelFilterAsync(labels);
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
            LabelId = _restoredLabelFilterId,
        });
        var projects = await _store.GetProjectsAsync(_currentSpaceId, includeArchived: true);
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

        RefreshLabelFilterOptions(labels);

        Tasks.Clear();
        var subtaskProgress = BuildSubtaskProgress(snapshot.AllSpaceTasks);
        var matchingSubtasks = BuildMatchingSubtaskText(snapshot.VisibleTasks, SearchText);
        var viewKind = SelectedProject is null
            ? SelectedNavigation?.Kind ?? TaskListKind.Inbox
            : TaskListKind.Open;
        foreach (var task in snapshot.VisibleTasks.Where(task => string.IsNullOrWhiteSpace(task.ParentId)))
        {
            projectNames.TryGetValue(task.ProjectId ?? string.Empty, out var projectName);
            Tasks.Add(new TaskListItemViewModel(
                task,
                projectName,
                viewKind,
                SelectedProject is not null,
                subtaskProgress.GetValueOrDefault(task.Id, string.Empty),
                matchingSubtasks.GetValueOrDefault(task.Id, string.Empty)));
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

    private void RefreshLabelFilterOptions(IReadOnlyList<LabelItem> labels)
    {
        var orderedLabels = labels
            .OrderBy(label => label.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var optionsAreCurrent = LabelFilterOptions.Count == orderedLabels.Length + 1 &&
            LabelFilterOptions[0].LabelId is null &&
            orderedLabels.Select((label, index) => (label, option: LabelFilterOptions[index + 1]))
                .All(pair =>
                    string.Equals(pair.label.Id, pair.option.LabelId, StringComparison.Ordinal) &&
                    string.Equals(pair.label.Name, pair.option.Title, StringComparison.Ordinal));

        if (optionsAreCurrent &&
            SelectedLabelFilter is not null &&
            LabelFilterOptions.Contains(SelectedLabelFilter))
        {
            return;
        }

        var selectedLabelId = _restoredLabelFilterId;
        if (!optionsAreCurrent)
        {
            LabelFilterOptions.Clear();
            LabelFilterOptions.Add(new LabelOptionViewModel(null));
            foreach (var label in orderedLabels)
            {
                LabelFilterOptions.Add(new LabelOptionViewModel(label));
            }
        }

        SelectedLabelFilter = LabelFilterOptions.FirstOrDefault(option => option.LabelId == selectedLabelId)
            ?? LabelFilterOptions[0];
    }

    private void ApplyTaskViewPreferencesForCurrentContext()
    {
        var key = TaskViewSettingsKey();
        if (string.Equals(_activeTaskViewSettingsKey, key, StringComparison.Ordinal))
        {
            return;
        }

        var preferences = _preferencesStore.Load();
        var stored = preferences.TaskViewSettings.GetValueOrDefault(key);
        _activeTaskViewSettingsKey = key;

        SortIndex = stored?.SortIndex ?? 0;
        SortDirectionIndex = stored?.SortDirectionIndex ?? 0;
        GroupIndex = stored?.GroupIndex ?? DefaultGroupIndexForCurrentView();
        PriorityFilterIndex = stored?.PriorityFilterIndex ?? 0;
        RepeatFilterIndex = stored?.RepeatFilterIndex ?? 0;
        SelectedLabelFilter = LabelFilterOptions.FirstOrDefault(option =>
                string.Equals(option.LabelId, stored?.LabelFilterId, StringComparison.Ordinal))
            ?? LabelFilterOptions.FirstOrDefault();
        _restoredLabelFilterId = stored?.LabelFilterId;
    }

    private async Task SaveTaskViewPreferencesAsync()
    {
        var key = TaskViewSettingsKey();
        var viewPreferences = new DesktopTaskViewPreferences
        {
            SortIndex = SortIndex,
            SortDirectionIndex = SortDirectionIndex,
            GroupIndex = GroupIndex,
            PriorityFilterIndex = PriorityFilterIndex,
            RepeatFilterIndex = RepeatFilterIndex,
            LabelFilterId = _restoredLabelFilterId,
        };
        _activeTaskViewSettingsKey = key;
        await _preferencesStore.UpdateAsync(preferences =>
        {
            preferences.TaskViewSettings[key] = viewPreferences;
            return preferences;
        });
    }

    private async Task ClearStaleLabelFilterAsync(IReadOnlyList<LabelItem> labels)
    {
        if (_restoredLabelFilterId is null ||
            labels.Any(label => string.Equals(label.Id, _restoredLabelFilterId, StringComparison.Ordinal)))
        {
            return;
        }

        _restoredLabelFilterId = null;
        SelectedLabelFilter = LabelFilterOptions.FirstOrDefault(option => option.LabelId is null);
        await SaveTaskViewPreferencesAsync();
    }

    private string TaskViewSettingsKey()
    {
        var view = SelectedProject is not null ? "tasks" : ViewKey(SelectedNavigation?.Kind ?? TaskListKind.Inbox);
        var project = string.Equals(view, "tasks", StringComparison.Ordinal)
            ? SelectedProject?.Project.Id ?? "all"
            : "all";
        return $"{_currentSpaceId ?? "all"}|{view}|{project}";
    }

    private int DefaultGroupIndexForCurrentView() => SelectedProject is not null
        ? 2
        : SelectedNavigation?.Kind switch
        {
            TaskListKind.Overdue or TaskListKind.Calendar => 1,
            TaskListKind.Completed => 9,
            TaskListKind.Open => 2,
            _ => 0,
        };

    private static string ViewKey(TaskListKind kind) => kind switch
    {
        TaskListKind.Inbox => "inbox",
        TaskListKind.NextActions => "next",
        TaskListKind.Today => "today",
        TaskListKind.Calendar => "calendar",
        TaskListKind.Overdue => "overdue",
        TaskListKind.Waiting => "waiting",
        TaskListKind.Someday => "someday",
        TaskListKind.Open => "tasks",
        TaskListKind.Completed => "completed",
        _ => "inbox",
    };

    private async Task LoadSubtasksAsync()
    {
        Subtasks.Clear();
        if (SelectedTask is null)
        {
            RefreshVisibleSubtasks();
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

        RefreshVisibleSubtasks();
    }

    private static Dictionary<string, string> BuildSubtaskProgress(IReadOnlyList<TaskItem> tasks) =>
        tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.ParentId))
            .GroupBy(task => task.ParentId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => $"{group.Count(task => task.IsCompleted)}/{group.Count()} subtasks",
                StringComparer.Ordinal);

    private static Dictionary<string, string> BuildMatchingSubtaskText(
        IReadOnlyList<TaskItem> visibleTasks,
        string? searchText)
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
                    var remaining = Math.Max(0, group.Count() - matches.Count);
                    var prefix = matches.Count == 1 && remaining == 0
                        ? "Matching subtask"
                        : "Matching subtasks";
                    return $"{prefix}: {string.Join(", ", matches)}{(remaining > 0 ? $" +{remaining}" : string.Empty)}";
                },
                StringComparer.Ordinal);
    }

    private void RefreshVisibleSubtasks()
    {
        VisibleSubtasks.Clear();
        foreach (var subtask in (_showAllSubtasks ? Subtasks : Subtasks.Take(SubtaskPreviewLimit)))
        {
            VisibleSubtasks.Add(subtask);
        }

        OnPropertyChanged(nameof(HasSubtasks));
        OnPropertyChanged(nameof(SubtasksProgressText));
        OnPropertyChanged(nameof(CanToggleSubtasks));
        OnPropertyChanged(nameof(SubtasksToggleText));
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
        OnPropertyChanged(nameof(ConnectedTaskCount));
        OnPropertyChanged(nameof(WaitingConnectedTaskCount));
        OnPropertyChanged(nameof(SkippedConnectedTaskCount));
        OnPropertyChanged(nameof(HasConnectedTasks));
        OnPropertyChanged(nameof(CanAddAllConnectedTasks));
        OnPropertyChanged(nameof(ConnectedTasksSummary));
        OnPropertyChanged(nameof(EmptyStateMessage));
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
        var selectedProjectId = SelectedProject?.Project.Id;
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

        SelectedProject = selectedProjectId is null
            ? null
            : ProjectItems.FirstOrDefault(item => item.Project.Id == selectedProjectId);
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
        _sourceDateMismatchAcknowledgementKey = ReadSourceDateMismatchAcknowledgementKey(task?.LocalMetadataJson);
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
        DetailLabelItems.Clear();
        if (task is not null)
        {
            foreach (var label in task.Labels.OrderBy(label => label.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                DetailLabelItems.Add(label.Name);
            }
        }
        SyncDetailLabels();
        DetailProject = ProjectOptions.FirstOrDefault(option => option.ProjectId == task?.ProjectId)
            ?? ProjectOptions.FirstOrDefault();
        NotifySourceDateMismatchChanged();
    }

    private void SyncDetailLabels() => DetailLabels = string.Join(", ", DetailLabelItems);

    private void NotifyTaskMetadataChanged()
    {
        OnPropertyChanged(nameof(HasSourceTask));
        OnPropertyChanged(nameof(HasSourceDescription));
        OnPropertyChanged(nameof(HasLocalTaskMetadata));
        OnPropertyChanged(nameof(SourceTaskName));
        OnPropertyChanged(nameof(SourceTaskHeader));
        OnPropertyChanged(nameof(SourceTaskTitle));
        OnPropertyChanged(nameof(SourceTaskDescription));
        OnPropertyChanged(nameof(SourceTaskDescriptionHint));
        OnPropertyChanged(nameof(SourceTaskProject));
        OnPropertyChanged(nameof(SourceTaskDate));
        OnPropertyChanged(nameof(SourceTaskDeadline));
        OnPropertyChanged(nameof(SourceTaskPriority));
        OnPropertyChanged(nameof(SourceTaskCreated));
        OnPropertyChanged(nameof(SourceTaskRecurrence));
        OnPropertyChanged(nameof(LocalTaskCreated));
        OnPropertyChanged(nameof(LocalTaskUpdated));
    }

    private static string FormatPriority(int priority) => priority switch
    {
        1 => "Urgent",
        2 => "High",
        3 => "Normal",
        _ => "Low",
    };

    private static string FormatTaskDate(DateTimeOffset? date)
    {
        if (date is null)
        {
            return "No date";
        }

        var localDate = date.Value.LocalDateTime.Date;
        var today = DateTimeOffset.Now.Date;
        if (localDate == today)
        {
            return "Today";
        }

        if (localDate == today.AddDays(1))
        {
            return "Tomorrow";
        }

        return date.Value.ToString("MMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture);
    }

    private static string FormatTaskDateTime(DateTimeOffset date) =>
        date.LocalDateTime.ToString("MMM d, yyyy h:mm tt", System.Globalization.CultureInfo.CurrentCulture);

    private void NotifySourceDateMismatchChanged()
    {
        OnPropertyChanged(nameof(HasSourceDateMismatch));
        OnPropertyChanged(nameof(SourceDateMismatchTitle));
        OnPropertyChanged(nameof(SourceDateMismatchMessage));
        OnPropertyChanged(nameof(UseSourceDatesText));
    }

    private SourceDateMismatchPresentation? BuildSourceDateMismatch(bool ignoreAcknowledgement = false)
    {
        if (SelectedTask?.Task is not { } task ||
            !task.HasProviderSource ||
            IsTodoistNonRecurringSourceTask(task) ||
            !string.IsNullOrWhiteSpace(task.RecurrenceRule))
        {
            return null;
        }

        DateOnly? localDate = DetailDate is null ? null : DateOnly.FromDateTime(DetailDate.Value.LocalDateTime);
        DateOnly? localDeadline = DetailDeadline is null ? null : DateOnly.FromDateTime(DetailDeadline.Value.LocalDateTime);
        var sourceDate = TaskDateValues.FromDateTimeOffset(task.SourcePlannedMoment);
        var sourceDeadline = TaskDateValues.FromDateTimeOffset(task.SourceDeadlineMoment);
        var dateMismatch = localDate != sourceDate;
        var deadlineMismatch = localDeadline != sourceDeadline;
        if (!dateMismatch && !deadlineMismatch)
        {
            return null;
        }

        var key = BuildSourceDateMismatchKey(task.Id, localDate, sourceDate, localDeadline, sourceDeadline);
        if (!ignoreAcknowledgement && string.Equals(_sourceDateMismatchAcknowledgementKey, key, StringComparison.Ordinal))
        {
            return null;
        }

        var sourceName = SourceTaskName;
        var title = dateMismatch && deadlineMismatch
            ? $"{sourceName} dates changed"
            : dateMismatch
                ? $"{sourceName} date changed"
                : $"{sourceName} deadline changed";
        var message = dateMismatch && deadlineMismatch
            ? $"{sourceName} date is {FormatDateOnly(sourceDate)} and deadline is {FormatDateOnly(sourceDeadline)}. Openza keeps {FormatDateOnly(localDate)} and {FormatDateOnly(localDeadline)} until you choose otherwise."
            : dateMismatch
                ? $"{sourceName} date is {FormatDateOnly(sourceDate)}. Openza date is {FormatDateOnly(localDate)}."
                : $"{sourceName} deadline is {FormatDateOnly(sourceDeadline)}. Openza deadline is {FormatDateOnly(localDeadline)}.";
        var useSourceText = dateMismatch && deadlineMismatch
            ? $"Use {sourceName} dates"
            : dateMismatch
                ? $"Use {sourceName} date"
                : $"Use {sourceName} deadline";
        return new SourceDateMismatchPresentation(title, message, useSourceText);
    }

    private string BuildSourceDateMismatchKey()
    {
        var task = SelectedTask?.Task;
        DateOnly? localDate = DetailDate is null ? null : DateOnly.FromDateTime(DetailDate.Value.LocalDateTime);
        DateOnly? localDeadline = DetailDeadline is null ? null : DateOnly.FromDateTime(DetailDeadline.Value.LocalDateTime);
        var sourceDate = TaskDateValues.FromDateTimeOffset(task?.SourcePlannedMoment);
        var sourceDeadline = TaskDateValues.FromDateTimeOffset(task?.SourceDeadlineMoment);
        return BuildSourceDateMismatchKey(task?.Id ?? string.Empty, localDate, sourceDate, localDeadline, sourceDeadline);
    }

    private static string BuildSourceDateMismatchKey(string taskId, DateOnly? localDate, DateOnly? sourceDate, DateOnly? localDeadline, DateOnly? sourceDeadline) =>
        $"{taskId}|{string.Join('|', localDate?.ToString("O") ?? "", sourceDate?.ToString("O") ?? "", localDeadline?.ToString("O") ?? "", sourceDeadline?.ToString("O") ?? "")}";

    private static string FormatDateOnly(DateOnly? date) =>
        date?.ToString("MMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture) ?? "no date";

    private static bool IsTodoistNonRecurringSourceTask(TaskItem task) =>
        string.Equals(task.SourceIntegrationId ?? task.IntegrationId, IntegrationIds.Todoist, StringComparison.Ordinal) &&
        string.IsNullOrWhiteSpace(task.RecurrenceRule);

    private static string? ReadSourceDateMismatchAcknowledgementKey(string? localMetadataJson)
    {
        if (string.IsNullOrWhiteSpace(localMetadataJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(localMetadataJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("openza", out var openza) &&
                openza.ValueKind == JsonValueKind.Object &&
                openza.TryGetProperty("sourceDateMismatchAcknowledgementKey", out var value) &&
                value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? WithSourceDateMismatchAcknowledgement(string? localMetadataJson, string? acknowledgementKey)
    {
        var currentKey = ReadSourceDateMismatchAcknowledgementKey(localMetadataJson);
        if (string.Equals(currentKey, acknowledgementKey, StringComparison.Ordinal) ||
            (string.IsNullOrWhiteSpace(currentKey) && string.IsNullOrWhiteSpace(acknowledgementKey)))
        {
            return localMetadataJson;
        }

        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(localMetadataJson)
                ? new JsonObject()
                : JsonNode.Parse(localMetadataJson) as JsonObject ?? throw new JsonException("Task metadata must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The task's existing metadata is not a JSON object, so its date choice could not be recorded safely.", exception);
        }

        var openza = root["openza"] as JsonObject;
        if (openza is null)
        {
            if (root["openza"] is not null)
            {
                throw new InvalidOperationException("The task's existing Openza metadata is incompatible, so its date choice could not be recorded safely.");
            }

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

    private sealed record SourceDateMismatchPresentation(string Title, string Message, string UseSourceText);

    private IDisposable BeginDetailUpdate()
    {
        _detailUpdateDepth++;
        return new DetailUpdateScope(this);
    }

    private void MarkDetailEdited()
    {
        if (_detailUpdateDepth == 0)
        {
            _detailEditVersion++;
        }
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

    private static bool HasDetailChanges(
        TaskItem original,
        DetailSaveDraft draft,
        bool completed,
        string? localMetadataJson)
    {
        var originalLabels = original.Labels
            .Select(label => label.Name)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase);
        var editedLabels = draft.Labels
            .Select(label => label.Name)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase);

        return !string.Equals(original.Title, draft.Title, StringComparison.Ordinal)
            || !string.Equals(NullIfEmpty(original.Notes), NullIfEmpty(draft.Notes), StringComparison.Ordinal)
            || original.Status != draft.Status
            || original.IsCompleted != completed
            || original.Priority != draft.Priority
            || !string.Equals(original.ProjectId, draft.ProjectId, StringComparison.Ordinal)
            || original.PlannedOn != draft.PlannedOn
            || original.DeadlineOn != draft.DeadlineOn
            || !string.Equals(original.LocalMetadataJson, localMetadataJson, StringComparison.Ordinal)
            || !originalLabels.SequenceEqual(editedLabels, StringComparer.CurrentCultureIgnoreCase);
    }

    private sealed record DetailSaveDraft(
        string TaskId,
        string Title,
        string Notes,
        TaskItemStatus Status,
        int Priority,
        string? ProjectId,
        DateOnly? PlannedOn,
        DateOnly? DeadlineOn,
        IReadOnlyList<LabelItem> Labels,
        string? SourceDateMismatchAcknowledgementKey,
        long Version);

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
            var isExpanded = !_collapsedTaskGroups.Contains(group.Assignment.Key);
            TaskEntries.Add(TaskListEntryViewModel.Header(group.Assignment.Key, group.Assignment.Title, group.Tasks.Count, isExpanded));
            if (!isExpanded)
            {
                continue;
            }

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

    private static DateTimeOffset? ToLocalDateTimeOffset(DateTime? date)
    {
        if (date is null)
        {
            return null;
        }

        var value = DateTime.SpecifyKind(date.Value.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(value, TimeZoneInfo.Local.GetUtcOffset(value));
    }

    private async Task<bool> RunBusyAsync(Func<Task> action, bool rethrow = false, bool reportError = true)
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
            if (reportError)
            {
                SetPersistentStatusMessage($"Could not complete that action: {exception.Message}");
            }
            if (rethrow)
            {
                throw;
            }
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

    private void SetPersistentStatusMessage(string message)
    {
        StatusMessage = message;
        IsStatusMessagePersistent = true;
    }
}
