using System.Collections.ObjectModel;

namespace Openza.Tasks.ViewModels;

public sealed class ProjectGroupViewModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Glyph { get; set; } = string.Empty;
    public int Count { get; set; }
    public bool IsExpanded { get; set; } = true;
    public bool ShowHeader { get; set; } = true;
    public ObservableCollection<ProjectListItemViewModel> Projects { get; } = [];
    public string CountText => Count == 0 ? string.Empty : Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string ExpandGlyph => IsExpanded ? "\uE70D" : "\uE76C";
    public Microsoft.UI.Xaml.Visibility HeaderVisibility => ShowHeader
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility ProjectsVisibility => IsExpanded
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;
}
