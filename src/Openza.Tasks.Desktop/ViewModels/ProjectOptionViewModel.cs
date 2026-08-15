using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Desktop.ViewModels;

public sealed class ProjectOptionViewModel(ProjectItem? project)
{
    public ProjectItem? Project { get; } = project;
    public string? ProjectId => Project?.Id;
    public string Title => Project?.Name ?? "Inbox (no project)";
}
