using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Openza.Tasks.Core.Models;
using Openza.Tasks.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace Openza.Tasks.Controls;

public sealed partial class TaskDetailsPaneControl : UserControl
{
    private const int SubtaskPreviewLimit = 5;

    private static readonly ProjectItem InboxProject = new()
    {
        Id = string.Empty,
        Name = "No project",
        IntegrationId = IntegrationIds.Local,
    };

    private bool _loading = true;
    private bool _suppressLabelTextChanged;
    private string _originalSnapshot = string.Empty;
    private TaskItem? _loadedTask;
    private List<ProjectItem> _projectOptions = [];
    private List<LabelItem> _availableLabels = [];
    private readonly ObservableCollection<string> _labelResults = [];
    private readonly ObservableCollection<LabelItem> _selectedLabels = [];
    private readonly ObservableCollection<TaskListItemViewModel> _subtasks = [];
    private readonly List<TaskListItemViewModel> _allSubtasks = [];
    private readonly ObservableCollection<ProjectItem> _filteredProjectOptions = [];
    private readonly HashSet<string> _dismissedSourceDateMismatchKeys = [];
    private readonly DispatcherTimer _autoSaveTimer = new();
    private readonly DispatcherTimer _autoSaveStateTimer = new();
    private ProjectItem? _selectedProject;
    private bool _showAllSubtasks;
    private bool _syncingCalendarSelection;
    private string? _sourceDateMismatchAcknowledgementKey;
    private TaskExternalLinkInfo? _gitHubIssueLink;

    public event RoutedEventHandler? AutoSaveRequested;
    public event RoutedEventHandler? ToggleCompleteClicked;
    public event RoutedEventHandler? SubtaskToggleCompleteClicked;
    public event TypedEventHandler<TaskDetailsPaneControl, string>? CreateProjectRequested;
    public event RoutedEventHandler? MoveToSpaceRequested;
    public event RoutedEventHandler? ManageGitHubIssueRequested;
    public event RoutedEventHandler? DeleteTaskClicked;
    public event RoutedEventHandler? CancelEditClicked;

    public TaskDetailsPaneControl()
    {
        InitializeComponent();
        SubtasksList.ItemsSource = _subtasks;
        ProjectResultsList.ItemsSource = _filteredProjectOptions;
        LabelResultsList.ItemsSource = _labelResults;
        LabelFieldCell.AddHandler(PointerPressedEvent, new PointerEventHandler(OnLabelFieldPointerPressed), handledEventsToo: true);
        _autoSaveTimer.Interval = TimeSpan.FromMilliseconds(700);
        _autoSaveTimer.Tick += OnAutoSaveTimerTick;
        _autoSaveStateTimer.Interval = TimeSpan.FromSeconds(2);
        _autoSaveStateTimer.Tick += OnAutoSaveStateTimerTick;
        ClearForNewTask(null, 3);
    }

    public string TitleText => TitleEditor.Text.Trim();

    public string NotesText => NotesEditor.Text;

    public string LabelsText => string.Join(", ", _selectedLabels.Select(label => label.Name));

    public DateOnly? SelectedPlannedOn => TaskDateValues.FromDateTimeOffset(DateEditor.Date);

    public DateOnly? SelectedDeadlineOn => TaskDateValues.FromDateTimeOffset(DeadlineDateEditor.Date);

    public ProjectItem? SelectedProject => string.IsNullOrWhiteSpace(_selectedProject?.Id) ? null : _selectedProject;

    public TaskItemStatus SelectedStatus =>
        (WorkflowEditor?.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "inbox" => TaskItemStatus.Inbox,
            "next" => TaskItemStatus.Next,
            "waiting" => TaskItemStatus.Waiting,
            "someday" => TaskItemStatus.Someday,
            _ => TaskItemStatus.Inbox,
        };

