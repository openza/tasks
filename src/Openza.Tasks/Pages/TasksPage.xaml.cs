using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Openza.Tasks.Controls;
using Openza.Tasks.Core.Models;
using Openza.Tasks.ViewModels;
using Windows.Foundation;

namespace Openza.Tasks.Pages;

public sealed partial class TasksPage : UserControl
{
    private List<ProjectItem> _projectOptions = [];
    private bool _detailsOpen;
    private bool _narrowProjectsOpen;
    private bool _projectsPaneEnabled = true;
    private bool _suppressTaskSelection;

    public event TypedEventHandler<AutoSuggestBox, AutoSuggestBoxTextChangedEventArgs>? SearchTextChanged;
    public event SelectionChangedEventHandler? SortChanged;
    public event SelectionChangedEventHandler? PriorityChanged;
    public event SelectionChangedEventHandler? LabelChanged;
    public event TypedEventHandler<AutoSuggestBox, AutoSuggestBoxTextChangedEventArgs>? ProjectSearchTextChanged;
    public event TypedEventHandler<TasksPage, string?>? ProjectSelected;
    public event RoutedEventHandler? ClearProjectClicked;
    public event RoutedEventHandler? AddProjectClicked;
    public event TypedEventHandler<TasksPage, string>? EditProjectClicked;
    public event TypedEventHandler<TasksPage, string>? DeleteProjectClicked;
    public event TypedEventHandler<TasksPage, TaskListItemViewModel>? TaskSelected;
    public event RoutedEventHandler? ToggleCompleteClicked;
    public event RoutedEventHandler? CopyTaskIdClicked;
    public event RoutedEventHandler? DeleteTaskClicked;
    public event RoutedEventHandler? SaveTaskClicked;
    public event RoutedEventHandler? DetailsToggleCompleteClicked;
    public event RoutedEventHandler? DetailsDeleteTaskClicked;
    public event RoutedEventHandler? DetailsCancelEditClicked;
    public event RoutedEventHandler? QuickAddClicked;
    public event RoutedEventHandler? ImportClicked;
    public event RoutedEventHandler? ExportClicked;

    public TasksViewModel ViewModel { get; } = new();

    public TasksPage()
    {
        InitializeComponent();
        LabelCombo.Items.Add(new ComboBoxItem { Content = "All labels", Tag = string.Empty, IsSelected = true });
    }

    public string SearchText
    {
        get => SearchBox.Text?.Trim() ?? string.Empty;
        set => SearchBox.Text = value;
    }

    public string ProjectSearchText => ProjectsPane.SearchText;

