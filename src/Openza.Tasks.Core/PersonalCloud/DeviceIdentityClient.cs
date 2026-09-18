using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Openza.Tasks.Core.Credentials;

namespace Openza.Tasks.Core.PersonalCloud;

public sealed record PersonalCloudDeviceAccess(
    string InstanceId,
    string DeviceId,
    string AccessToken,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> Capabilities);

public sealed record PersonalCloudStoredDevice(Uri ServiceOrigin, string InstanceId, string DeviceId);

public sealed class PersonalCloudDeviceIdentityClient(HttpClient http, ICredentialStore credentials)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task<PersonalCloudStoredDevice> PairAsync(
        Uri serviceOrigin,
        string grant,
        string deviceName,
        bool providerBridge,
        CancellationToken cancellationToken = default)
    {
        var origin = NormalizeOrigin(serviceOrigin);
        RequireText(grant, 20, 256, nameof(grant));
        RequireText(deviceName, 1, 80, nameof(deviceName));
        var requested = new PairRequest(grant, deviceName.Trim(),
            providerBridge ? ["desktop_sync", "provider_bridge"] : ["desktop_sync"]);
        using var response = await http.PostAsJsonAsync(new Uri(origin, "/auth/pair"), requested, Json, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadRequiredAsync<PairResponse>(response, cancellationToken).ConfigureAwait(false);
        RequireProtocol(body.ProtocolVersion);
        RequireIdentifier(body.InstanceId, nameof(body.InstanceId));
        RequireIdentifier(body.DeviceId, nameof(body.DeviceId));
        RequireText(body.RefreshCredential, 20, 256, nameof(body.RefreshCredential));
        if (body.RefreshExpiresAt <= DateTimeOffset.UtcNow ||
            !body.Capabilities.Contains("desktop_sync", StringComparer.Ordinal) ||
            body.Capabilities.Any(value => value is not ("desktop_sync" or "provider_bridge")))
            throw Invalid("The pairing response contains invalid device authority.");

        var stored = new StoredCredential(origin.AbsoluteUri, body.InstanceId, body.DeviceId, body.RefreshCredential);
        await credentials.SaveAsync(CredentialKey(origin), JsonSerializer.Serialize(stored, Json), cancellationToken)
            .ConfigureAwait(false);
        return new PersonalCloudStoredDevice(origin, body.InstanceId, body.DeviceId);
    }

    public async Task<PersonalCloudStoredDevice> GetStoredDeviceAsync(
        Uri serviceOrigin,
        CancellationToken cancellationToken = default)
    {
        var origin = NormalizeOrigin(serviceOrigin);
        var stored = await ReadStoredCredentialAsync(origin, cancellationToken).ConfigureAwait(false);
        return new PersonalCloudStoredDevice(origin, stored.InstanceId, stored.DeviceId);
    }

    public async Task<PersonalCloudDeviceAccess> IssueAccessTokenAsync(
        Uri serviceOrigin,
        CancellationToken cancellationToken = default)
    {
        var origin = NormalizeOrigin(serviceOrigin);
        var stored = await ReadStoredCredentialAsync(origin, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(stored.ServiceOrigin, origin.AbsoluteUri, StringComparison.Ordinal))
            throw Invalid("The stored desktop credential belongs to another Personal Cloud origin.");

        using var response = await http.PostAsJsonAsync(new Uri(origin, "/auth/device/token"),
            new TokenRequest(stored.DeviceId, stored.RefreshCredential), Json, cancellationToken).ConfigureAwait(false);
        var body = await ReadRequiredAsync<TokenResponse>(response, cancellationToken).ConfigureAwait(false);
        RequireProtocol(body.ProtocolVersion);
        RequireText(body.AccessToken, 20, 512, nameof(body.AccessToken));
        if (body.ExpiresAt <= DateTimeOffset.UtcNow || !body.Capabilities.Contains("desktop_sync", StringComparer.Ordinal))
            throw Invalid("The access response contains invalid or expired authority.");
        return new PersonalCloudDeviceAccess(stored.InstanceId, stored.DeviceId, body.AccessToken, body.ExpiresAt, body.Capabilities);
    }

    private async Task<StoredCredential> ReadStoredCredentialAsync(Uri origin, CancellationToken cancellationToken)
    {
        var raw = await credentials.GetAsync(CredentialKey(origin), cancellationToken).ConfigureAwait(false)
            ?? throw new PersonalCloudK0ContractException("unauthorized", "This desktop is not paired with Personal Cloud.");
        StoredCredential stored;
        try { stored = JsonSerializer.Deserialize<StoredCredential>(raw, Json) ?? throw Invalid("The stored desktop credential is empty."); }
        catch (JsonException exception) { throw new PersonalCloudK0ContractException("invalid_request", "The stored desktop credential is invalid.", exception); }
        if (!string.Equals(stored.ServiceOrigin, origin.AbsoluteUri, StringComparison.Ordinal))
            throw Invalid("The stored desktop credential belongs to another Personal Cloud origin.");
        RequireIdentifier(stored.InstanceId, nameof(stored.InstanceId));
        RequireIdentifier(stored.DeviceId, nameof(stored.DeviceId));
        RequireText(stored.RefreshCredential, 20, 256, nameof(stored.RefreshCredential));
        return stored;
    }

    public Task ForgetAsync(Uri serviceOrigin, CancellationToken cancellationToken = default) =>
        credentials.RemoveAsync(CredentialKey(NormalizeOrigin(serviceOrigin)), cancellationToken);

    public async Task RevokeAsync(Uri serviceOrigin, CancellationToken cancellationToken = default)
    {
        var origin = NormalizeOrigin(serviceOrigin);
        var access = await IssueAccessTokenAsync(origin, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "/auth/device/revoke"))
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var revoked = await ReadRequiredAsync<RevokeResponse>(response, cancellationToken).ConfigureAwait(false);
        RequireProtocol(revoked.ProtocolVersion);
        RequireIdentifier(revoked.DeviceId, nameof(revoked.DeviceId));
        if (!string.Equals(revoked.DeviceId, access.DeviceId, StringComparison.Ordinal) ||
            revoked.Outcome is not ("revoked" or "already_revoked"))
            throw Invalid("Personal Cloud returned an invalid device revocation response.");
        await ForgetAsync(origin, cancellationToken).ConfigureAwait(false);
    }

    internal static string CredentialKey(Uri serviceOrigin)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(serviceOrigin.AbsoluteUri));
        return $"personal-cloud-device-{Convert.ToHexStringLower(hash)}";
    }

    private static Uri NormalizeOrigin(Uri value)
    {
        if (!value.IsAbsoluteUri || !string.IsNullOrEmpty(value.Query) || !string.IsNullOrEmpty(value.Fragment) ||
            (value.Scheme != Uri.UriSchemeHttps && !(value.Scheme == Uri.UriSchemeHttp && value.IsLoopback)))
            throw Invalid("Personal Cloud must use an HTTPS origin, except for a loopback development server.");
        return new Uri(value.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
    }

    private static async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new PersonalCloudK0ContractException("unauthorized", "Personal Cloud rejected the desktop credential request.");
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false)
                ?? throw Invalid("Personal Cloud returned an empty identity response.");
        }
        catch (JsonException exception)
        {
            throw new PersonalCloudK0ContractException("invalid_request", "Personal Cloud returned an invalid identity response.", exception);
        }
    }

    private static void RequireIdentifier(string value, string field) => PersonalCloudK0Codec.ValidateIdentifier(value, field);
    private static void RequireProtocol(int value)
    {
        if (value != PersonalCloudK0Protocol.Version)
            throw new PersonalCloudK0ContractException("unsupported_protocol", "Personal Cloud returned an unsupported identity protocol version.");
    }
    private static void RequireText(string value, int minimum, int maximum, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < minimum || value.Length > maximum)
            throw Invalid($"{field} is invalid.");
    }
    private static PersonalCloudK0ContractException Invalid(string message) => new("invalid_request", message);

    private sealed record PairRequest(string Grant, string DeviceName, IReadOnlyList<string> Capabilities);
    private sealed record PairResponse(int ProtocolVersion, string InstanceId, string DeviceId, string RefreshCredential,
        IReadOnlyList<string> Capabilities, DateTimeOffset RefreshExpiresAt);
    private sealed record TokenRequest(string DeviceId, string RefreshCredential);
    private sealed record TokenResponse(int ProtocolVersion, string AccessToken, DateTimeOffset ExpiresAt,
        IReadOnlyList<string> Capabilities);
    private sealed record RevokeResponse(int ProtocolVersion, string Outcome, string DeviceId);
    private sealed record StoredCredential(string ServiceOrigin, string InstanceId, string DeviceId, string RefreshCredential);
}
