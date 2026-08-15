using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Desktop.ViewModels;

public sealed class LabelOptionViewModel(LabelItem? label)
{
    public LabelItem? Label { get; } = label;
    public string? LabelId => Label?.Id;
    public string Title => Label?.Name ?? "All labels";
}
