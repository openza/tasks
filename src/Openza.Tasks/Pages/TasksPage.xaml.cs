using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Openza.Tasks.Controls;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.ViewModels;
using Windows.Foundation;

namespace Openza.Tasks.Pages;

public sealed partial class TasksPage : UserControl
{
    public readonly record struct TaskListViewportState(bool IsGrouped, double? VerticalOffset);

    private List<ProjectItem> _projectOptions = [];
    private IReadOnlyList<ProviderSourceItem> _connectedTasks = [];
    private int _waitingConnectedTaskCount;
    private int _skippedConnectedTaskCount;
    private bool _updatingConnectedTaskFilters;
    private bool _detailsOpen;
    private bool _intakeOpen;
    private bool _narrowProjectsOpen;
    private bool _projectsPaneEnabled = true;
    private bool _suppressTaskSelection;
    private bool _suppressViewControlEvents;
    private string _sortTag = "priority";
    private string _sortDirectionTag = "asc";
    private string _groupTag = "none";
    private string _priorityTag = string.Empty;
    private string _repeatScopeTag = "include";
    private string? _labelFilterId;
    private string _labelFilterSearchText = string.Empty;
    private List<LabelItem> _labelFilterOptions = [];

    public event TypedEventHandler<AutoSuggestBox, AutoSuggestBoxTextChangedEventArgs>? SearchTextChanged;
    public event EventHandler? SortChanged;
    public event EventHandler? GroupChanged;
    public event EventHandler? PriorityChanged;
    public event EventHandler? RepeatScopeChanged;
    public event EventHandler? LabelChanged;
    public event TypedEventHandler<AutoSuggestBox, AutoSuggestBoxTextChangedEventArgs>? ProjectSearchTextChanged;
    public event TypedEventHandler<TasksPage, string>? ProjectFilterChanged;
    public event TypedEventHandler<TasksPage, string?>? ProjectSelected;
    public event RoutedEventHandler? ClearProjectClicked;
    public event RoutedEventHandler? AddProjectClicked;
    public event TypedEventHandler<TasksPage, string>? EditProjectClicked;
    public event TypedEventHandler<TasksPage, string>? DeleteProjectClicked;
    public event TypedEventHandler<TasksPage, TaskListItemViewModel>? TaskSelected;
    public event TypedEventHandler<TasksPage, TaskRowActionRequestedEventArgs>? TaskRowActionRequested;
    public event RoutedEventHandler? ToggleCompleteClicked;
    public event RoutedEventHandler? DetailsAutoSaveRequested;
    public event RoutedEventHandler? DetailsToggleCompleteClicked;
    public event RoutedEventHandler? DetailsSubtaskToggleCompleteClicked;
    public event TypedEventHandler<TasksPage, string>? DetailsCreateProjectRequested;
    public event RoutedEventHandler? DetailsMoveToSpaceClicked;
    public event RoutedEventHandler? DetailsManageGitHubIssueRequested;
    public event RoutedEventHandler? DetailsDeleteTaskClicked;
    public event RoutedEventHandler? DetailsCancelEditClicked;
    public event RoutedEventHandler? QuickAddClicked;
    public event RoutedEventHandler? ImportClicked;
    public event RoutedEventHandler? ExportClicked;
    public event RoutedEventHandler? ConnectProvidersClicked;
    public event RoutedEventHandler? ExploreSyncClicked;
    public event RoutedEventHandler? DismissGetStartedClicked;
    public event TypedEventHandler<TasksPage, string>? AddConnectedTaskClicked;
    public event TypedEventHandler<TasksPage, string>? SkipConnectedTaskClicked;
    public event TypedEventHandler<TasksPage, string>? UnskipConnectedTaskClicked;
    public event RoutedEventHandler? AddAllConnectedTasksClicked;
    public event RoutedEventHandler? ReviewConnectedTasksClicked;
    public event RoutedEventHandler? ShowSkippedConnectedTasksChanged;

    public TasksViewModel ViewModel { get; } = new();

    public TasksPage()
    {
        InitializeComponent();
        ShowFilterPane("priority");
        RefreshLabelFilterList();
        UpdateViewControlText();
    }

    public string SearchText
    {
        get => SearchBox.Text?.Trim() ?? string.Empty;
        set => SearchBox.Text = value;
    }

    public string ProjectSearchText => ProjectsPane.SearchText;

    public string SortTag => _sortTag;

    public TaskSortDirection SortDirection => _sortDirectionTag == "desc"
        ? TaskSortDirection.Descending
        : TaskSortDirection.Ascending;

    public TaskGroupMode GroupMode => _groupTag switch
    {
        "date" => TaskGroupMode.Date,
        "date-type" => TaskGroupMode.Date,
        "project" => TaskGroupMode.Project,
        "status" => TaskGroupMode.Status,
        "priority" => TaskGroupMode.Priority,
        "label" => TaskGroupMode.Label,
        "source" => TaskGroupMode.Source,
        "repeating" => TaskGroupMode.Repeating,
        "created-date" => TaskGroupMode.CreatedDate,
        "completed-date" => TaskGroupMode.CompletedDate,
        _ => TaskGroupMode.None,
    };

    public int? PriorityFilter
    {
        get
        {
            return int.TryParse(_priorityTag, out var priority) ? priority : null;
        }
    }

    public TaskDateScope DateScopeFilter => TaskDateScope.All;

    public TaskRepeatScope RepeatScopeFilter => _repeatScopeTag switch
    {
        "exclude" => TaskRepeatScope.Exclude,
        "only" => TaskRepeatScope.Only,
        _ => TaskRepeatScope.Include,
    };

    public string? LabelFilterId => _labelFilterId;

