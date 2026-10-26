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

public interface IPersonalCloudArtifactSource
{
    // Null selects the newest release this Desktop supports; a version resumes
    // an interrupted setup with exactly that artifact.
    Task<LoadedPersonalCloudArtifact> ResolveAsync(string? requiredVersion, CancellationToken cancellationToken = default);
    Task<PersonalCloudReleaseStatus> CheckAsync(string? installedVersion, CancellationToken cancellationToken = default);
}

// A locally built artifact, for Dev builds and tests of unreleased cloud changes.
public sealed class DirectoryPersonalCloudArtifactSource(string directory) : IPersonalCloudArtifactSource
{
    private readonly Lazy<LoadedPersonalCloudArtifact> _artifact = new(() => PersonalCloudArtifactDirectory.Load(directory));

    public LoadedPersonalCloudArtifact Artifact => _artifact.Value;

    public Task<LoadedPersonalCloudArtifact> ResolveAsync(string? requiredVersion, CancellationToken cancellationToken = default)
    {
        if (requiredVersion is not null && !string.Equals(requiredVersion, Artifact.Deployment.Version, StringComparison.Ordinal))
            throw new PersonalCloudK0ContractException("setup_resume_required",
                "An interrupted Personal Cloud setup must be finished with its original artifact before starting another setup.");
        return Task.FromResult(Artifact);
    }

    public Task<PersonalCloudReleaseStatus> CheckAsync(string? installedVersion, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PersonalCloudReleaseStatus(Artifact.Deployment.Version,
            !string.Equals(installedVersion, Artifact.Deployment.Version, StringComparison.Ordinal)));
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
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PersonalCloudReleaseStatus> CheckAsync(string? installedVersion, CancellationToken cancellationToken = default)
    {
        var releases = await ListVerifiedReleasesAsync(cancellationToken).ConfigureAwait(false);
        var compatible = releases.Where(Compatible).MaxBy(release => release.Metadata.ReleaseNumber);
        var newest = releases.MaxBy(release => release.Metadata.ReleaseNumber);
        var blocked = newest is not null && newest != compatible && !Compatible(newest) ? newest : null;
        return new PersonalCloudReleaseStatus(
            compatible?.Metadata.ArtifactVersion,
            compatible is not null && !string.Equals(installedVersion, compatible.Metadata.ArtifactVersion, StringComparison.Ordinal),
            blocked?.Metadata.ArtifactVersion,
            blocked?.Metadata.MinDesktopVersion);
    }

    public async Task<LoadedPersonalCloudArtifact> ResolveAsync(string? requiredVersion, CancellationToken cancellationToken = default)
    {
        var releases = await ListVerifiedReleasesAsync(cancellationToken).ConfigureAwait(false);
        var selected = requiredVersion is null
            ? releases.Where(Compatible).MaxBy(release => release.Metadata.ReleaseNumber)
            : releases.FirstOrDefault(release => string.Equals(release.Metadata.ArtifactVersion, requiredVersion, StringComparison.Ordinal));
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

    private bool Compatible(VerifiedRelease release) => release.MinDesktop <= options.DesktopVersion;

    private async Task<IReadOnlyList<VerifiedRelease>> ListVerifiedReleasesAsync(CancellationToken cancellationToken)
    {
        if (!RepositoryPattern().IsMatch(options.Repository))
            throw new PersonalCloudK0ContractException("release_unavailable", "The Personal Cloud release repository is invalid.");
        IReadOnlyList<GitHubRelease> releases;
        try
        {
            using var request = Request(new Uri(options.ApiRoot, $"repos/{options.Repository}/releases?per_page=30"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw Unavailable();
            var bytes = await ReadLimitedAsync(response, 2 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            releases = JsonSerializer.Deserialize<IReadOnlyList<GitHubRelease>>(bytes, Json) ?? [];
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw Unavailable(exception);
        }

        var verified = new List<VerifiedRelease>();
        foreach (var release in releases.Where(release => !release.Draft))
        {
            var assets = release.Assets ?? [];
            var metadataAsset = assets.FirstOrDefault(asset => asset.Name == "cloud-release.json");
            var signatureAsset = assets.FirstOrDefault(asset => asset.Name == "cloud-release.json.sig");
            if (metadataAsset is null || signatureAsset is null) continue;
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
            if (!SignatureValid(metadataBytes, signatureText)) continue;
            var parsed = ParseMetadata(metadataBytes);
            if (parsed is null) continue;
            var archive = assets.FirstOrDefault(asset => asset.Name == parsed.Value.Metadata.Archive.File);
            if (archive is null) continue;
            verified.Add(new VerifiedRelease(parsed.Value.Metadata, parsed.Value.MinDesktop, archive.BrowserDownloadUrl));
        }
        return verified;
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

    private static LoadedPersonalCloudArtifact LoadVerified(string directory, ReleaseMetadata metadata)
    {
        var manifestPath = Path.Combine(directory, "artifact-manifest.json");
        if (!File.Exists(manifestPath) ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(manifestPath)), Convert.FromHexString(metadata.ManifestSha256)))
            throw new PersonalCloudK0ContractException("release_invalid", "The Personal Cloud release manifest does not match its signed checksum.");
        var loaded = PersonalCloudArtifactDirectory.Load(directory);
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

    private sealed record VerifiedRelease(ReleaseMetadata Metadata, Version MinDesktop, Uri ArchiveUrl);
    private sealed record GitHubRelease(bool Draft, IReadOnlyList<GitHubAsset>? Assets);
    private sealed record GitHubAsset(string Name, [property: JsonPropertyName("browser_download_url")] Uri BrowserDownloadUrl);
    private sealed record ReleaseMetadata(int FormatVersion, string Product, string ArtifactVersion, int ReleaseNumber,
        string MinDesktopVersion, string SourceRevision, string ManifestSha256, ReleaseArchive Archive);
    private sealed record ReleaseArchive(string File, long Size, string Sha256);
}
