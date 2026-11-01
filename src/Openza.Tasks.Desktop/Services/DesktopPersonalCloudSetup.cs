using System.Diagnostics;
using System.Reflection;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Desktop.Services.Continuity;

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
        if (string.IsNullOrWhiteSpace(clientId) || scopes.Length == 0 || !Uri.TryCreate(redirect, UriKind.Absolute, out var redirectUri)) return null;
        var service = PersonalCloudSetupFactory.Create(
            clientId,
            scopes,
            redirectUri,
            ArtifactSource(assembly),
            Path.Combine(DesktopDataPaths.DataDirectory, "personal-cloud-provisioning.json"),
            DesktopCredentialStore.Create(DesktopDataPaths.Runtime),
            taskStore,
            Environment.MachineName,
            OpenBrowserAsync,
            WindowsContinuityHost.Session is { } session && new DesktopPreferencesStore(session.SettingsPath).Load().WindowsCloudRecoveryRequired
                ? new WindowsRecoveryConnectionStore(session.Data.ConnectionPath) : null);
        return WindowsContinuityHost.Session is { } active ? active.GuardCloudService(service) : service;
    }

    // Cloud releases ship separately and are installed from signed GitHub releases.
    // A Dev build may bundle a locally built artifact to test unreleased cloud changes.
    private static IPersonalCloudArtifactSource ArtifactSource(Assembly assembly)
    {
#if DEBUG
        var bundled = Path.Combine(AppContext.BaseDirectory, "PersonalCloudArtifact");
        if (Metadata(assembly, "OpenzaChannel") == "Dev" && File.Exists(Path.Combine(bundled, "artifact-manifest.json")))
            return new DirectoryPersonalCloudArtifactSource(bundled);
#endif
        return new GitHubPersonalCloudReleaseSource(new HttpClient { Timeout = TimeSpan.FromMinutes(2) },
            ReleaseFeedOptions(assembly, Environment.GetEnvironmentVariable));
    }

    private static PersonalCloudReleaseFeedOptions ReleaseFeedOptions(Assembly assembly, Func<string, string?> environment)
    {
        var version = assembly.GetName().Version ?? new Version(0, 0, 0);
        var options = new PersonalCloudReleaseFeedOptions
        {
            Repository = PersonalCloudReleaseKeys.Repository,
            PublicKeysPem = [PersonalCloudReleaseKeys.Production],
            DesktopVersion = new Version(version.Major, version.Minor, Math.Max(0, version.Build)),
            CacheDirectory = Path.Combine(DesktopDataPaths.DataDirectory, "personal-cloud-releases"),
        };
#if DEBUG
        if (Metadata(assembly, "OpenzaChannel") == "Dev")
        {
            var apiRoot = environment("OPENZA_TASKS_DEV_CLOUD_RELEASE_API_ROOT");
            var keyFile = environment("OPENZA_TASKS_DEV_CLOUD_RELEASE_PUBLIC_KEY_FILE");
            if (!string.IsNullOrWhiteSpace(apiRoot) && !string.IsNullOrWhiteSpace(keyFile))
            {
                var uri = new Uri(apiRoot, UriKind.Absolute);
                if (!uri.IsLoopback || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    throw new InvalidOperationException("The Dev cloud release feed must use a loopback HTTP(S) URL.");
                options = options with { ApiRoot = uri, PublicKeysPem = [File.ReadAllText(keyFile)] };
            }
        }
#endif
        return options;
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
