using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Openza.Tasks.Core.Models;
using Openza.Tasks.ViewModels;

namespace Openza.Tasks.Controls;

public sealed partial class TaskDetailsPaneControl : UserControl
{
    private static readonly ProjectItem InboxProject = new()
    {
        Id = string.Empty,
        Name = "Inbox",
        IntegrationId = IntegrationIds.Local,
    };

    private bool _loading;
    private string _originalSnapshot = string.Empty;
    private TaskItem? _loadedTask;
    private List<ProjectItem> _projectOptions = [];
    private List<LabelItem> _availableLabels = [];
    private readonly ObservableCollection<LabelItem> _selectedLabels = [];

    public event RoutedEventHandler? SaveTaskClicked;
    public event RoutedEventHandler? ToggleCompleteClicked;
    public event RoutedEventHandler? DeleteTaskClicked;
    public event RoutedEventHandler? CancelEditClicked;

    public TaskDetailsPaneControl()
    {
        InitializeComponent();
        ClearForNewTask(null, 3);
    }

    public string TitleText => TitleEditor.Text.Trim();

    public string DescriptionText => DescriptionEditor.Text;

    public string NotesText => NotesEditor.Text;

    public string LabelsText => string.Join(", ", _selectedLabels.Select(label => label.Name));

    public DateTimeOffset? SelectedDueDate => DueDateEditor.Date;

    public ProjectItem? SelectedProject =>
        ProjectEditor.SelectedItem is ProjectItem project && !string.IsNullOrWhiteSpace(project.Id)
            ? project
            : null;

    public TaskItemStatus SelectedStatus =>
        (WorkflowEditor.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "next" => TaskItemStatus.Next,
            "waiting" => TaskItemStatus.Waiting,
            "someday" => TaskItemStatus.Someday,
            _ => TaskItemStatus.None,
        };

    public int SelectedPriority =>
        int.TryParse((PriorityEditor.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var priority) ? priority : 3;

    public bool IsEditingExistingProviderTask => _loadedTask?.IsProviderTask == true;

    public bool HasUnsavedChanges => !_loading && CurrentSnapshot() != _originalSnapshot;

    public void SetProjects(IEnumerable<ProjectItem> projects)
    {
        _projectOptions = [InboxProject, .. projects];
        ProjectEditor.ItemsSource = _projectOptions;
        if (ProjectEditor.SelectedItem is null)
        {
            ProjectEditor.SelectedItem = InboxProject;
        }
    }

    public void SetLabels(IEnumerable<LabelItem> labels)
    {
        _availableLabels = labels.OrderBy(label => label.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        RefreshLabelSuggestions();
    }

    public void LoadTask(TaskItem task, ProjectItem? project)
    {
        _loading = true;
        _loadedTask = task;
        var editor = new TaskEditorViewModel { Task = task, Project = project };
        SourceText.Text = editor.SourceText;
        ProviderInfo.IsOpen = editor.IsProviderOwned;
        ProviderInfo.Message = editor.ProviderOwnershipText;

        TitleEditor.Text = task.Title;
        DescriptionEditor.Text = task.Description ?? string.Empty;
        NotesEditor.Text = task.Notes ?? string.Empty;
        SetSelectedLabels(task.Labels);
        DueDateEditor.Date = task.DueDate;
        SelectProject(project);
        SelectStatus(task.Status);
        SelectPriority(task.Priority);
        SetProviderFieldEditability(editor.CanEditProviderFields);

        CompleteButton.IsEnabled = true;
        CompleteButton.Content = task.IsCompleted ? "Reopen" : "Complete";
        DeleteButton.IsEnabled = true;
        SaveButton.Content = task.IsProviderTask ? "Save local fields" : "Save";
        _originalSnapshot = CurrentSnapshot();
        _loading = false;
    }

    public void ClearForNewTask(ProjectItem? defaultProject, int defaultPriority, TaskItemStatus defaultStatus = TaskItemStatus.None)
    {
        _loading = true;
        _loadedTask = null;
        SourceText.Text = "Openza Tasks";
        ProviderInfo.IsOpen = false;
        TitleEditor.Text = string.Empty;
        DescriptionEditor.Text = string.Empty;
        NotesEditor.Text = string.Empty;
        SetSelectedLabels([]);
        DueDateEditor.Date = null;
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
    }

    public void ResetDirtyState() => _originalSnapshot = CurrentSnapshot();

    private void SetProviderFieldEditability(bool canEditProviderFields)
    {
        TitleEditor.IsReadOnly = !canEditProviderFields;
        DescriptionEditor.IsReadOnly = !canEditProviderFields;
        ProjectEditor.IsEnabled = canEditProviderFields;
        PriorityEditor.IsEnabled = canEditProviderFields;
        DueDateEditor.IsEnabled = canEditProviderFields;
    }

    private void SelectProject(ProjectItem? project)
    {
        ProjectEditor.SelectedItem = _projectOptions.FirstOrDefault(option =>
            string.Equals(option.Id, project?.Id ?? string.Empty, StringComparison.Ordinal)) ?? InboxProject;
    }

    private void SelectStatus(TaskItemStatus status)
    {
        var tag = status switch
        {
            TaskItemStatus.Next => "next",
            TaskItemStatus.Waiting => "waiting",
            TaskItemStatus.Someday => "someday",
            _ => "none",
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

    private string CurrentSnapshot() =>
        string.Join('\u001f',
            TitleEditor.Text,
            DescriptionEditor.Text,
            NotesEditor.Text,
            LabelsText,
            DueDateEditor.Date?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            SelectedStatus.ToStorageValue(),
            SelectedProject?.Id ?? string.Empty,
            SelectedPriority.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private void OnEditorChanged(object sender, TextChangedEventArgs e)
    {
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    private void OnDueDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
    }

    private void OnLabelTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        RefreshLabelSuggestions(sender.Text, includeAllWhenEmpty: false);
    }

    private void OnLabelEditorGotFocus(object sender, RoutedEventArgs e)
    {
        RefreshLabelSuggestions(LabelEditor.Text, includeAllWhenEmpty: true);
    }

    private void OnLabelSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is string labelName)
        {
            AddSelectedLabel(GetOrCreateLabel(labelName));
        }
        else if (!string.IsNullOrWhiteSpace(args.QueryText))
        {
            AddSelectedLabel(GetOrCreateLabel(args.QueryText));
        }

        ClearLabelInputAfterSelection(sender);
    }

    private void OnLabelSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not string labelName)
        {
            return;
        }

        AddSelectedLabel(GetOrCreateLabel(labelName));
        ClearLabelInputAfterSelection(sender);
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
            RefreshLabelSuggestions(LabelEditor.Text, includeAllWhenEmpty: true);
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
        RefreshLabelSuggestions(LabelEditor.Text, includeAllWhenEmpty: true);
    }