    public TaskDetailsPaneControl DetailsPanel => TaskDetailsPanel;

    public bool IsDetailsPaneOpen => _detailsOpen && DetailsHost.Visibility == Visibility.Visible;

    public bool IsConnectedTasksDrawerOpen => _intakeOpen && IntakeDrawerHost.Visibility == Visibility.Visible;

    public bool ShowSkippedConnectedTasks => ShowSkippedConnectedTasksToggle.IsOn;

    public void SetHeader(string title, string subtitle, bool hasProjectFilter)
    {
        var hasSearch = !string.IsNullOrWhiteSpace(SearchText);
        var hasFilters = HasActiveListFilters();
        ViewModel.Title = title;
        ViewModel.Subtitle = subtitle;
        ClearProjectButton.Visibility = hasProjectFilter ? Visibility.Visible : Visibility.Collapsed;
        UpdateFilterState();
        EmptyState.Title = title switch
        {
            "Inbox" => "Inbox is clear",
            "Today" => "Nothing for today",
            "Calendar" => "No dated tasks",
            "Overdue" => "Nothing overdue",
            "Waiting For" => "Nothing waiting",
            "Someday" => "No someday tasks",
            "Completed" => "No completed tasks yet",
            _ => "Nothing here",
        };
        EmptyState.Message = hasSearch || hasFilters ? "No tasks match the current filters." : title switch
        {
            "Inbox" => _waitingConnectedTaskCount > 0
                ? "Clarify captured tasks here, or review tasks waiting from connected apps."
                : "Capture anything on your mind here. Clarify it later when you are ready.",
            "Next Actions" => "Clarified next actions will appear here.",
            "Today" => "Tasks dated, scheduled, or repeating today will appear here.",
            "Calendar" => "Dated work will appear here.",
            "Overdue" => "Nothing needs recovery right now.",
            "Waiting For" => "Delegated or blocked tasks you are waiting on will appear here.",
            "Someday" => "Ideas you may want later will appear here.",
            "Completed" => "Completed tasks will appear here.",
            _ => "Create a task to start filling this list.",
        };
        EmptyState.ActionText = hasSearch || hasFilters ? "Clear filters" : "Add task";
        EmptyState.ActionVisibility = ViewModel.IsGetStartedVisible && !hasSearch && !hasFilters
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public void SetGroupMode(TaskGroupMode mode)
    {
        SetGroupTag(GroupTag(mode), notify: false);
    }

    public void SetSortMode(TaskSortMode mode)
    {
        SetSortTag(SortTagForMode(mode), notify: false);
    }

    public void SetSortDirection(TaskSortDirection direction)
    {
        SetSortDirectionTag(direction == TaskSortDirection.Descending ? "desc" : "asc", notify: false);
    }

    public void SetPriorityFilter(int? priority)
    {
        SetPriorityTag(priority?.ToString() ?? string.Empty, notify: false);
    }

    public void SetDateScopeFilter(TaskDateScope scope)
    {
        // Date-field filtering is intentionally not exposed in the everyday UI.
        // Today and Calendar include all date-related facets.
    }

    public void SetRepeatScopeFilter(TaskRepeatScope scope)
    {
        SetRepeatScopeTag(RepeatScopeTag(scope), notify: false);
    }

    public void SetLabelFilter(string? labelId)
    {
        SetLabelFilterId(labelId, notify: false);
    }

    public void SetProjectOptions(IEnumerable<ProjectItem> projects)
    {
        _projectOptions = projects.ToList();
        TaskDetailsPanel.SetProjects(_projectOptions);
    }

    public void SetGetStartedVisible(bool visible)
    {
        ViewModel.IsGetStartedVisible = visible;
        GetStartedPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        FilterGrid.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        var hasActiveListFilter = HasActiveListFilters();
        EmptyState.ActionVisibility = visible && !hasActiveListFilter ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetConnectedTasks(IReadOnlyList<ProviderSourceItem> items, bool visible, int waitingCount, int skippedCount)
    {
        _connectedTasks = items;
        _waitingConnectedTaskCount = waitingCount;
        _skippedConnectedTaskCount = skippedCount;
        RefreshConnectedTaskFilters(items);
        ApplyConnectedTaskDrawerItems();
        ConnectedTasksDrawerText.Text = skippedCount == 0
            ? waitingCount == 1
                ? "1 task is waiting. Add it to Inbox first, then clarify it like any other task."
                : $"{waitingCount} tasks are waiting. Add them to Inbox first, then clarify them like any other task."
            : $"{waitingCount} waiting, {skippedCount} skipped. Skipped tasks stay recoverable here.";
        AddAllConnectedTasksDrawerButton.IsEnabled = waitingCount > 0;
        ConnectedTasksCommand.Visibility = visible && (waitingCount > 0 || skippedCount > 0)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!visible || (waitingCount == 0 && skippedCount == 0))
        {
            HideConnectedTasksDrawer();
            return;
        }

        if (!_detailsOpen)
        {
            _intakeOpen = true;
            UpdateWorkbenchLayoutForCurrentWidth();
        }
    }

    public void SetLabelOptions(IEnumerable<LabelItem> labels)
    {
        TaskDetailsPanel.SetLabels(labels);
    }

    public void RefreshLabelFilter(IEnumerable<LabelItem> labels, string? selectedLabelId)
    {
        ViewModel.LabelOptions.Clear();
        _labelFilterOptions = labels
            .OrderBy(label => label.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        foreach (var label in labels.OrderBy(label => label.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            ViewModel.LabelOptions.Add(label);
        }

        _labelFilterId = selectedLabelId;
        RefreshLabelFilterList();
        UpdateFilterState();
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

        UpdateWorkbenchLayoutForCurrentWidth();
    }

    public void ShowDetailsPane()
    {
        _detailsOpen = true;
        _intakeOpen = false;
        UpdateWorkbenchLayoutForCurrentWidth();
    }

    public void HideDetailsPane()
    {
        _detailsOpen = false;
        UpdateWorkbenchLayoutForCurrentWidth();
    }

    public void ShowConnectedTasksDrawer()
    {
        if (_waitingConnectedTaskCount == 0 && _skippedConnectedTaskCount == 0)
        {
            return;
        }

        if (_waitingConnectedTaskCount == 0 && _skippedConnectedTaskCount > 0 && !ShowSkippedConnectedTasksToggle.IsOn)
        {
            ShowSkippedConnectedTasksToggle.IsOn = true;
        }

        _intakeOpen = true;
        _detailsOpen = false;
        UpdateWorkbenchLayoutForCurrentWidth();
    }

    public void HideConnectedTasksDrawer()
    {
        _intakeOpen = false;
        UpdateWorkbenchLayoutForCurrentWidth();
    }

    public void SelectTask(string taskId)
    {
        _suppressTaskSelection = true;
        TaskList.SelectedItem = ViewModel.TaskEntries.FirstOrDefault(entry => entry.Task?.Id == taskId);
        _suppressTaskSelection = false;
    }

    public TaskListViewportState CaptureTaskListViewport()
    {
        var list = ActiveTaskList();
        var scrollViewer = FindDescendant<ScrollViewer>(list);
        return new TaskListViewportState(ViewModel.IsGrouped, scrollViewer?.VerticalOffset);
    }

    public void RestoreTaskListViewport(TaskListViewportState state, string? selectedTaskId)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var list = ActiveTaskList();
            if (list is null)
            {
                return;
            }

            var scrollViewer = FindDescendant<ScrollViewer>(list);
            if (scrollViewer is not null &&
                state.VerticalOffset is { } offset &&
                state.IsGrouped == ViewModel.IsGrouped)
            {
                scrollViewer.ChangeView(null, offset, null, disableAnimation: true);
            }

            if (!string.IsNullOrWhiteSpace(selectedTaskId))
            {
                    var selectedItem = ViewModel.TaskEntries.FirstOrDefault(entry => entry.Task?.Id == selectedTaskId);
                    if (selectedItem is not null)
                    {
                    if (list is ListView listView)
                    {
                        _suppressTaskSelection = true;
                        listView.SelectedItem = selectedItem;
                        _suppressTaskSelection = false;
                        listView.ScrollIntoView(selectedItem, ScrollIntoViewAlignment.Default);
                    }
                }
            }
        });
    }

