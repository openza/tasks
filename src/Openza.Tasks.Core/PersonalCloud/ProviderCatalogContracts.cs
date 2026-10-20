using System.Net.Http.Headers;
using System.Text.Json;

namespace Openza.Tasks.Core.PersonalCloud;

public sealed partial class PersonalCloudProviderDeliveryClient
{
    public async Task<bool> IsCatalogEnabledAsync(CancellationToken cancellationToken = default)
    {
        var access = await identity.IssueAccessTokenAsync(connection.ServiceOrigin, cancellationToken).ConfigureAwait(false);
        if (access.InstanceId != connection.InstanceId || access.DeviceId != connection.DeviceId)
            throw new PersonalCloudK0ContractException("unauthorized", "The provider catalog identity has changed.");
        if (!access.Capabilities.Contains("provider_bridge", StringComparer.Ordinal)) return false;
        return (await new PersonalCloudHealthClient(http).InspectAsync(connection.ServiceOrigin, cancellationToken).ConfigureAwait(false)).SupportsProviderCatalog;
    }

    public async Task<IReadOnlyList<JsonElement>> GetOwnedIntentsAsync(CancellationToken cancellationToken = default)
    {
        var access = await identity.IssueAccessTokenAsync(connection.ServiceOrigin, cancellationToken).ConfigureAwait(false);
        if (access.InstanceId != connection.InstanceId || access.DeviceId != connection.DeviceId || !access.Capabilities.Contains("provider_bridge", StringComparer.Ordinal))
            throw new PersonalCloudK0ContractException("unauthorized", "Provider catalog intents require this owning bridge.");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(connection.ServiceOrigin, "/v1/provider/intents"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessToken);
        using var response = await PersonalCloudDispatchHttp.SendAsync(http, request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                await identity.InvalidateAccessTokenAsync(connection.ServiceOrigin, CancellationToken.None).ConfigureAwait(false);
            throw new PersonalCloudK0ContractException("sync_failed", "The bridge could not read its intents. Pending actions are preserved.");
        }
        await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _ = PersonalCloudK0Codec.Canonicalize(json);
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        PersonalCloudWorkspaceCodec.Keys(root, "protocolVersion product instanceId serverReadAt intents");
        if (root.GetProperty("protocolVersion").GetInt32() != 1 || root.GetProperty("product").GetString() != "tasks" || root.GetProperty("instanceId").GetString() != connection.InstanceId) throw PersonalCloudWorkspaceCodec.Invalid();
        PersonalCloudK0Codec.ValidateTimestamp(PersonalCloudWorkspaceCodec.Text(root, "serverReadAt")!, "serverReadAt");
        var intents = root.GetProperty("intents");
        if (intents.ValueKind != JsonValueKind.Array || intents.GetArrayLength() > 100) throw PersonalCloudWorkspaceCodec.Invalid();
        var result = new List<JsonElement>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var intent in intents.EnumerateArray())
        {
            PersonalCloudWorkspaceCodec.ValidateRecord("intents", intent);
            if (PersonalCloudWorkspaceCodec.Text(intent, "bridgeDeviceId") != connection.DeviceId ||
                PersonalCloudWorkspaceCodec.Text(intent, "state") is not ("pending_desktop" or "started" or "uncertain") ||
                !ids.Add(PersonalCloudWorkspaceCodec.Text(intent, "id")!)) throw PersonalCloudWorkspaceCodec.Invalid();
            result.Add(intent.Clone());
        }
        return result;
    }
}
