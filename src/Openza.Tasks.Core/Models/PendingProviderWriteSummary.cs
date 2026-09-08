namespace Openza.Tasks.Core.Models;

public sealed record PendingProviderWriteSummary(
    int Completions,
    int Reopens,
    int DateUpdates)
{
    public int Total => Completions + Reopens + DateUpdates;
}