    public void ClearTaskSelection()
    {
        _suppressTaskSelection = true;
        TaskList.SelectedItem = null;
        _suppressTaskSelection = false;
    }

    private FrameworkElement? ActiveTaskList() => TaskList;

    private static T? FindDescendant<T>(DependencyObject? root)
        where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        if (root is T rootMatch)
        {
            return rootMatch;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    public async Task<QuickAddViewModel?> ShowQuickAddAsync(ProjectItem? defaultProject, TaskItemStatus defaultStatus, DateTimeOffset? defaultDate = null)
    {
        var inboxProject = new ProjectItem
        {
            Id = string.Empty,
            Name = "No project",
            IntegrationId = IntegrationIds.Local,
        };
        var projectOptions = new List<ProjectItem> { inboxProject };
        projectOptions.AddRange(_projectOptions);
        var selectedLabels = new List<LabelItem>();
        var titleBox = new TextBox { Header = "Task", PlaceholderText = "What needs doing?" };
        var notesBox = new TextBox
        {
            Header = "Notes",
            PlaceholderText = "",
            AcceptsReturn = true,
            MinHeight = 72,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
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
        workflowBox.Items.Add(new ComboBoxItem { Content = "Inbox", Tag = "inbox", IsSelected = defaultStatus == TaskItemStatus.Inbox });
        workflowBox.Items.Add(new ComboBoxItem { Content = "Next", Tag = "next", IsSelected = defaultStatus == TaskItemStatus.Next });
        workflowBox.Items.Add(new ComboBoxItem { Content = "Waiting For", Tag = "waiting", IsSelected = defaultStatus == TaskItemStatus.Waiting });
        workflowBox.Items.Add(new ComboBoxItem { Content = "Someday", Tag = "someday", IsSelected = defaultStatus == TaskItemStatus.Someday });
        var priorityBox = new ComboBox { Header = "Priority", HorizontalAlignment = HorizontalAlignment.Stretch };
        priorityBox.Items.Add(new ComboBoxItem { Content = "Urgent", Tag = "1" });
        priorityBox.Items.Add(new ComboBoxItem { Content = "High", Tag = "2" });
        priorityBox.Items.Add(new ComboBoxItem { Content = "Normal", Tag = "3", IsSelected = true });
        priorityBox.Items.Add(new ComboBoxItem { Content = "Low", Tag = "4" });
        var datePicker = new CalendarDatePicker { Header = "Date", Date = defaultDate, HorizontalAlignment = HorizontalAlignment.Stretch };
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
            if (!selectedLabels.Any(item => string.Equals(item.Name, label.Name, StringComparison.CurrentCultureIgnoreCase)))
            {
                selectedLabels.Add(label);
                RefreshQuickLabelChips();
            }

            ClearQuickLabelInputAfterSelection();
        }

        void ClearQuickLabelInputAfterSelection()
        {
            labelBox.ItemsSource = Array.Empty<string>();
            labelBox.DispatcherQueue.TryEnqueue(() =>
            {
                labelBox.Text = string.Empty;
                labelBox.ItemsSource = Array.Empty<string>();
                labelBox.Focus(FocusState.Programmatic);
            });
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

        var detailsGrid = new Grid
        {
            ColumnSpacing = 10,
            RowSpacing = 10,
        };
        detailsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        detailsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(projectBox, 0);
        Grid.SetColumn(projectBox, 0);
        Grid.SetRow(workflowBox, 0);
        Grid.SetColumn(workflowBox, 1);
        Grid.SetRow(priorityBox, 1);
        Grid.SetColumn(priorityBox, 0);
        Grid.SetRow(datePicker, 1);
        Grid.SetColumn(datePicker, 1);
        detailsGrid.Children.Add(projectBox);
        detailsGrid.Children.Add(workflowBox);
        detailsGrid.Children.Add(priorityBox);
        detailsGrid.Children.Add(datePicker);

        var stack = new StackPanel { Spacing = 14, MinWidth = 460 };
        stack.Children.Add(titleBox);
        stack.Children.Add(notesBox);
        stack.Children.Add(detailsGrid);
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
            Notes = notesBox.Text.Trim(),
            ProjectId = string.IsNullOrWhiteSpace((projectBox.SelectedItem as ProjectItem)?.Id) ? null : (projectBox.SelectedItem as ProjectItem)?.Id,
            Status = StatusFromTag((workflowBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()),
            Priority = int.TryParse((priorityBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var priority) ? priority : 3,
            PlannedOn = TaskDateValues.FromDateTimeOffset(datePicker.Date),
            LabelsText = string.Join(", ", selectedLabels.Select(label => label.Name)),
            OpenAfterCreate = result == ContentDialogResult.Secondary,
        };
    }

    private static TaskItemStatus StatusFromTag(string? tag) => tag switch
    {
        "next" => TaskItemStatus.Next,
        "waiting" => TaskItemStatus.Waiting,
        "someday" => TaskItemStatus.Someday,
        _ => TaskItemStatus.Inbox,
    };

    private void OnWorkbenchSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateWorkbenchLayout(e.NewSize.Width);
    }

    private void UpdateWorkbenchLayoutForCurrentWidth()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(UpdateWorkbenchLayoutForCurrentWidth);
            return;
        }

        UpdateWorkbenchLayout(ActualWidth);
    }

