using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Openza.Tasks.Core.Models;
using Openza.Tasks.ViewModels;
using Windows.Foundation;
using Windows.System;

namespace Openza.Tasks.Controls;

public sealed partial class TaskDetailsPaneControl : UserControl
{
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
    private readonly ObservableCollection<ProjectItem> _filteredProjectOptions = [];
    private ProjectItem? _selectedProject;

    public event RoutedEventHandler? SaveTaskClicked;
    public event RoutedEventHandler? ToggleCompleteClicked;
    public event RoutedEventHandler? SubtaskToggleCompleteClicked;
    public event TypedEventHandler<TaskDetailsPaneControl, string>? CreateProjectRequested;
    public event RoutedEventHandler? DeleteTaskClicked;
    public event RoutedEventHandler? CancelEditClicked;

    public TaskDetailsPaneControl()
    {
        InitializeComponent();
        SubtasksList.ItemsSource = _subtasks;
        ProjectResultsList.ItemsSource = _filteredProjectOptions;
        LabelResultsList.ItemsSource = _labelResults;
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

    public bool HasUnsavedChanges => !_loading && CurrentSnapshot() != _originalSnapshot;

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
        var editor = new TaskEditorViewModel { Task = task, Project = project };
        HeaderTitleText.Text = string.IsNullOrWhiteSpace(task.Title) ? "Task details" : task.Title;
        HeaderCompleteBox.IsChecked = task.IsCompleted;
        HeaderCompleteBox.IsEnabled = true;
        SourceText.Text = HeaderContextText(editor.SourceText, project);
        SetProviderPresentation(editor, task, project);

        TitleEditor.Text = task.Title;
        NotesEditor.Text = task.Notes ?? string.Empty;
        SetSelectedLabels(task.Labels);
        DateEditor.Date = TaskDateValues.ToLocalDateTime(task.PlannedOn);
        DeadlineDateEditor.Date = TaskDateValues.ToLocalDateTime(task.DeadlineOn);
        SelectProject(project);
        SelectStatus(task.Status);
        SelectPriority(task.Priority);
        SetProviderFieldEditability(editor.CanEditProviderFields);
        SetSubtasks(subtasks ?? []);

        CompleteButton.IsEnabled = true;
        var isLinkedProviderTask = task.IsProviderTask || task.HasProviderSource;
        CompleteButton.Content = task.IsCompleted ? "Reopen" : "Complete";
        DeleteButton.IsEnabled = true;
        SaveButton.Content = "Save";
        _originalSnapshot = CurrentSnapshot();
        _loading = false;
        ResetScrollPosition();
    }

    public void ClearForNewTask(ProjectItem? defaultProject, int defaultPriority, TaskItemStatus defaultStatus = TaskItemStatus.Inbox)
    {
        _loading = true;
        _loadedTask = null;
        HeaderTitleText.Text = "New task";
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
        CompleteButton.IsEnabled = false;
        CompleteButton.Content = "Complete";
        DeleteButton.IsEnabled = false;
        SaveButton.Content = "Create task";
        _originalSnapshot = CurrentSnapshot();
        _loading = false;
        ResetScrollPosition();
    }

    public void ResetDirtyState() => _originalSnapshot = CurrentSnapshot();

    private void SetProviderFieldEditability(bool canEditProviderContent)
    {
        TitleEditor.IsReadOnly = !canEditProviderContent;
        EditableTaskSection.Visibility = canEditProviderContent ? Visibility.Visible : Visibility.Collapsed;
        ProviderTaskSection.Visibility = canEditProviderContent ? Visibility.Collapsed : Visibility.Visible;
        DateEditor.Visibility = Visibility.Visible;
        DeadlineDateEditor.Visibility = Visibility.Visible;
        ProjectEditor.Visibility = Visibility.Visible;
        PriorityEditor.Visibility = Visibility.Visible;
        OrganizeHeader.Text = "Organize";
    }