    public int SelectedPriority =>
        int.TryParse((PriorityEditor?.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var priority) ? priority : 3;

    public bool IsEditingExistingProviderTask => _loadedTask?.IsProviderTask == true || _loadedTask?.HasProviderSource == true;

    public string? SourceDateMismatchAcknowledgementKey => _sourceDateMismatchAcknowledgementKey;

    public string Snapshot => CurrentSnapshot();

    public bool HasUnsavedChanges => !_loading && CurrentSnapshot() != _originalSnapshot;

    public TaskExternalLinkInfo? GitHubIssueLink => _gitHubIssueLink;

    public void SetProjects(IEnumerable<ProjectItem> projects)
    {
        var selectedProjectId = _selectedProject?.Id ?? string.Empty;
        _projectOptions = [InboxProject, .. projects];
        SetSelectedProject(_projectOptions.FirstOrDefault(project =>
            string.Equals(project.Id, selectedProjectId, StringComparison.Ordinal)) ?? InboxProject);
        RefreshProjectResults();
    }

    public void SelectProject(ProjectItem? project)
    {
        SetSelectedProject(_projectOptions.FirstOrDefault(option =>
            string.Equals(option.Id, project?.Id ?? string.Empty, StringComparison.Ordinal)) ?? InboxProject);
        OnProjectSelectionChanged();
    }

    public void SetLabels(IEnumerable<LabelItem> labels)
    {
        _availableLabels = labels.OrderBy(label => label.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        RefreshLabelResults();
    }

    public void LoadTask(TaskItem task, ProjectItem? project, IEnumerable<TaskListItemViewModel>? subtasks = null)
    {
        _loading = true;
        _loadedTask = task;
        _sourceDateMismatchAcknowledgementKey = ReadSourceDateMismatchAcknowledgementKey(task.LocalMetadataJson);
        var editor = new TaskEditorViewModel { Task = task, Project = project };
        HeaderCompleteBox.IsChecked = task.IsCompleted;
        HeaderCompleteBox.IsEnabled = true;
        SourceText.Text = HeaderContextText(editor.SourceText, project);
        SetProviderPresentation(editor, task, project);

        TitleEditor.Text = task.Title;
        NotesEditor.Text = task.Notes ?? string.Empty;
        SetSelectedLabels(task.Labels);
        DateEditor.Date = task.PlannedMoment;
        DeadlineDateEditor.Date = task.DeadlineMoment;
        SelectProject(project);
        SelectStatus(task.Status);
        SelectPriority(task.Priority);
        SetProviderFieldEditability(editor.CanEditProviderFields);
        SetSubtasks(subtasks ?? []);
        RefreshInspectorValues();

        CompleteButton.IsEnabled = true;
        var isLinkedProviderTask = task.IsProviderTask || task.HasProviderSource;
        CompleteButton.Content = task.IsCompleted ? "Reopen" : "Complete";
        DeleteButton.IsEnabled = true;
        MoveToSpaceMenuItem.IsEnabled = true;
        SetLocalMetadataPresentation(task);
        _originalSnapshot = CurrentSnapshot();
        SetAutoSaveState(null);
        _loading = false;
        ResetScrollPosition();
    }

    public void SetGitHubLink(TaskExternalLinkInfo? link, bool isConnected)
    {
        _gitHubIssueLink = link;
        GitHubActionMenuItem.Text = "GitHub issue...";
    }

    public void ClearForNewTask(ProjectItem? defaultProject, int defaultPriority, TaskItemStatus defaultStatus = TaskItemStatus.Inbox)
    {
        _loading = true;
        _loadedTask = null;
        _sourceDateMismatchAcknowledgementKey = null;
        HeaderCompleteBox.IsChecked = false;
        HeaderCompleteBox.IsEnabled = false;
        SourceText.Text = HeaderContextText("Openza Tasks", defaultProject);
        SetProviderPresentation(new TaskEditorViewModel(), null, defaultProject);
        TitleEditor.Text = string.Empty;
        NotesEditor.Text = string.Empty;
        SetSelectedLabels([]);
        SetSubtasks([]);
        SubtasksSection.Visibility = Visibility.Collapsed;
        DateEditor.Date = null;
        DeadlineDateEditor.Date = null;
        SelectProject(defaultProject);
        SelectStatus(defaultStatus);
        SelectPriority(defaultPriority);
        SetProviderFieldEditability(true);
        RefreshInspectorValues();
        CompleteButton.IsEnabled = false;
        CompleteButton.Content = "Complete";
        DeleteButton.IsEnabled = false;
        MoveToSpaceMenuItem.IsEnabled = false;
        LocalMetadataSection.Visibility = Visibility.Collapsed;
        SetGitHubLink(null, false);
        _originalSnapshot = CurrentSnapshot();
        SetAutoSaveState(null);
        _loading = false;
        ResetScrollPosition();
    }

    public void ResetDirtyState() => _originalSnapshot = CurrentSnapshot();

    public bool MarkSaved(string snapshot)
    {
        if (!string.Equals(CurrentSnapshot(), snapshot, StringComparison.Ordinal))
        {
            return false;
        }

        _originalSnapshot = snapshot;
        return true;
    }

    public void StopPendingAutoSave() => _autoSaveTimer.Stop();

    public void SetAutoSaveState(string? text, bool autoDismiss = false)
    {
        _autoSaveStateTimer.Stop();
        AutoSaveStateText.Text = text ?? string.Empty;
        AutoSaveStateText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        if (autoDismiss && AutoSaveStateText.Visibility == Visibility.Visible)
        {
            _autoSaveStateTimer.Start();
        }
    }

    private void RequestImmediateAutoSave()
    {
        if (_loading)
        {
            return;
        }

        _autoSaveTimer.Stop();
        if (HasUnsavedChanges)
        {
            AutoSaveRequested?.Invoke(this, new RoutedEventArgs());
        }
    }

    private void RequestDebouncedAutoSave()
    {
        if (_loading)
        {
            return;
        }

        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void OnAutoSaveTimerTick(object? sender, object e)
    {
        _autoSaveTimer.Stop();
        RequestImmediateAutoSave();
    }

    private void OnAutoSaveStateTimerTick(object? sender, object e)
    {
        _autoSaveStateTimer.Stop();
        SetAutoSaveState(null);
    }

    private void OnGitHubPrimaryClicked(object sender, RoutedEventArgs e) => ManageGitHubIssueRequested?.Invoke(this, e);

    private void OnCopyTitleClicked(object sender, RoutedEventArgs e) =>
        CopyText(TitleEditor.Text.Trim(), "Title copied");

    private void OnCopyNotesClicked(object sender, RoutedEventArgs e) =>
        CopyText(NotesEditor.Text.Trim(), "Notes copied");

    private void OnCopySourceClicked(object sender, RoutedEventArgs e) =>
        CopyText(BuildSourceCopyText(), "Source copied");

    private void OnCopyMetadataClicked(object sender, RoutedEventArgs e) =>
        CopyText(BuildMetadataCopyText(), "Metadata copied");

    private void CopyText(string text, string statusText)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            SetAutoSaveState("Nothing to copy", autoDismiss: true);
            return;
        }

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        SetAutoSaveState(statusText, autoDismiss: true);
    }

    private void SetProviderFieldEditability(bool canEditProviderContent)
    {
        TitleEditor.IsReadOnly = false;
        ProviderTaskSection.Visibility = canEditProviderContent ? Visibility.Collapsed : Visibility.Visible;
        WorkflowEditor.Visibility = Visibility.Collapsed;
        DateEditor.Visibility = Visibility.Collapsed;
        DeadlineDateEditor.Visibility = Visibility.Collapsed;
        ProjectEditor.Visibility = Visibility.Visible;
        PriorityEditor.Visibility = Visibility.Collapsed;
    }

    private void OnOrganizeFieldsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 560;
        OrganizeFieldsGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

        Grid.SetRow(StatusFieldCell, 0);
        Grid.SetColumn(StatusFieldCell, 0);
        Grid.SetRow(ProjectEditor, compact ? 1 : 0);
        Grid.SetColumn(ProjectEditor, compact ? 0 : 1);
        Grid.SetRow(DateFieldCell, compact ? 2 : 1);
        Grid.SetColumn(DateFieldCell, 0);
        Grid.SetRow(DeadlineFieldCell, compact ? 3 : 1);
        Grid.SetColumn(DeadlineFieldCell, compact ? 0 : 1);
        Grid.SetRow(PriorityFieldCell, compact ? 4 : 2);
        Grid.SetColumn(PriorityFieldCell, 0);
        Grid.SetRow(LabelFieldCell, compact ? 5 : 3);
        Grid.SetColumn(LabelFieldCell, 0);
        Grid.SetColumnSpan(LabelFieldCell, compact ? 1 : 2);
    }

