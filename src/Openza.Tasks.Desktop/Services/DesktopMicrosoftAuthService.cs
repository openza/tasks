using System.Reflection;
using Microsoft.Identity.Client;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Services;

namespace Openza.Tasks.Desktop.Services;

public sealed record MicrosoftAccount(string HomeAccountId, string Username);
public sealed record MicrosoftAccess(string AccessToken, MicrosoftAccount Account);
public sealed record SignInCode(string Code, string VerificationUrl, string Message);

/// <summary>MSAL device sign-in and silent renewal with feature-isolated Secret Service caches.</summary>
public interface IDesktopMicrosoftAuthService
{
    Task<MicrosoftAccess> ConnectAsync(string feature, Func<SignInCode, Task> showCode, CancellationToken cancellationToken);
    Task<string?> GetTokenAsync(string feature, MicrosoftAccount? account, CancellationToken cancellationToken = default);
    Task DisconnectAsync(string feature);
}

public sealed class DesktopMicrosoftAuthService(ICredentialStore credentials) : IDesktopMicrosoftAuthService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public static IReadOnlyList<string> ToDoScopes { get; } =
    ["https://graph.microsoft.com/MailboxSettings.Read", "https://graph.microsoft.com/Tasks.Read", "https://graph.microsoft.com/Tasks.ReadWrite", "https://graph.microsoft.com/User.Read", "offline_access"];

    private static string Configuration(string key, string fallback, params string[] environmentNames)
    {
        foreach (var name in environmentNames)
            if (Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        return typeof(DesktopMicrosoftAuthService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)?.Value ?? fallback;
    }

    public static string ClientId => Configuration("MicrosoftGraphClientId", "", "OPENZA_TASKS_MS_GRAPH_CLIENT_ID", "OPENZA_TASKS_MSTODO_CLIENT_ID", "VITE_MSTODO_CLIENT_ID");
    private static string TenantId => Configuration("MicrosoftGraphTenantId", "common", "OPENZA_TASKS_MS_GRAPH_TENANT_ID", "OPENZA_TASKS_MSTODO_TENANT_ID", "VITE_MSTODO_TENANT_ID");

    private IPublicClientApplication CreateClient(string feature)
    {
        if (string.IsNullOrWhiteSpace(ClientId)) throw new InvalidOperationException("Microsoft sign-in is not configured for this build (Microsoft Graph client ID is missing).");
        var app = PublicClientApplicationBuilder.Create(ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, TenantId).WithRedirectUri("http://localhost").Build();
        var key = $"microsoft-{feature}-msal-cache";
        app.UserTokenCache.SetBeforeAccessAsync(async args =>
        {
            var cache = await credentials.GetAsync(key).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(cache)) args.TokenCache.DeserializeMsalV3(Convert.FromBase64String(cache));
        });
        app.UserTokenCache.SetAfterAccessAsync(async args =>
        {
            if (args.HasStateChanged)
                await credentials.SaveAsync(key, Convert.ToBase64String(args.TokenCache.SerializeMsalV3())).ConfigureAwait(false);
        });
        return app;
    }

    public async Task<MicrosoftAccess> ConnectAsync(string feature, Func<SignInCode, Task> showCode, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await CreateClient(feature).AcquireTokenWithDeviceCode(Scopes(feature),
                code => showCode(new SignInCode(code.UserCode, code.VerificationUrl, code.Message)))
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return new MicrosoftAccess(result.AccessToken, new MicrosoftAccount(result.Account.HomeAccountId.Identifier, result.Account.Username));
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> GetTokenAsync(string feature, MicrosoftAccount? account, CancellationToken cancellationToken = default)
    {
        if (account is null) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var app = CreateClient(feature);
            var saved = (await app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault(item => item.HomeAccountId.Identifier == account.HomeAccountId);
            if (saved is null) throw new InvalidOperationException("Sign in to your Microsoft account again.");
            try { return (await app.AcquireTokenSilent(Scopes(feature), saved).ExecuteAsync(cancellationToken).ConfigureAwait(false)).AccessToken; }
            catch (MsalUiRequiredException) { throw new InvalidOperationException("Sign in to your Microsoft account again."); }
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(string feature)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await credentials.RemoveAsync($"microsoft-{feature}-msal-cache").ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private static IEnumerable<string> Scopes(string feature) => feature == "todo" ? ToDoScopes : OneDriveBackupProvider.Scopes;
}
