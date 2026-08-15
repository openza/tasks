using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Desktop.ViewModels;

public sealed class ProjectNavigationItemViewModel
{
    private readonly int _count;

    public ProjectNavigationItemViewModel(ProjectItem project, int count)
    {
        Project = project;
        _count = count;
    }

    public ProjectItem Project { get; }
    public string Title => Project.Name;
    public string Color => Project.Color;
    public string CountText => _count == 0 ? string.Empty : _count.ToString();
}
