using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.PersonalCloud;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudProviderClaim(string ClaimId, string TaskId, long RequestedChangeSeq,
    string DeviceId, string Command, string IntegrationKind, string ExternalIdHash, string State);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudProviderClaimResponse(int ProtocolVersion, string Product, string InstanceId,
    PersonalCloudProviderClaim Claim);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudProviderRetryResponse(int ProtocolVersion, string Product, string InstanceId,
    string RetryId, string TaskId, long RequestedChangeSeq);

public static class PersonalCloudProviderBridgeCodec
{
    public static string ClaimRequest(string instanceId, string deviceId, string claimId, string taskId,
        long requestedChangeSeq, string integrationKind, string externalIdHash) => BindingRequest(
            instanceId, deviceId, claimId, taskId, requestedChangeSeq, integrationKind, externalIdHash, false);

    public static string RetryRequest(string instanceId, string deviceId, string retryId, string taskId,
        long requestedChangeSeq, string integrationKind, string externalIdHash) => BindingRequest(
            instanceId, deviceId, retryId, taskId, requestedChangeSeq, integrationKind, externalIdHash, true);

    private static string BindingRequest(string instanceId, string deviceId, string id, string taskId,
        long requestedChangeSeq, string integrationKind, string externalIdHash, bool retry)
    {
        foreach (var value in new[] { instanceId, deviceId, id, taskId })
            PersonalCloudK0Codec.ValidateIdentifier(value, "delivery identifier");
        if (requestedChangeSeq <= 0 || integrationKind is not ("todoist" or "msToDo" or "github") ||
            externalIdHash.Length != 64 || externalIdHash.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)))
            throw new PersonalCloudK0ContractException("invalid_request", "The provider source binding is invalid.");
        var body = new Dictionary<string, object?>
        {
            ["protocolVersion"] = 1, ["product"] = "tasks", ["instanceId"] = instanceId,
            ["deviceId"] = deviceId, [retry ? "retryId" : "claimId"] = id, ["taskId"] = taskId,
            ["requestedChangeSeq"] = requestedChangeSeq, ["integrationKind"] = integrationKind,
            ["externalIdHash"] = externalIdHash,
        };
        return PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize(body));
    }

    public static string StartRequest(string instanceId, string deviceId, string claimId)
    {
        foreach (var value in new[] { instanceId, deviceId, claimId })
            PersonalCloudK0Codec.ValidateIdentifier(value, "delivery identifier");
        return PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize(new
        { protocolVersion = 1, product = "tasks", instanceId, deviceId, claimId }));
    }

    public static string ResultMutation(string instanceId, string deviceId, string mutationId, long deviceSequence,
        string taskId, string claimId, long requestedChangeSeq, string resolvedAt, string? errorCode)
    {
        foreach (var value in new[] { instanceId, deviceId, mutationId, taskId, claimId })
            PersonalCloudK0Codec.ValidateIdentifier(value, "delivery identifier");
        if (deviceSequence <= 0 || requestedChangeSeq <= 0 ||
            errorCode is not (null or "provider_rejected" or "authentication_required" or "retry_exhausted" or "unsupported"))
            throw new PersonalCloudK0ContractException("invalid_request", "The provider result is invalid.");
        PersonalCloudK0Codec.ValidateTimestamp(resolvedAt, "resolvedAt");
        return PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize(new
        {
            protocolVersion = 1, product = "tasks", instanceId, deviceId, mutationId, deviceSequence,
            operation = errorCode is null ? "externalCommand.confirm" : "externalCommand.reject",
            entityId = taskId, baseEntityVersion = (long?)null,
            payload = new { claimId, requestedChangeSeq, resolvedAt, errorCode }, clientCreatedAt = resolvedAt,
        }));
    }

    public static string SourceMutation(string instanceId, string deviceId, string mutationId, long deviceSequence,
        string taskId, K0SafeSourceDescriptor source, string createdAt)
    {
        PersonalCloudK0Codec.ValidateSafeSource(source);
        foreach (var value in new[] { instanceId, deviceId, mutationId, taskId })
            PersonalCloudK0Codec.ValidateIdentifier(value, "source identifier");
        if (deviceSequence <= 0) throw new PersonalCloudK0ContractException("invalid_request", "Invalid source sequence.");
        PersonalCloudK0Codec.ValidateTimestamp(createdAt, "clientCreatedAt");
        return PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize(new
        {
            protocolVersion = 1, product = "tasks", instanceId, deviceId, mutationId, deviceSequence,
            operation = "source.refreshSnapshot", entityId = taskId, baseEntityVersion = (long?)null,
            payload = new { source }, clientCreatedAt = createdAt,
        }));
    }
}

