using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.PersonalCloud;

public sealed record PersonalCloudStaticAsset(string Path, string ContentType, byte[] Content);

public sealed record PersonalCloudDeploymentArtifact(
    string Version,
    DateOnly CompatibilityDate,
    byte[] WorkerModule,
    IReadOnlyList<PersonalCloudStaticAsset> Assets);

public sealed class CloudflareArtifactDeploymentClient(
    HttpClient http,
    PersonalCloudDeploymentArtifact artifact) : ICloudflareArtifactDeploymentClient
{
    private static readonly Uri ApiRoot = new("https://api.cloudflare.com/client/v4/");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CloudflareProvisioningApplication> DeployAsync(
        string accessToken,
        string accountId,
        string databaseId,
        string resourceName,
        string attemptId,
        string artifactVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateArtifact(artifact, artifactVersion);
        var assets = BuildAssets(artifact.Assets);
        var completionToken = await UploadAssetsAsync(
            accessToken, accountId, resourceName, assets, cancellationToken).ConfigureAwait(false);
        await UploadWorkerAsync(
            accessToken, accountId, databaseId, resourceName, attemptId,
            artifactVersion, completionToken, assets.Headers, cancellationToken).ConfigureAwait(false);
        await EnableWorkersDevAsync(accessToken, accountId, resourceName, cancellationToken).ConfigureAwait(false);
        var subdomain = await GetAccountSubdomainAsync(accessToken, accountId, cancellationToken).ConfigureAwait(false);
        return new CloudflareProvisioningApplication(
            resourceName,
            new Uri($"https://{resourceName}.{subdomain}.workers.dev/"));
    }

    private async Task<string> UploadAssetsAsync(
        string accessToken,
        string accountId,
        string resourceName,
        PreparedAssetSet assets,
        CancellationToken cancellationToken)
    {
        var manifest = assets.Items.ToDictionary(
            item => item.Path,
            item => new AssetManifestEntry(item.Hash, item.Content.LongLength),
            StringComparer.Ordinal);
        using var sessionRequest = Authorized(
            HttpMethod.Post,
            $"accounts/{Segment(accountId)}/workers/scripts/{Segment(resourceName)}/assets-upload-session",
            accessToken);
        sessionRequest.Content = JsonContent.Create(new { manifest });
        using var sessionResponse = await http.SendAsync(sessionRequest, cancellationToken).ConfigureAwait(false);
        var session = await ReadEnvelopeAsync<AssetUploadSession>(sessionResponse, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(session.Jwt) || session.Buckets is null)
            throw InvalidResponse();
        if (session.Buckets.Count == 0) return session.Jwt;

        string? completionToken = null;
        foreach (var bucket in session.Buckets)
        {
            if (bucket.Count == 0) throw InvalidResponse();
            using var content = new MultipartFormDataContent();
            foreach (var hash in bucket)
            {
                if (!assets.ByHash.TryGetValue(hash, out var asset)) throw InvalidResponse();
                var encoded = new ByteArrayContent(Encoding.ASCII.GetBytes(Convert.ToBase64String(asset.Content)));
                encoded.Headers.ContentType = MediaTypeHeaderValue.Parse(asset.ContentType);
                content.Add(encoded, hash);
            }
            using var upload = Authorized(
                HttpMethod.Post,
                $"accounts/{Segment(accountId)}/workers/assets/upload?base64=true",
                session.Jwt);
            upload.Content = content;
            using var uploadResponse = await http.SendAsync(upload, cancellationToken).ConfigureAwait(false);
            var uploaded = await ReadEnvelopeAsync<AssetUploadCompletion>(uploadResponse, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(uploaded.Jwt)) completionToken = uploaded.Jwt;
        }
        return completionToken ?? throw InvalidResponse();
    }

    private async Task UploadWorkerAsync(
        string accessToken,
        string accountId,
        string databaseId,
        string resourceName,
        string attemptId,
        string artifactVersion,
        string completionToken,
        string? headers,
        CancellationToken cancellationToken)
    {
        // Cloudflare parses this control file from metadata, rather than serving it as an asset.
        // https://developers.cloudflare.com/api/resources/workers/subresources/scripts/methods/update/
        var assetConfig = new Dictionary<string, object>
        {
            ["not_found_handling"] = "single-page-application",
        };
        if (headers is not null) assetConfig["_headers"] = headers;
        var metadata = new
        {
            main_module = "index.js",
            compatibility_date = artifact.CompatibilityDate.ToString("yyyy-MM-dd"),
            assets = new
            {
                jwt = completionToken,
                config = assetConfig,
            },
            bindings = new object[]
            {
                new { type = "d1", name = "DB", database_id = databaseId },
                new { type = "assets", name = "ASSETS" },
                new { type = "plain_text", name = "INSTANCE_ID", text = attemptId },
                new { type = "plain_text", name = "ARTIFACT_VERSION", text = artifactVersion },
            },
            annotations = new Dictionary<string, string>
            {
                ["workers/message"] = $"Openza Personal Cloud {artifactVersion}",
                ["workers/tag"] = artifactVersion,
            },
            tags = new[] { "openza-personal-cloud" },
        };
        using var multipart = new MultipartFormDataContent();
        var metadataContent = new StringContent(JsonSerializer.Serialize(metadata, Json), Encoding.UTF8, "application/json");
        multipart.Add(metadataContent, "metadata");
        var module = new ByteArrayContent(artifact.WorkerModule);
        module.Headers.ContentType = MediaTypeHeaderValue.Parse("application/javascript+module");
        multipart.Add(module, "index.js", "index.js");
        using var request = Authorized(
            HttpMethod.Put,
            $"accounts/{Segment(accountId)}/workers/scripts/{Segment(resourceName)}",
            accessToken);
        request.Content = multipart;
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        _ = await ReadEnvelopeAsync<JsonElement>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnableWorkersDevAsync(
        string accessToken,
        string accountId,
        string resourceName,
        CancellationToken cancellationToken)
    {
        using var request = Authorized(
            HttpMethod.Post,
            $"accounts/{Segment(accountId)}/workers/scripts/{Segment(resourceName)}/subdomain",
            accessToken);
        request.Content = JsonContent.Create(new { enabled = true, previews_enabled = false });
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var enabled = await ReadEnvelopeAsync<WorkerSubdomainState>(response, cancellationToken).ConfigureAwait(false);
        if (!enabled.Enabled) throw InvalidResponse();
    }

    private async Task<string> GetAccountSubdomainAsync(
        string accessToken,
        string accountId,
        CancellationToken cancellationToken)
    {
        using var request = Authorized(
            HttpMethod.Get,
            $"accounts/{Segment(accountId)}/workers/subdomain",
            accessToken);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var result = await ReadEnvelopeAsync<AccountSubdomain>(response, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.Subdomain) ||
            result.Subdomain.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw InvalidResponse();
        return result.Subdomain;
    }

    private static PreparedAssetSet BuildAssets(IReadOnlyList<PersonalCloudStaticAsset> input)
    {
        if (input.Count == 0 || input.Count > 20_000)
            throw new PersonalCloudK0ContractException("artifact_invalid", "The Personal Cloud artifact assets are invalid.");
        var items = new List<PreparedAsset>(input.Count);
        var byHash = new Dictionary<string, PreparedAsset>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        string? headers = null;
        foreach (var item in input)
        {
            if (string.IsNullOrWhiteSpace(item.Path) || !item.Path.StartsWith('/') || item.Path.Contains("..", StringComparison.Ordinal) ||
                item.Path.IndexOfAny(['?', '#', '\\']) >= 0 || !paths.Add(item.Path) || item.Content.Length > 25 * 1024 * 1024)
                throw new PersonalCloudK0ContractException("artifact_invalid", "The Personal Cloud artifact assets are invalid.");
            if (!MediaTypeHeaderValue.TryParse(item.ContentType, out _))
                throw new PersonalCloudK0ContractException("artifact_invalid", "The Personal Cloud artifact content type is invalid.");
            if (item.Path == "/_headers")
            {
                try
                {
                    headers = new UTF8Encoding(false, true).GetString(item.Content);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new PersonalCloudK0ContractException("artifact_invalid", "The Personal Cloud headers file is not valid UTF-8.", exception);
                }
                continue;
            }
            var hash = Convert.ToHexStringLower(SHA256.HashData(item.Content).AsSpan(0, 16));
            var prepared = new PreparedAsset(item.Path, item.ContentType, item.Content, hash);
            items.Add(prepared);
            byHash.TryAdd(hash, prepared);
        }
        if (items.Count == 0)
            throw new PersonalCloudK0ContractException("artifact_invalid", "The Personal Cloud artifact has no public assets.");
        return new PreparedAssetSet(items, byHash, headers);
    }

    private static void ValidateArtifact(PersonalCloudDeploymentArtifact value, string expectedVersion)
    {
        if (!string.Equals(value.Version, expectedVersion, StringComparison.Ordinal) || value.WorkerModule.Length == 0 ||
            value.WorkerModule.Length > 10 * 1024 * 1024 || value.CompatibilityDate < new DateOnly(2024, 1, 1))
            throw new PersonalCloudK0ContractException("artifact_mismatch", "The Personal Cloud deployment artifact is invalid or does not match the requested version.");
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string relativePath, string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 4096)
            throw new PersonalCloudK0ContractException("unauthorized", "Temporary Cloudflare authorization is missing.");
        var request = new HttpRequestMessage(method, new Uri(ApiRoot, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task<T> ReadEnvelopeAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new PersonalCloudK0ContractException("cloudflare_api_failed", "Cloudflare could not deploy the Personal Cloud artifact.");
        try
        {
            var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(Json, cancellationToken).ConfigureAwait(false);
            if (envelope is null || !envelope.Success || envelope.Result is null) throw InvalidResponse();
            return envelope.Result;
        }
        catch (JsonException exception)
        {
            throw new PersonalCloudK0ContractException("invalid_response", "Cloudflare returned an invalid deployment response.", exception);
        }
    }

    private static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new PersonalCloudK0ContractException("invalid_request", "A Cloudflare resource identifier is invalid.");
        return Uri.EscapeDataString(value);
    }

    private static PersonalCloudK0ContractException InvalidResponse() =>
        new("invalid_response", "Cloudflare returned an invalid deployment response.");

    private sealed record PreparedAsset(string Path, string ContentType, byte[] Content, string Hash);
    private sealed record PreparedAssetSet(IReadOnlyList<PreparedAsset> Items, IReadOnlyDictionary<string, PreparedAsset> ByHash, string? Headers);
    private sealed record AssetManifestEntry(string Hash, long Size);
    private sealed record AssetUploadSession(IReadOnlyList<IReadOnlyList<string>>? Buckets, string? Jwt);
    private sealed record AssetUploadCompletion(string? Jwt);
    private sealed record WorkerSubdomainState(bool Enabled, [property: JsonPropertyName("previews_enabled")] bool PreviewsEnabled);
    private sealed record AccountSubdomain(string Subdomain);
    private sealed record ApiEnvelope<T>(bool Success, T Result);
}
