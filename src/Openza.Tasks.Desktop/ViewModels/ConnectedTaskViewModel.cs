using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Desktop.ViewModels;

public sealed class ConnectedTaskViewModel(ProviderSourceItem source)
{
    public ProviderSourceItem Source { get; } = source;
    public string Title => Source.Title;
    public string SourceName => Source.SourceName;
    public string SourceText => Source.SourceText;
    public string MetadataText => Source.IntakeMetadataText;
    public string? Description => Source.Description;
    public string PrimaryActionText => Source.IntakeActionText;
    public string SecondaryActionText => Source.SecondaryIntakeActionText;
    public bool CanSkip => Source.CanSkip;
}
