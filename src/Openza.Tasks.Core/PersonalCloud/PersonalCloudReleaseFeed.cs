using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Openza.Tasks.Core.PersonalCloud;

// Signed Personal Cloud releases are published separately from Desktop releases;
// see personal-cloud/docs/cloud-releases.md. Desktop downloads, verifies and
// installs them with its own short-lived Cloudflare authorization.
public sealed record PersonalCloudReleaseStatus(
    string? AvailableVersion,
    bool UpdateAvailable,
    string? NewerVersionNeedingDesktop = null,
    string? RequiredDesktopVersion = null);

// The cloud a Desktop is connected to, used to refuse installing an older release over it.
public sealed record PersonalCloudInstalledRelease(string InstanceId, string ArtifactVersion);

public interface IPersonalCloudArtifactSource
{
    // A null version selects the newest release this Desktop supports, never older
    // than what the connected cloud already runs; a version resumes an interrupted
    // setup with exactly that artifact.
    Task<LoadedPersonalCloudArtifact> ResolveAsync(string? requiredVersion, PersonalCloudInstalledRelease? installed, CancellationToken cancellationToken = default);
    Task<PersonalCloudReleaseStatus> CheckAsync(PersonalCloudInstalledRelease? installed, CancellationToken cancellationToken = default);
    Task RecordInstalledAsync(PersonalCloudInstalledRelease installed, CancellationToken cancellationToken = default);
}

// A locally built artifact, for Dev builds and tests of unreleased cloud changes.
public sealed class DirectoryPersonalCloudArtifactSource(string directory) : IPersonalCloudArtifactSource
{
    private readonly Lazy<LoadedPersonalCloudArtifact> _artifact = new(() => PersonalCloudArtifactDirectory.Load(directory));

    public LoadedPersonalCloudArtifact Artifact => _artifact.Value;

    public Task<LoadedPersonalCloudArtifact> ResolveAsync(string? requiredVersion, PersonalCloudInstalledRelease? installed, CancellationToken cancellationToken = default)
    {
        if (requiredVersion is not null && !string.Equals(requiredVersion, Artifact.Deployment.Version, StringComparison.Ordinal))
            throw new PersonalCloudK0ContractException("setup_resume_required",
                "An interrupted Personal Cloud setup must be finished with its original artifact before starting another setup.");
        return Task.FromResult(Artifact);
    }

    public Task<PersonalCloudReleaseStatus> CheckAsync(PersonalCloudInstalledRelease? installed, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PersonalCloudReleaseStatus(Artifact.Deployment.Version,
            !string.Equals(installed?.ArtifactVersion, Artifact.Deployment.Version, StringComparison.Ordinal)));

