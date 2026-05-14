using Microsoft.UI.Xaml;

namespace Openza.Tasks.ViewModels;

public sealed class TaskMetadataPartViewModel
{
    public string Text { get; init; } = string.Empty;

    public bool ShowSeparator { get; init; }

    public Visibility SeparatorVisibility => ShowSeparator ? Visibility.Visible : Visibility.Collapsed;
}
