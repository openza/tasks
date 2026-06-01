using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace Openza.Tasks.ViewModels;

public sealed class ProjectPaneEntryViewModel : ObservableObject
{
    private bool _isGroupHeader;
    private ProjectGroupViewModel? _group;
    private ProjectListItemViewModel? _project;

    public string Key { get; private set; } = string.Empty;

    public bool IsGroupHeader
    {
        get => _isGroupHeader;
        private set
        {
            if (SetProperty(ref _isGroupHeader, value))
            {
                OnPropertyChanged(nameof(HeaderVisibility));
                OnPropertyChanged(nameof(ProjectVisibility));
            }
        }
    }

    public ProjectGroupViewModel? Group
    {
        get => _group;
        private set
        {
            if (SetProperty(ref _group, value))
            {
                OnPropertyChanged(nameof(GroupId));
                OnPropertyChanged(nameof(GroupName));
                OnPropertyChanged(nameof(GroupGlyph));
                OnPropertyChanged(nameof(GroupCountText));
                OnPropertyChanged(nameof(GroupExpandGlyph));
                OnPropertyChanged(nameof(HeaderVisibility));
            }
        }
    }

    public ProjectListItemViewModel? Project
    {
        get => _project;
        private set
        {
            if (SetProperty(ref _project, value))
            {
                OnPropertyChanged(nameof(ProjectId));
                OnPropertyChanged(nameof(ProjectName));
                OnPropertyChanged(nameof(ProjectCountText));
                OnPropertyChanged(nameof(ProjectAccessibilityName));
                OnPropertyChanged(nameof(ProjectCanEdit));
                OnPropertyChanged(nameof(ProjectCanDelete));
                OnPropertyChanged(nameof(ProjectIsSelectedOpacity));
                OnPropertyChanged(nameof(ProjectNameWeight));
            }
        }
    }

    public string GroupId => Group?.Id ?? string.Empty;
    public string GroupName => Group?.Name ?? string.Empty;
    public string GroupGlyph => Group?.Glyph ?? string.Empty;
    public string GroupCountText => Group?.CountText ?? string.Empty;
    public string GroupExpandGlyph => Group?.ExpandGlyph ?? string.Empty;
    public string ProjectId => Project?.Id ?? string.Empty;
    public string ProjectName => Project?.Name ?? string.Empty;
    public string ProjectCountText => Project?.CountText ?? string.Empty;
    public string ProjectAccessibilityName => Project?.AccessibilityName ?? string.Empty;
    public bool ProjectCanEdit => Project?.CanEdit == true;
    public bool ProjectCanDelete => Project?.CanDelete == true;
    public double ProjectIsSelectedOpacity => Project?.IsSelectedOpacity ?? 0;
    public Windows.UI.Text.FontWeight ProjectNameWeight => Project?.NameWeight ?? Microsoft.UI.Text.FontWeights.Normal;
    public Visibility HeaderVisibility => IsGroupHeader && Group?.ShowHeader == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProjectVisibility => IsGroupHeader ? Visibility.Collapsed : Visibility.Visible;

    public static ProjectPaneEntryViewModel ForGroup(ProjectGroupViewModel group) => new()
    {
        Key = $"group:{group.Id}",
        IsGroupHeader = true,
        Group = group,
    };

    public static ProjectPaneEntryViewModel ForProject(ProjectListItemViewModel project, string groupId) => new()
    {
        Key = $"group:{groupId}:project:{project.Id}",
        Project = project,
    };

    public void UpdateFrom(ProjectPaneEntryViewModel source)
    {
        Key = source.Key;
        IsGroupHeader = source.IsGroupHeader;
        Group = source.Group;
        Project = source.Project;
        OnPropertyChanged(nameof(GroupId));
        OnPropertyChanged(nameof(GroupName));
        OnPropertyChanged(nameof(GroupGlyph));
        OnPropertyChanged(nameof(GroupCountText));
        OnPropertyChanged(nameof(GroupExpandGlyph));
        OnPropertyChanged(nameof(ProjectId));
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(ProjectCountText));
        OnPropertyChanged(nameof(ProjectAccessibilityName));
        OnPropertyChanged(nameof(ProjectCanEdit));
        OnPropertyChanged(nameof(ProjectCanDelete));
        OnPropertyChanged(nameof(ProjectIsSelectedOpacity));
        OnPropertyChanged(nameof(ProjectNameWeight));
        OnPropertyChanged(nameof(HeaderVisibility));
        OnPropertyChanged(nameof(ProjectVisibility));
    }
}
