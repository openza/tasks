using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Openza.Tasks.Core.Services;

namespace Openza.Tasks.Services;

public enum MicrosoftGraphFeature
{
    MicrosoftToDo,
    OneDriveBackup,
}

public sealed class MicrosoftGraphAccountState
{
    public string HomeAccountId { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public DateTimeOffset? ConnectedAt { get; set; }

    public string LastAuthStatus { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsConnected => !string.IsNullOrWhiteSpace(HomeAccountId);

    public void Clear()
    {
        HomeAccountId = string.Empty;
        Username = string.Empty;
        ConnectedAt = null;
        LastAuthStatus = string.Empty;
    }
}

public sealed class MicrosoftGraphAuthService(
    string cachePath,
    Func<IntPtr>? parentWindowHandle = null)
{
    private readonly object _cacheLock = new();

    public static IReadOnlyList<string> MicrosoftToDoScopes { get; } =
    [
        "https://graph.microsoft.com/MailboxSettings.Read",
        "https://graph.microsoft.com/Tasks.Read",
        "https://graph.microsoft.com/Tasks.ReadWrite",
        "https://graph.microsoft.com/User.Read",
        "offline_access",
    ];

    public static IReadOnlyList<string> OneDriveBackupScopes => OneDriveBackupProvider.Scopes;

    public static string ResolveDefaultClientId()
    {
        var fromEnvironment =
            Environment.GetEnvironmentVariable("OPENZA_TASKS_MS_GRAPH_CLIENT_ID") ??
            Environment.GetEnvironmentVariable("OPENZA_TASKS_MSTODO_CLIENT_ID") ??
            Environment.GetEnvironmentVariable("VITE_MSTODO_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        return Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "MicrosoftGraphClientId")
            ?.Value ??
            Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "MicrosoftToDoClientId")
                ?.Value ??
            string.Empty;
    }

    public static string ResolveDefaultTenantId()
    {
        var fromEnvironment =
            Environment.GetEnvironmentVariable("OPENZA_TASKS_MS_GRAPH_TENANT_ID") ??
            Environment.GetEnvironmentVariable("OPENZA_TASKS_MSTODO_TENANT_ID") ??
            Environment.GetEnvironmentVariable("VITE_MSTODO_TENANT_ID");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        return Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "MicrosoftGraphTenantId")
            ?.Value ??
            Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "MicrosoftToDoTenantId")
                ?.Value ??
            "common";
    }

    public Task<MicrosoftGraphAuthResult> ConnectAsync(
        MicrosoftGraphFeature feature,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken = default) =>
        AcquireInteractiveTokenAsync(
            feature,
            scopes,
            savedAccount: null,
            forceAccountSelection: true,
            cancellationToken);

