using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Openza.Tasks.Core.Credentials;

namespace Openza.Tasks.Services;

public sealed class MicrosoftToDoAuthService(
    ICredentialStore credentials,
    string cachePath,
    Func<IntPtr>? parentWindowHandle = null)
{
    public const string AccessTokenKey = "mstodo.accessToken";
    private const string AccountKey = "mstodo.account";
    private static readonly string[] Scopes =
    [
        "https://graph.microsoft.com/MailboxSettings.Read",
        "https://graph.microsoft.com/Tasks.Read",
        "https://graph.microsoft.com/Tasks.ReadWrite",
        "https://graph.microsoft.com/User.Read",
        "offline_access",
    ];

    private readonly object _cacheLock = new();

    public static string ResolveDefaultClientId()
    {
        var fromEnvironment =
            Environment.GetEnvironmentVariable("OPENZA_TASKS_MSTODO_CLIENT_ID") ??
            Environment.GetEnvironmentVariable("VITE_MSTODO_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        return Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "MicrosoftToDoClientId")
            ?.Value ?? string.Empty;
    }

    public static string ResolveDefaultTenantId()
    {
        var fromEnvironment =
            Environment.GetEnvironmentVariable("OPENZA_TASKS_MSTODO_TENANT_ID") ??
            Environment.GetEnvironmentVariable("VITE_MSTODO_TENANT_ID");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        return Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "MicrosoftToDoTenantId")
            ?.Value ?? "common";
    }

    public async Task<MicrosoftToDoAuthResult> SignInAsync(string clientId, string tenantId, CancellationToken cancellationToken = default)
    {
        var app = CreateClient(clientId, tenantId);
        var result = await app.AcquireTokenInteractive(Scopes)
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(true);

        await StoreResultAsync(result, cancellationToken).ConfigureAwait(false);
        return ToAuthResult(result);
    }

    public async Task<string?> GetAccessTokenAsync(string clientId, string tenantId, bool interactiveIfNeeded, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return await credentials.GetAsync(AccessTokenKey, cancellationToken).ConfigureAwait(false);
        }

        var app = CreateClient(clientId, tenantId);
        var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault();
        if (account is not null)
        {
            try
            {
                var silent = await app.AcquireTokenSilent(Scopes, account)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                await StoreResultAsync(silent, cancellationToken).ConfigureAwait(false);
                return silent.AccessToken;
            }
            catch (MsalUiRequiredException) when (!interactiveIfNeeded)
            {
                return await credentials.GetAsync(AccessTokenKey, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!interactiveIfNeeded)
        {
            try
            {
                var silent = await app.AcquireTokenSilent(Scopes, PublicClientApplication.OperatingSystemAccount)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                await StoreResultAsync(silent, cancellationToken).ConfigureAwait(false);
                return silent.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                // Expected when WAM cannot satisfy the request silently.
            }
            catch (MsalClientException)
            {
                // Broker support varies by Windows account state; fall back to stored token if present.
            }

            return await credentials.GetAsync(AccessTokenKey, cancellationToken).ConfigureAwait(false);
        }

        var interactive = await SignInAsync(clientId, tenantId, cancellationToken).ConfigureAwait(true);
        return interactive.AccessToken;
    }

    public async Task<bool> IsConnectedAsync(string clientId, string tenantId, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            var app = CreateClient(clientId, tenantId);
            if ((await app.GetAccountsAsync().ConfigureAwait(false)).Any())
            {
                return true;
            }
        }

        return !string.IsNullOrWhiteSpace(await credentials.GetAsync(AccessTokenKey, cancellationToken).ConfigureAwait(false));
    }

    public async Task SignOutAsync(string clientId, string tenantId, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            var app = CreateClient(clientId, tenantId);
            foreach (var account in await app.GetAccountsAsync().ConfigureAwait(false))
            {
                await app.RemoveAsync(account).ConfigureAwait(false);
            }
        }

        await credentials.RemoveAsync(AccessTokenKey, cancellationToken).ConfigureAwait(false);
        await credentials.RemoveAsync(AccountKey, cancellationToken).ConfigureAwait(false);
        if (File.Exists(cachePath))
        {
            File.Delete(cachePath);
        }
    }

    private IPublicClientApplication CreateClient(string clientId, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("Microsoft To Do client ID is required.");
        }

        var brokerOptions = new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
        {
            Title = "Openza Tasks",
            ListOperatingSystemAccounts = true,
        };

        var builder = PublicClientApplicationBuilder
            .Create(clientId.Trim())
            .WithAuthority(AzureCloudInstance.AzurePublic, string.IsNullOrWhiteSpace(tenantId) ? "common" : tenantId.Trim())
            .WithDefaultRedirectUri()
            .WithBroker(brokerOptions);

        if (parentWindowHandle is not null)
        {
            builder = builder.WithParentActivityOrWindow(parentWindowHandle);
        }

        var app = builder.Build();

        ConfigureTokenCache(app.UserTokenCache);
        return app;
    }

    private void ConfigureTokenCache(ITokenCache tokenCache)
    {
        tokenCache.SetBeforeAccess(args =>
        {
            lock (_cacheLock)
            {
                if (!File.Exists(cachePath))
                {
                    return;
                }

                var protectedBytes = File.ReadAllBytes(cachePath);
                var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                args.TokenCache.DeserializeMsalV3(bytes);
            }
        });

        tokenCache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged)
            {
                return;
            }

            lock (_cacheLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath) ?? ".");
                var bytes = args.TokenCache.SerializeMsalV3();
                var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(cachePath, protectedBytes);
            }
        });
    }

    private async Task StoreResultAsync(AuthenticationResult result, CancellationToken cancellationToken)
    {
        await credentials.SaveAsync(AccessTokenKey, result.AccessToken, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.Account?.Username))
        {
            await credentials.SaveAsync(AccountKey, result.Account.Username, cancellationToken).ConfigureAwait(false);
        }
    }

    private static MicrosoftToDoAuthResult ToAuthResult(AuthenticationResult result) =>
        new(result.AccessToken, result.Account?.Username, result.ExpiresOn);
}

public sealed record MicrosoftToDoAuthResult(string AccessToken, string? Username, DateTimeOffset ExpiresOn);
