using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace Openza.Tasks.ViewModels;

public sealed partial class SpaceSettingsItemViewModel : ObservableObject
{
    public string Id { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    public string DetailText { get; set; } = string.Empty;

    public bool IsCurrent { get; set; }

    public bool CanArchive { get; set; }

    public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
}
