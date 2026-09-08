using Openza.Tasks.Core.Data;
using FluentIcons.Common;

namespace Openza.Tasks.Desktop.ViewModels;

public sealed class NavigationItemViewModel(string title, TaskListKind kind, Icon icon) : ObservableObject
{
    private int _count;

    public string Title { get; } = title;
    public TaskListKind Kind { get; } = kind;
    public Icon Icon { get; } = icon;
    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value))
            {
                OnPropertyChanged(nameof(CountText));
                OnPropertyChanged(nameof(HasCount));
            }
        }
    }

    public string CountText => Count == 0 ? string.Empty : Count.ToString();
    public bool HasCount => Count > 0;
}