    public Task RecordInstalledAsync(PersonalCloudInstalledRelease installed, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed record PersonalCloudReleaseFeedOptions
{
    public required string Repository { get; init; }
    public required IReadOnlyList<string> PublicKeysPem { get; init; }
    public required Version DesktopVersion { get; init; }
    public required string CacheDirectory { get; init; }
    public Uri ApiRoot { get; init; } = new("https://api.github.com/");
}

public static class PersonalCloudReleaseKeys
{
    public const string Repository = "openza/personal-cloud";

    // ECDSA P-256 public key for releases signed by openza/personal-cloud's
    // tag-restricted cloud-release environment. Add a key here before rotating.
    public const string Production = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEwH4JA1H9U+9bY1Yic3sfUc+f8c4t
        YY3ZVsbujX+9TxuW+OLUSYGHnUjvZyaIjIEy0z13OQPAgL06teS3WbCtwg==
        -----END PUBLIC KEY-----
        """;
}

public sealed partial class GitHubPersonalCloudReleaseSource(HttpClient http, PersonalCloudReleaseFeedOptions options) : IPersonalCloudArtifactSource
{
    private const long MaxMetadataBytes = 64 * 1024;
    private const long MaxArchiveBytes = 64L * 1024 * 1024;
    private const int MaxArchiveEntries = 4096;
    private const int MaxListingPages = 10;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions StoreJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private IReadOnlyList<VerifiedRelease> _lastVerified = [];

    public async Task<PersonalCloudReleaseStatus> CheckAsync(PersonalCloudInstalledRelease? installed, CancellationToken cancellationToken = default)
    {
        var releases = await ListVerifiedReleasesAsync(cancellationToken).ConfigureAwait(false);
        var floor = await FloorAsync(releases, installed, cancellationToken).ConfigureAwait(false);
        var compatible = releases.Where(Compatible).MaxBy(release => release.Metadata.ReleaseNumber);
        var newest = releases.MaxBy(release => release.Metadata.ReleaseNumber);
        var blocked = newest is not null && newest != compatible && !Compatible(newest) ? newest : null;
        return new PersonalCloudReleaseStatus(
            compatible?.Metadata.ArtifactVersion,
            compatible is not null && compatible.Metadata.ReleaseNumber > floor &&
                !string.Equals(installed?.ArtifactVersion, compatible.Metadata.ArtifactVersion, StringComparison.Ordinal),
            blocked?.Metadata.ArtifactVersion,
            blocked?.Metadata.MinDesktopVersion);
    }

    public async Task<LoadedPersonalCloudArtifact> ResolveAsync(string? requiredVersion, PersonalCloudInstalledRelease? installed, CancellationToken cancellationToken = default)
    {
        var releases = await ListVerifiedReleasesAsync(cancellationToken).ConfigureAwait(false);
        VerifiedRelease? selected;
        if (requiredVersion is null)
        {
            selected = releases.Where(Compatible).MaxBy(release => release.Metadata.ReleaseNumber);
            var floor = await FloorAsync(releases, installed, cancellationToken).ConfigureAwait(false);
            if (selected is not null && selected.Metadata.ReleaseNumber < floor)
                throw new PersonalCloudK0ContractException("release_rollback",
                    "The newest available Personal Cloud release is older than the one already installed, so it was not installed.");
        }
        else
        {
            selected = releases.FirstOrDefault(release => string.Equals(release.Metadata.ArtifactVersion, requiredVersion, StringComparison.Ordinal))
                ?? await FindByTagAsync(requiredVersion, cancellationToken).ConfigureAwait(false);
        }
        if (selected is null)
        {
            var newest = releases.MaxBy(release => release.Metadata.ReleaseNumber);
            if (requiredVersion is null && newest is not null)
                throw new PersonalCloudK0ContractException("release_requires_newer_desktop",
                    $"Personal Cloud {newest.Metadata.ArtifactVersion} needs Openza Desktop {newest.Metadata.MinDesktopVersion} or newer. Update Openza Desktop first.");
            throw new PersonalCloudK0ContractException("release_unavailable", requiredVersion is null
                ? "No signed Personal Cloud release is available yet."
                : $"Personal Cloud release {requiredVersion} is no longer available to finish the interrupted setup.");
        }
        if (!Compatible(selected))
            throw new PersonalCloudK0ContractException("release_requires_newer_desktop",
                $"Personal Cloud {selected.Metadata.ArtifactVersion} needs Openza Desktop {selected.Metadata.MinDesktopVersion} or newer. Update Openza Desktop first.");
        return await DownloadAsync(selected, cancellationToken).ConfigureAwait(false);
    }

    // Remembers the highest release installed on each connected cloud, so a later
    // listing that hides newer releases cannot roll that cloud back. Uses the
    // releases verified for the operation that just finished; no network access.
    public async Task RecordInstalledAsync(PersonalCloudInstalledRelease installed, CancellationToken cancellationToken = default)
    {
        var number = _lastVerified.FirstOrDefault(release => release.Metadata.ArtifactVersion == installed.ArtifactVersion)?.Metadata.ReleaseNumber ?? 0;
        if (number == 0) return;
        var floors = ReadFloors();
        if (floors.TryGetValue(installed.InstanceId, out var existing) && existing >= number) return;
        floors[installed.InstanceId] = number;
        Directory.CreateDirectory(Path.GetFullPath(options.CacheDirectory));
        var path = FloorPath();
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(floors, StoreJson), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    // The lowest release number an update may install: the installed version's
    // signed number (looked up by tag when it has left the listing) or the highest
    // number previously installed on that cloud. Clouds from before signed releases
    // (for example the 1.2.0 bundled 0.1.0-preview.10) have no number and floor 0.
    private async Task<int> FloorAsync(IReadOnlyList<VerifiedRelease> releases, PersonalCloudInstalledRelease? installed, CancellationToken cancellationToken)
    {
        if (installed is null) return 0;
        var listed = releases.FirstOrDefault(release => release.Metadata.ArtifactVersion == installed.ArtifactVersion)
            ?? await FindByTagAsync(installed.ArtifactVersion, cancellationToken).ConfigureAwait(false);
        return Math.Max(listed?.Metadata.ReleaseNumber ?? 0, ReadFloors().GetValueOrDefault(installed.InstanceId));
    }

    private string FloorPath() => Path.Combine(Path.GetFullPath(options.CacheDirectory), "installed-releases.json");

    private Dictionary<string, int> ReadFloors()
    {
        try
        {
            return File.Exists(FloorPath())
                ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllBytes(FloorPath()), StoreJson) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private bool Compatible(VerifiedRelease release) => release.MinDesktop <= options.DesktopVersion;

    private async Task<IReadOnlyList<VerifiedRelease>> ListVerifiedReleasesAsync(CancellationToken cancellationToken)
    {
        if (!RepositoryPattern().IsMatch(options.Repository))
            throw new PersonalCloudK0ContractException("release_unavailable", "The Personal Cloud release repository is invalid.");
        var releases = new List<GitHubRelease>();
        Uri? next = new(options.ApiRoot, $"repos/{options.Repository}/releases?per_page=100");
        for (var page = 0; next is not null && page < MaxListingPages; page++)
        {
            var (items, link) = await GetJsonAsync<IReadOnlyList<GitHubRelease>>(next, cancellationToken).ConfigureAwait(false);
            releases.AddRange(items ?? []);
            next = NextPage(link);
        }
        var verified = new List<VerifiedRelease>();
        foreach (var release in releases.Where(release => !release.Draft))
            if (await VerifyAsync(release, cancellationToken).ConfigureAwait(false) is { } item) verified.Add(item);
        _lastVerified = verified;
        return verified;
    }

    // An interrupted setup must finish with its exact release, even after it has
    // dropped off the release listing.
    private async Task<VerifiedRelease?> FindByTagAsync(string version, CancellationToken cancellationToken)
    {
        if (!ArtifactVersionPattern().IsMatch(version)) return null;
        try
        {
            var (release, _) = await GetJsonAsync<GitHubRelease>(
                new Uri(options.ApiRoot, $"repos/{options.Repository}/releases/tags/cloud-v{version}"), cancellationToken).ConfigureAwait(false);
            var verified = release is null || release.Draft ? null : await VerifyAsync(release, cancellationToken).ConfigureAwait(false);
            return verified?.Metadata.ArtifactVersion == version ? verified : null;
        }
        catch (PersonalCloudK0ContractException exception) when (exception.Code == "release_unavailable")
        {
            return null;
        }
    }

    private async Task<(T? Value, string? Link)> GetJsonAsync<T>(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = Request(url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw Unavailable();
            var bytes = await ReadLimitedAsync(response, 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            var link = response.Headers.TryGetValues("Link", out var values) ? string.Join(",", values) : null;
            return (JsonSerializer.Deserialize<T>(bytes, Json), link);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw Unavailable(exception);
        }
    }

    private Uri? NextPage(string? link)
    {
        var match = link is null ? null : NextLinkPattern().Match(link);
        if (match is not { Success: true } || !Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var next)) return null;
        // Follow pagination only within the configured API host.
        return next.Scheme == options.ApiRoot.Scheme && next.Host == options.ApiRoot.Host ? next : null;
    }

    private async Task<VerifiedRelease?> VerifyAsync(GitHubRelease release, CancellationToken cancellationToken)
    {
        var assets = release.Assets ?? [];
        var metadataAsset = assets.FirstOrDefault(asset => asset.Name == "cloud-release.json");
        var signatureAsset = assets.FirstOrDefault(asset => asset.Name == "cloud-release.json.sig");
        if (metadataAsset is null || signatureAsset is null) return null;
        byte[] metadataBytes, signatureText;
        try
        {
            metadataBytes = await DownloadLimitedAsync(metadataAsset.BrowserDownloadUrl, MaxMetadataBytes, cancellationToken).ConfigureAwait(false);
            signatureText = await DownloadLimitedAsync(signatureAsset.BrowserDownloadUrl, MaxMetadataBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw Unavailable(exception);
        }
        // Nothing in an unsigned or wrongly signed release is trusted, so it is skipped.
        if (!SignatureValid(metadataBytes, signatureText)) return null;
        var parsed = ParseMetadata(metadataBytes);
        if (parsed is null) return null;
        var archive = assets.FirstOrDefault(asset => asset.Name == parsed.Value.Metadata.Archive.File);
        return archive is null ? null : new VerifiedRelease(parsed.Value.Metadata, parsed.Value.MinDesktop, archive.BrowserDownloadUrl);
    }

    private bool SignatureValid(byte[] metadata, byte[] signatureText)
    {
        byte[] signature;
        try { signature = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(signatureText).Trim()); }
        catch (FormatException) { return false; }
        foreach (var pem in options.PublicKeysPem)
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            if (key.KeySize == 256 && key.VerifyData(metadata, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                return true;
        }
        return false;
    }

    private static (ReleaseMetadata Metadata, Version MinDesktop)? ParseMetadata(byte[] bytes)
    {
        try
        {
            var metadata = JsonSerializer.Deserialize<ReleaseMetadata>(bytes, Json);
            if (metadata is null || metadata.FormatVersion != 1 || metadata.Product != "openza-personal-cloud" ||
                metadata.ArtifactVersion is null || metadata.ArtifactVersion.Length > 128 || !ArtifactVersionPattern().IsMatch(metadata.ArtifactVersion) ||
                metadata.ReleaseNumber < 1 || metadata.MinDesktopVersion is null || !DesktopVersionPattern().IsMatch(metadata.MinDesktopVersion) ||
                !Sha256(metadata.ManifestSha256) || metadata.Archive is null || !Sha256(metadata.Archive.Sha256) ||
                metadata.Archive.Size is < 1 or > MaxArchiveBytes ||
                metadata.Archive.File != $"openza-personal-cloud-{metadata.ArtifactVersion}.zip")
                return null;
            return (metadata, Version.Parse(metadata.MinDesktopVersion));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<LoadedPersonalCloudArtifact> DownloadAsync(VerifiedRelease release, CancellationToken cancellationToken)
    {
        var metadata = release.Metadata;
        var root = Path.GetFullPath(options.CacheDirectory);
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, metadata.ArtifactVersion);
        if (Directory.Exists(target))
        {
            try { return LoadVerified(target, metadata); }
            catch (PersonalCloudK0ContractException) { Directory.Delete(target, recursive: true); }
        }

        byte[] archive;
        try
        {
            archive = await DownloadLimitedAsync(release.ArchiveUrl, MaxArchiveBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw Unavailable(exception);
        }
        if (archive.LongLength != metadata.Archive.Size ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(archive), Convert.FromHexString(metadata.Archive.Sha256)))
            throw new PersonalCloudK0ContractException("release_invalid", "The downloaded Personal Cloud release does not match its signed checksum.");

        var staging = Path.Combine(root, $".{metadata.ArtifactVersion}.{Guid.NewGuid():N}.tmp");
        try
        {
            ExtractSafely(archive, staging);
            LoadVerified(staging, metadata);
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        return LoadVerified(target, metadata);
    }

    // Every file is read once and verified in memory, so the loaded artifact is
    // exactly what the signed manifest describes even if the cache changes later.
    private static LoadedPersonalCloudArtifact LoadVerified(string directory, ReleaseMetadata metadata)
    {
        LoadedPersonalCloudArtifact loaded;
        try { loaded = PersonalCloudArtifactDirectory.Load(directory, metadata.ManifestSha256); }
        catch (PersonalCloudK0ContractException exception) when (exception.Code == "artifact_invalid")
        {
            throw new PersonalCloudK0ContractException("release_invalid", "The Personal Cloud release files do not match their signed checksums.", exception);
        }
        if (!string.Equals(loaded.Deployment.Version, metadata.ArtifactVersion, StringComparison.Ordinal))
            throw new PersonalCloudK0ContractException("release_invalid", "The Personal Cloud release version does not match its signed metadata.");
        return loaded;
    }

    private static void ExtractSafely(byte[] archive, string destination)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxArchiveEntries) throw InvalidArchive();
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var path = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!path.StartsWith(root, StringComparison.Ordinal)) throw InvalidArchive();
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
                total += entry.Length;
                if (entry.Length > 25 * 1024 * 1024 || total > MaxArchiveBytes * 2) throw InvalidArchive();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: false);
            }
        }
        catch (InvalidDataException exception)
        {
            throw new PersonalCloudK0ContractException("release_invalid", "The Personal Cloud release archive is invalid.", exception);
        }
    }

    private async Task<byte[]> DownloadLimitedAsync(Uri url, long limit, CancellationToken cancellationToken)
    {
        if (url.Scheme != Uri.UriSchemeHttps && !url.IsLoopback) throw Unavailable();
        using var request = Request(url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw Unavailable();
        return await ReadLimitedAsync(response, limit, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, long limit, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > limit) throw new PersonalCloudK0ContractException("release_invalid", "A Personal Cloud release file is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit) throw new PersonalCloudK0ContractException("release_invalid", "A Personal Cloud release file is too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static HttpRequestMessage Request(Uri url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Openza-Tasks", "1"));
        return request;
    }

    private static bool Sha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static PersonalCloudK0ContractException InvalidArchive() => new("release_invalid", "The Personal Cloud release archive is invalid.");
    private static PersonalCloudK0ContractException Unavailable(Exception? inner = null) => new("release_unavailable",
        "Openza couldn't get the Personal Cloud release from GitHub. Check your connection and try again.", inner);

    [GeneratedRegex("^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")] private static partial Regex RepositoryPattern();
    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+(-[0-9A-Za-z.-]+)?$")] private static partial Regex ArtifactVersionPattern();
    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+$")] private static partial Regex DesktopVersionPattern();
    [GeneratedRegex("<([^>]+)>;\\s*rel=\"next\"")] private static partial Regex NextLinkPattern();

    private sealed record VerifiedRelease(ReleaseMetadata Metadata, Version MinDesktop, Uri ArchiveUrl);
    private sealed record GitHubRelease(bool Draft, IReadOnlyList<GitHubAsset>? Assets);
    private sealed record GitHubAsset(string Name, [property: JsonPropertyName("browser_download_url")] Uri BrowserDownloadUrl);
    private sealed record ReleaseMetadata(int FormatVersion, string Product, string ArtifactVersion, int ReleaseNumber,
        string MinDesktopVersion, string SourceRevision, string ManifestSha256, ReleaseArchive Archive);
    private sealed record ReleaseArchive(string File, long Size, string Sha256);
}
