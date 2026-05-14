using Openza.Tasks.Core.Models;
using FontWeight = Windows.UI.Text.FontWeight;
using WinUIFontWeights = Microsoft.UI.Text.FontWeights;

namespace Openza.Tasks.ViewModels;

public sealed class ProjectListItemViewModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string IntegrationId { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string Color { get; set; } = "#808080";
    public string Status { get; set; } = ProjectLifecycleStates.Active;
    public int ActiveTaskCount { get; set; }
    public int NextActionCount { get; set; }
    public bool IsSelected { get; set; }
    public bool CanEdit { get; set; }
    public bool CanDelete { get; set; }
    public bool IsFavorite { get; set; }
    public string CountText => ActiveTaskCount == 0 ? string.Empty : ActiveTaskCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string OpenTaskCountText => ActiveTaskCount == 0 ? "no open tasks" : $"{ActiveTaskCount} open";
    public string StatusTitle => Status switch
    {
        ProjectLifecycleStates.Completed => "Completed",
        ProjectLifecycleStates.Archived => "Archived",
        _ => "Active",
    };
    public string AccessibilityName => $"{Name}, {SourceName}, {StatusTitle}, {OpenTaskCountText}";
    public double IsSelectedOpacity => IsSelected ? 1 : 0;
    public FontWeight NameWeight => IsSelected ? WinUIFontWeights.SemiBold : WinUIFontWeights.Normal;
    public string Glyph => "\uE8B7";

    public static ProjectListItemViewModel FromProject(ProjectItem project, int activeTaskCount, int nextActionCount, bool isSelected) => new()
    {
        Id = project.Id,
        Name = project.Name,
        IntegrationId = project.IntegrationId,
        SourceName = TaskListItemViewModel.SourceName(project.IntegrationId),
        Color = project.Color,
        Status = project.EffectiveStatus,
        ActiveTaskCount = activeTaskCount,
        NextActionCount = nextActionCount,
        IsSelected = isSelected,
        CanEdit = project.IntegrationId == IntegrationIds.Local,
        CanDelete = project.IntegrationId == IntegrationIds.Local,
        IsFavorite = project.IsFavorite,
    };
}
