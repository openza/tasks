using System.Text.Json;

namespace Openza.Tasks.Core.PersonalCloud;

public sealed partial class PersonalCloudSyncClient
{
    private async Task<(IReadOnlyList<PersonalCloudK1OutboxEntry> Items, bool PauseAfter, long? OversizedUnits)> SelectBudgetPrefixAsync(
        IReadOnlyList<PersonalCloudK1OutboxEntry> pending, CancellationToken cancellationToken)
    {
        if (PersonalCloudDispatchHttp.PolicyFor(http) is not { } policy)
            return (pending.Take(PushBatchLimit).ToArray(), false, null);
        var status = await policy.GetStatusAsync(connection.ServiceOrigin, cancellationToken).ConfigureAwait(false);
        if (status.IsPaused) throw status.ToException();
        var remainingEnvelopes = status.EnvelopeLimit - status.AttemptedEnvelopes;
        var result = new List<PersonalCloudK1OutboxEntry>();
        long reserved = 0;
        var pauseAfter = false;
        long? oversized = null;
        foreach (var item in pending)
        {
            using var json = JsonDocument.Parse(item.CanonicalJson);
            var cost = PersonalCloudMutationCost.ForBudget(json.RootElement, status.WriteUnitLimit);
            if (cost > status.WriteUnitLimit) { pauseAfter = true; oversized = cost; break; }
            if (result.Count >= remainingEnvelopes || cost > status.RemainingWriteUnits - reserved)
            { pauseAfter = true; break; }
            if (result.Count >= PushBatchLimit) break;
            result.Add(item);
            reserved += cost;
        }
        if (pauseAfter && result.Count == 0)
            throw (await policy.PauseBudgetAsync(connection.ServiceOrigin, oversized, cancellationToken).ConfigureAwait(false)).ToException();
        return (result, pauseAfter, oversized);
    }
}