    private void SetSubtasks(IEnumerable<TaskListItemViewModel> subtasks)
    {
        _allSubtasks.Clear();
        _subtasks.Clear();
        foreach (var subtask in subtasks)
        {
            _allSubtasks.Add(subtask);
        }

        _showAllSubtasks = false;
        RefreshSubtasksSection();
    }

    private void RefreshSubtasksSection()
    {
        _subtasks.Clear();
        var visibleSubtasks = _showAllSubtasks ? _allSubtasks : _allSubtasks.Take(SubtaskPreviewLimit);
        foreach (var subtask in visibleSubtasks)
        {
            _subtasks.Add(subtask);
        }

        var canShowSubtasks = _loadedTask is not null && string.IsNullOrWhiteSpace(_loadedTask.ParentId) && _allSubtasks.Count > 0;
        SubtasksSection.Visibility = canShowSubtasks ? Visibility.Visible : Visibility.Collapsed;
        SubtasksProgressText.Text = canShowSubtasks
            ? $"{_allSubtasks.Count(subtask => subtask.Task.IsCompleted)}/{_allSubtasks.Count}"
            : string.Empty;
        SubtasksToggleButton.Visibility = canShowSubtasks && _allSubtasks.Count > SubtaskPreviewLimit
            ? Visibility.Visible
            : Visibility.Collapsed;
        SubtasksToggleButton.Content = _showAllSubtasks
            ? "Show fewer"
            : $"Show all {_allSubtasks.Count} subtasks";
    }

    private void SetProviderPresentation(TaskEditorViewModel editor, TaskItem? task, ProjectItem? project)
    {
        if (!editor.IsProviderOwned || task is null)
        {
            ProviderTaskHeaderText.Text = "Source task";
            ProviderTaskSection.IsExpanded = false;
            ProviderTitleText.Text = string.Empty;
            SourceDescriptionText.Text = string.Empty;
            SourceDescriptionHintText.Text = string.Empty;
            SourceDescriptionSection.Visibility = Visibility.Collapsed;
            ProviderProjectText.Text = string.Empty;
            ProviderDateText.Text = string.Empty;
            ProviderDeadlineText.Text = string.Empty;
            ProviderPriorityText.Text = string.Empty;
            ProviderSourceText.Text = string.Empty;
            ProviderCreatedText.Text = string.Empty;
            ProviderRecurringText.Text = string.Empty;
            return;
        }

        ProviderTaskHeaderText.Text = $"Source: {editor.SourceText}";
        ProviderTitleText.Text = string.IsNullOrWhiteSpace(task.SourceTitle) ? task.Title : task.SourceTitle;
        SourceDescriptionText.Text = task.SourceDescription ?? string.Empty;
        SourceDescriptionHintText.Text = $"From {editor.SourceText}";
        var hasSourceDescription = !string.IsNullOrWhiteSpace(task.SourceDescription);
        SourceDescriptionSection.Visibility = hasSourceDescription ? Visibility.Visible : Visibility.Collapsed;
        ProviderTaskSection.IsExpanded = hasSourceDescription;
        ProviderProjectText.Text = string.IsNullOrWhiteSpace(task.SourceProjectName) ? "No source project" : task.SourceProjectName;
        ProviderDateText.Text = FormatDate(task.SourcePlannedMoment);
        ProviderDeadlineText.Text = FormatDate(task.SourceDeadlineMoment);
        ProviderPriorityText.Text = FormatSourcePriority(task.SourcePriority);
        ProviderSourceText.Text = editor.SourceText;
        ProviderCreatedText.Text = FormatDate(task.CreatedAt);
        ProviderRecurringText.Text = FormatRecurrence(task.RecurrenceRule);
    }