    public string SortTag => (SortCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "priority";

    public int? PriorityFilter
    {
        get
        {
            var tag = (PriorityCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            return int.TryParse(tag, out var priority) ? priority : null;
        }
    }

    public string? LabelFilterId
    {
        get
        {
            var tag = (LabelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            return string.IsNullOrWhiteSpace(tag) ? null : tag;
        }
    }

    public TaskDetailsPaneControl DetailsPanel => TaskDetailsPanel;

    public bool IsDetailsPaneOpen => _detailsOpen && DetailsHost.Visibility == Visibility.Visible;

    public void SetHeader(string title, string subtitle, bool hasProjectFilter)
    {
        var hasSearch = !string.IsNullOrWhiteSpace(SearchText);
        var hasFilters = PriorityFilter is not null || LabelFilterId is not null;
        ViewModel.Title = title;
        ViewModel.Subtitle = subtitle;
        ClearProjectButton.Visibility = hasProjectFilter ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Title = title switch
        {
            "Inbox" => "Inbox is clear",
            "Today" => "Nothing due today",
            "Overdue" => "Nothing overdue",
            "Waiting For" => "Nothing waiting",
            "Someday" => "No someday tasks",
            "Completed" => "No completed tasks yet",
            _ => "Nothing here",
        };
        EmptyState.Message = hasSearch || hasFilters ? "No tasks match the current filters." : "Create a task to start filling this list.";
        EmptyState.ActionText = hasSearch || hasFilters ? "Clear filters" : "Add task";
    }

    public void SetProjectOptions(IEnumerable<ProjectItem> projects)
    {
        _projectOptions = projects.ToList();
        TaskDetailsPanel.SetProjects(_projectOptions);
    }

    public void SetLabelOptions(IEnumerable<LabelItem> labels)
    {
        TaskDetailsPanel.SetLabels(labels);
    }

    public void RefreshLabelFilter(IEnumerable<LabelItem> labels, string? selectedLabelId)
    {
        ViewModel.LabelOptions.Clear();
        LabelCombo.Items.Clear();
        LabelCombo.Items.Add(new ComboBoxItem { Content = "All labels", Tag = string.Empty });
        foreach (var label in labels.OrderBy(label => label.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            ViewModel.LabelOptions.Add(label);
            LabelCombo.Items.Add(new ComboBoxItem { Content = label.Name, Tag = label.Id });
        }

        LabelCombo.SelectedItem = LabelCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), selectedLabelId, StringComparison.Ordinal)) ??
            LabelCombo.Items.FirstOrDefault();
    }

    public void FocusSearch() => SearchBox.Focus(FocusState.Keyboard);

    public void FocusProjectSearch() => ProjectsPane.FocusSearch();

    public void SetProjectsPaneEnabled(bool enabled)
    {
        _projectsPaneEnabled = enabled;
        if (!enabled)
        {
            _narrowProjectsOpen = false;
        }

        UpdateWorkbenchLayout(ActualWidth);
    }

    public void ShowDetailsPane()
    {
        _detailsOpen = true;
        UpdateWorkbenchLayout(ActualWidth);
    }

    public void HideDetailsPane()
    {
        _detailsOpen = false;
        UpdateWorkbenchLayout(ActualWidth);
    }

    public void SelectTask(string taskId)
    {
        _suppressTaskSelection = true;
        TaskList.SelectedItem = ViewModel.Tasks.FirstOrDefault(task => task.Id == taskId);
        _suppressTaskSelection = false;
    }

    public void ClearTaskSelection()
    {
        _suppressTaskSelection = true;
        TaskList.SelectedItem = null;
        _suppressTaskSelection = false;
    }

    public async Task<QuickAddViewModel?> ShowQuickAddAsync(ProjectItem? defaultProject, TaskItemStatus defaultStatus, DateTimeOffset? defaultDueDate = null)
    {
        var inboxProject = new ProjectItem
        {
            Id = string.Empty,
            Name = "Inbox",
            IntegrationId = IntegrationIds.Local,
        };
        var projectOptions = new List<ProjectItem> { inboxProject };
        projectOptions.AddRange(_projectOptions);
        var selectedLabels = new List<LabelItem>();
        var titleBox = new TextBox { Header = "Task", PlaceholderText = "What needs doing?" };
        var projectBox = new ComboBox
        {
            Header = "Project",
            DisplayMemberPath = "Name",
            SelectedValuePath = "Id",
            ItemsSource = projectOptions,
            SelectedItem = projectOptions.FirstOrDefault(project => string.Equals(project.Id, defaultProject?.Id ?? string.Empty, StringComparison.Ordinal)) ?? inboxProject,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var workflowBox = new ComboBox { Header = "Status", HorizontalAlignment = HorizontalAlignment.Stretch };
        workflowBox.Items.Add(new ComboBoxItem { Content = "Open", Tag = "none", IsSelected = defaultStatus == TaskItemStatus.None });
        workflowBox.Items.Add(new ComboBoxItem { Content = "Next", Tag = "next", IsSelected = defaultStatus == TaskItemStatus.Next });
        workflowBox.Items.Add(new ComboBoxItem { Content = "Waiting For", Tag = "waiting", IsSelected = defaultStatus == TaskItemStatus.Waiting });
        workflowBox.Items.Add(new ComboBoxItem { Content = "Someday/Maybe", Tag = "someday", IsSelected = defaultStatus == TaskItemStatus.Someday });
        var priorityBox = new ComboBox { Header = "Priority", HorizontalAlignment = HorizontalAlignment.Stretch };
        priorityBox.Items.Add(new ComboBoxItem { Content = "Urgent", Tag = "1" });
        priorityBox.Items.Add(new ComboBoxItem { Content = "High", Tag = "2" });
        priorityBox.Items.Add(new ComboBoxItem { Content = "Normal", Tag = "3", IsSelected = true });
        priorityBox.Items.Add(new ComboBoxItem { Content = "Low", Tag = "4" });
        var duePicker = new CalendarDatePicker { Header = "Due date", Date = defaultDueDate, HorizontalAlignment = HorizontalAlignment.Stretch };
        var labelBox = new AutoSuggestBox
        {
            Header = "Labels",
            PlaceholderText = "Add label",
            QueryIcon = new SymbolIcon(Symbol.Add),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var labelChips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        void RefreshQuickLabelSuggestions(string search = "", bool includeAllWhenEmpty = false)
        {
            if (string.IsNullOrWhiteSpace(search) && !includeAllWhenEmpty)
            {
                labelBox.ItemsSource = Array.Empty<string>();
                return;
            }

            var selectedNames = selectedLabels.Select(label => label.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            labelBox.ItemsSource = ViewModel.LabelOptions
                .Where(label => !selectedNames.Contains(label.Name))
                .Where(label => string.IsNullOrWhiteSpace(search) || label.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))
                .Select(label => label.Name)
                .Take(8)
                .ToList();
        }

        void RefreshQuickLabelChips()
        {
            labelChips.Children.Clear();
            foreach (var label in selectedLabels)
            {
                var chip = new Button
                {
                    Content = label.Name,
                    Padding = new Thickness(10, 4, 10, 4),
                    Tag = label.Id,
                };
                chip.Click += (_, _) =>
                {
                    selectedLabels.RemoveAll(item => item.Id == label.Id);
                    RefreshQuickLabelChips();
                    RefreshQuickLabelSuggestions(labelBox.Text, includeAllWhenEmpty: true);
                };
                labelChips.Children.Add(chip);
            }
        }

        LabelItem GetOrCreateQuickLabel(string name)
        {
            var trimmed = name.Trim();
            return ViewModel.LabelOptions.FirstOrDefault(label => string.Equals(label.Name, trimmed, StringComparison.CurrentCultureIgnoreCase)) ??
                new LabelItem
                {
                    Id = $"label_pending_{Guid.NewGuid():N}",
                    IntegrationId = IntegrationIds.Local,
                    Name = trimmed,
                    Color = "#808080",
                    CreatedAt = DateTimeOffset.UtcNow,
                };
        }

        void AddQuickLabel(LabelItem label)
        {
            if (selectedLabels.Any(item => string.Equals(item.Name, label.Name, StringComparison.CurrentCultureIgnoreCase)))
            {
                return;
            }

            selectedLabels.Add(label);
            labelBox.Text = string.Empty;
            RefreshQuickLabelChips();
            labelBox.ItemsSource = Array.Empty<string>();
        }

        labelBox.TextChanged += (_, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                RefreshQuickLabelSuggestions(labelBox.Text, includeAllWhenEmpty: false);
            }
        };
        labelBox.GotFocus += (_, _) => RefreshQuickLabelSuggestions(labelBox.Text, includeAllWhenEmpty: true);
        labelBox.SuggestionChosen += (_, args) =>
        {
            if (args.SelectedItem is string labelName)
            {
                AddQuickLabel(GetOrCreateQuickLabel(labelName));
            }
        };
        labelBox.QuerySubmitted += (_, args) =>
        {
            if (args.ChosenSuggestion is string labelName)
            {
                AddQuickLabel(GetOrCreateQuickLabel(labelName));
            }
            else if (!string.IsNullOrWhiteSpace(args.QueryText))
            {
                AddQuickLabel(GetOrCreateQuickLabel(args.QueryText));
            }
        };
        RefreshQuickLabelSuggestions(includeAllWhenEmpty: false);

        var stack = new StackPanel { Spacing = 12, MinWidth = 420 };
        stack.Children.Add(titleBox);
        stack.Children.Add(projectBox);
        stack.Children.Add(workflowBox);
        stack.Children.Add(priorityBox);
        stack.Children.Add(duePicker);
        stack.Children.Add(labelBox);
        stack.Children.Add(labelChips);

        var dialog = new ContentDialog
        {
            Title = "Add task",
            Content = stack,
            PrimaryButtonText = "Create",
            SecondaryButtonText = "Create and open",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        dialog.Opened += (_, _) => titleBox.Focus(FocusState.Programmatic);
        var result = await dialog.ShowAsync();
        if (result is not ContentDialogResult.Primary and not ContentDialogResult.Secondary ||
            string.IsNullOrWhiteSpace(titleBox.Text))
        {
            return null;
        }

        return new QuickAddViewModel
        {
            Title = titleBox.Text.Trim(),
            ProjectId = string.IsNullOrWhiteSpace((projectBox.SelectedItem as ProjectItem)?.Id) ? null : (projectBox.SelectedItem as ProjectItem)?.Id,
            Status = StatusFromTag((workflowBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()),
            Priority = int.TryParse((priorityBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var priority) ? priority : 3,
            DueDate = duePicker.Date,
            LabelsText = string.Join(", ", selectedLabels.Select(label => label.Name)),
            OpenAfterCreate = result == ContentDialogResult.Secondary,
        };
    }

    private static TaskItemStatus StatusFromTag(string? tag) => tag switch
    {
        "next" => TaskItemStatus.Next,
        "waiting" => TaskItemStatus.Waiting,
        "someday" => TaskItemStatus.Someday,
        _ => TaskItemStatus.None,
    };

    private void OnWorkbenchSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateWorkbenchLayout(e.NewSize.Width);
    }

    private void UpdateWorkbenchLayout(double width)
    {
        var showProjectsPane = _projectsPaneEnabled;

        if (width < 980)
        {
            ProjectsCommand.Visibility = showProjectsPane ? Visibility.Visible : Visibility.Collapsed;
            DetailsColumn.Width = new GridLength(0);
            Grid.SetColumn(DetailsHost, 1);

            if (_detailsOpen)
            {
                ProjectsColumn.Width = new GridLength(0);
                ProjectsPane.Visibility = Visibility.Collapsed;
                ListHost.Visibility = Visibility.Collapsed;
                DetailsHost.Visibility = Visibility.Visible;
                return;
            }

            ListHost.Visibility = Visibility.Visible;
            DetailsHost.Visibility = Visibility.Collapsed;
            ProjectsColumn.Width = showProjectsPane && _narrowProjectsOpen ? new GridLength(Math.Min(300, width * 0.42)) : new GridLength(0);
            ProjectsPane.Visibility = showProjectsPane && _narrowProjectsOpen ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        ProjectsCommand.Visibility = Visibility.Collapsed;
        _narrowProjectsOpen = false;
        ListHost.Visibility = Visibility.Visible;
        Grid.SetColumn(DetailsHost, 2);
        ProjectsPane.Visibility = showProjectsPane ? Visibility.Visible : Visibility.Collapsed;
        ProjectsColumn.Width = showProjectsPane ? (width < 1280 ? new GridLength(280) : new GridLength(330)) : new GridLength(0);

        if (!_detailsOpen)
        {
            DetailsHost.Visibility = Visibility.Collapsed;
            DetailsColumn.Width = new GridLength(0);
            return;
        }

        DetailsHost.Visibility = Visibility.Visible;
        DetailsColumn.Width = width < 1280 ? new GridLength(380) : new GridLength(440);
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => SearchTextChanged?.Invoke(sender, args);

    private void OnSortChanged(object sender, SelectionChangedEventArgs e) => SortChanged?.Invoke(sender, e);

    private void OnPriorityChanged(object sender, SelectionChangedEventArgs e) => PriorityChanged?.Invoke(sender, e);

    private void OnLabelChanged(object sender, SelectionChangedEventArgs e) => LabelChanged?.Invoke(sender, e);

    private void OnProjectSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ProjectSearchTextChanged?.Invoke(sender, args);

    private void OnProjectSelected(ProjectsPaneControl sender, string? id)
    {
        ProjectSelected?.Invoke(this, id);
        if (ActualWidth < 980)
        {
            _narrowProjectsOpen = false;
            UpdateWorkbenchLayout(ActualWidth);
        }
    }

    private void OnAddProjectClicked(object sender, RoutedEventArgs e) => AddProjectClicked?.Invoke(sender, e);

    private void OnClearProjectClicked(object sender, RoutedEventArgs e) => ClearProjectClicked?.Invoke(sender, e);

    private void OnEditProjectClicked(ProjectsPaneControl sender, string id) => EditProjectClicked?.Invoke(this, id);

    private void OnDeleteProjectClicked(ProjectsPaneControl sender, string id) => DeleteProjectClicked?.Invoke(this, id);

    private void OnTaskSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTaskSelection)
        {
            return;
        }

        if (TaskList.SelectedItem is TaskListItemViewModel item)
        {
            TaskSelected?.Invoke(this, item);
        }
    }

    private void OnToggleCompleteClicked(object sender, RoutedEventArgs e) => ToggleCompleteClicked?.Invoke(sender, e);

    private void OnCopyTaskIdClicked(object sender, RoutedEventArgs e) => CopyTaskIdClicked?.Invoke(sender, e);

    private void OnDeleteTaskClicked(object sender, RoutedEventArgs e) => DeleteTaskClicked?.Invoke(sender, e);

    private void OnSaveTaskClicked(object sender, RoutedEventArgs e) => SaveTaskClicked?.Invoke(sender, e);

    private void OnDetailsToggleCompleteClicked(object sender, RoutedEventArgs e) => DetailsToggleCompleteClicked?.Invoke(sender, e);

    private void OnDetailsDeleteTaskClicked(object sender, RoutedEventArgs e) => DetailsDeleteTaskClicked?.Invoke(sender, e);

    private void OnDetailsCancelEditClicked(object sender, RoutedEventArgs e) => DetailsCancelEditClicked?.Invoke(sender, e);

    private void OnQuickAddClicked(object sender, RoutedEventArgs e) => QuickAddClicked?.Invoke(sender, e);

    private void OnProjectsCommandClicked(object sender, RoutedEventArgs e)
    {
        _narrowProjectsOpen = !_narrowProjectsOpen;
        UpdateWorkbenchLayout(ActualWidth);
    }

    private void OnEmptyStateActionClicked(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(SearchText) || PriorityFilter is not null || LabelFilterId is not null)
        {
            ClearListFilters();
            return;
        }

        QuickAddClicked?.Invoke(sender, e);
    }

    private void OnImportClicked(object sender, RoutedEventArgs e) => ImportClicked?.Invoke(sender, e);

    private void OnExportClicked(object sender, RoutedEventArgs e) => ExportClicked?.Invoke(sender, e);

    private void ClearListFilters()
    {
        SearchText = string.Empty;
        PriorityCombo.SelectedIndex = 0;
        LabelCombo.SelectedIndex = 0;
    }
}
