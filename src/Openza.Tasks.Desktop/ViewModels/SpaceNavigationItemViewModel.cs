using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Desktop.ViewModels;

public sealed class SpaceNavigationItemViewModel
{
    public SpaceNavigationItemViewModel(SpaceItem? space)
    {
        Space = space;
    }

    public SpaceItem? Space { get; }
    public string? SpaceId => Space?.Id;
    public string Title => Space?.Name ?? "All spaces";
}
