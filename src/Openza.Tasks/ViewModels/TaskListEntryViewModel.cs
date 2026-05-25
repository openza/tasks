using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace Openza.Tasks.ViewModels;

public sealed class TaskListEntryViewModel : ObservableObject
{
    private bool _isGroupHeader;
    private TaskGroupViewModel? _group;
    private TaskListItemViewModel? _task;

    public string Key { get; private set; } = string.Empty;

    public bool IsGroupHeader
    {
        get => _isGroupHeader;
        private set
        {
            if (SetProperty(ref _isGroupHeader, value))
            {
                OnPropertyChanged(nameof(HeaderVisibility));
                OnPropertyChanged(nameof(TaskVisibility));
            }
        }
    }

    public TaskGroupViewModel? Group
    {
        get => _group;
        private set
        {
            if (SetProperty(ref _group, value))
            {
                OnPropertyChanged(nameof(GroupKey));
                OnPropertyChanged(nameof(GroupTitle));
                OnPropertyChanged(nameof(GroupCountText));
                OnPropertyChanged(nameof(GroupExpandGlyph));
            }
        }
    }

    public TaskListItemViewModel? Task
    {
        get => _task;
        private set => SetProperty(ref _task, value);
    }

    public string GroupKey => Group?.Key ?? string.Empty;
    public string GroupTitle => Group?.Title ?? string.Empty;
    public string GroupCountText => Group?.CountText ?? string.Empty;
    public string GroupExpandGlyph => Group?.IsExpanded == true ? "\uE70D" : "\uE76C";
    public Visibility HeaderVisibility => IsGroupHeader ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TaskVisibility => IsGroupHeader ? Visibility.Collapsed : Visibility.Visible;

    public static TaskListEntryViewModel ForGroup(TaskGroupViewModel group) => new()
    {
        Key = $"group:{group.Key}",
        IsGroupHeader = true,
        Group = group,
    };

    public static TaskListEntryViewModel ForTask(TaskListItemViewModel task, string? groupKey = null) => new()
    {
        Key = string.IsNullOrWhiteSpace(groupKey) ? $"task:{task.Id}" : $"group:{groupKey}:task:{task.Id}",
        Task = task,
    };

    public void UpdateFrom(TaskListEntryViewModel source)
    {
        Key = source.Key;
        IsGroupHeader = source.IsGroupHeader;
        Group = source.Group;
        Task = source.Task;
        OnPropertyChanged(nameof(GroupKey));
        OnPropertyChanged(nameof(GroupTitle));
        OnPropertyChanged(nameof(GroupCountText));
        OnPropertyChanged(nameof(GroupExpandGlyph));
        OnPropertyChanged(nameof(HeaderVisibility));
        OnPropertyChanged(nameof(TaskVisibility));
    }
}
