using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.ViewModels;

public sealed class TasksViewModel : ObservableObject
{
    private string _title = "Inbox";
    private string _subtitle = "0 tasks";
    private bool _isEmpty = true;
    private bool _isGetStartedVisible;
    private string _emptyTitle = "Nothing here";
    private string _emptyMessage = "Add a task or change your filters.";
    private string _searchText = string.Empty;
    private ProjectItem? _selectedProject;
    private bool _isGrouped;

    public ObservableCollection<TaskListItemViewModel> Tasks { get; } = [];
    public ObservableCollection<TaskGroupViewModel> TaskGroups { get; } = [];
    public ObservableCollection<ProjectGroupViewModel> ProjectGroups { get; } = [];
    public ObservableCollection<ProjectListItemViewModel> ProjectOptions { get; } = [];
    public ObservableCollection<LabelItem> LabelOptions { get; } = [];

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        set => SetProperty(ref _subtitle, value);
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        set
        {
            if (SetProperty(ref _isEmpty, value))
            {
                OnPropertyChanged(nameof(EmptyVisibility));
                OnPropertyChanged(nameof(ListVisibility));
                OnPropertyChanged(nameof(UngroupedListVisibility));
                OnPropertyChanged(nameof(GroupedListVisibility));
            }
        }
    }

    public Visibility EmptyVisibility => IsEmpty && !IsGetStartedVisible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ListVisibility => IsEmpty ? Visibility.Collapsed : Visibility.Visible;

    public Visibility UngroupedListVisibility => !IsEmpty && !IsGrouped ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GroupedListVisibility => !IsEmpty && IsGrouped ? Visibility.Visible : Visibility.Collapsed;

    public bool IsGetStartedVisible
    {
        get => _isGetStartedVisible;
        set
        {
            if (SetProperty(ref _isGetStartedVisible, value))
            {
                OnPropertyChanged(nameof(EmptyVisibility));
            }
        }
    }

    public string EmptyTitle
    {
        get => _emptyTitle;
        set => SetProperty(ref _emptyTitle, value);
    }

    public string EmptyMessage
    {
        get => _emptyMessage;
        set => SetProperty(ref _emptyMessage, value);
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public ProjectItem? SelectedProject
    {
        get => _selectedProject;
        set => SetProperty(ref _selectedProject, value);
    }

    public bool IsGrouped
    {
        get => _isGrouped;
        set
        {
            if (SetProperty(ref _isGrouped, value))
            {
                OnPropertyChanged(nameof(UngroupedListVisibility));
                OnPropertyChanged(nameof(GroupedListVisibility));
            }
        }
    }

    public void SetTasks(IEnumerable<TaskListItemViewModel> tasks, TaskGroupMode groupMode)
    {
        var taskItems = tasks.ToList();

        Tasks.Clear();
        foreach (var task in taskItems)
        {
            Tasks.Add(task);
        }

        TaskGroups.Clear();
        foreach (var group in TaskGroupViewModel.Build(taskItems, groupMode))
        {
            TaskGroups.Add(group);
        }

        IsGrouped = groupMode != TaskGroupMode.None;
        IsEmpty = taskItems.Count == 0;
    }
}
