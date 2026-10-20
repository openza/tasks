using System.Text.Json;

namespace Openza.Tasks.Core.PersonalCloud;

public sealed partial class PersonalCloudSyncClient
{
    private string? _singleAttemptMutationId;
    private readonly Dictionary<string, long> _fanoutBounds = new(StringComparer.Ordinal);

    private void ApplyFanoutBound(HttpRequestMessage request, IReadOnlyList<PersonalCloudK1OutboxEntry> entries)
    {
        if (!_supportsFanoutBudget) return;
        var bounds = entries.Where(entry => _fanoutBounds.ContainsKey(entry.MutationId))
            .Select(entry => _fanoutBounds[entry.MutationId]).ToArray();
        if (bounds.Length == 0) return;
        var bound = bounds.Max();
        request.Headers.Add(PersonalCloudMutationCost.FanoutHeader, bound.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Options.Set(PersonalCloudMutationCost.GuardedFanoutBound, bound);
    }

    private async Task RememberStaleFanoutAsync(PersonalCloudK1OutboxEntry entry, CancellationToken cancellationToken)
    {
        if (PersonalCloudDispatchHttp.PolicyFor(http) is { } policy)
            await policy.RememberStaleFanoutAsync(connection.ServiceOrigin, entry.CanonicalJson, cancellationToken).ConfigureAwait(false);
    }
    private async Task<(IReadOnlyList<PersonalCloudK1OutboxEntry> Items, bool PauseAfter, long? OversizedUnits)> SelectBudgetPrefixAsync(
        IReadOnlyList<PersonalCloudK1OutboxEntry> pending, CancellationToken cancellationToken)
    {
        _fanoutBounds.Clear();
        if (PersonalCloudDispatchHttp.PolicyFor(http) is not { } policy)
            return (pending.Take(PushBatchLimit).ToArray(), false, null);
        var status = await policy.GetStatusAsync(connection.ServiceOrigin, cancellationToken).ConfigureAwait(false);
        if (status.BlocksReads) throw status.ToException();
        _singleAttemptMutationId = null;
        if (_supportsFanoutBudget)
            foreach (var entry in pending.Take(PushBatchLimit).Where(entry => PersonalCloudMutationCost.IsFanout(entry.Operation)))
                _fanoutBounds[entry.MutationId] = await store.GetPersonalCloudFanoutBoundAsync(entry,
                    policy.NeedsFanoutFallback(connection.ServiceOrigin, entry.CanonicalJson), cancellationToken).ConfigureAwait(false);
        if (pending.Count > 0 && policy.HasSingleAttemptPermission(connection.ServiceOrigin, pending[0].CanonicalJson))
        {
            _singleAttemptMutationId = pending[0].MutationId;
            return (pending.Take(1).ToArray(), false, null);
        }
        if (status.Code == "local_operation_budget_exceeded" && pending.Count > 0)
        {
            using var head = JsonDocument.Parse(pending[0].CanonicalJson);
            var estimate = PersonalCloudMutationCost.ForBudget(head.RootElement, status.WriteUnitLimit,
                _fanoutBounds.TryGetValue(pending[0].MutationId, out var firstBound) ? firstBound : null);
            status = await policy.ReconsiderOperationHoldAsync(connection.ServiceOrigin, estimate, cancellationToken).ConfigureAwait(false);
        }
        if (status.BlocksReads) throw status.ToException();
        if (status.IsPaused) return ([], true, status.BlockingWriteUnits);
        var remainingEnvelopes = status.EnvelopeLimit - status.AttemptedEnvelopes;
        var result = new List<PersonalCloudK1OutboxEntry>();
        long reserved = 0;
        var pauseAfter = false;
        long? oversized = null;
        foreach (var item in pending)
        {
            using var json = JsonDocument.Parse(item.CanonicalJson);
            var cost = PersonalCloudMutationCost.ForBudget(json.RootElement, status.WriteUnitLimit,
                _fanoutBounds.TryGetValue(item.MutationId, out var itemBound) ? itemBound : null);
            if (cost > status.WriteUnitLimit) { pauseAfter = true; oversized = cost; break; }
            if (result.Count >= remainingEnvelopes || cost > status.RemainingWriteUnits - reserved)
            { pauseAfter = true; break; }
            if (result.Count >= PushBatchLimit) break;
            result.Add(item);
            reserved += cost;
        }
        if (pauseAfter && result.Count == 0)
            await policy.PauseBudgetAsync(connection.ServiceOrigin, oversized, cancellationToken).ConfigureAwait(false);
        return (result, pauseAfter, oversized);
    }
}