    private void UpdateWorkbenchLayout(double width)
    {
        var showProjectsPane = _projectsPaneEnabled;
        var rightPaneOpen = _detailsOpen || _intakeOpen;
        UpdateFilterLayout(width < 1120 || (width < 1360 && rightPaneOpen));

        if (width < 980)
        {
            ProjectsCommand.Visibility = showProjectsPane ? Visibility.Visible : Visibility.Collapsed;
            DetailsColumn.Width = new GridLength(0);
            Grid.SetColumn(DetailsHost, 1);
            Grid.SetColumn(IntakeDrawerHost, 1);

            if (rightPaneOpen)
            {
                ProjectsColumn.Width = new GridLength(0);
                ProjectsPane.Visibility = Visibility.Collapsed;
                ListHost.Visibility = Visibility.Collapsed;
                DetailsHost.Visibility = _detailsOpen ? Visibility.Visible : Visibility.Collapsed;
                IntakeDrawerHost.Visibility = _intakeOpen ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            ListHost.Visibility = Visibility.Visible;
            DetailsHost.Visibility = Visibility.Collapsed;
            IntakeDrawerHost.Visibility = Visibility.Collapsed;
            ProjectsColumn.Width = showProjectsPane && _narrowProjectsOpen ? new GridLength(Math.Min(300, width * 0.42)) : new GridLength(0);
            ProjectsPane.Visibility = showProjectsPane && _narrowProjectsOpen ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        ProjectsCommand.Visibility = Visibility.Collapsed;
        _narrowProjectsOpen = false;
        ListHost.Visibility = Visibility.Visible;
        Grid.SetColumn(DetailsHost, 2);
        Grid.SetColumn(IntakeDrawerHost, 2);
        ProjectsPane.Visibility = showProjectsPane ? Visibility.Visible : Visibility.Collapsed;
        if (!showProjectsPane)
        {
            ProjectsColumn.Width = new GridLength(0);
        }
        else if (rightPaneOpen)
        {
            ProjectsColumn.Width = width < 1280 ? new GridLength(280) : new GridLength(330);
        }
        else
        {
            ProjectsColumn.Width = width < 1280 ? new GridLength(320) : new GridLength(390);
        }

        if (!rightPaneOpen)
        {
            DetailsHost.Visibility = Visibility.Collapsed;
            IntakeDrawerHost.Visibility = Visibility.Collapsed;
            DetailsColumn.Width = new GridLength(0);
            return;
        }

        DetailsHost.Visibility = _detailsOpen ? Visibility.Visible : Visibility.Collapsed;
        IntakeDrawerHost.Visibility = _intakeOpen ? Visibility.Visible : Visibility.Collapsed;
        if (_detailsOpen && !showProjectsPane)
        {
            var detailWidth = Math.Min(width - 420, Math.Max(560, width * 0.56));
            DetailsColumn.Width = new GridLength(Math.Max(440, detailWidth));
            return;
        }

        DetailsColumn.Width = width < 1280 ? new GridLength(380) : new GridLength(440);
    }

    private void UpdateFilterLayout(bool compact)
    {
        if (FilterGrid.ColumnDefinitions.Count < 5)
        {
            return;
        }

        if (compact)
        {
            SearchBox.Width = double.NaN;
            SearchBox.MaxWidth = double.PositiveInfinity;
            SearchBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetRow(SearchBox, 0);
            Grid.SetColumn(SearchBox, 0);
            Grid.SetColumnSpan(SearchBox, 6);
            Grid.SetRow(SortButton, 1);
            Grid.SetColumn(SortButton, 0);
            Grid.SetRow(GroupButton, 1);
            Grid.SetColumn(GroupButton, 1);
            Grid.SetRow(FilterButton, 1);
            Grid.SetColumn(FilterButton, 2);
            Grid.SetRow(ActiveFiltersPanel, 2);
            Grid.SetColumn(ActiveFiltersPanel, 0);
            Grid.SetColumnSpan(ActiveFiltersPanel, 6);
            UpdateFilterState();
            return;
        }

        SearchBox.Width = 460;
        SearchBox.MaxWidth = 480;
        SearchBox.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetRow(SearchBox, 0);
        Grid.SetColumn(SearchBox, 0);
        Grid.SetColumnSpan(SearchBox, 1);
        Grid.SetRow(SortButton, 0);
        Grid.SetColumn(SortButton, 1);
        Grid.SetRow(GroupButton, 0);
        Grid.SetColumn(GroupButton, 2);
        Grid.SetRow(FilterButton, 0);
        Grid.SetColumn(FilterButton, 3);
        Grid.SetRow(ActiveFiltersPanel, 1);
        Grid.SetColumn(ActiveFiltersPanel, 0);
        Grid.SetColumnSpan(ActiveFiltersPanel, 6);
        UpdateFilterState();
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        UpdateFilterState();
        SearchTextChanged?.Invoke(sender, args);
    }

    private void OnSortMenuItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item)
        {
            SetSortTag(item.Tag?.ToString() ?? "priority", notify: true);
        }
    }

