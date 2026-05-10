using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.ViewModels;

public sealed class TasksViewModel : ObservableObject
{
    private string _title = "Inbox";
    private string _subtitle = "0 tasks";
    private bool _isEmpty = true;
    private string _emptyTitle = "Nothing here";
    private string _emptyMessage = "Add a task or change your filters.";
    private string _searchText = string.Empty;
    private ProjectItem? _selectedProject;

    public ObservableCollection<TaskListItemViewModel> Tasks { get; } = [];
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
            }
        }
    }

    public Visibility EmptyVisibility => IsEmpty ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ListVisibility => IsEmpty ? Visibility.Collapsed : Visibility.Visible;

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
}
