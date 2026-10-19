using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Openza.Tasks.Core.Data;

namespace Openza.Tasks.Core.PersonalCloud;

public sealed record PersonalCloudSyncResult(int PushedMutations, int PulledChanges, long Checkpoint)
{
    public string? BlockedCode { get; init; }
    public PersonalCloudInitialSyncProgress? InitialSyncProgress { get; init; }
}

public sealed partial class PersonalCloudSyncClient(
    HttpClient http,
    PersonalCloudDeviceIdentityClient identity,
    SqliteTaskStore store,
    PersonalCloudConnectionProfile connection,
    Func<PersonalCloudInitialSyncPhase, CancellationToken, Task>? progress = null)
{
    private const int ChangePageLimit = 50;
    private const int PushBatchLimit = 100;
    private const int MaximumChangePages = 100;
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private bool _supportsWorkspacePaging;
    private DateTimeOffset? _responseDate;

    public async Task<PersonalCloudSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        // Negotiation is scoped to this pass, including retries on the same client.
        _batchCapabilityKnown = false;
        var pending = await store.GetPendingPersonalCloudOutboxAsync(connection.InstanceId, connection.DeviceId, cancellationToken).ConfigureAwait(false);
        var prefix = await SelectBudgetPrefixAsync(pending, cancellationToken).ConfigureAwait(false);
        await DiscoverCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        var access = await identity.IssueAccessTokenAsync(connection.ServiceOrigin, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(access.InstanceId, connection.InstanceId, StringComparison.Ordinal) ||
            !string.Equals(access.DeviceId, connection.DeviceId, StringComparison.Ordinal))
        {
            throw new PersonalCloudK0ContractException(
                "unauthorized",
                "The paired desktop identity does not match the saved Personal Cloud connection.");
        }

        var bootstrapAttempts = 0;
        bool ReserveBootstrapAttempt() => ++bootstrapAttempts <= 3;
        if (progress is not null) await progress(PersonalCloudInitialSyncPhase.Uploading, cancellationToken).ConfigureAwait(false);
        var pushed = 0;
        string? blockedCode = null;
        if (prefix.Items.Count > 0)
        {
            (pushed, blockedCode) = await PushPendingAsync(prefix.Items, access.AccessToken, cancellationToken).ConfigureAwait(false);
            if (blockedCode is null && pushed == prefix.Items.Count && prefix.PauseAfter && PersonalCloudDispatchHttp.PolicyFor(http) is { } dispatch)
                throw (await dispatch.PauseBudgetAsync(connection.ServiceOrigin, prefix.OversizedUnits, cancellationToken).ConfigureAwait(false)).ToException();
        }

        if (progress is not null) await progress(PersonalCloudInitialSyncPhase.Downloading, cancellationToken).ConfigureAwait(false);
        if (_supportsWorkspace && blockedCode is null && !await store.HasPersonalCloudWorkspaceBaselineAsync(connection.InstanceId, cancellationToken).ConfigureAwait(false) &&
            (await store.GetPendingPersonalCloudOutboxAsync(connection.InstanceId, connection.DeviceId, cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            await ReplaceWorkspaceBaselineAsync(access.AccessToken, ReserveBootstrapAttempt, cancellationToken).ConfigureAwait(false);
        }
        var checkpoint = await store.GetPersonalCloudCheckpointAsync(
            connection.InstanceId,
            connection.DeviceId,
            cancellationToken).ConfigureAwait(false);
        var pulled = 0;
        var caughtUp = false;
        for (var pageNumber = 0; pageNumber < MaximumChangePages; pageNumber++)
        {
            var path = $"/sync/changes?after={checkpoint.ToString(CultureInfo.InvariantCulture)}&limit={ChangePageLimit}";
            if (_supportsWorkspace) path += "&workspace=1";
            if (_supportsWorkspacePaging) path += "&paged=1";
            using var request = CreateAuthorizedRequest(HttpMethod.Get, path, access.AccessToken);
            string pageJson;
            try { pageJson = await SendForJsonAsync(request, cancellationToken).ConfigureAwait(false); }
            catch (PersonalCloudK0ContractException exception) when (exception.Code == "checkpoint_expired")
            {
                try { await store.EnsureNoPendingPersonalCloudIntentsAsync(cancellationToken).ConfigureAwait(false); }
                catch (PersonalCloudK0ContractException pendingException) when (pendingException.Code == "pending_intents")
                {
                    throw new PersonalCloudK0ContractException("checkpoint_expired_pending",
                        "Cloud history expired while local edits or deferred changes remain unresolved. They have been preserved; resolve them before replacing the replica.");
                }
                if (_supportsWorkspace) checkpoint = await ReplaceWorkspaceBaselineAsync(access.AccessToken, ReserveBootstrapAttempt, cancellationToken).ConfigureAwait(false);
                else
                {
                    using var bootstrapRequest = CreateAuthorizedRequest(HttpMethod.Get, "/sync/bootstrap", access.AccessToken);
                    var bootstrapJson = await SendForJsonAsync(bootstrapRequest, cancellationToken).ConfigureAwait(false);
                    checkpoint = await store.ReplacePersonalCloudReplicaFromBootstrapAsync(connection.InstanceId, connection.DeviceId, bootstrapJson, cancellationToken).ConfigureAwait(false);
                }
                continue;
            }
            var page = PersonalCloudK3Codec.ParseChangePage(pageJson);
            if (_supportsWorkspacePaging && (page.HasMore is null || page.HasMore == true && page.Changes.Count == 0))
                throw new PersonalCloudK0ContractException("invalid_response", "Personal Cloud returned an invalid paged change response.");
            checkpoint = await store.ApplyPersonalCloudChangePageAsync(
                connection.InstanceId,
                connection.DeviceId,
                pageJson,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            pulled = checked(pulled + page.Changes.Count);
            if (page.HasMore is false || page.HasMore is null && page.Changes.Count < ChangePageLimit)
            {
                caughtUp = true;
                break;
            }
        }
        if (!caughtUp)
        {
            throw new PersonalCloudK0ContractException(
                "sync_incomplete",
                "Personal Cloud returned too many change pages for one sync pass.");
        }

        var acknowledgement = PersonalCloudK4Codec.CreateCheckpointAcknowledgement(
            connection.InstanceId,
            connection.DeviceId,
            checkpoint);
        using (var request = CreateAuthorizedRequest(HttpMethod.Post, "/sync/checkpoints", access.AccessToken))
        {
            request.Content = new StringContent(
                PersonalCloudK0Codec.Serialize(acknowledgement),
                Encoding.UTF8,
                "application/json");
            var receiptJson = await SendForJsonAsync(request, cancellationToken).ConfigureAwait(false);
            var receipt = PersonalCloudK4Codec.ParseCheckpointReceipt(receiptJson, connection.DeviceId);
            if (receipt.AcknowledgedCheckpoint != checkpoint)
            {
                throw new PersonalCloudK0ContractException(
                    "sequence_gap",
                    "Personal Cloud acknowledged a different replica checkpoint.");
            }
        }

        if (await store.GetDeferredPersonalCloudChangeCountAsync(connection.InstanceId, cancellationToken).ConfigureAwait(false) > 0)
            blockedCode ??= "pending_changes";
        return new PersonalCloudSyncResult(pushed, pulled, checkpoint) { BlockedCode = blockedCode };
    }

    private HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string path, string accessToken)
    {
        var request = new HttpRequestMessage(method, new Uri(connection.ServiceOrigin, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<string> SendForJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken, bool batchRequest = false)
    {
        using var response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        _responseDate = response.Headers.Date;
        if (batchRequest && response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed or System.Net.HttpStatusCode.NotImplemented)
            throw new BatchEndpointUnavailableException();
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            await identity.InvalidateAccessTokenAsync(connection.ServiceOrigin, CancellationToken.None).ConfigureAwait(false);
            throw new PersonalCloudK0ContractException("unauthorized", "Personal Cloud authorization must be renewed. Pending edits have been preserved.");
        }
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new PersonalCloudK0ContractException("sync_failed", "Personal Cloud returned an oversized response.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length + read > MaximumResponseBytes)
            {
                throw new PersonalCloudK0ContractException("sync_failed", "Personal Cloud returned an oversized response.");
            }
            destination.Write(buffer, 0, read);
        }
        if (destination.Length == 0)
        {
            throw new PersonalCloudK0ContractException("sync_failed", "Personal Cloud returned an empty response.");
        }
        var json = Encoding.UTF8.GetString(destination.GetBuffer(), 0, checked((int)destination.Length));
        if (!response.IsSuccessStatusCode)
        {
            var code = response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? "unauthorized" : "sync_failed";
            if (code != "unauthorized")
            {
                try
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                        error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String &&
                        value.GetString() is { } returnedCode &&
                        (IsPermanentMutationRejection(returnedCode) || returnedCode is "checkpoint_expired" or "upgrade_required" or "bootstrap_changed" or "response_too_large"))
                        code = returnedCode;
                }
                catch (JsonException) { /* Do not expose untrusted response contents. */ }
            }
            throw new PersonalCloudK0ContractException(code, code switch
            {
                "bootstrap_changed" => "Personal Cloud changed while downloading its baseline. Retry Sync to download a consistent snapshot.",
                "response_too_large" => "A Personal Cloud record exceeds the supported response size. The current replica and pending edits have been preserved.",
                _ => "Personal Cloud could not complete the sync request. Pending edits have been preserved.",
            });
        }
        return json;
    }

    private static bool IsPermanentMutationRejection(string code) => code is
        "version_conflict" or "entity_exists" or "entity_not_found" or "invalid_transition" or
        "command_superseded" or "sequence_gap" or "mutation_id_reuse" or "mutation_id_reused" or "invalid_request" or
        "parent_not_found" or "source_not_found" or "command_not_found" or "source_identity_conflict" or "stale_snapshot" or "response_too_large";
}
