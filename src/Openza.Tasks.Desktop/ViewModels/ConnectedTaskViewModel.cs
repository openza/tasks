using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Desktop.ViewModels;

public sealed class ConnectedTaskViewModel(ProviderSourceItem source)
{
    public ProviderSourceItem Source { get; } = source;
    public string Title => Source.Title;
    public string SourceText => Source.SourceText;
    public string MetadataText => Source.IntakeMetadataText;
    public string PrimaryActionText => Source.IsSkipped ? "Unskip" : "Add to Openza";
    public string SecondaryActionText => Source.IsSkipped ? string.Empty : "Skip";
    public bool CanSkip => Source.CanSkip;
}