    private void SetLocalMetadataPresentation(TaskItem task)
    {
        LocalMetadataSection.Visibility = task.IntegrationId == IntegrationIds.Local
            ? Visibility.Visible
            : Visibility.Collapsed;
        LocalCreatedText.Text = FormatDateTime(task.CreatedAt);
        LocalUpdatedText.Text = task.UpdatedAt is null ? "Not modified" : FormatDateTime(task.UpdatedAt.Value);
    }

    private void SetSelectedProject(ProjectItem? project)
    {
        _selectedProject = project ?? InboxProject;
        ProjectPickerText.Text = string.IsNullOrWhiteSpace(_selectedProject.Name) ? "No project" : _selectedProject.Name;
        SourceText.Text = HeaderContextText(CurrentSourceText(), SelectedProject);
        RefreshInspectorValues();
    }

    private void SelectStatus(TaskItemStatus status)
    {
        var tag = status switch
        {
            TaskItemStatus.Inbox => "inbox",
            TaskItemStatus.Next => "next",
            TaskItemStatus.Waiting => "waiting",
            TaskItemStatus.Someday => "someday",
            _ => "inbox",
        };

        foreach (var item in WorkflowEditor.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                WorkflowEditor.SelectedItem = item;
                RefreshInspectorValues();
                return;
            }
        }

