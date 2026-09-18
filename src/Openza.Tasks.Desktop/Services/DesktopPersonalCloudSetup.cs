using System.Diagnostics;
using System.Reflection;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Desktop.Services;

public static class DesktopPersonalCloudSetup
{
    public static IPersonalCloudSetupService? TryCreate(Openza.Tasks.Core.Data.SqliteTaskStore taskStore)
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
            Path.Combine(DesktopDataPaths.DataDirectory, "personal-cloud-provisioning.json"),
            DesktopCredentialStore.Create(DesktopDataPaths.Runtime),
            taskStore,
            Environment.MachineName,
            OpenBrowserAsync);
    }

    private static Task OpenBrowserAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private static string Metadata(Assembly assembly, string key) => assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal))?.Value ?? string.Empty;
}
