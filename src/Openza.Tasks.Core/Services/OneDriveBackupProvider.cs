using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Openza.Tasks.Core.Services;

public sealed class OneDriveBackupProvider(
    HttpClient httpClient,
    Func<CancellationToken, Task<string?>> accessTokenProvider) : ICloudBackupProvider
{
    public const string AppFolderScope = "https://graph.microsoft.com/Files.ReadWrite.AppFolder";
    private const string BaseUrl = "https://graph.microsoft.com/v1.0";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static IReadOnlyList<string> Scopes { get; } =
    [
        AppFolderScope,
        "https://graph.microsoft.com/User.Read",
        "offline_access",
    ];

    public async Task UploadFileAsync(
        string remotePath,
        string localPath,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureFolderPathAsync(RemoteDirectoryName(remotePath), cancellationToken).ConfigureAwait(false);
        await using var stream = File.OpenRead(localPath);
        using var request = await CreateRequestAsync(HttpMethod.Put, $"{PathAddress(remotePath)}:/content", cancellationToken).ConfigureAwait(false);
        request.Content = new StreamContent(stream);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task DownloadFileAsync(
        string remotePath,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(localPath) ?? ".");
        using var request = await CreateRequestAsync(HttpMethod.Get, $"{PathAddress(remotePath)}:/content", cancellationToken).ConfigureAwait(false);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(localPath);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CloudBackupInfo>> ListBackupsAsync(
        string appFlavor,
        CancellationToken cancellationToken = default)
    {
        var remoteDirectory = CloudBackupPaths.BackupDirectory(appFlavor);
        var files = await ListChildrenAsync(remoteDirectory, cancellationToken).ConfigureAwait(false);
        var backups = new List<CloudBackupInfo>();
        foreach (var file in files.Where(file => file.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await DownloadStringAsync($"{remoteDirectory}/{file.Name}", cancellationToken).ConfigureAwait(false);
                var backup = JsonSerializer.Deserialize<CloudBackupInfo>(json);
                if (backup is not null &&
                    string.Equals(backup.CloudProvider, CloudBackupProviders.OneDrive, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(backup.AppFlavor, appFlavor, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(backup.BackupId) &&
                    !string.IsNullOrWhiteSpace(backup.CloudPath))
                {
                    backups.Add(backup);
                }
            }
            catch
            {
                // Ignore corrupt metadata sidecars; they must not block valid backup discovery.
            }
        }

        return backups
            .OrderByDescending(backup => backup.CreatedAt)
            .ToList();
    }

    public async Task DeleteBackupAsync(CloudBackupInfo backup, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await DeleteIfExistsAsync(backup.CloudPath, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(backup.MetadataPath))
        {
            await DeleteIfExistsAsync(backup.MetadataPath, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UploadManifestAsync(
        string appFlavor,
        CloudBackupManifest manifest,
        CancellationToken cancellationToken = default)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"openza-cloud-manifest-{Guid.NewGuid():N}.json");
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            await UploadFileAsync(CloudBackupPaths.ManifestPath(appFlavor), tempPath, "application/json", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async Task EnsureFolderPathAsync(string remoteDirectory, CancellationToken cancellationToken)
    {
        var segments = SplitPath(remoteDirectory).ToList();
        var currentPath = string.Empty;
        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nextPath = string.IsNullOrWhiteSpace(currentPath) ? segment : $"{currentPath}/{segment}";
            if (!await FolderExistsAsync(nextPath, cancellationToken).ConfigureAwait(false))
            {
                await CreateFolderAsync(currentPath, segment, cancellationToken).ConfigureAwait(false);
            }

            currentPath = nextPath;
        }
    }

    private async Task<bool> FolderExistsAsync(string remotePath, CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, PathAddress(remotePath), cancellationToken).ConfigureAwait(false);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    private async Task CreateFolderAsync(string parentPath, string folderName, CancellationToken cancellationToken)
    {
        var endpoint = string.IsNullOrWhiteSpace(parentPath)
            ? "/me/drive/special/approot/children"
            : $"{PathAddress(parentPath)}:/children";
        using var request = await CreateRequestAsync(HttpMethod.Post, endpoint, cancellationToken).ConfigureAwait(false);
        request.Content = JsonContent.Create(new Dictionary<string, object?>
        {
            ["name"] = folderName,
            ["folder"] = new Dictionary<string, object>(),
            ["@microsoft.graph.conflictBehavior"] = "fail",
        });
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    private async Task<IReadOnlyList<DriveItemInfo>> ListChildrenAsync(
        string remoteDirectory,
        CancellationToken cancellationToken)
    {
        var items = new List<DriveItemInfo>();
        var nextEndpoint = $"{PathAddress(remoteDirectory)}:/children?$top=200";
        while (!string.IsNullOrWhiteSpace(nextEndpoint))
        {
            using var request = await CreateRequestAsync(HttpMethod.Get, nextEndpoint, cancellationToken).ConfigureAwait(false);
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return items;
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("value", out var value) &&
                value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } fileName)
                    {
                        items.Add(new DriveItemInfo(fileName));
                    }
                }
            }

            nextEndpoint = document.RootElement.TryGetProperty("@odata.nextLink", out var nextLink)
                ? nextLink.GetString()
                : null;
        }

        return items;
    }

    private async Task<string> DownloadStringAsync(string remotePath, CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, $"{PathAddress(remotePath)}:/content", cancellationToken).ConfigureAwait(false);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteIfExistsAsync(string remotePath, CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Delete, PathAddress(remotePath), cancellationToken).ConfigureAwait(false);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string pathOrUrl,
        CancellationToken cancellationToken)
    {
        var accessToken = await accessTokenProvider(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Sign in to OneDrive before using cloud backup.");
        }

        var uri = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? pathOrUrl
            : $"{BaseUrl}{pathOrUrl}";
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static string PathAddress(string remotePath) =>
        $"/me/drive/special/approot:/{EscapePath(remotePath)}";

    private static string EscapePath(string remotePath) =>
        string.Join("/", SplitPath(remotePath).Select(Uri.EscapeDataString));

    private static IEnumerable<string> SplitPath(string remotePath) =>
        remotePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string RemoteDirectoryName(string remotePath)
    {
        var parts = SplitPath(remotePath).ToList();
        return parts.Count <= 1 ? string.Empty : string.Join("/", parts.Take(parts.Count - 1));
    }

    private sealed record DriveItemInfo(string Name);
}