    private void SetSubtasks(IEnumerable<TaskListItemViewModel> subtasks)
    {
        _subtasks.Clear();
        foreach (var subtask in subtasks)
        {
            _subtasks.Add(subtask);
        }

        var canShowSubtasks = _loadedTask is not null && string.IsNullOrWhiteSpace(_loadedTask.ParentId) && _subtasks.Count > 0;
        SubtasksSection.Visibility = canShowSubtasks ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetProviderPresentation(TaskEditorViewModel editor, TaskItem? task, ProjectItem? project)
    {
        if (!editor.IsProviderOwned || task is null)
        {
            ProviderTitleText.Text = string.Empty;
            SourceDescriptionText.Text = string.Empty;
            SourceDescriptionHintText.Text = string.Empty;
            SourceDescriptionSection.Visibility = Visibility.Collapsed;
            ProviderProjectText.Text = string.Empty;
            ProviderDateText.Text = string.Empty;
            ProviderDeadlineText.Text = string.Empty;
            ProviderPriorityText.Text = string.Empty;
            ProviderSourceText.Text = string.Empty;
            return;
        }

        ProviderTitleText.Text = task.Title;
        SourceDescriptionText.Text = task.SourceDescription ?? string.Empty;
        SourceDescriptionHintText.Text = $"From {editor.SourceText}";
        SourceDescriptionSection.Visibility = string.IsNullOrWhiteSpace(task.SourceDescription) ? Visibility.Collapsed : Visibility.Visible;
        ProviderProjectText.Text = task.SourceProjectName ?? project?.Name ?? "No project";
        ProviderDateText.Text = FormatDate(task.SourcePlannedMoment ?? task.PlannedMoment);
        ProviderDeadlineText.Text = FormatDate(task.SourceDeadlineMoment ?? task.DeadlineMoment);
        ProviderPriorityText.Text = FormatPriority(task.SourcePriority ?? task.Priority);
        ProviderSourceText.Text = editor.SourceText;
    }

    private void SetSelectedProject(ProjectItem? project)
    {
        _selectedProject = project ?? InboxProject;
        ProjectPickerText.Text = string.IsNullOrWhiteSpace(_selectedProject.Name) ? "No project" : _selectedProject.Name;
        SourceText.Text = HeaderContextText(CurrentSourceText(), SelectedProject);
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
                return;
            }
        }

        WorkflowEditor.SelectedIndex = 0;
    }

    private void SelectPriority(int priority)
    {
        foreach (var item in PriorityEditor.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag?.ToString() == priority.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                PriorityEditor.SelectedItem = item;
                return;
            }
        }

        PriorityEditor.SelectedIndex = 2;
    }

    private static string FormatPriority(int priority) => priority switch
    {
        1 => "Urgent",
        2 => "High",
        3 => "Normal",
        _ => "Low",
    };

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
            SelectedPriority.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private void OnEditorChanged(object sender, TextChangedEventArgs e)
    {
        if (ReferenceEquals(sender, TitleEditor) && !_loading)
        {
            HeaderTitleText.Text = string.IsNullOrWhiteSpace(TitleEditor.Text) ? "New task" : TitleEditor.Text.Trim();
        }
    }

    private void OnWorkflowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

    }

    private void OnProjectSelectionChanged()
    {
        if (_loading || WorkflowEditor is null)
        {
            return;
        }

        SourceText.Text = HeaderContextText(CurrentSourceText(), SelectedProject);
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
    }

    private void OnTaskDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
    }

    private void OnLabelPickerClicked(object sender, RoutedEventArgs e)
    {
        ClearLabelSearch();
        RefreshLabelResults();
        LabelSearchBox.DispatcherQueue.TryEnqueue(() => LabelSearchBox.Focus(FocusState.Programmatic));
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
        }
    }

    private void SetSelectedLabels(IEnumerable<LabelItem> labels)
    {
        _selectedLabels.Clear();
        foreach (var label in labels)
        {
            _selectedLabels.Add(label);
        }

        SelectedLabelsList.ItemsSource = _selectedLabels;
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

    private void OnSaveTaskClicked(object sender, RoutedEventArgs e) => SaveTaskClicked?.Invoke(sender, e);

    private void OnToggleCompleteClicked(object sender, RoutedEventArgs e) => ToggleCompleteClicked?.Invoke(sender, e);

    private void OnSubtaskToggleCompleteClicked(object sender, RoutedEventArgs e) => SubtaskToggleCompleteClicked?.Invoke(sender, e);

    private void OnDeleteTaskClicked(object sender, RoutedEventArgs e) => DeleteTaskClicked?.Invoke(sender, e);

    private void OnCancelEditClicked(object sender, RoutedEventArgs e) => CancelEditClicked?.Invoke(sender, e);
}
