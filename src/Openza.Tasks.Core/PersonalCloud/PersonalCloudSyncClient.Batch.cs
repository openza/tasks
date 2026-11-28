using System.Text;
using System.Text.Json;
using Openza.Tasks.Core.Data;

namespace Openza.Tasks.Core.PersonalCloud;

public sealed partial class PersonalCloudSyncClient
{
    private volatile bool _batchCapabilityKnown;
    internal bool HasCurrentCapabilities => _batchCapabilityKnown;
    private PersonalCloudMutationBatchCapability? _batchCapability;
    private bool _supportsWorkspace;
    private bool _supportsProviderScopes;
    private bool _supportsFanoutBudget;
    private bool _supportsProviderObservedReopen;
    private bool _supportsProviderCommandCancellation;
    private bool _supportsHistoricalCompletionImport;
    private string? _cloudArtifactVersion;
    private sealed class BatchEndpointUnavailableException : Exception { }

    private async Task<(int Pushed, string? Blocked, bool Parked)> PushPendingAsync(IReadOnlyList<PersonalCloudK1OutboxEntry> pending,
        string accessToken, CancellationToken cancellationToken)
    {
        await DiscoverCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        var pushed = 0;
        while (pushed < pending.Count)
        {
            var operation = pending[pushed].Operation;
            if (operation == PersonalCloudK0Protocol.ExternalCommandCancelOperation && !_supportsProviderCommandCancellation ||
                operation == PersonalCloudK0Protocol.TaskHistoricalCompletionImportOperation && !_supportsHistoricalCompletionImport)
            {
                await store.RetireDeferredPersonalCloudMutationAsync(connection.InstanceId, connection.DeviceId,
                    pending[pushed].MutationId,
                    null,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return (pushed, operation == PersonalCloudK0Protocol.TaskHistoricalCompletionImportOperation ? "upgrade_required" : null, true);
            }
            if (operation is PersonalCloudK0Protocol.ExternalCommandCancelOperation or PersonalCloudK0Protocol.TaskHistoricalCompletionImportOperation &&
                await store.IsPersonalCloudMutationDeferredAsync(connection.InstanceId, pending[pushed].MutationId,
                    _cloudArtifactVersion ?? "unknown", cancellationToken).ConfigureAwait(false))
            {
                await store.RetireDeferredPersonalCloudMutationAsync(connection.InstanceId, connection.DeviceId,
                    pending[pushed].MutationId,
                    operation == PersonalCloudK0Protocol.TaskHistoricalCompletionImportOperation ? null : _cloudArtifactVersion ?? "unknown",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return (pushed, operation == PersonalCloudK0Protocol.TaskHistoricalCompletionImportOperation ? "upgrade_required" : null, true);
            }
            if (pending[pushed].Operation == PersonalCloudK0Protocol.ProviderObservedReopenOperation && !_supportsProviderObservedReopen)
            {
                await store.DeferRejectedProviderObservedReopenAsync(connection.InstanceId, connection.DeviceId,
                    pending[pushed].MutationId, ProviderObservedReopenCapabilityKey(), cancellationToken).ConfigureAwait(false);
                return (pushed, null, true);
            }
            if (!_supportsWorkspace && IsWorkspaceOperation(pending[pushed].Operation)) return (pushed, "upgrade_required", false);
            if (!_supportsProviderScopes && IsScopedProviderOperation(pending[pushed])) return (pushed, "upgrade_required", false);
            var batch = BuildBatch(pending, pushed);
            if (batch is { } value)
            {
                string response;
                using var request = CreateAuthorizedRequest(HttpMethod.Post, "/v1/tasks/mutations/batch", accessToken);
                ApplyFanoutBound(request, value.Items);
                request.Content = new StringContent(value.Json, Encoding.UTF8, "application/json");
                try { response = await SendForJsonAsync(request, cancellationToken, batchRequest: true).ConfigureAwait(false); }
                catch (BatchEndpointUnavailableException)
                {
                    _batchCapability = null;
                    continue; // Retry the exact persisted envelopes via the old endpoint; no new IDs/sequences.
                }
                catch (PersonalCloudK0ContractException exception) when (IsPermanentMutationRejection(exception.Code))
                { return (pushed, exception.Code, false); }

                var receipt = ValidateBatchResponse(response, value.Items);
                foreach (var json in receipt.Receipts)
                {
                    await store.AcknowledgePersonalCloudMutationAsync(connection.InstanceId, connection.DeviceId, json, cancellationToken).ConfigureAwait(false);
                    pushed++;
                    if (progress is not null) await progress(PersonalCloudInitialSyncPhase.Uploading, cancellationToken).ConfigureAwait(false);
                }
                if (receipt.ErrorCode is { } code)
                {
                    if ((!receipt.Retryable || code == "parent_not_found") &&
                        await store.ParkRejectedPersonalCloudTaskAsync(value.Items[receipt.Receipts.Count], code, cancellationToken).ConfigureAwait(false))
                        return (pushed, null, true);
                    if (code == "fanout_bound_exceeded")
                    {
                        await RememberStaleFanoutAsync(value.Items[receipt.Receipts.Count], cancellationToken).ConfigureAwait(false);
                        return (pushed, code, false);
                    }
                    if (code == "daily_quota_exceeded")
                    {
                        var status = PersonalCloudDispatchHttp.PolicyFor(http) is { } dispatch
                            ? await dispatch.GetStatusAsync(connection.ServiceOrigin, cancellationToken).ConfigureAwait(false) : null;
                        throw status is { IsPaused: true } ? status.ToException() :
                            new PersonalCloudK0ContractException(code, "The daily cloud quota was reached. Confirmed changes were acknowledged; remaining exact intents are preserved.");
                    }
                    if (IsPermanentMutationRejection(code)) return (pushed, code, false);
                    throw new PersonalCloudK0ContractException("sync_failed", "The cloud could not finish this batch. Remaining exact intents have been preserved.");
                }
                continue;
            }
            var mutation = pending[pushed];
            using var single = CreateAuthorizedRequest(HttpMethod.Post, "/v1/tasks/mutations", accessToken);
            ApplyFanoutBound(single, [mutation]);
            single.Content = new StringContent(mutation.CanonicalJson, Encoding.UTF8, "application/json");
            string receiptJson;
            try { receiptJson = await SendForJsonAsync(single, cancellationToken).ConfigureAwait(false); }
            catch (PersonalCloudK0ContractException exception) when (exception.Code == "fanout_bound_exceeded")
            {
                await RememberStaleFanoutAsync(mutation, cancellationToken).ConfigureAwait(false);
                return (pushed, exception.Code, false);
            }
            catch (PersonalCloudK0ContractException exception) when (IsPermanentMutationRejection(exception.Code))
            {
                if (mutation.Operation == PersonalCloudK0Protocol.ExternalCommandCancelOperation && exception.Code == "command_busy")
                {
                    await store.RetireDeferredPersonalCloudMutationAsync(connection.InstanceId, connection.DeviceId,
                        mutation.MutationId, _cloudArtifactVersion ?? "unknown", cancellationToken: cancellationToken).ConfigureAwait(false);
                    return (pushed, exception.Code, true);
                }
                if (mutation.Operation == PersonalCloudK0Protocol.ExternalCommandCancelOperation && exception.Code == "command_superseded")
                {
                    await store.RetireDeferredPersonalCloudMutationAsync(connection.InstanceId, connection.DeviceId,
                        mutation.MutationId, _cloudArtifactVersion ?? "unknown", superseded: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return (pushed, null, true);
                }
                if (mutation.Operation is PersonalCloudK0Protocol.ExternalCommandCancelOperation or PersonalCloudK0Protocol.TaskHistoricalCompletionImportOperation &&
                    exception.IsDefinitiveMutationRejection)
                {
                    await store.RetireDeferredPersonalCloudMutationAsync(connection.InstanceId, connection.DeviceId,
                        mutation.MutationId, _cloudArtifactVersion ?? "unknown", cancellationToken: cancellationToken).ConfigureAwait(false);
                    return (pushed, mutation.Operation == PersonalCloudK0Protocol.TaskHistoricalCompletionImportOperation ? exception.Code : null, true);
                }
                if (mutation.Operation == PersonalCloudK0Protocol.ProviderObservedReopenOperation && exception.IsDefinitiveMutationRejection)
                {
                    await store.DeferRejectedProviderObservedReopenAsync(connection.InstanceId, connection.DeviceId,
                        mutation.MutationId, ProviderObservedReopenCapabilityKey(), cancellationToken).ConfigureAwait(false);
                    return (pushed, null, true);
                }
                if (exception.IsDefinitiveMutationRejection && await store.ParkRejectedPersonalCloudTaskAsync(mutation, exception.Code, cancellationToken, _cloudArtifactVersion).ConfigureAwait(false))
                    return (pushed, null, true);
                return (pushed, exception.Code, false);
            }
            catch (PersonalCloudK0ContractException exception) when (exception.Code == "unknown_operation" &&
                mutation.Operation is PersonalCloudK0Protocol.ExternalCommandCancelOperation or PersonalCloudK0Protocol.TaskHistoricalCompletionImportOperation)
            {
                await store.RetireDeferredPersonalCloudMutationAsync(connection.InstanceId, connection.DeviceId,
                    mutation.MutationId,
                    mutation.Operation == PersonalCloudK0Protocol.TaskHistoricalCompletionImportOperation ? _cloudArtifactVersion : _cloudArtifactVersion ?? "unknown",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return (pushed, mutation.Operation == PersonalCloudK0Protocol.TaskHistoricalCompletionImportOperation ? "upgrade_required" : null, true);
            }
            SqliteTaskStore.ValidatePersonalCloudMutationReceipt(mutation, receiptJson);
            await store.AcknowledgePersonalCloudMutationAsync(connection.InstanceId, connection.DeviceId, receiptJson, cancellationToken).ConfigureAwait(false);
            pushed++;
            if (progress is not null) await progress(PersonalCloudInitialSyncPhase.Uploading, cancellationToken).ConfigureAwait(false);
        }
        return (pushed, null, false);
    }

    private async Task<(int Pushed, string? Blocked)> PushBudgetedPendingAsync(
        (IReadOnlyList<PersonalCloudK1OutboxEntry> Items, bool PauseAfter, long? OversizedUnits) prefix,
        string accessToken, CancellationToken cancellationToken)
    {
        var pushed = 0;
        string? deferredBlocked = null;
        for (var parked = 0; pushed + parked < PushBatchLimit; parked++)
        {
            var items = prefix.Items.Take(PushBatchLimit - pushed - parked).ToArray();
            var result = await PushPendingAsync(items, accessToken, cancellationToken).ConfigureAwait(false);
            pushed += result.Pushed;
            deferredBlocked ??= result.Blocked;
            if (!result.Parked)
            {
                if (result.Blocked is null && items.Length == prefix.Items.Count && prefix.PauseAfter && PersonalCloudDispatchHttp.PolicyFor(http) is { } dispatch)
                    return (pushed, deferredBlocked ?? (await dispatch.PauseBudgetAsync(connection.ServiceOrigin, prefix.OversizedUnits, cancellationToken).ConfigureAwait(false)).Code);
                return (pushed, result.Blocked ?? deferredBlocked);
            }

            // Only the validated accepted prefix was acknowledged. Re-read the
            // rewritten suffix before sending anything at the rejected sequence.
            var pending = await store.GetPendingPersonalCloudOutboxAsync(connection.InstanceId, connection.DeviceId, cancellationToken).ConfigureAwait(false);
            prefix = await SelectBudgetPrefixAsync(pending, cancellationToken).ConfigureAwait(false);
        }
        return (pushed, deferredBlocked);
    }

    internal async Task DiscoverCapabilitiesAsync(CancellationToken cancellationToken)
    {
        if (_batchCapabilityKnown) return;
        _supportsWorkspace = await store.SupportsPersonalCloudWorkspaceAsync(connection.InstanceId, cancellationToken).ConfigureAwait(false);
        var catalogProtocol = await store.GetPersonalCloudProviderCatalogProtocolVersionAsync(connection.InstanceId, cancellationToken).ConfigureAwait(false);
        _supportsProviderScopes = catalogProtocol == 2;
        _batchCapability = null;
        _supportsWorkspacePaging = false;
        _supportsFanoutBudget = false;
        _supportsCatchupStatus = false;
        _supportsProviderObservedReopen = false;
        _supportsProviderCommandCancellation = false;
        _supportsHistoricalCompletionImport = false;
        _cloudArtifactVersion = null;
        await store.ReconcileUndeliverableProviderCommandsAsync(connection.InstanceId, connection.DeviceId, cancellationToken).ConfigureAwait(false);
        try
        {
            var health = await new PersonalCloudHealthClient(http).InspectAsync(connection.ServiceOrigin, cancellationToken).ConfigureAwait(false);
            if (_supportsWorkspace && !health.SupportsWorkspace || catalogProtocol > health.ProviderCatalogProtocolVersion)
                throw new PersonalCloudK0ContractException("health_mismatch", "Personal Cloud no longer supports the previously negotiated workspace capabilities. Pending edits are preserved.");
            _batchCapability = health.MutationBatch;
            _supportsWorkspace = health.SupportsWorkspace;
            _supportsProviderScopes = health.ProviderCatalogProtocolVersion == 2;
            _supportsWorkspacePaging = health.SupportsWorkspacePaging;
            _supportsFanoutBudget = health.SupportsFanoutBudget;
            _supportsCatchupStatus = health.SupportsCatchupStatus;
            _supportsProviderObservedReopen = health.SupportsProviderObservedReopen;
            _supportsProviderCommandCancellation = health.SupportsProviderCommandCancellation;
            _supportsHistoricalCompletionImport = health.SupportsHistoricalCompletionImport;
            _cloudArtifactVersion = health.ArtifactVersion;
            await store.SavePersonalCloudWorkspaceCapabilitiesAsync(connection.InstanceId, health.SupportsWorkspace, health.SupportsProviderCatalog, cancellationToken, health.ProviderCatalogProtocolVersion).ConfigureAwait(false);
            if (_supportsHistoricalCompletionImport)
                await store.PrepareDeferredHistoricalCompletionImportsAsync(connection.InstanceId, connection.DeviceId,
                    health.ArtifactVersion, cancellationToken).ConfigureAwait(false);
            if (_supportsProviderObservedReopen)
                await store.PrepareProviderObservedReopensAsync(connection.InstanceId, connection.DeviceId,
                    ProviderObservedReopenCapabilityKey(), cancellationToken).ConfigureAwait(false);
            if (_supportsProviderCommandCancellation)
                await store.PrepareProviderCommandCancellationsAsync(connection.InstanceId, connection.DeviceId,
                    health.ArtifactVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (PersonalCloudK0ContractException exception) when (exception.Code == "health_unsupported" && !_supportsWorkspace && catalogProtocol == 0)
        {
            await store.SavePersonalCloudWorkspaceCapabilitiesAsync(connection.InstanceId, false, false, cancellationToken).ConfigureAwait(false);
        }
        _batchCapabilityKnown = true;
    }

    private static bool IsWorkspaceOperation(string operation) => operation is "space.put" or "project.put" or "project.delete" or "task.createRich" or "task.patch" or "task.moveTree" || operation.StartsWith("provider.", StringComparison.Ordinal);

    private string ProviderObservedReopenCapabilityKey() =>
        $"{(_supportsProviderObservedReopen ? "supported" : "unsupported")}:{_cloudArtifactVersion ?? "unknown"}";

    private static bool IsScopedProviderOperation(PersonalCloudK1OutboxEntry entry)
    {
        if (entry.Operation == "provider.scope.put") return true;
        if (entry.Operation is not ("provider.catalog.put" or "provider.intent.request")) return false;
        using var document = JsonDocument.Parse(entry.CanonicalJson); var payload = document.RootElement.GetProperty("payload");
        return entry.Operation == "provider.catalog.put" ? payload.GetProperty("item").TryGetProperty("scopeId", out _) :
            payload.GetProperty("intent").GetProperty("route") is { ValueKind: JsonValueKind.Object } route && route.TryGetProperty("scopeId", out _);
    }

    private (string Json, IReadOnlyList<PersonalCloudK1OutboxEntry> Items)? BuildBatch(IReadOnlyList<PersonalCloudK1OutboxEntry> pending, int start)
    {
        if (_batchCapability is not { } capability) return null;
        if (pending[start].MutationId == _singleAttemptMutationId) return null;
        if (pending[start].Operation is PersonalCloudK0Protocol.ProviderObservedReopenOperation or
            PersonalCloudK0Protocol.ExternalCommandCancelOperation or PersonalCloudK0Protocol.TaskHistoricalCompletionImportOperation) return null;
        if (PersonalCloudDispatchHttp.PolicyFor(http) is not null && PersonalCloudMutationCost.IsFanout(pending[start].Operation))
            return null; // A failed route probe must not double-reserve a fanout attempt.
        var items = new List<PersonalCloudK1OutboxEntry>();
        string? json = null;
        for (var index = start; index < pending.Count && items.Count < capability.MaxMutations; index++)
        {
            if (!_supportsWorkspace && IsWorkspaceOperation(pending[index].Operation)) break;
            if (pending[index].Operation is PersonalCloudK0Protocol.ProviderObservedReopenOperation or
                PersonalCloudK0Protocol.ExternalCommandCancelOperation or PersonalCloudK0Protocol.TaskHistoricalCompletionImportOperation) break;
            if (items.Count > 0 && PersonalCloudMutationCost.IsFanout(pending[index].Operation)) break;
            if (!_supportsProviderScopes && IsScopedProviderOperation(pending[index])) break;
            if (items.Count > 0 && pending[index].DeviceSequence != items[^1].DeviceSequence + 1) break;
            items.Add(pending[index]);
            var candidate = "{\"protocolVersion\":1,\"mutations\":[" + string.Join(',', items.Select(item => item.CanonicalJson)) + "]}";
            if (Encoding.UTF8.GetByteCount(candidate) > capability.MaxRequestUtf8Bytes)
            {
                items.RemoveAt(items.Count - 1);
                break;
            }
            json = candidate;
        }
        return json is null ? null : (json, items);
    }

    private (IReadOnlyList<string> Receipts, string? ErrorCode, bool Retryable) ValidateBatchResponse(string json, IReadOnlyList<PersonalCloudK1OutboxEntry> submitted)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            RequireKeys(root, "protocolVersion", "product", "instanceId", "receipts", "processedCount", "stoppedAt", "error");
            if (root.GetProperty("protocolVersion").GetInt32() != 1 || root.GetProperty("product").GetString() != "tasks" ||
                root.GetProperty("instanceId").GetString() != connection.InstanceId)
                throw InvalidBatch();
            var receipts = root.GetProperty("receipts");
            if (receipts.ValueKind != JsonValueKind.Array) throw InvalidBatch();
            var processed = root.GetProperty("processedCount").GetInt32();
            if (processed < 0 || processed > submitted.Count || receipts.GetArrayLength() != processed) throw InvalidBatch();
            var error = root.GetProperty("error");
            var stopped = root.GetProperty("stoppedAt");
            string? errorCode = null;
            var retryable = false;
            if (error.ValueKind == JsonValueKind.Null)
            {
                if (stopped.ValueKind != JsonValueKind.Null || processed != submitted.Count) throw InvalidBatch();
            }
            else
            {
                var isQuota = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var codeValue) &&
                    codeValue.ValueKind == JsonValueKind.String && codeValue.GetString() == "daily_quota_exceeded";
                if (isQuota)
                {
                    RequireKeys(error, "code", "message", "retryable", "requestId", "quota");
                    if (!PersonalCloudQuotaResponse.TryReadError(error, PersonalCloudQuotaResponse.ReferenceTime(_responseDate,
                        PersonalCloudDispatchHttp.PolicyFor(http)?.UtcNow ?? DateTimeOffset.UtcNow), out _)) throw InvalidBatch();
                }
                else RequireKeys(error, "code", "message", "retryable", "requestId");
                if (processed >= submitted.Count || stopped.GetInt32() != processed ||
                    error.GetProperty("code").ValueKind != JsonValueKind.String ||
                    error.GetProperty("message").ValueKind != JsonValueKind.String ||
                    error.GetProperty("requestId").ValueKind != JsonValueKind.String ||
                    error.GetProperty("retryable").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw InvalidBatch();
                errorCode = error.GetProperty("code").GetString();
                retryable = error.GetProperty("retryable").GetBoolean();
                if (errorCode == "fanout_bound_exceeded" && (!retryable ||
                    !_supportsFanoutBudget || !PersonalCloudMutationCost.IsFanout(submitted[processed].Operation)))
                    throw InvalidBatch();
                if (string.IsNullOrWhiteSpace(errorCode) || errorCode.Length > 128) throw InvalidBatch();
            }
            var result = new List<string>();
            foreach (var receipt in receipts.EnumerateArray())
            {
                var serialized = receipt.GetRawText();
                SqliteTaskStore.ValidatePersonalCloudMutationReceipt(submitted[result.Count], serialized);
                result.Add(serialized);
            }
            return (result, errorCode, retryable); // Validate the entire returned prefix before ACKing its first item.
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw InvalidBatch(); }
    }

    private static void RequireKeys(JsonElement value, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != keys.Length ||
            keys.Any(key => !value.TryGetProperty(key, out _))) throw InvalidBatch();
    }
    private static PersonalCloudK0ContractException InvalidBatch() => new("invalid_response", "The batch response did not match the submitted ordered intents. Pending edits have been preserved.");
}
