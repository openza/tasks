using System.Security.Cryptography;
using System.Text;
using Microsoft.Identity.Client;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Desktop.Services.Continuity;

public static class WindowsCredentialContinuity
{
    public static async Task TransferCloudAsync(PersonalCloudConnectionProfile profile, ICredentialStore source,
        ICredentialStore target, CancellationToken cancellationToken)
    {
        // Same exact-origin key contract as PersonalCloudDeviceIdentityClient. The
        // profile and replica tuple are validated from package-local files first.
        var key = "personal-cloud-device-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(profile.ServiceOrigin.AbsoluteUri)));
        var raw = await source.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (raw is null) throw new WindowsContinuityException("The saved Personal Cloud device credential is missing. Recover the existing pairing before continuing; task data and pending operations were retained.");
        await ValidateCloudAsync(profile, new SingleCredential(key, raw), cancellationToken).ConfigureAwait(false);
        var existing = await target.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (existing is not null && !string.Equals(existing, raw, StringComparison.Ordinal))
            throw new WindowsContinuityException("The destination has a different Personal Cloud credential. Resolve the pairing conflict before continuing; neither credential was removed.");
        if (existing is null) await target.SaveAsync(key, raw, cancellationToken).ConfigureAwait(false);
        await ValidateCloudAsync(profile, target, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(raw, await target.GetAsync(key, cancellationToken).ConfigureAwait(false), StringComparison.Ordinal))
            throw new WindowsContinuityException("Personal Cloud credential verification failed. The original pairing was retained.");
    }

    public static async Task ValidateCloudAsync(PersonalCloudConnectionProfile profile, ICredentialStore store, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient(new NoNetwork());
            var device = await new PersonalCloudDeviceIdentityClient(http, store).GetStoredDeviceAsync(profile.ServiceOrigin, cancellationToken).ConfigureAwait(false);
            if (device.InstanceId != profile.InstanceId || device.DeviceId != profile.DeviceId || device.ServiceOrigin != profile.ServiceOrigin)
                throw new WindowsContinuityException("Personal Cloud connection and device identity do not match. Recover the existing pairing before continuing.");
        }
        catch (PersonalCloudK0ContractException)
        { throw new WindowsContinuityException("The saved Personal Cloud credential is invalid. Recover the existing pairing before continuing."); }
    }

    public static async Task<IReadOnlyList<string>> TransferMicrosoftAsync(byte[]? protectedCache,
        DesktopPreferences preferences, string clientId, string tenantId, Func<byte[], byte[]> unprotect,
        ICredentialStore target, CancellationToken cancellationToken)
    {
        var notices = new List<string>();
        byte[]? cache = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (protectedCache is not null)
            {
                try { cache = unprotect(protectedCache); }
                catch (CryptographicException) { /* Explicit reconnect below; never delete the protected source. */ }
            }
            foreach (var (feature, account) in new[] { ("todo", preferences.MicrosoftToDoAccount), ("backup", preferences.OneDriveAccount) })
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (account is null) continue;
                var key = $"microsoft-{feature}-msal-cache";
                var existing = await target.GetAsync(key, cancellationToken).ConfigureAwait(false);
                var candidate = existing is null ? cache : DecodeCache(existing);
                var prepared = candidate is null ? null : await PrepareAccountCacheAsync(candidate, account, clientId, tenantId).ConfigureAwait(false);
                if (prepared is null)
                {
                    notices.Add(feature == "todo" ? "Microsoft To Do cache was not imported. Sign in again if requested; its original cache and tasks were retained."
                        : "OneDrive cache was not imported. Sign in again if requested; its original cache and backup settings were retained.");
                    if (existing is not null && candidate is not null) CryptographicOperations.ZeroMemory(candidate);
                    continue;
                }
                try
                {
                    if (existing is null)
                    {
                        var encoded = Convert.ToBase64String(prepared);
                        await target.SaveAsync(key, encoded, cancellationToken).ConfigureAwait(false);
                        if (!string.Equals(encoded, await target.GetAsync(key, cancellationToken).ConfigureAwait(false), StringComparison.Ordinal))
                            throw new WindowsContinuityException("Microsoft cache storage could not be verified. The original cache was retained.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(prepared);
                    if (existing is not null && candidate is not null) CryptographicOperations.ZeroMemory(candidate);
                }
            }
        }
        finally { if (cache is not null) CryptographicOperations.ZeroMemory(cache); }
        return notices;
    }

    private static byte[]? DecodeCache(string encoded)
    {
        try { return Convert.FromBase64String(encoded); }
        catch (FormatException) { return null; }
    }

    private static async Task<byte[]?> PrepareAccountCacheAsync(byte[] cache, MicrosoftAccount account, string clientId, string tenantId)
    {
        byte[]? serialized = null;
        var returned = false;
        try
        {
            using var transport = new OfflineHttpFactory();
            var app = PublicClientApplicationBuilder.Create(clientId).WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
                .WithRedirectUri("http://localhost").WithHttpClientFactory(transport).Build();
            var loaded = false;
            app.UserTokenCache.SetBeforeAccess(args =>
            {
                if (!loaded) { args.TokenCache.DeserializeMsalV3(cache, shouldClearExistingCache: true); loaded = true; }
            });
            app.UserTokenCache.SetAfterAccess(args =>
            {
                if (serialized is not null) CryptographicOperations.ZeroMemory(serialized);
                serialized = args.TokenCache.SerializeMsalV3();
            });
            // MSAL selects accounts available to this application/client. Do not
            // depend on undocumented cache JSON fields or transfer other accounts.
            var accounts = (await app.GetAccountsAsync().ConfigureAwait(false)).ToArray();
            if (!accounts.Any(value => value.HomeAccountId.Identifier == account.HomeAccountId)) return null;
            foreach (var other in accounts.Where(value => value.HomeAccountId.Identifier != account.HomeAccountId))
                await app.RemoveAsync(other).ConfigureAwait(false);
            returned = true;
            return serialized;
        }
        catch (Exception exception) when (exception is MsalException or ArgumentException or InvalidOperationException or FormatException)
        { return null; }
        finally { if (!returned && serialized is not null) CryptographicOperations.ZeroMemory(serialized); }
    }

    private sealed class OfflineHttpFactory : IMsalHttpClientFactory, IDisposable
    {
        private readonly HttpClient _http = new(new NoNetwork());
        public HttpClient GetHttpClient() => _http;
        public void Dispose() => _http.Dispose();
    }

    private sealed class SingleCredential(string key, string value) : ICredentialStore
    {
        public Task<string?> GetAsync(string requested, CancellationToken cancellationToken = default) =>
            Task.FromResult(requested == key ? value : null);
        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Continuity validation never contacts a server.");
    }
}
