using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Desktop.ViewModels;

public sealed class SpaceNavigationItemViewModel : ObservableObject
{
    public SpaceNavigationItemViewModel(SpaceItem? space)
    {
        Space = space;
    }

    public SpaceItem? Space { get; private set; }
    public string? SpaceId => Space?.Id;
    public string Title => Space?.Name ?? "All spaces";
    public override string ToString() => Title;

    internal void UpdateSpace(SpaceItem space)
    {
        if (Space == space) return;
        Space = space;
        OnPropertyChanged(nameof(Space));
        OnPropertyChanged(nameof(Title));
    }
}