        WorkflowEditor.SelectedIndex = 0;
        RefreshInspectorValues();
    }

    private void SelectPriority(int priority)
    {
        foreach (var item in PriorityEditor.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag?.ToString() == priority.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                PriorityEditor.SelectedItem = item;
                RefreshInspectorValues();
                return;
            }
        }

        PriorityEditor.SelectedIndex = 2;
        RefreshInspectorValues();
    }

    private static string FormatPriority(int priority) => priority switch
    {
        1 => "Urgent",
        2 => "High",
        3 => "Normal",
        _ => "Low",
    };

    private static string FormatSourcePriority(int? priority) =>
        priority is null ? "No priority" : FormatPriority(priority.Value);

    private static string FormatDate(DateTimeOffset? date)
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

    private static string FormatDateTime(DateTimeOffset date) =>
        date.LocalDateTime.ToString("MMM d, yyyy h:mm tt", System.Globalization.CultureInfo.CurrentCulture);

    private static string FormatRecurrence(string? recurrenceRule) =>
        string.IsNullOrWhiteSpace(recurrenceRule) ? "Not recurring" : recurrenceRule;

    private string BuildSourceCopyText()
    {
        if (_loadedTask is null)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, new[]
        {
            $"Source: {CurrentSourceText()}",
            $"Title: {ProviderTitleText.Text}",
            $"Project: {ProviderProjectText.Text}",
            $"Date: {ProviderDateText.Text}",
            $"Deadline: {ProviderDeadlineText.Text}",
            $"Priority: {ProviderPriorityText.Text}",
            $"Created: {ProviderCreatedText.Text}",
            $"Recurring: {ProviderRecurringText.Text}",
            string.IsNullOrWhiteSpace(_loadedTask.SourceUrl) ? string.Empty : $"URL: {_loadedTask.SourceUrl}",
        }.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private string BuildMetadataCopyText()
    {
        if (_loadedTask is null)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, new[]
        {
            $"Created: {FormatDateTime(_loadedTask.CreatedAt)}",
            $"Modified: {(_loadedTask.UpdatedAt is null ? "Not modified" : FormatDateTime(_loadedTask.UpdatedAt.Value))}",
            $"Status: {StatusValueText.Text}",
            $"Project: {ProjectPickerText.Text}",
            $"Date: {DateValueText.Text}",
            $"Deadline: {DeadlineValueText.Text}",
            $"Priority: {PriorityValueText.Text}",
            $"Labels: {(string.IsNullOrWhiteSpace(LabelsText) ? "No labels" : LabelsText)}",
            $"Source: {CurrentSourceText()}",
        });
    }

    private static string FormatPickerDate(DateTimeOffset? date, string emptyText)
    {
        if (date is null)
        {
            return emptyText;
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

    private void RefreshInspectorValues()
    {
        if (StatusValueText is not null)
        {
            StatusValueText.Text = (WorkflowEditor?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Inbox";
        }

        if (PriorityValueText is not null)
        {
            PriorityValueText.Text = (PriorityEditor?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Normal";
        }

        if (DateValueText is not null)
        {
            DateValueText.Text = FormatPickerDate(DateEditor?.Date, "Add date");
        }

        if (DeadlineValueText is not null)
        {
            DeadlineValueText.Text = FormatPickerDate(DeadlineDateEditor?.Date, "Add deadline");
        }

        if (ProjectPickerText is not null)
        {
            ProjectPickerText.Text = string.IsNullOrWhiteSpace(_selectedProject?.Name) ? "No project" : _selectedProject.Name;
        }

        if (LabelsEmptyText is not null && SelectedLabelsScrollViewer is not null)
        {
            var hasLabels = _selectedLabels.Count > 0;
            LabelsEmptyText.Visibility = hasLabels ? Visibility.Collapsed : Visibility.Visible;
            SelectedLabelsScrollViewer.Visibility = hasLabels ? Visibility.Visible : Visibility.Collapsed;
        }

        SyncCalendarViewSelection(DateCalendarView, DateEditor?.Date);
        SyncCalendarViewSelection(DeadlineCalendarView, DeadlineDateEditor?.Date);
        UpdateSourceDateMismatchNotice();
    }

    private void UpdateSourceDateMismatchNotice()
    {
        if (_loadedTask is null || !_loadedTask.HasProviderSource || !string.IsNullOrWhiteSpace(_loadedTask.RecurrenceRule))
        {
            HideSourceDateMismatchNotice();
            return;
        }

        var localDate = DateOnlyFrom(DateEditor?.Date);
        var localDeadline = DateOnlyFrom(DeadlineDateEditor?.Date);
        var sourceDate = DateOnlyFrom(_loadedTask.SourcePlannedOn, _loadedTask.SourcePlannedAt);
        var sourceDeadline = DateOnlyFrom(_loadedTask.SourceDeadlineOn, _loadedTask.SourceDeadlineAt);
        var dateMismatch = localDate != sourceDate;
        var deadlineMismatch = localDeadline != sourceDeadline;
        if (!dateMismatch && !deadlineMismatch)
        {
            HideSourceDateMismatchNotice();
            return;
        }

        var key = BuildSourceDateMismatchKey(_loadedTask.Id, localDate, sourceDate, localDeadline, sourceDeadline);
        if (_dismissedSourceDateMismatchKeys.Contains(key) ||
            string.Equals(_sourceDateMismatchAcknowledgementKey, key, StringComparison.Ordinal))
        {
            HideSourceDateMismatchNotice();
            return;
        }

        var sourceName = CurrentSourceText();
        SourceDateMismatchTitleText.Text = dateMismatch && deadlineMismatch
            ? $"{sourceName} dates changed"
            : dateMismatch
                ? $"{sourceName} date changed"
                : $"{sourceName} deadline changed";
        SourceDateMismatchBodyText.Text = BuildSourceDateMismatchText(sourceName, localDate, sourceDate, localDeadline, sourceDeadline, dateMismatch, deadlineMismatch);
        UseSourceDateButton.Content = dateMismatch && deadlineMismatch
            ? $"Use {sourceName} dates"
            : dateMismatch
                ? $"Use {sourceName} date"
                : $"Use {sourceName} deadline";
        SourceDateMismatchNotice.Visibility = Visibility.Visible;
    }

    private void HideSourceDateMismatchNotice()
    {
        if (SourceDateMismatchNotice is not null)
        {
            SourceDateMismatchNotice.Visibility = Visibility.Collapsed;
        }
    }

    private static string BuildSourceDateMismatchText(
        string sourceName,
        DateOnly? localDate,
        DateOnly? sourceDate,
        DateOnly? localDeadline,
        DateOnly? sourceDeadline,
        bool dateMismatch,
        bool deadlineMismatch)
    {
        if (dateMismatch && deadlineMismatch)
        {
            return $"{sourceName} date is {FormatDateOnly(sourceDate)} and deadline is {FormatDateOnly(sourceDeadline)}. Openza keeps {FormatDateOnly(localDate)} and {FormatDateOnly(localDeadline)} until you choose otherwise.";
        }

        if (dateMismatch)
        {
            return $"{sourceName} date is {FormatDateOnly(sourceDate)}. Openza date is {FormatDateOnly(localDate)}.";
        }

        return $"{sourceName} deadline is {FormatDateOnly(sourceDeadline)}. Openza deadline is {FormatDateOnly(localDeadline)}.";
    }

    private string BuildSourceDateMismatchKey()
    {
        var localDate = DateOnlyFrom(DateEditor?.Date);
        var localDeadline = DateOnlyFrom(DeadlineDateEditor?.Date);
        var sourceDate = DateOnlyFrom(_loadedTask?.SourcePlannedOn, _loadedTask?.SourcePlannedAt);
        var sourceDeadline = DateOnlyFrom(_loadedTask?.SourceDeadlineOn, _loadedTask?.SourceDeadlineAt);
        return BuildSourceDateMismatchKey(_loadedTask?.Id ?? string.Empty, localDate, sourceDate, localDeadline, sourceDeadline);
    }

    private static string BuildSourceDateMismatchKey(string taskId, DateOnly? localDate, DateOnly? sourceDate, DateOnly? localDeadline, DateOnly? sourceDeadline) =>
        $"{taskId}|{BuildSourceDateMismatchKey(localDate, sourceDate, localDeadline, sourceDeadline)}";

    private static string BuildSourceDateMismatchKey(DateOnly? localDate, DateOnly? sourceDate, DateOnly? localDeadline, DateOnly? sourceDeadline) =>
        string.Join('|', localDate?.ToString("O") ?? "", sourceDate?.ToString("O") ?? "", localDeadline?.ToString("O") ?? "", sourceDeadline?.ToString("O") ?? "");

    private static string? ReadSourceDateMismatchAcknowledgementKey(string? localMetadataJson)
    {
        if (string.IsNullOrWhiteSpace(localMetadataJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(localMetadataJson);
            if (document.RootElement.TryGetProperty("openza", out var openza) &&
                openza.TryGetProperty("sourceDateMismatchAcknowledgementKey", out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static DateOnly? DateOnlyFrom(DateTimeOffset? value) => TaskDateValues.FromDateTimeOffset(value);

    private static DateOnly? DateOnlyFrom(DateOnly? date, DateTimeOffset? moment) => TaskDateValues.PreferredDate(date, moment);

    private static string FormatDateOnly(DateOnly? value) => FormatDate(TaskDateValues.ToLocalDateTime(value));

    private void SyncCalendarViewSelection(CalendarView calendar, DateTimeOffset? date)
    {
        _syncingCalendarSelection = true;
        calendar.SelectedDates.Clear();
        if (date is not null)
        {
            calendar.SelectedDates.Add(date.Value);
        }

        _syncingCalendarSelection = false;
    }

    private static string HeaderContextText(string source, ProjectItem? project)
    {
        return string.IsNullOrWhiteSpace(project?.Name) ? source : $"{project.Name} · {source}";
    }

    private string CurrentSourceText()
    {
        if (_loadedTask is null)
        {
            return "Openza Tasks";
        }

        return TaskListItemViewModel.SourceName(_loadedTask.SourceIntegrationId ?? _loadedTask.IntegrationId);
    }

    private string CurrentSnapshot() =>
        string.Join('\u001f',
            TitleEditor.Text,
            NotesEditor.Text,
            LabelsText,
            DateEditor.Date?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            DeadlineDateEditor.Date?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            SelectedStatus.ToStorageValue(),
            SelectedProject?.Id ?? string.Empty,
            SelectedPriority.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _sourceDateMismatchAcknowledgementKey ?? string.Empty);

    private void OnEditorChanged(object sender, TextChangedEventArgs e)
    {
        RequestDebouncedAutoSave();
    }

    private void OnWorkflowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        RefreshInspectorValues();
        RequestImmediateAutoSave();
    }

    private void OnWorkflowMenuItemClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not string tag)
        {
            return;
        }

        var status = tag switch
        {
            "next" => TaskItemStatus.Next,
            "waiting" => TaskItemStatus.Waiting,
            "someday" => TaskItemStatus.Someday,
            _ => TaskItemStatus.Inbox,
        };

        SelectStatus(status);
        RequestImmediateAutoSave();
    }

    private void OnProjectSelectionChanged()
    {
        if (_loading || WorkflowEditor is null)
        {
            return;
        }

        SourceText.Text = HeaderContextText(CurrentSourceText(), SelectedProject);
        RequestImmediateAutoSave();
    }

    private void OnProjectPickerClicked(object sender, RoutedEventArgs e)
    {
        ProjectSearchBox.Text = string.Empty;
        RefreshProjectResults();
        ProjectSearchBox.DispatcherQueue.TryEnqueue(() => ProjectSearchBox.Focus(FocusState.Programmatic));
    }

    private void OnProjectSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            RefreshProjectResults(sender.Text);
        }
    }

    private void OnProjectSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var query = args.QueryText.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        var match = FindProject(query) ?? FilterProjects(query).FirstOrDefault(project => !string.IsNullOrWhiteSpace(project.Id));
        if (match is not null)
        {
            ChooseProject(match);
            return;
        }

        RequestProjectCreation(query);
    }

    private void OnProjectSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            ProjectPickerFlyout.Hide();
            e.Handled = true;
        }
    }

    private void OnProjectResultClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id)
        {
            return;
        }

        var project = _projectOptions.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.Ordinal));
        if (project is not null)
        {
            ChooseProject(project);
        }
    }

    private void OnCreateProjectClicked(object sender, RoutedEventArgs e)
    {
        RequestProjectCreation(ProjectSearchBox.Text.Trim());
    }

    private void ChooseProject(ProjectItem project)
    {
        SetSelectedProject(project);
        ProjectPickerFlyout.Hide();
        ProjectSearchBox.Text = string.Empty;
        RefreshProjectResults();
        OnProjectSelectionChanged();
    }

    private void RequestProjectCreation(string name)
    {
        var projectName = name.Trim();
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return;
        }

        var existing = FindProject(projectName);
        if (existing is not null)
        {
            ChooseProject(existing);
            return;
        }

        ProjectPickerFlyout.Hide();
        CreateProjectRequested?.Invoke(this, projectName);
    }

    private ProjectItem? FindProject(string name) =>
        _projectOptions.FirstOrDefault(project =>
            string.Equals(project.Name, name.Trim(), StringComparison.CurrentCultureIgnoreCase));

    private IEnumerable<ProjectItem> FilterProjects(string search)
    {
        var query = search.Trim();
        return _projectOptions
            .Where(project => string.IsNullOrWhiteSpace(query) || project.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(project => string.IsNullOrWhiteSpace(project.Id) ? 0 : 1)
            .ThenBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(30);
    }

    private void RefreshProjectResults(string search = "")
    {
        _filteredProjectOptions.Clear();
        foreach (var project in FilterProjects(search))
        {
            _filteredProjectOptions.Add(project);
        }

        var trimmed = search.Trim();
        var canCreate = !string.IsNullOrWhiteSpace(trimmed) && FindProject(trimmed) is null;
        CreateProjectButton.Visibility = canCreate ? Visibility.Visible : Visibility.Collapsed;
        CreateProjectButton.Content = canCreate ? $"Create project \"{trimmed}\"" : "Create project";
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshInspectorValues();
        RequestImmediateAutoSave();
    }

    private void OnPriorityMenuItemClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse((sender as MenuFlyoutItem)?.Tag?.ToString(), out var priority))
        {
            return;
        }

        SelectPriority(priority);
        RequestImmediateAutoSave();
    }

    private void OnTaskDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        RefreshInspectorValues();
        RequestImmediateAutoSave();
    }

    private void OnDateCalendarSelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
    {
        if (_syncingCalendarSelection || sender.SelectedDates.Count == 0)
        {
            return;
        }

        DateEditor.Date = sender.SelectedDates[0];
        DatePropertyButton.Flyout.Hide();
    }

    private void OnDeadlineCalendarSelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
    {
        if (_syncingCalendarSelection || sender.SelectedDates.Count == 0)
        {
            return;
        }

        DeadlineDateEditor.Date = sender.SelectedDates[0];
        DeadlinePropertyButton.Flyout.Hide();
    }

    private void OnClearDateClicked(object sender, RoutedEventArgs e)
    {
        DateEditor.Date = null;
        DatePropertyButton.Flyout.Hide();
    }

    private void OnClearDeadlineClicked(object sender, RoutedEventArgs e)
    {
        DeadlineDateEditor.Date = null;
        DeadlinePropertyButton.Flyout.Hide();
    }

    private void OnUseSourceDateClicked(object sender, RoutedEventArgs e)
    {
        if (_loadedTask is null)
        {
            return;
        }

        var wasLoading = _loading;
        _loading = true;
        DateEditor.Date = TaskDateValues.PreferredMoment(_loadedTask.SourcePlannedOn, _loadedTask.SourcePlannedAt);
        DeadlineDateEditor.Date = TaskDateValues.PreferredMoment(_loadedTask.SourceDeadlineOn, _loadedTask.SourceDeadlineAt);
        _sourceDateMismatchAcknowledgementKey = null;
        _loading = wasLoading;
        RefreshInspectorValues();
        RequestImmediateAutoSave();
    }

    private void OnKeepOpenzaDateClicked(object sender, RoutedEventArgs e)
    {
        _sourceDateMismatchAcknowledgementKey = BuildSourceDateMismatchKey();
        _dismissedSourceDateMismatchKeys.Add(_sourceDateMismatchAcknowledgementKey);
        HideSourceDateMismatchNotice();
        RequestImmediateAutoSave();
    }

    private void OnLabelPickerClicked(object sender, RoutedEventArgs e)
    {
        PrepareLabelPicker();
    }

    private void OnLabelFieldTapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source))
        {
            return;
        }

        PrepareLabelPicker();
        LabelPickerFlyout.ShowAt(LabelFieldCell);
        e.Handled = true;
    }

    private void OnLabelFieldPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source))
        {
            return;
        }

        PrepareLabelPicker();
        LabelPickerFlyout.ShowAt(LabelFieldCell);
        e.Handled = true;
    }

    private void PrepareLabelPicker()
    {
        ClearLabelSearch();
        RefreshLabelResults();
        LabelSearchBox.DispatcherQueue.TryEnqueue(() => LabelSearchBox.Focus(FocusState.Programmatic));
    }

    private static bool IsInsideButton(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is Button)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void OnLabelSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_suppressLabelTextChanged || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        RefreshLabelResults(sender.Text);
    }

    private void OnLabelSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var labelName = args.ChosenSuggestion is string suggestion
            ? LabelNameFromSuggestion(suggestion)
            : args.QueryText;
        SelectLabelAndResetPicker(labelName, closeFlyout: true);
    }

    private void OnLabelResultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LabelResultsList.SelectedItem is not string labelName)
        {
            return;
        }

        SelectLabelAndResetPicker(LabelNameFromSuggestion(labelName), closeFlyout: true);
    }

    private void OnRemoveLabelClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id)
        {
            return;
        }

        var label = _selectedLabels.FirstOrDefault(item => item.Id == id);
        if (label is not null)
        {
            _selectedLabels.Remove(label);
            SyncLabelsText();
            RefreshLabelResults(LabelSearchBox.Text);
            RequestImmediateAutoSave();
        }
    }

    private void SetSelectedLabels(IEnumerable<LabelItem> labels)
    {
        _selectedLabels.Clear();
        foreach (var label in labels)
        {
            _selectedLabels.Add(label);
        }

        SyncLabelsText();
        ClearLabelSearch();
        RefreshLabelResults();
    }

    private void AddSelectedLabel(LabelItem label)
    {
        if (_selectedLabels.Any(item => string.Equals(item.Name, label.Name, StringComparison.CurrentCultureIgnoreCase)))
        {
            return;
        }

        _selectedLabels.Add(label);
        SyncLabelsText();
        RefreshLabelResults(LabelSearchBox.Text);
    }

    private LabelItem GetOrCreateLabel(string name)
    {
        var trimmed = name.Trim();
        return _availableLabels.FirstOrDefault(label => string.Equals(label.Name, trimmed, StringComparison.CurrentCultureIgnoreCase)) ??
            new LabelItem
            {
                Id = $"label_pending_{Guid.NewGuid():N}",
                IntegrationId = IntegrationIds.Local,
                Name = trimmed,
                Color = "#808080",
                CreatedAt = DateTimeOffset.UtcNow,
            };
    }

    private void SyncLabelsText()
    {
        LabelsEditor.Text = LabelsText;
        RefreshSelectedLabelChips();
        RefreshInspectorValues();
    }

    private void RefreshSelectedLabelChips()
    {
        if (SelectedLabelsPanel is null)
        {
            return;
        }

        SelectedLabelsPanel.Children.Clear();
        var chipsPerRow = GetSelectedLabelChipsPerRow();
        StackPanel? row = null;
        foreach (var label in _selectedLabels)
        {
            var labelText = new TextBlock
            {
                Text = label.Name,
                MaxWidth = GetSelectedLabelChipTextWidth(chipsPerRow),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var removeIcon = new FontIcon
            {
                Glyph = "\uE711",
                FontSize = 10,
            };

            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
            };
            content.Children.Add(labelText);
            content.Children.Add(removeIcon);

            var chip = new Button
            {
                Padding = new Thickness(10, 4, 10, 4),
                Tag = label.Id,
                Content = content,
                MaxWidth = GetSelectedLabelChipWidth(chipsPerRow),
            };
            chip.Click += OnRemoveLabelClicked;

            if (row is null || row.Children.Count >= chipsPerRow)
            {
                row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                };
                SelectedLabelsPanel.Children.Add(row);
            }

            row.Children.Add(chip);
        }
    }

    private int GetSelectedLabelChipsPerRow()
    {
        var availableWidth = SelectedLabelsScrollViewer?.ActualWidth ?? 0;
        if (availableWidth >= 640)
        {
            return 3;
        }

        if (availableWidth >= 360)
        {
            return 2;
        }

        return 1;
    }

    private double GetSelectedLabelChipWidth(int chipsPerRow)
    {
        var availableWidth = SelectedLabelsScrollViewer?.ActualWidth ?? 0;
        if (availableWidth <= 0)
        {
            return chipsPerRow == 1 ? 320 : 220;
        }

        var totalGap = Math.Max(0, chipsPerRow - 1) * 8;
        return Math.Max(120, Math.Floor((availableWidth - totalGap) / chipsPerRow));
    }

    private double GetSelectedLabelChipTextWidth(int chipsPerRow)
    {
        return Math.Max(80, GetSelectedLabelChipWidth(chipsPerRow) - 42);
    }

    private void OnSelectedLabelsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_selectedLabels.Count > 0)
        {
            RefreshSelectedLabelChips();
        }
    }

    private void RefreshLabelResults(string search = "")
    {
        _labelResults.Clear();
        var selectedNames = _selectedLabels.Select(label => label.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var trimmed = search.Trim();
        var labels = _availableLabels
            .Where(label => !selectedNames.Contains(label.Name))
            .Where(label => string.IsNullOrWhiteSpace(search) || label.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .Select(label => label.Name)
            .Take(20)
            .ToList();

        if (!string.IsNullOrWhiteSpace(trimmed) &&
            !selectedNames.Contains(trimmed) &&
            _availableLabels.All(label => !string.Equals(label.Name, trimmed, StringComparison.CurrentCultureIgnoreCase)))
        {
            labels.Insert(0, CreateLabelSuggestion(trimmed));
        }

        foreach (var label in labels)
        {
            _labelResults.Add(label);
        }
    }

    private void ClearLabelSearch()
    {
        _suppressLabelTextChanged = true;
        LabelSearchBox.Text = string.Empty;
        _suppressLabelTextChanged = false;
        LabelResultsList.SelectedItem = null;
    }

    private void SelectLabelAndResetPicker(string labelName, bool closeFlyout)
    {
        if (string.IsNullOrWhiteSpace(labelName))
        {
            return;
        }

        AddSelectedLabel(GetOrCreateLabel(labelName));
        ClearLabelSearch();
        RefreshLabelResults();
        if (closeFlyout)
        {
            LabelPickerFlyout.Hide();
        }
        else
        {
            LabelSearchBox.DispatcherQueue.TryEnqueue(() => LabelSearchBox.Focus(FocusState.Programmatic));
        }

        RequestImmediateAutoSave();
    }

    private static string CreateLabelSuggestion(string labelName) => $"Create \"{labelName}\"";

    private static string LabelNameFromSuggestion(string suggestion)
    {
        const string prefix = "Create \"";
        return suggestion.StartsWith(prefix, StringComparison.Ordinal) && suggestion.EndsWith('"')
            ? suggestion[prefix.Length..^1]
            : suggestion;
    }

    private void ResetScrollPosition()
    {
        DetailsScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        DispatcherQueue.TryEnqueue(() =>
            DetailsScrollViewer.ChangeView(null, 0, null, disableAnimation: true));
    }

    private void OnToggleCompleteClicked(object sender, RoutedEventArgs e) => ToggleCompleteClicked?.Invoke(sender, e);

    private void OnSubtaskToggleCompleteClicked(object sender, RoutedEventArgs e) => SubtaskToggleCompleteClicked?.Invoke(sender, e);

    private void OnToggleSubtasksClicked(object sender, RoutedEventArgs e)
    {
        _showAllSubtasks = !_showAllSubtasks;
        RefreshSubtasksSection();
    }

    private void OnDeleteTaskClicked(object sender, RoutedEventArgs e) => DeleteTaskClicked?.Invoke(sender, e);

    private void OnMoveToSpaceClicked(object sender, RoutedEventArgs e) => MoveToSpaceRequested?.Invoke(sender, e);

    private void OnCancelEditClicked(object sender, RoutedEventArgs e) => CancelEditClicked?.Invoke(sender, e);
}