    private void AddSelectedLabel(LabelItem label)
    {
        if (_selectedLabels.Any(item => string.Equals(item.Name, label.Name, StringComparison.CurrentCultureIgnoreCase)))
        {
            return;
        }

        _selectedLabels.Add(label);
        SyncLabelsText();
        ClearLabelSuggestions();
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

    private void RefreshLabelSuggestions(string search = "", bool includeAllWhenEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(search) && !includeAllWhenEmpty)
        {
            ClearLabelSuggestions();
            return;
        }

        var selectedNames = _selectedLabels.Select(label => label.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        LabelEditor.ItemsSource = _availableLabels
            .Where(label => !selectedNames.Contains(label.Name))
            .Where(label => string.IsNullOrWhiteSpace(search) || label.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .Select(label => label.Name)
            .Take(8)
            .ToList();
    }

    private void ClearLabelSuggestions()
    {
        LabelEditor.ItemsSource = Array.Empty<string>();
    }

    private void ClearLabelInputAfterSelection(AutoSuggestBox sender)
    {
        ClearLabelSuggestions();
        sender.DispatcherQueue.TryEnqueue(() =>
        {
            sender.Text = string.Empty;
            ClearLabelSuggestions();
            sender.Focus(FocusState.Programmatic);
        });
    }

    private void OnSaveTaskClicked(object sender, RoutedEventArgs e) => SaveTaskClicked?.Invoke(sender, e);

    private void OnToggleCompleteClicked(object sender, RoutedEventArgs e) => ToggleCompleteClicked?.Invoke(sender, e);

    private void OnDeleteTaskClicked(object sender, RoutedEventArgs e) => DeleteTaskClicked?.Invoke(sender, e);

    private void OnCancelEditClicked(object sender, RoutedEventArgs e) => CancelEditClicked?.Invoke(sender, e);
}
