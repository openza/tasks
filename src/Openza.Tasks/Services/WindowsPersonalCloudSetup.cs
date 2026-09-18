using System.Reflection;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Services;

public static class WindowsPersonalCloudSetup
{
    public static IPersonalCloudSetupService? TryCreate(
        string appDataPath,
        ICredentialStore credentials,
        Openza.Tasks.Core.Data.SqliteTaskStore taskStore)
    {
        var assembly = typeof(App).Assembly;
        var clientId = Metadata(assembly, "CloudflareOAuthClientId");
        var scopes = Metadata(assembly, "CloudflareOAuthScopes")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var redirect = Metadata(assembly, "CloudflareOAuthRedirectUri");
        var artifactDirectory = Path.Combine(AppContext.BaseDirectory, "PersonalCloudArtifact");
        if (string.IsNullOrWhiteSpace(clientId) || scopes.Length == 0 || !Uri.TryCreate(redirect, UriKind.Absolute, out var redirectUri) ||
            !File.Exists(Path.Combine(artifactDirectory, "artifact-manifest.json"))) return null;
        return PersonalCloudSetupFactory.Create(
            clientId,
            scopes,
            redirectUri,
            artifactDirectory,
            Path.Combine(appDataPath, "personal-cloud-provisioning.json"),
            credentials,
            taskStore,
            Environment.MachineName,
            OpenBrowserAsync);
    }

    private static async Task OpenBrowserAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await Windows.System.Launcher.LaunchUriAsync(uri))
            throw new PersonalCloudK0ContractException("browser_unavailable", "Openza could not open the secure browser setup.");
    }

    private static string Metadata(Assembly assembly, string key) => assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal))?.Value ?? string.Empty;
}
