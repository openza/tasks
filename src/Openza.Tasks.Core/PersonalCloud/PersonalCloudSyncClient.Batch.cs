using System.Text;
using System.Text.Json;
using Openza.Tasks.Core.Data;

namespace Openza.Tasks.Core.PersonalCloud;

public sealed partial class PersonalCloudSyncClient
{
    private bool _batchCapabilityKnown;
    private PersonalCloudMutationBatchCapability? _batchCapability;
    private bool _supportsWorkspace;
    private bool _supportsProviderScopes;
    private sealed class BatchEndpointUnavailableException : Exception { }

    private async Task<(int Pushed, string? Blocked)> PushPendingAsync(IReadOnlyList<PersonalCloudK1OutboxEntry> pending,
        string accessToken, CancellationToken cancellationToken)
    {
        await DiscoverCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        var pushed = 0;
        while (pushed < pending.Count)
        {
            if (!_supportsWorkspace && IsWorkspaceOperation(pending[pushed].Operation)) return (pushed, "upgrade_required");
            if (!_supportsProviderScopes && IsScopedProviderOperation(pending[pushed])) return (pushed, "upgrade_required");
            var batch = BuildBatch(pending, pushed);
            if (batch is { } value)
            {
                string response;
                using var request = CreateAuthorizedRequest(HttpMethod.Post, "/v1/tasks/mutations/batch", accessToken);
                request.Content = new StringContent(value.Json, Encoding.UTF8, "application/json");
                try { response = await SendForJsonAsync(request, cancellationToken, batchRequest: true).ConfigureAwait(false); }
                catch (BatchEndpointUnavailableException)
                {
                    _batchCapability = null;
                    continue; // Retry the exact persisted envelopes via the old endpoint; no new IDs/sequences.
                }
                catch (PersonalCloudK0ContractException exception) when (IsPermanentMutationRejection(exception.Code))
                { return (pushed, exception.Code); }

                var receipt = ValidateBatchResponse(response, value.Items);
                foreach (var json in receipt.Receipts)
                {
                    await store.AcknowledgePersonalCloudMutationAsync(connection.InstanceId, connection.DeviceId, json, cancellationToken).ConfigureAwait(false);
                    pushed++;
                    if (progress is not null) await progress(PersonalCloudInitialSyncPhase.Uploading, cancellationToken).ConfigureAwait(false);
                }
                if (receipt.ErrorCode is { } code)
                {
                    if (IsPermanentMutationRejection(code)) return (pushed, code);
                    throw new PersonalCloudK0ContractException("sync_failed", "The cloud could not finish this batch. Remaining exact intents have been preserved.");
                }
                continue;
            }
            var mutation = pending[pushed];
            using var single = CreateAuthorizedRequest(HttpMethod.Post, "/v1/tasks/mutations", accessToken);
            single.Content = new StringContent(mutation.CanonicalJson, Encoding.UTF8, "application/json");
            string receiptJson;
            try { receiptJson = await SendForJsonAsync(single, cancellationToken).ConfigureAwait(false); }
            catch (PersonalCloudK0ContractException exception) when (IsPermanentMutationRejection(exception.Code))
            { return (pushed, exception.Code); }
            SqliteTaskStore.ValidatePersonalCloudMutationReceipt(mutation, receiptJson);
            await store.AcknowledgePersonalCloudMutationAsync(connection.InstanceId, connection.DeviceId, receiptJson, cancellationToken).ConfigureAwait(false);
            pushed++;
            if (progress is not null) await progress(PersonalCloudInitialSyncPhase.Uploading, cancellationToken).ConfigureAwait(false);
        }
        return (pushed, null);
    }

    internal async Task DiscoverCapabilitiesAsync(CancellationToken cancellationToken)
    {
        if (_batchCapabilityKnown) return;
        _supportsWorkspace = await store.SupportsPersonalCloudWorkspaceAsync(connection.InstanceId, cancellationToken).ConfigureAwait(false);
        _supportsProviderScopes = await store.SupportsPersonalCloudProviderScopesAsync(connection.InstanceId, cancellationToken).ConfigureAwait(false);
        try
        {
            var health = await new PersonalCloudHealthClient(http).InspectAsync(connection.ServiceOrigin, cancellationToken).ConfigureAwait(false);
            _batchCapability = health.MutationBatch;
            _supportsWorkspace = health.SupportsWorkspace;
            _supportsProviderScopes = health.ProviderCatalogProtocolVersion == 2;
            _supportsWorkspacePaging = health.SupportsWorkspacePaging;
            await store.SavePersonalCloudWorkspaceCapabilitiesAsync(connection.InstanceId, health.SupportsWorkspace, health.SupportsProviderCatalog, cancellationToken, health.ProviderCatalogProtocolVersion).ConfigureAwait(false);
        }
        catch (HttpRequestException) { /* Keep durable negotiation during temporary discovery failure. */ }
        catch (PersonalCloudK0ContractException) { /* Old servers without health retain old delivery. */ }
        _batchCapabilityKnown = true;
    }

    private static bool IsWorkspaceOperation(string operation) => operation is "space.put" or "project.put" or "project.delete" or "task.createRich" or "task.patch" or "task.moveTree" || operation.StartsWith("provider.", StringComparison.Ordinal);

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
        var items = new List<PersonalCloudK1OutboxEntry>();
        string? json = null;
        for (var index = start; index < pending.Count && items.Count < capability.MaxMutations; index++)
        {
            if (!_supportsWorkspace && IsWorkspaceOperation(pending[index].Operation)) break;
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

    private (IReadOnlyList<string> Receipts, string? ErrorCode) ValidateBatchResponse(string json, IReadOnlyList<PersonalCloudK1OutboxEntry> submitted)
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
            if (error.ValueKind == JsonValueKind.Null)
            {
                if (stopped.ValueKind != JsonValueKind.Null || processed != submitted.Count) throw InvalidBatch();
            }
            else
            {
                RequireKeys(error, "code", "message", "retryable", "requestId");
                if (processed >= submitted.Count || stopped.GetInt32() != processed ||
                    error.GetProperty("code").ValueKind != JsonValueKind.String ||
                    error.GetProperty("message").ValueKind != JsonValueKind.String ||
                    error.GetProperty("requestId").ValueKind != JsonValueKind.String ||
                    error.GetProperty("retryable").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw InvalidBatch();
                errorCode = error.GetProperty("code").GetString();
                if (string.IsNullOrWhiteSpace(errorCode) || errorCode.Length > 128) throw InvalidBatch();
            }
            var result = new List<string>();
            foreach (var receipt in receipts.EnumerateArray())
            {
                var serialized = receipt.GetRawText();
                SqliteTaskStore.ValidatePersonalCloudMutationReceipt(submitted[result.Count], serialized);
                result.Add(serialized);
            }
            return (result, errorCode); // Validate the entire returned prefix before ACKing its first item.
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