public sealed partial class PersonalCloudProviderDeliveryClient(HttpClient http, PersonalCloudDeviceIdentityClient identity,
    PersonalCloudConnectionProfile connection)
{
    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        var access = await identity.IssueAccessTokenAsync(connection.ServiceOrigin, cancellationToken).ConfigureAwait(false);
        if (access.InstanceId != connection.InstanceId || access.DeviceId != connection.DeviceId)
            throw new PersonalCloudK0ContractException("unauthorized", "The provider delivery identity has changed.");
        if (!access.Capabilities.Contains("provider_bridge", StringComparer.Ordinal)) return false;
        var health = await new PersonalCloudHealthClient(http).InspectAsync(connection.ServiceOrigin, cancellationToken).ConfigureAwait(false);
        if (health.SchemaVersion < 20)
            throw new PersonalCloudK0ContractException("bridge_upgrade_required", "Upgrade Personal Cloud before provider delivery. Pending requests have been preserved.");
        return true;
    }
    public async Task<PersonalCloudProviderClaim> ClaimAsync(string claimId, string taskId, long sequence,
        K0SafeSourceDescriptor source, CancellationToken cancellationToken = default) => await SendClaimAsync(
            "/v1/provider/commands/claim", PersonalCloudProviderBridgeCodec.ClaimRequest(connection.InstanceId,
                connection.DeviceId, claimId, taskId, sequence, source.IntegrationKind, source.ExternalIdHash),
            claimId, taskId, sequence, source, cancellationToken).ConfigureAwait(false);

    public Task<PersonalCloudProviderClaim> StartAsync(PersonalCloudProviderClaim claim,
        CancellationToken cancellationToken = default) => SendClaimAsync("/v1/provider/commands/start",
        PersonalCloudProviderBridgeCodec.StartRequest(connection.InstanceId, connection.DeviceId, claim.ClaimId),
        claim.ClaimId, claim.TaskId, claim.RequestedChangeSeq, new K0SafeSourceDescriptor
        { IntegrationKind = claim.IntegrationKind, ExternalIdHash = claim.ExternalIdHash, Title = "", RefreshedAt = "" }, cancellationToken);

    private async Task<PersonalCloudProviderClaim> SendClaimAsync(string path, string json, string id, string taskId,
        long sequence, K0SafeSourceDescriptor source, CancellationToken cancellationToken)
    {
        var response = PersonalCloudK0Codec.Deserialize<PersonalCloudProviderClaimResponse>(
            await SendAsync(path, json, cancellationToken).ConfigureAwait(false));
        var claim = response.Claim;
        if (response.ProtocolVersion != 1 || response.Product != "tasks" || response.InstanceId != connection.InstanceId ||
            claim is null || claim.ClaimId != id || claim.TaskId != taskId || claim.RequestedChangeSeq != sequence ||
            claim.DeviceId != connection.DeviceId || claim.IntegrationKind != source.IntegrationKind ||
            claim.ExternalIdHash != source.ExternalIdHash || claim.Command is not ("complete" or "reopen") ||
            claim.State is not ("reserved" or "started" or "resolved" or "cancelled"))
            throw new PersonalCloudK0ContractException("invalid_request", "The delivery receipt does not match this attempt.");
        return claim;
    }

    public async Task<long> RetryAsync(string retryId, string taskId, long sequence, K0SafeSourceDescriptor source,
        CancellationToken cancellationToken = default)
    {
        var response = PersonalCloudK0Codec.Deserialize<PersonalCloudProviderRetryResponse>(await SendAsync(
            "/v1/provider/commands/retry", PersonalCloudProviderBridgeCodec.RetryRequest(connection.InstanceId,
                connection.DeviceId, retryId, taskId, sequence, source.IntegrationKind, source.ExternalIdHash), cancellationToken).ConfigureAwait(false));
        if (response.ProtocolVersion != 1 || response.Product != "tasks" || response.InstanceId != connection.InstanceId ||
            response.RetryId != retryId || response.TaskId != taskId || response.RequestedChangeSeq <= sequence)
            throw new PersonalCloudK0ContractException("invalid_request", "The retry receipt does not match this request.");
        return response.RequestedChangeSeq;
    }

    private async Task<string> SendAsync(string path, string json, CancellationToken cancellationToken)
    {
        var access = await identity.IssueAccessTokenAsync(connection.ServiceOrigin, cancellationToken).ConfigureAwait(false);
        if (access.InstanceId != connection.InstanceId || access.DeviceId != connection.DeviceId)
            throw new PersonalCloudK0ContractException("unauthorized", "The provider delivery identity has changed.");
        if (!access.Capabilities.Contains("provider_bridge", StringComparer.Ordinal))
            throw new PersonalCloudK0ContractException("unauthorized", "This desktop is not paired for provider delivery.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(connection.ServiceOrigin, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessToken);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await PersonalCloudDispatchHttp.SendAsync(http, request, cancellationToken).ConfigureAwait(false);
        const int limit = 64 * 1024;
        if (response.Content.Headers.ContentLength > limit)
            throw new PersonalCloudK0ContractException("invalid_request", "Oversized delivery receipt.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > limit) throw new PersonalCloudK0ContractException("invalid_request", "Oversized delivery receipt.");
            output.Write(buffer, 0, count);
        }
        var body = Encoding.UTF8.GetString(output.ToArray());
        if (!response.IsSuccessStatusCode)
        {
            var code = "delivery_failed";
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                await identity.InvalidateAccessTokenAsync(connection.ServiceOrigin, CancellationToken.None).ConfigureAwait(false);
                code = "unauthorized";
            }
            else
            {
                try
                {
                    using var document = JsonDocument.Parse(body);
                    var returned = document.RootElement.GetProperty("error").GetProperty("code").GetString();
                    if (returned is "delivery_busy" or "command_superseded" or "source_identity_conflict" or "invalid_transition") code = returned;
                }
                catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException) { }
            }
            throw new PersonalCloudK0ContractException(code, "Provider delivery could not proceed. Its durable attempt has been preserved.");
        }
        _ = PersonalCloudK0Codec.Canonicalize(body);
        return body;
    }
}
