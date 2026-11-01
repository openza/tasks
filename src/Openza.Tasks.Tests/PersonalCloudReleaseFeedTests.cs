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

        var status = await source.CheckAsync(Installed("0.1.0-preview.10"));
        var loaded = await source.ResolveAsync(null, null);
        var archiveDownloads = _feed.Requests.Count(path => path.EndsWith(".zip", StringComparison.Ordinal));
        var again = await source.ResolveAsync(null, null);

        Assert.Equal("0.1.0-preview.11", status.AvailableVersion);
        Assert.True(status.UpdateAvailable);
        Assert.Equal("0.1.0-preview.11", loaded.Deployment.Version);
        Assert.Equal("0.1.0-preview.11", loaded.Migrations.ArtifactVersion);
        Assert.Equal("0.1.0-preview.11", again.Deployment.Version);
        Assert.Equal(1, archiveDownloads);
        Assert.Equal(1, _feed.Requests.Count(path => path.EndsWith(".zip", StringComparison.Ordinal)));
        Assert.False((await source.CheckAsync(Installed("0.1.0-preview.11"))).UpdateAvailable);
    }

    [Fact]
    public async Task A_release_needing_a_newer_desktop_is_reported_but_not_installed()
    {
        Publish("0.1.0-preview.11", 11, "1.3.0");
        Publish("0.2.0", 12, "1.4.0");
        var source = Source(new Version(1, 3, 0));

        var status = await source.CheckAsync(Installed("0.1.0-preview.11"));
        var loaded = await source.ResolveAsync(null, null);

        Assert.False(status.UpdateAvailable);
        Assert.Equal("0.1.0-preview.11", status.AvailableVersion);
        Assert.Equal("0.2.0", status.NewerVersionNeedingDesktop);
        Assert.Equal("1.4.0", status.RequiredDesktopVersion);
        Assert.Equal("0.1.0-preview.11", loaded.Deployment.Version);

        var onlyNewer = new PersonalCloudReleaseFeedTests();
        onlyNewer.Publish("0.2.0", 12, "1.4.0");
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => onlyNewer.Source(new Version(1, 3, 0)).ResolveAsync(null, null));
        Assert.Equal("release_requires_newer_desktop", error.Code);
        Assert.Contains("1.4.0", error.Message, StringComparison.Ordinal);
        onlyNewer.Dispose();
    }

    [Fact]
    public async Task An_interrupted_setup_resumes_with_its_exact_release_even_when_a_newer_one_exists()
    {
        Publish("0.1.0-preview.11", 11, "1.3.0");
        Publish("0.1.0-preview.12", 12, "1.3.0");

        var loaded = await Source().ResolveAsync("0.1.0-preview.11", null);

        Assert.Equal("0.1.0-preview.11", loaded.Deployment.Version);
        var missing = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => Source().ResolveAsync("0.1.0-preview.9", null));
        Assert.Equal("release_unavailable", missing.Code);
    }

    [Fact]
    public async Task A_pinned_release_available_by_tag_resumes_despite_an_invalid_only_listing()
    {
        Publish("0.1.0-preview.11", 11, "1.3.0");
        _feed.Tagged["cloud-v0.1.0-preview.11"] = _feed.Releases[0];
        _feed.Releases.Clear();
        Publish("0.1.0-preview.12", 12, "1.3.0", signature: "not-base64!");

        var loaded = await Source().ResolveAsync("0.1.0-preview.11", null);

        Assert.Equal("0.1.0-preview.11", loaded.Deployment.Version);
        Assert.Contains("/repos/openza/personal-cloud/releases/tags/cloud-v0.1.0-preview.11", _feed.Requests);
        Assert.DoesNotContain(_feed.Requests, path => path.EndsWith("preview.12.zip", StringComparison.Ordinal));
        var missing = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => Source().ResolveAsync("0.1.0-preview.9", null));
        Assert.Equal("release_invalid", missing.Code);
        Assert.Contains("couldn't verify", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unverifiable_releases_report_verification_failure_instead_of_an_empty_feed()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Publish("0.1.0-preview.11", 11, "1.3.0", signer: other);
        Publish("0.1.0-preview.12", 12, "1.3.0", signature: "not-base64!");

        var statusError = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => Source().CheckAsync(null));
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => Source().ResolveAsync(null, null));

        Assert.Equal("release_invalid", statusError.Code);
        Assert.Equal("release_invalid", error.Code);
        Assert.Contains("couldn't verify", error.Message, StringComparison.Ordinal);
        Assert.Contains("damaged or tampered with", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changed_metadata_reports_verification_failure()
    {
        Publish("0.1.0-preview.11", 11, "1.3.0");
        _feed.Files["/download/0.1.0-preview.11/cloud-release.json"][0] ^= 1;

        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => Source().ResolveAsync(null, null));

        Assert.Equal("release_invalid", error.Code);
        Assert.Contains("couldn't verify", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signed_but_invalid_metadata_reports_verification_failure()
    {
        Publish("0.1.0-preview.11", 11, "1.3.0");
        var prefix = "/download/0.1.0-preview.11/";
        var metadata = Encoding.UTF8.GetBytes("{}");
        _feed.Files[prefix + "cloud-release.json"] = metadata;
        _feed.Files[prefix + "cloud-release.json.sig"] = Encoding.ASCII.GetBytes(Convert.ToBase64String(
            _key.SignData(metadata, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));

        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => Source().ResolveAsync(null, null));

        Assert.Equal("release_invalid", error.Code);
        Assert.Contains("couldn't verify", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_an_empty_feed_reports_no_release_available_yet()
    {
        Assert.Null((await Source().CheckAsync(null)).AvailableVersion);
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => Source().ResolveAsync(null, null));

        Assert.Equal("release_unavailable", error.Code);
        Assert.Equal("No signed Personal Cloud release is available yet.", error.Message);
    }

    [Fact]
    public async Task Invalid_releases_are_still_skipped_when_a_verified_release_is_available()
    {
        Publish("0.1.0-preview.11", 11, "1.3.0");
        Publish("0.1.0-preview.12", 12, "1.3.0", signature: "not-base64!");

        Assert.Equal("0.1.0-preview.11", (await Source().ResolveAsync(null, null)).Deployment.Version);
    }

    [Fact]
    public async Task A_replaced_archive_fails_its_signed_checksum()
    {
        Publish("0.1.0-preview.11", 11, "1.3.0");
        _feed.Files["/download/0.1.0-preview.11/openza-personal-cloud-0.1.0-preview.11.zip"] = Zip(Artifact("0.1.0-preview.11", "tampered"));

        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => Source().ResolveAsync(null, null));

        Assert.Equal("release_invalid", error.Code);
        Assert.False(Directory.Exists(Path.Combine(_cache, "0.1.0-preview.11")));
    }

    [Fact]
    public async Task Archive_entries_outside_the_release_folder_are_rejected()
    {
        var files = Artifact("0.1.0-preview.11");
        files["../escape.txt"] = Encoding.UTF8.GetBytes("outside");
        Publish("0.1.0-preview.11", 11, "1.3.0", archive: Zip(files));

        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => Source().ResolveAsync(null, null));

        Assert.Equal("release_invalid", error.Code);
        Assert.False(File.Exists(Path.Combine(_cache, "escape.txt")));
    }

    [Fact]
    public async Task A_corrupted_cached_release_is_downloaded_again()
    {
        Publish("0.1.0-preview.11", 11, "1.3.0");
        var source = Source();
        await source.ResolveAsync(null, null);
        File.WriteAllText(Path.Combine(_cache, "0.1.0-preview.11", "worker.js"), "changed");

        var loaded = await source.ResolveAsync(null, null);

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
        await slot.PrepareForUpgradeAsync(new PersonalCloudConnectionProfile
        {
            ServiceOrigin = new Uri("https://openza-personal-test.example.workers.dev/"),
            InstanceId = "pc_connected",
            DeviceId = "device_test",
            ArtifactVersion = "0.1.0-preview.11",
            ConnectedAt = DateTimeOffset.UnixEpoch,
        });
        Assert.Equal("0.1.0-preview.12", slot.Version);
    }

    [Fact]
    public async Task Update_never_installs_an_older_release_than_the_cloud_already_runs()
    {
        Publish("0.1.0-preview.11", 11, "1.3.0");
        Publish("0.1.0-preview.12", 12, "1.3.0");
        var source = Source();
        await source.ResolveAsync(null, Installed("0.1.0-preview.11"));
        await source.RecordInstalledAsync(Installed("0.1.0-preview.12"));
        _feed.Releases.RemoveAt(1); // The newer release disappears from the listing.

        var status = await source.CheckAsync(Installed("0.1.0-preview.12"));
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => source.ResolveAsync(null, Installed("0.1.0-preview.12")));
        var otherCloud = await source.ResolveAsync(null, new PersonalCloudInstalledRelease("pc_other", "0.1.0-preview.10"));

        Assert.False(status.UpdateAvailable);
        Assert.Equal("release_rollback", error.Code);
        Assert.Equal("0.1.0-preview.11", otherCloud.Deployment.Version);
    }

    [Fact]
    public async Task An_installed_release_that_left_the_listing_still_sets_the_floor_by_its_tag()
    {
        Publish("0.1.0-preview.11", 11, "1.3.0");
        Publish("0.1.0-preview.12", 12, "1.3.0");
        _feed.Tagged["cloud-v0.1.0-preview.12"] = _feed.Releases[1];
        _feed.Releases.RemoveAt(1);

        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => Source().ResolveAsync(null, Installed("0.1.0-preview.12")));
        var status = await Source().CheckAsync(Installed("0.1.0-preview.12"));

        Assert.Equal("release_rollback", error.Code);
        Assert.False(status.UpdateAvailable);
    }

    [Fact]
    public async Task A_failed_lookup_of_the_installed_release_stops_the_update_instead_of_dropping_the_floor()
    {
        Publish("0.1.0-preview.11", 11, "1.3.0");
        _feed.FailTags = true;

        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => Source().ResolveAsync(null, Installed("0.1.0-preview.12")));
        _feed.FailTags = false;
        var legacy = await Source().ResolveAsync(null, Installed("0.1.0-preview.10"));

        Assert.Equal("release_unavailable", error.Code);
        Assert.Equal("0.1.0-preview.11", legacy.Deployment.Version);
    }

    [Fact]
    public async Task A_tagged_release_that_fails_verification_does_not_lower_the_floor()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Publish("0.1.0-preview.11", 11, "1.3.0");
        Publish("0.1.0-preview.12", 12, "1.3.0", signer: other);
        _feed.Tagged["cloud-v0.1.0-preview.12"] = _feed.Releases[1];
        _feed.Releases.RemoveAt(1);

        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => Source().ResolveAsync(null, Installed("0.1.0-preview.12")));

        Assert.Equal("release_invalid", error.Code);
    }

    [Fact]
    public async Task A_null_tag_response_is_not_treated_as_a_missing_release()
    {
        Publish("0.1.0-preview.11", 11, "1.3.0");
        _feed.Tagged["cloud-v0.1.0-preview.12"] = null;

        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => Source().ResolveAsync(null, Installed("0.1.0-preview.12")));

        Assert.Equal("release_invalid", error.Code);
    }

    [Fact]
    public async Task A_connection_dropped_mid_download_reports_the_release_as_unavailable()
    {
        Publish("0.1.0-preview.11", 11, "1.3.0");
        _feed.BreakBodies = true;

        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => Source().CheckAsync(null));

        Assert.Equal("release_unavailable", error.Code);
    }

    [Fact]
    public async Task Recording_an_installed_release_uses_the_verified_listing_without_another_download()
    {
        Publish("0.1.0-preview.11", 11, "1.3.0");
        var source = Source();
        await source.CheckAsync(null);
        var requests = _feed.Requests.Count;

        await source.RecordInstalledAsync(Installed("0.1.0-preview.11"));

        Assert.Equal(requests, _feed.Requests.Count);
        Assert.Contains("\"pc_connected\": 11", File.ReadAllText(Path.Combine(_cache, "installed-releases.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_page_of_releases_is_searched()
    {
        for (var number = 1; number <= 3; number++) Publish($"0.1.{number}", number, "1.3.0");
        _feed.PageSize = 1;

        var status = await Source().CheckAsync(null);

        Assert.Equal("0.1.3", status.AvailableVersion);
        Assert.Contains(_feed.Requests, path => path == "/repos/openza/personal-cloud/releases");
        Assert.Equal(3, _feed.Requests.Count(path => path == "/repos/openza/personal-cloud/releases"));
    }

    [Fact]
    public async Task An_interrupted_setup_finds_its_release_by_tag_when_it_left_the_listing()
    {
        Publish("0.1.0-preview.11", 11, "1.3.0");
        Publish("0.1.0-preview.12", 12, "1.3.0");
        _feed.Tagged["cloud-v0.1.0-preview.11"] = _feed.Releases[0];
        _feed.Releases.RemoveAt(0);

        var loaded = await Source().ResolveAsync("0.1.0-preview.11", null);

        Assert.Equal("0.1.0-preview.11", loaded.Deployment.Version);
    }

    [Fact]
    public async Task A_cached_release_with_a_swapped_manifest_is_not_trusted()
    {
        Publish("0.1.0-preview.11", 11, "1.3.0");
        var source = Source();
        await source.ResolveAsync(null, null);
        var cached = Path.Combine(_cache, "0.1.0-preview.11");
        // A self-consistent but different artifact with the same version.
        foreach (var (name, content) in Artifact("0.1.0-preview.11", "export default {fetch(){return new Response('swapped')}}"))
            File.WriteAllBytes(Path.Combine(cached, name), content);

        var loaded = await source.ResolveAsync(null, null);

        Assert.DoesNotContain("swapped", Encoding.UTF8.GetString(loaded.Deployment.WorkerModule), StringComparison.Ordinal);
        Assert.Equal(2, _feed.Requests.Count(path => path.EndsWith(".zip", StringComparison.Ordinal)));
    }

    private static PersonalCloudInstalledRelease Installed(string version) => new("pc_connected", version);

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
        public int PageSize { get; set; } = 100;
        public bool FailTags { get; set; }
        public bool BreakBodies { get; set; }
        public Dictionary<string, object?> Tagged { get; } = new(StringComparer.Ordinal);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            if (BreakBodies)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) });
            if (path == "/repos/openza/personal-cloud/releases")
            {
                if (FailListing) throw new HttpRequestException("offline");
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                var page = int.TryParse(query["page"], out var value) ? value : 1;
                var items = Releases.Skip((page - 1) * PageSize).Take(PageSize).ToArray();
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(items)) };
                if (page * PageSize < Releases.Count)
                    response.Headers.TryAddWithoutValidation("Link", $"<https://api.example.test/repos/openza/personal-cloud/releases?per_page=100&page={page + 1}>; rel=\"next\"");
                return Task.FromResult(response);
            }
            if (path.StartsWith("/repos/openza/personal-cloud/releases/tags/", StringComparison.Ordinal) && FailTags)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
            if (path.StartsWith("/repos/openza/personal-cloud/releases/tags/", StringComparison.Ordinal))
                return Task.FromResult(Tagged.TryGetValue(path[(path.LastIndexOf('/') + 1)..], out var tagged)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(tagged)) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(Files.TryGetValue(path, out var content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("connection reset");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class MemoryStateStore : IPersonalCloudProvisioningStateStore
    {
        public PersonalCloudProvisioningState? State { get; set; }
        public Task<PersonalCloudProvisioningState?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
        public Task SaveAsync(PersonalCloudProvisioningState state, CancellationToken cancellationToken = default) { State = state; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken cancellationToken = default) { State = null; return Task.CompletedTask; }
    }
}