    public async Task<MicrosoftGraphAuthResult?> GetAccessTokenAsync(
        MicrosoftGraphFeature feature,
        MicrosoftGraphAccountState accountState,
        IReadOnlyList<string> scopes,
        bool interactiveIfNeeded,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ResolveDefaultClientId()))
        {
            accountState.LastAuthStatus = "Microsoft Graph client ID is not configured.";
            return null;
        }

        if (!accountState.IsConnected)
        {
            if (!interactiveIfNeeded)
            {
                accountState.LastAuthStatus = "Sign in required.";
                return null;
            }

            return await ConnectAsync(feature, scopes, cancellationToken).ConfigureAwait(true);
        }

        var app = CreateClient(useBroker: true);
        var account = await FindAccountAsync(app, accountState).ConfigureAwait(false);
        if (account is not null)
        {
            try
            {
                var silent = await app.AcquireTokenSilent(scopes, account)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                return ToAuthResult(feature, silent, "Connected");
            }
            catch (MsalUiRequiredException)
            {
                if (!interactiveIfNeeded)
                {
                    accountState.LastAuthStatus = "Sign in required.";
                    return null;
                }
            }
            catch (MsalException exception)
            {
                if (!interactiveIfNeeded)
                {
                    accountState.LastAuthStatus = FormatAuthException(exception);
                    return null;
                }
            }
        }
        else if (!interactiveIfNeeded)
        {
            accountState.LastAuthStatus = "Sign in required.";
            return null;
        }

        return await AcquireInteractiveTokenAsync(
                feature,
                scopes,
                accountState,
                forceAccountSelection: false,
                cancellationToken)
            .ConfigureAwait(true);
    }

    public async Task DisconnectAsync(
        MicrosoftGraphAccountState accountState,
        IEnumerable<MicrosoftGraphAccountState> accountsToKeep,
        CancellationToken cancellationToken = default)
    {
        var homeAccountId = accountState.HomeAccountId;
        if (string.IsNullOrWhiteSpace(homeAccountId) ||
            accountsToKeep.Any(account => string.Equals(account.HomeAccountId, homeAccountId, StringComparison.Ordinal)))
        {
            return;
        }

        foreach (var useBroker in new[] { true, false })
        {
            var app = CreateClient(useBroker);
            foreach (var account in await app.GetAccountsAsync().ConfigureAwait(false))
            {
                if (string.Equals(account.HomeAccountId?.Identifier, homeAccountId, StringComparison.Ordinal))
                {
                    await app.RemoveAsync(account).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<MicrosoftGraphAuthResult> AcquireInteractiveTokenAsync(
        MicrosoftGraphFeature feature,
        IReadOnlyList<string> scopes,
        MicrosoftGraphAccountState? savedAccount,
        bool forceAccountSelection,
        CancellationToken cancellationToken)
    {
        AuthenticationResult result;
        try
        {
            result = await AcquireTokenInteractiveAsync(
                    CreateClient(useBroker: true),
                    scopes,
                    savedAccount,
                    forceAccountSelection,
                    cancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (ShouldFallbackFromBroker(exception))
        {
            AppLog.Write($"Microsoft Graph broker sign-in failed for {feature}; falling back to browser. {FormatAuthException(exception)}");
            result = await AcquireTokenInteractiveAsync(
                    CreateClient(useBroker: false),
                    scopes,
                    savedAccount,
                    forceAccountSelection,
                    cancellationToken)
                .ConfigureAwait(true);
        }

        return ToAuthResult(feature, result, "Connected");
    }

    private IPublicClientApplication CreateClient(bool useBroker)
    {
        var clientId = ResolveDefaultClientId();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("Microsoft Graph client ID is required for this build.");
        }

        var builder = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, ResolveDefaultTenantId());

        if (useBroker)
        {
            var brokerOptions = new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
            {
                Title = "Openza Tasks",
                ListOperatingSystemAccounts = true,
            };

            builder = builder
                .WithDefaultRedirectUri()
                .WithBroker(brokerOptions);
        }
        else
        {
            builder = builder.WithRedirectUri("http://localhost");
        }

        if (parentWindowHandle is not null)
        {
            builder = builder.WithParentActivityOrWindow(parentWindowHandle);
        }

        var app = builder.Build();
        ConfigureTokenCache(app.UserTokenCache);
        return app;
    }

    private static async Task<IAccount?> FindAccountAsync(
        IPublicClientApplication app,
        MicrosoftGraphAccountState accountState)
    {
        var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
        return accounts.FirstOrDefault(account =>
            string.Equals(account.HomeAccountId?.Identifier, accountState.HomeAccountId, StringComparison.Ordinal));
    }

    private static async Task<AuthenticationResult> AcquireTokenInteractiveAsync(
        IPublicClientApplication app,
        IReadOnlyList<string> scopes,
        MicrosoftGraphAccountState? savedAccount,
        bool forceAccountSelection,
        CancellationToken cancellationToken)
    {
        var builder = app.AcquireTokenInteractive(scopes);
        if (savedAccount?.IsConnected == true && !forceAccountSelection)
        {
            var account = await FindAccountAsync(app, savedAccount).ConfigureAwait(false);
            if (account is not null)
            {
                builder = builder.WithAccount(account);
            }
            else if (!string.IsNullOrWhiteSpace(savedAccount.Username))
            {
                builder = builder.WithLoginHint(savedAccount.Username);
            }
        }

        if (forceAccountSelection)
        {
            builder = builder.WithPrompt(Prompt.SelectAccount);
        }

        return await builder.ExecuteAsync(cancellationToken).ConfigureAwait(true);
    }

    private static bool ShouldFallbackFromBroker(Exception exception) =>
        exception is MsalClientException ||
        exception.GetType().FullName?.Contains("Interop", StringComparison.OrdinalIgnoreCase) == true ||
        exception.Message.Contains("WAM", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("broker", StringComparison.OrdinalIgnoreCase) ||
        exception.ToString().Contains("3399614476", StringComparison.OrdinalIgnoreCase);

    private static string FormatAuthException(Exception exception)
    {
        if (exception is MsalException msalException)
        {
            return $"{msalException.GetType().Name}: ErrorCode={msalException.ErrorCode}; Message={msalException.Message}";
        }

        return $"{exception.GetType().Name}: {exception.Message}";
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

    private static MicrosoftGraphAuthResult ToAuthResult(
        MicrosoftGraphFeature feature,
        AuthenticationResult result,
        string status)
    {
        var homeAccountId = result.Account?.HomeAccountId?.Identifier;
        if (string.IsNullOrWhiteSpace(homeAccountId))
        {
            throw new InvalidOperationException("Microsoft sign-in completed without an account identifier.");
        }

        var account = new MicrosoftGraphAccountState
        {
            HomeAccountId = homeAccountId,
            Username = result.Account?.Username ?? string.Empty,
            ConnectedAt = DateTimeOffset.Now,
            LastAuthStatus = status,
        };
        AppLog.Write($"Microsoft Graph {feature} authenticated as {account.Username}.");
        return new MicrosoftGraphAuthResult(result.AccessToken, account, result.ExpiresOn);
    }
}

public sealed record MicrosoftGraphAuthResult(
    string AccessToken,
    MicrosoftGraphAccountState Account,
    DateTimeOffset ExpiresOn)
{
    public string? Username => string.IsNullOrWhiteSpace(Account.Username) ? null : Account.Username;
}