    private void OnSortDirectionMenuItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item)
        {
            SetSortDirectionTag(item.Tag?.ToString() ?? "asc", notify: true);
        }
    }

    private void OnGroupMenuItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item)
        {
            SetGroupTag(item.Tag?.ToString() ?? "none", notify: true);
        }
    }

    private void OnPriorityRadioChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressViewControlEvents)
        {
            return;
        }

        if (sender is RadioButton button && button.IsChecked == true)
        {
            SetPriorityTag(button.Tag?.ToString() ?? string.Empty, notify: true);
        }
    }

    private void OnRepeatScopeRadioChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressViewControlEvents)
        {
            return;
        }

        if (sender is RadioButton button && button.IsChecked == true)
        {
            SetRepeatScopeTag(button.Tag?.ToString() ?? "include", notify: true);
        }
    }

    private void OnLabelFilterSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _labelFilterSearchText = sender.Text?.Trim() ?? string.Empty;
        RefreshLabelFilterList();
    }

    private void OnLabelFilterListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressViewControlEvents || LabelFilterList.SelectedItem is not ListViewItem item)
        {
            return;
        }

        var tag = item.Tag?.ToString();
        SetLabelFilterId(string.IsNullOrWhiteSpace(tag) ? null : tag, notify: true);
    }

    private void OnFilterCategorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilterCategoryList.SelectedItem is ListViewItem item)
        {
            ShowFilterPane(item.Tag?.ToString() ?? "priority");
        }
    }

    private void OnProjectSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ProjectSearchTextChanged?.Invoke(sender, args);

    private void OnProjectFilterChanged(ProjectsPaneControl sender, string filter) => ProjectFilterChanged?.Invoke(this, filter);

    private void OnProjectGroupToggled(ProjectsPaneControl sender, string id) => ViewModel.ToggleProjectGroup(id);

    private void OnProjectSelected(ProjectsPaneControl sender, string? id)
    {
        ProjectSelected?.Invoke(this, id);
        if (ActualWidth < 980)
        {
            _narrowProjectsOpen = false;
            UpdateWorkbenchLayoutForCurrentWidth();
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

        var selectedItem = sender is ListView listView
            ? (listView.SelectedItem as TaskListEntryViewModel)?.Task
            : (TaskList.SelectedItem as TaskListEntryViewModel)?.Task;

        if (selectedItem is TaskListItemViewModel item)
        {
            TaskSelected?.Invoke(this, item);
        }
    }

    private void OnGroupedTaskInvoked(TaskRowControl sender, TaskListItemViewModel item)
    {
        if (!_suppressTaskSelection)
        {
            TaskSelected?.Invoke(this, item);
        }
    }

    private void OnTaskGroupHeaderClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string key && !string.IsNullOrWhiteSpace(key))
        {
            ViewModel.ToggleTaskGroup(key);
        }
    }

    private void OnToggleCompleteClicked(object sender, RoutedEventArgs e) => ToggleCompleteClicked?.Invoke(sender, e);

    private void OnTaskRowActionRequested(TaskRowControl sender, TaskRowActionRequestedEventArgs args) =>
        TaskRowActionRequested?.Invoke(this, args);

    private void OnDetailsAutoSaveRequested(object sender, RoutedEventArgs e) => DetailsAutoSaveRequested?.Invoke(sender, e);

    private void OnDetailsToggleCompleteClicked(object sender, RoutedEventArgs e) => DetailsToggleCompleteClicked?.Invoke(sender, e);

    private void OnDetailsSubtaskToggleCompleteClicked(object sender, RoutedEventArgs e) => DetailsSubtaskToggleCompleteClicked?.Invoke(sender, e);

    private void OnDetailsCreateProjectRequested(TaskDetailsPaneControl sender, string name) => DetailsCreateProjectRequested?.Invoke(this, name);

    private void OnDetailsMoveToSpaceRequested(object sender, RoutedEventArgs e) => DetailsMoveToSpaceClicked?.Invoke(sender, e);

    private void OnDetailsManageGitHubIssueRequested(object sender, RoutedEventArgs e) => DetailsManageGitHubIssueRequested?.Invoke(sender, e);

    private void OnDetailsDeleteTaskClicked(object sender, RoutedEventArgs e) => DetailsDeleteTaskClicked?.Invoke(sender, e);

    private void OnDetailsCancelEditClicked(object sender, RoutedEventArgs e) => DetailsCancelEditClicked?.Invoke(sender, e);

    private void OnQuickAddClicked(object sender, RoutedEventArgs e) => QuickAddClicked?.Invoke(sender, e);

    private void OnProjectsCommandClicked(object sender, RoutedEventArgs e)
    {
        _narrowProjectsOpen = !_narrowProjectsOpen;
        UpdateWorkbenchLayoutForCurrentWidth();
    }

    private void OnEmptyStateActionClicked(object sender, RoutedEventArgs e)
    {
        if (HasActiveListFilters())
        {
            ClearListFilters();
            return;
        }

        QuickAddClicked?.Invoke(sender, e);
    }

    private void OnImportClicked(object sender, RoutedEventArgs e) => ImportClicked?.Invoke(sender, e);

    private void OnExportClicked(object sender, RoutedEventArgs e) => ExportClicked?.Invoke(sender, e);

    private void OnClearFiltersClicked(object sender, RoutedEventArgs e) => ClearListFilters();

    private void OnConnectProvidersClicked(object sender, RoutedEventArgs e) => ConnectProvidersClicked?.Invoke(sender, e);

    private void OnExploreSyncClicked(object sender, RoutedEventArgs e) => ExploreSyncClicked?.Invoke(sender, e);

    private void OnDismissGetStartedClicked(object sender, RoutedEventArgs e) => DismissGetStartedClicked?.Invoke(sender, e);

    private void OnCloseConnectedTasksDrawerClicked(object sender, RoutedEventArgs e) => HideConnectedTasksDrawer();

    private void OnPrimaryConnectedTaskActionClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id)
        {
            return;
        }

        var item = _connectedTasks.FirstOrDefault(source => string.Equals(source.Id, id, StringComparison.Ordinal));
        if (item?.IsSkipped == true)
        {
            UnskipConnectedTaskClicked?.Invoke(this, id);
            return;
        }

        AddConnectedTaskClicked?.Invoke(this, id);
    }

    private void OnSkipConnectedTaskClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string id)
        {
            SkipConnectedTaskClicked?.Invoke(this, id);
        }
    }

    private void OnAddAllConnectedTasksClicked(object sender, RoutedEventArgs e) => AddAllConnectedTasksClicked?.Invoke(sender, e);

    private void OnReviewConnectedTasksClicked(object sender, RoutedEventArgs e)
    {
        ShowConnectedTasksDrawer();
        ReviewConnectedTasksClicked?.Invoke(sender, e);
    }

    private void OnShowSkippedConnectedTasksToggled(object sender, RoutedEventArgs e) => ShowSkippedConnectedTasksChanged?.Invoke(sender, e);

    private void OnConnectedTasksSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ApplyConnectedTaskDrawerItems();
        }
    }

    private void OnConnectedTasksSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingConnectedTaskFilters)
        {
            RefreshConnectedTaskListFilter(_connectedTasks);
            ApplyConnectedTaskDrawerItems();
        }
    }

    private void OnConnectedTasksListChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingConnectedTaskFilters)
        {
            ApplyConnectedTaskDrawerItems();
        }
    }

    private void ClearListFilters()
    {
        SearchText = string.Empty;
        SetPriorityTag(string.Empty, notify: false);
        SetRepeatScopeTag("include", notify: false);
        SetLabelFilterId(null, notify: false);
        UpdateFilterState();
        LabelChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateFilterState()
    {
        if (FilterButtonText is null || ActiveFiltersPanel is null || ClearFiltersButton is null)
        {
            return;
        }

        UpdateViewControlText();
        ActiveFiltersPanel.Children.Clear();
        var activeFilterCount = 0;

        if (PriorityFilter is not null)
        {
            activeFilterCount++;
            AddFilterChip($"Priority: {PriorityText(_priorityTag)}", () => SetPriorityTag(string.Empty, notify: true));
        }

        if (RepeatScopeFilter != TaskRepeatScope.Include)
        {
            activeFilterCount++;
            AddFilterChip($"Repeating: {RepeatScopeText(_repeatScopeTag)}", () => SetRepeatScopeTag("include", notify: true));
        }

        if (LabelFilterId is not null)
        {
            activeFilterCount++;
            var labelName = _labelFilterOptions.FirstOrDefault(label => string.Equals(label.Id, LabelFilterId, StringComparison.Ordinal))?.Name ?? "Label";
            AddFilterChip($"Label: {labelName}", () => SetLabelFilterId(null, notify: true));
        }

        if (activeFilterCount > 0)
        {
            var clearAllButton = new Button
            {
                Content = "Clear all",
                Padding = new Thickness(10, 2, 10, 2),
                MinHeight = 30,
            };
            clearAllButton.Click += OnClearFiltersClicked;
            ActiveFiltersPanel.Children.Add(clearAllButton);
        }

        FilterButtonText.Text = activeFilterCount == 0
            ? "Filters"
            : $"Filters ({activeFilterCount})";
        ActiveFiltersPanel.Visibility = activeFilterCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearFiltersButton.Visibility = activeFilterCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(FilterButton, activeFilterCount == 0 ? "Filters" : $"Filters, {activeFilterCount} active");
        UpdateFilterCategoryCounts();
    }

    private bool HasActiveListFilters() =>
        !string.IsNullOrWhiteSpace(SearchText) ||
        PriorityFilter is not null ||
        RepeatScopeFilter != TaskRepeatScope.Include ||
        LabelFilterId is not null;

    private void AddFilterChip(string text, Action clearAction)
    {
        var button = new Button
        {
            Content = $"{text}  \u00d7",
            Padding = new Thickness(10, 2, 10, 2),
            MinHeight = 30,
        };
        button.Click += (_, _) => clearAction();
        ActiveFiltersPanel.Children.Add(button);
    }

    private void ShowFilterPane(string category)
    {
        if (PriorityFilterPane is null)
        {
            return;
        }

        PriorityFilterPane.Visibility = category == "priority" ? Visibility.Visible : Visibility.Collapsed;
        RepeatFilterPane.Visibility = category == "repeating" ? Visibility.Visible : Visibility.Collapsed;
        LabelFilterPane.Visibility = category == "label" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateFilterCategoryCounts()
    {
        SetFilterCategoryCount(PriorityFilterCategoryCount, PriorityFilter is null ? 0 : 1);
        SetFilterCategoryCount(RepeatFilterCategoryCount, RepeatScopeFilter == TaskRepeatScope.Include ? 0 : 1);
        SetFilterCategoryCount(LabelFilterCategoryCount, LabelFilterId is null ? 0 : 1);
    }

    private static void SetFilterCategoryCount(TextBlock textBlock, int count)
    {
        textBlock.Text = count > 0 ? count.ToString() : string.Empty;
        textBlock.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetSortTag(string tag, bool notify)
    {
        tag = string.IsNullOrWhiteSpace(tag) ? "priority" : tag;
        if (string.Equals(_sortTag, tag, StringComparison.Ordinal) && notify)
        {
            return;
        }

        _sortTag = tag;
        UpdateViewControlText();
        if (notify)
        {
            SortChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetSortDirectionTag(string tag, bool notify)
    {
        tag = string.Equals(tag, "desc", StringComparison.Ordinal) ? "desc" : "asc";
        if (string.Equals(_sortDirectionTag, tag, StringComparison.Ordinal) && notify)
        {
            return;
        }

        _sortDirectionTag = tag;
        UpdateViewControlText();
        if (notify)
        {
            SortChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetGroupTag(string tag, bool notify)
    {
        tag = string.IsNullOrWhiteSpace(tag) ? "none" : tag;
        if (string.Equals(tag, "date-type", StringComparison.Ordinal))
        {
            tag = "date";
        }

        if (string.Equals(_groupTag, tag, StringComparison.Ordinal) && notify)
        {
            return;
        }

        _groupTag = tag;
        UpdateViewControlText();
        if (notify)
        {
            GroupChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetPriorityTag(string tag, bool notify)
    {
        tag = string.IsNullOrWhiteSpace(tag) ? string.Empty : tag;
        if (string.Equals(_priorityTag, tag, StringComparison.Ordinal) && notify)
        {
            return;
        }

        _priorityTag = tag;
        SetCheckedRadio(PriorityAnyRadio, tag == string.Empty);
        SetCheckedRadio(PriorityUrgentRadio, tag == "1");
        SetCheckedRadio(PriorityHighRadio, tag == "2");
        SetCheckedRadio(PriorityNormalRadio, tag == "3");
        SetCheckedRadio(PriorityLowRadio, tag == "4");
        UpdateFilterState();
        if (notify)
        {
            PriorityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetRepeatScopeTag(string tag, bool notify)
    {
        tag = string.IsNullOrWhiteSpace(tag) ? "include" : tag;
        if (string.Equals(_repeatScopeTag, tag, StringComparison.Ordinal) && notify)
        {
            return;
        }

        _repeatScopeTag = tag;
        SetCheckedRadio(RepeatIncludeRadio, tag == "include");
        SetCheckedRadio(RepeatExcludeRadio, tag == "exclude");
        SetCheckedRadio(RepeatOnlyRadio, tag == "only");
        UpdateFilterState();
        if (notify)
        {
            RepeatScopeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetLabelFilterId(string? labelId, bool notify)
    {
        labelId = string.IsNullOrWhiteSpace(labelId) ? null : labelId;
        if (string.Equals(_labelFilterId, labelId, StringComparison.Ordinal) && notify)
        {
            return;
        }

        _labelFilterId = labelId;
        RefreshLabelFilterList();
        UpdateFilterState();
        if (notify)
        {
            LabelChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetCheckedRadio(RadioButton button, bool isChecked)
    {
        if (button.IsChecked == isChecked)
        {
            return;
        }

        _suppressViewControlEvents = true;
        button.IsChecked = isChecked;
        _suppressViewControlEvents = false;
    }

    private void RefreshLabelFilterList()
    {
        if (LabelFilterList is null)
        {
            return;
        }

        _suppressViewControlEvents = true;
        LabelFilterList.Items.Clear();
        var anyItem = new ListViewItem { Content = "Any label", Tag = string.Empty };
        LabelFilterList.Items.Add(anyItem);
        var matches = _labelFilterOptions
            .Where(label => string.IsNullOrWhiteSpace(_labelFilterSearchText) ||
                label.Name.Contains(_labelFilterSearchText, StringComparison.CurrentCultureIgnoreCase));
        foreach (var label in matches)
        {
            LabelFilterList.Items.Add(new ListViewItem { Content = label.Name, Tag = label.Id });
        }

        LabelFilterList.SelectedItem = LabelFilterList.Items
            .OfType<ListViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _labelFilterId ?? string.Empty, StringComparison.Ordinal)) ??
            anyItem;
        _suppressViewControlEvents = false;
    }

    private void UpdateViewControlText()
    {
        if (SortButtonText is null || GroupButtonText is null)
        {
            return;
        }

        SortButtonText.Text = $"Sort: {SortText(_sortTag)} {SortDirectionLabel(_sortDirectionTag)}";
        GroupButtonText.Text = $"Group: {GroupText(_groupTag)}";
        AutomationProperties.SetName(SortButton, $"Sort by {SortText(_sortTag)}, {SortDirectionText(_sortDirectionTag)}");
        AutomationProperties.SetName(GroupButton, $"Group by {GroupText(_groupTag)}");
    }

    private static string GroupTag(TaskGroupMode mode) => mode switch
    {
        TaskGroupMode.Date => "date",
        TaskGroupMode.Project => "project",
        TaskGroupMode.Status => "status",
        TaskGroupMode.Priority => "priority",
        TaskGroupMode.Label => "label",
        TaskGroupMode.Source => "source",
        TaskGroupMode.Repeating => "repeating",
        TaskGroupMode.CreatedDate => "created-date",
        TaskGroupMode.CompletedDate => "completed-date",
        _ => "none",
    };

    private static string SortTagForMode(TaskSortMode mode) => mode switch
    {
        TaskSortMode.Date => "date",
        TaskSortMode.CreatedNewest => "created",
        TaskSortMode.Title => "title",
        TaskSortMode.Project => "project",
        _ => "priority",
    };

    private static string RepeatScopeTag(TaskRepeatScope scope) => scope switch
    {
        TaskRepeatScope.Exclude => "exclude",
        TaskRepeatScope.Only => "only",
        _ => "include",
    };

    private static string SortText(string tag) => tag switch
    {
        "date" or "due" => "Date",
        "created" => "Newest",
        "title" => "Title",
        "project" => "Project",
        _ => "Priority",
    };

    private static string SortDirectionLabel(string tag) => tag == "desc" ? "Desc" : "Asc";

    private static string SortDirectionText(string tag) => tag == "desc" ? "descending" : "ascending";

    private static string GroupText(string tag) => tag switch
    {
        "date" => "Date",
        "date-type" => "Date",
        "project" => "Project",
        "status" => "Status",
        "priority" => "Priority",
        "label" => "Label",
        "source" => "Source",
        "repeating" => "Repeating",
        "created-date" => "Created date",
        "completed-date" => "Completed date",
        _ => "None",
    };

    private static string PriorityText(string tag) => tag switch
    {
        "1" => "Urgent",
        "2" => "High",
        "3" => "Normal",
        "4" => "Low",
        _ => "Any",
    };

    private static string RepeatScopeText(string tag) => tag switch
    {
        "exclude" => "Exclude",
        "only" => "Only",
        _ => "Include",
    };

    private void RefreshConnectedTaskFilters(IReadOnlyList<ProviderSourceItem> items)
    {
        _updatingConnectedTaskFilters = true;
        var selectedSource = (ConnectedTasksSourceFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        ConnectedTasksSourceFilter.Items.Clear();
        ConnectedTasksSourceFilter.Items.Add(new ComboBoxItem { Content = "All sources", Tag = string.Empty });
        foreach (var source in items
            .GroupBy(item => item.IntegrationId)
            .OrderBy(group => group.First().SourceName, StringComparer.CurrentCultureIgnoreCase))
        {
            ConnectedTasksSourceFilter.Items.Add(new ComboBoxItem
            {
                Content = source.First().SourceName,
                Tag = source.Key,
            });
        }

        ConnectedTasksSourceFilter.SelectedItem = ConnectedTasksSourceFilter.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), selectedSource, StringComparison.Ordinal)) ??
            ConnectedTasksSourceFilter.Items.FirstOrDefault();
        _updatingConnectedTaskFilters = false;
        RefreshConnectedTaskListFilter(items);
    }

    private void RefreshConnectedTaskListFilter(IReadOnlyList<ProviderSourceItem> items)
    {
        _updatingConnectedTaskFilters = true;
        var selectedSource = (ConnectedTasksSourceFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        var selectedList = (ConnectedTasksListFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        ConnectedTasksListFilter.Items.Clear();
        ConnectedTasksListFilter.Items.Add(new ComboBoxItem { Content = "All lists", Tag = string.Empty });
        foreach (var sourceList in items
            .Where(item => string.IsNullOrWhiteSpace(selectedSource) ||
                string.Equals(item.IntegrationId, selectedSource, StringComparison.Ordinal))
            .Where(item => !string.IsNullOrWhiteSpace(item.SourceProjectName))
            .GroupBy(item => item.SourceProjectName!)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            ConnectedTasksListFilter.Items.Add(new ComboBoxItem
            {
                Content = sourceList.Key,
                Tag = sourceList.Key,
            });
        }

        ConnectedTasksListFilter.SelectedItem = ConnectedTasksListFilter.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), selectedList, StringComparison.Ordinal)) ??
            ConnectedTasksListFilter.Items.FirstOrDefault();
        _updatingConnectedTaskFilters = false;
    }

    private void ApplyConnectedTaskDrawerItems()
    {
        var searchText = ConnectedTasksSearchBox.Text?.Trim() ?? string.Empty;
        var sourceFilter = (ConnectedTasksSourceFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        var listFilter = (ConnectedTasksListFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        var filteredItems = _connectedTasks
            .Where(item => string.IsNullOrWhiteSpace(sourceFilter) ||
                string.Equals(item.IntegrationId, sourceFilter, StringComparison.Ordinal))
            .Where(item => string.IsNullOrWhiteSpace(listFilter) ||
                string.Equals(item.SourceProjectName, listFilter, StringComparison.CurrentCultureIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(searchText) ||
                item.Title.Contains(searchText, StringComparison.CurrentCultureIgnoreCase) ||
                (item.Description?.Contains(searchText, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
                (item.SourceProjectName?.Contains(searchText, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
                item.SourceName.Contains(searchText, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        ConnectedTasksDrawerList.ItemsSource = filteredItems;
    }
}
