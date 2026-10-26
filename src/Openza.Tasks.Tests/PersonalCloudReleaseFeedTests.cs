using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudReleaseFeedTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), $"openza-cloud-releases-{Guid.NewGuid():N}");
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Feed _feed = new();

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_cache)) Directory.Delete(_cache, recursive: true);
    }

    [Fact]
    public async Task Newest_compatible_signed_release_is_downloaded_verified_and_cached()
    {
        Publish("0.1.0-preview.10", 10, "1.3.0");
        Publish("0.1.0-preview.11", 11, "1.3.0");
        var source = Source();

        var status = await source.CheckAsync("0.1.0-preview.10");
        var loaded = await source.ResolveAsync(null);
        var archiveDownloads = _feed.Requests.Count(path => path.EndsWith(".zip", StringComparison.Ordinal));
        var again = await source.ResolveAsync(null);

        Assert.Equal("0.1.0-preview.11", status.AvailableVersion);
        Assert.True(status.UpdateAvailable);
        Assert.Equal("0.1.0-preview.11", loaded.Deployment.Version);
        Assert.Equal("0.1.0-preview.11", loaded.Migrations.ArtifactVersion);
        Assert.Equal("0.1.0-preview.11", again.Deployment.Version);
        Assert.Equal(1, archiveDownloads);
        Assert.Equal(1, _feed.Requests.Count(path => path.EndsWith(".zip", StringComparison.Ordinal)));
        Assert.False((await source.CheckAsync("0.1.0-preview.11")).UpdateAvailable);
    }

    [Fact]
    public async Task A_release_needing_a_newer_desktop_is_reported_but_not_installed()
    {
        Publish("0.1.0-preview.11", 11, "1.3.0");
        Publish("0.2.0", 12, "1.4.0");
        var source = Source(new Version(1, 3, 0));

        var status = await source.CheckAsync("0.1.0-preview.11");
        var loaded = await source.ResolveAsync(null);

        Assert.False(status.UpdateAvailable);
        Assert.Equal("0.1.0-preview.11", status.AvailableVersion);
        Assert.Equal("0.2.0", status.NewerVersionNeedingDesktop);
        Assert.Equal("1.4.0", status.RequiredDesktopVersion);
        Assert.Equal("0.1.0-preview.11", loaded.Deployment.Version);

        var onlyNewer = new PersonalCloudReleaseFeedTests();
        onlyNewer.Publish("0.2.0", 12, "1.4.0");
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => onlyNewer.Source(new Version(1, 3, 0)).ResolveAsync(null));
        Assert.Equal("release_requires_newer_desktop", error.Code);
        Assert.Contains("1.4.0", error.Message, StringComparison.Ordinal);
        onlyNewer.Dispose();
    }

    [Fact]
    public async Task An_interrupted_setup_resumes_with_its_exact_release_even_when_a_newer_one_exists()
    {
        Publish("0.1.0-preview.11", 11, "1.3.0");
        Publish("0.1.0-preview.12", 12, "1.3.0");

        var loaded = await Source().ResolveAsync("0.1.0-preview.11");

        Assert.Equal("0.1.0-preview.11", loaded.Deployment.Version);
        var missing = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => Source().ResolveAsync("0.1.0-preview.9"));
        Assert.Equal("release_unavailable", missing.Code);
    }

    [Fact]
    public async Task Releases_signed_with_another_key_or_unsigned_are_ignored()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Publish("0.1.0-preview.11", 11, "1.3.0", signer: other);
        Publish("0.1.0-preview.12", 12, "1.3.0", signature: "not-base64!");

        var status = await Source().CheckAsync(null);
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => Source().ResolveAsync(null));

        Assert.Null(status.AvailableVersion);
        Assert.Equal("release_unavailable", error.Code);
    }

    [Fact]
    public async Task A_replaced_archive_fails_its_signed_checksum()
    {
        Publish("0.1.0-preview.11", 11, "1.3.0");
        _feed.Files["/download/0.1.0-preview.11/openza-personal-cloud-0.1.0-preview.11.zip"] = Zip(Artifact("0.1.0-preview.11", "tampered"));

        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => Source().ResolveAsync(null));

        Assert.Equal("release_invalid", error.Code);
        Assert.False(Directory.Exists(Path.Combine(_cache, "0.1.0-preview.11")));
    }

    [Fact]
    public async Task Archive_entries_outside_the_release_folder_are_rejected()
    {
        var files = Artifact("0.1.0-preview.11");
        files["../escape.txt"] = Encoding.UTF8.GetBytes("outside");
        Publish("0.1.0-preview.11", 11, "1.3.0", archive: Zip(files));

        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => Source().ResolveAsync(null));

        Assert.Equal("release_invalid", error.Code);
        Assert.False(File.Exists(Path.Combine(_cache, "escape.txt")));
    }

    [Fact]
    public async Task A_corrupted_cached_release_is_downloaded_again()
    {
        Publish("0.1.0-preview.11", 11, "1.3.0");
        var source = Source();
        await source.ResolveAsync(null);
        File.WriteAllText(Path.Combine(_cache, "0.1.0-preview.11", "worker.js"), "changed");

        var loaded = await source.ResolveAsync(null);

        Assert.Equal("0.1.0-preview.11", loaded.Deployment.Version);
        Assert.Equal(2, _feed.Requests.Count(path => path.EndsWith(".zip", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task GitHub_failures_report_that_the_release_could_not_be_reached()
    {
        _feed.FailListing = true;

        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => Source().CheckAsync(null));

        Assert.Equal("release_unavailable", error.Code);
        Assert.Contains("GitHub", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_artifact_slot_resolves_the_pending_setup_version_and_the_newest_release_for_upgrades()
    {
        Publish("0.1.0-preview.11", 11, "1.3.0");
        Publish("0.1.0-preview.12", 12, "1.3.0");
        var store = new MemoryStateStore
        {
            State = new PersonalCloudProvisioningState { AttemptId = "pc_resume", ArtifactVersion = "0.1.0-preview.11", Stage = PersonalCloudProvisioningStage.DatabaseReady },
        };
        var slot = new PersonalCloudArtifactSlot(Source(), store);

        Assert.Throws<PersonalCloudK0ContractException>(() => slot.Version);
        await slot.PrepareForSetupAsync();
        Assert.Equal("0.1.0-preview.11", slot.Version);
        Assert.Equal("0.1.0-preview.11", slot.MigrationBundle().ArtifactVersion);
        await slot.PrepareForUpgradeAsync();
        Assert.Equal("0.1.0-preview.12", slot.Version);
    }

    // Produced by personal-cloud/scripts/cloud-release.mjs (Node.js crypto) with a
    // throwaway key, so the release workflow and Desktop agree on the signature format.
    private const string NodePublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEeOn5i8wi20qNXXWih7vqJHT/N/AY
        mlHjhf0I12uvmKxw0H5xzuSwfAoOWzzBoEWJ+ZEDJSGRhQRhMhm7YmSEMA==
        -----END PUBLIC KEY-----
        """;
    private const string NodeMetadata = """
        {
          "formatVersion": 1,
          "product": "openza-personal-cloud",
          "artifactVersion": "0.1.0-preview.11",
          "releaseNumber": 11,
          "minDesktopVersion": "1.3.0",
          "sourceRevision": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "manifestSha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
          "archive": {
            "file": "openza-personal-cloud-0.1.0-preview.11.zip",
            "size": 10,
            "sha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
          }
        }
        """;
    private const string NodeSignature = "MEUCIQD5L1G0a3Y0nsJxRBnfJoFBn+fUToAdtdTVjOHKsHVXcAIgSog3byYOXeb4JY0xFyseQtubQNRqTGwksuklPT9DSaM=";

    [Fact]
    public async Task Metadata_signed_by_the_cloud_release_script_verifies_in_Desktop()
    {
        var metadata = Encoding.UTF8.GetBytes(NodeMetadata.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
        _feed.Files["/download/node/cloud-release.json"] = metadata;
        _feed.Files["/download/node/cloud-release.json.sig"] = Encoding.ASCII.GetBytes(NodeSignature + "\n");
        _feed.Releases.Add(new
        {
            draft = false,
            assets = new[] { "cloud-release.json", "cloud-release.json.sig", "openza-personal-cloud-0.1.0-preview.11.zip" }
                .Select(name => new { name, browser_download_url = $"https://github.example.test/download/node/{name}" }).ToArray(),
        });
        var source = new GitHubPersonalCloudReleaseSource(new HttpClient(_feed), new PersonalCloudReleaseFeedOptions
        {
            Repository = "openza/personal-cloud",
            PublicKeysPem = [NodePublicKey],
            DesktopVersion = new Version(1, 3, 0),
            CacheDirectory = _cache,
            ApiRoot = new Uri("https://api.example.test/"),
        });

        var status = await source.CheckAsync(null);

        Assert.Equal("0.1.0-preview.11", status.AvailableVersion);
    }

    private GitHubPersonalCloudReleaseSource Source(Version? desktop = null) => new(new HttpClient(_feed), new PersonalCloudReleaseFeedOptions
    {
        Repository = "openza/personal-cloud",
        PublicKeysPem = [_key.ExportSubjectPublicKeyInfoPem()],
        DesktopVersion = desktop ?? new Version(1, 3, 0),
        CacheDirectory = _cache,
        ApiRoot = new Uri("https://api.example.test/"),
    });

    private void Publish(string version, int number, string minDesktop, ECDsa? signer = null, string? signature = null, byte[]? archive = null)
    {
        var zip = archive ?? Zip(Artifact(version));
        var manifest = Artifact(version)["artifact-manifest.json"];
        var metadata = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            product = "openza-personal-cloud",
            artifactVersion = version,
            releaseNumber = number,
            minDesktopVersion = minDesktop,
            sourceRevision = new string('a', 40),
            manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifest)),
            archive = new { file = $"openza-personal-cloud-{version}.zip", size = zip.LongLength, sha256 = Convert.ToHexStringLower(SHA256.HashData(zip)) },
        }));
        var signed = signature ?? Convert.ToBase64String((signer ?? _key).SignData(metadata, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        var prefix = $"/download/{version}/";
        _feed.Files[prefix + "cloud-release.json"] = metadata;
        _feed.Files[prefix + "cloud-release.json.sig"] = Encoding.ASCII.GetBytes(signed + "\n");
        _feed.Files[prefix + $"openza-personal-cloud-{version}.zip"] = zip;
        _feed.Releases.Add(new
        {
            draft = false,
            assets = new[] { "cloud-release.json", "cloud-release.json.sig", $"openza-personal-cloud-{version}.zip" }
                .Select(name => new { name, browser_download_url = $"https://github.example.test{prefix}{name}" }).ToArray(),
        });
    }

    private static Dictionary<string, byte[]> Artifact(string version, string workerText = "export default {fetch(){return new Response('ok')}}")
    {
        var worker = Encoding.UTF8.GetBytes(workerText);
        var asset = Encoding.UTF8.GetBytes("<!doctype html><title>Openza</title>");
        var migration = Encoding.UTF8.GetBytes("CREATE TABLE example(id TEXT PRIMARY KEY);");
        var manifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            artifactVersion = version,
            compatibilityDate = "2026-09-13",
            worker = new { file = "worker.js", size = worker.LongLength, sha256 = Convert.ToHexStringLower(SHA256.HashData(worker)) },
            assets = new[] { new { file = "assets/index.html", size = asset.LongLength, sha256 = Convert.ToHexStringLower(SHA256.HashData(asset)), path = "/index.html", contentType = "text/html; charset=utf-8" } },
            migrations = new[] { new { file = "migrations/0001_example.sql", size = migration.LongLength, sha256 = Convert.ToHexStringLower(SHA256.HashData(migration)) } },
        }));
        return new Dictionary<string, byte[]>
        {
            ["artifact-manifest.json"] = manifest,
            ["worker.js"] = worker,
            ["assets/index.html"] = asset,
            ["migrations/0001_example.sql"] = migration,
        };
    }

    private static byte[] Zip(Dictionary<string, byte[]> files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in files)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(content);
            }
        return buffer.ToArray();
    }

    private sealed class Feed : HttpMessageHandler
    {
        public List<object> Releases { get; } = [];
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public List<string> Requests { get; } = [];
        public bool FailListing { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            if (path == "/repos/openza/personal-cloud/releases")
            {
                if (FailListing) throw new HttpRequestException("offline");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(Releases)) });
            }
            return Task.FromResult(Files.TryGetValue(path, out var content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class MemoryStateStore : IPersonalCloudProvisioningStateStore
    {
        public PersonalCloudProvisioningState? State { get; set; }
        public Task<PersonalCloudProvisioningState?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
        public Task SaveAsync(PersonalCloudProvisioningState state, CancellationToken cancellationToken = default) { State = state; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken cancellationToken = default) { State = null; return Task.CompletedTask; }
    }
}
