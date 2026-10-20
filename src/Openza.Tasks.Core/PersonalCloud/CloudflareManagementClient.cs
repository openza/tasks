using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.PersonalCloud;

public sealed record CloudflareAccount(string Id, string Name);
public sealed record CloudflareD1Database(string Id, string Name);
public sealed record CloudflareD1Export(string FileName, Uri DownloadUri);

public sealed class CloudflareManagementClient(HttpClient http)
{
    private static readonly Uri ApiRoot = new("https://api.cloudflare.com/client/v4/");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<CloudflareAccount>> ListAccountsAsync(string accessToken, CancellationToken cancellationToken = default) =>
        ListPagesAsync("accounts", accessToken, MapAccount, cancellationToken);

    public Task<IReadOnlyList<CloudflareD1Database>> ListDatabasesAsync(string accessToken, string accountId, CancellationToken cancellationToken = default) =>
        ListPagesAsync($"accounts/{Segment(accountId)}/d1/database", accessToken, MapDatabase, cancellationToken);

    public async Task VerifyApplicationAsync(string accessToken, string accountId, string workerName,
        string databaseId, PersonalCloudConnectionProfile connection, CancellationToken cancellationToken = default)
    {
        using var originRequest = Authorized(HttpMethod.Get, $"accounts/{Segment(accountId)}/workers/subdomain", accessToken);
        using var originResponse = await http.SendAsync(originRequest, cancellationToken).ConfigureAwait(false);
        var subdomain = (await ReadEnvelopeAsync<ApiSubdomain>(originResponse, cancellationToken).ConfigureAwait(false)).Result.Subdomain;
        if (string.IsNullOrWhiteSpace(subdomain) || subdomain.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw InvalidResponse();
        if (new Uri($"https://{workerName}.{subdomain}.workers.dev/") != connection.ServiceOrigin)
            throw ResourceMismatch();

        // Read existing metadata before any upload can replace the DB/identity bindings.
        // https://developers.cloudflare.com/api/typescript/resources/workers/subresources/scripts/subresources/script_and_version_settings/methods/get/
        using var request = Authorized(HttpMethod.Get, $"accounts/{Segment(accountId)}/workers/scripts/{Segment(workerName)}/settings", accessToken);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var settings = (await ReadEnvelopeAsync<JsonElement>(response, cancellationToken).ConfigureAwait(false)).Result;
        if (settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty("bindings", out var bindings) || bindings.ValueKind != JsonValueKind.Array)
            throw InvalidResponse();
        bool Matches(string name, string type, string field, string expected)
        {
            var matches = bindings.EnumerateArray().Where(item => Text(item, "name", name)).ToArray();
            return matches.Length == 1 && Text(matches[0], "type", type) && Text(matches[0], field, expected);
        }
        if (!Matches("DB", "d1", "database_id", databaseId) || !Matches("INSTANCE_ID", "plain_text", "text", connection.InstanceId))
            throw ResourceMismatch();
    }

    private static bool Text(JsonElement value, string field, string expected) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(field, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() == expected;

    private static PersonalCloudK0ContractException ResourceMismatch() => new("resource_mismatch",
        "The authorized Personal Cloud origin, database or instance does not match the saved connection. No update was started.");

    public async Task<CloudflareD1Database> EnsureDatabaseAsync(
        string accessToken,
        string accountId,
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        ValidateName(databaseName, nameof(databaseName));
        var existing = (await ListDatabasesAsync(accessToken, accountId, cancellationToken).ConfigureAwait(false))
            .Where(database => string.Equals(database.Name, databaseName, StringComparison.Ordinal)).ToArray();
        if (existing.Length > 1)
            throw new PersonalCloudK0ContractException("resource_ambiguous", "Cloudflare contains multiple Personal Cloud databases with the expected name.");
        if (existing.Length == 1) return existing[0];

        using var request = Authorized(HttpMethod.Post, $"accounts/{Segment(accountId)}/d1/database", accessToken);
        request.Content = JsonContent.Create(new { name = databaseName });
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var envelope = await ReadEnvelopeAsync<ApiResource>(response, cancellationToken).ConfigureAwait(false);
        var id = envelope.Result.Uuid ?? envelope.Result.Id;
        if (string.IsNullOrWhiteSpace(id) || !string.Equals(envelope.Result.Name, databaseName, StringComparison.Ordinal))
            throw InvalidResponse();
        return new CloudflareD1Database(id, envelope.Result.Name);
    }

    public async Task ExecuteD1Async(
        string accessToken,
        string accountId,
        string databaseId,
        string sql,
        IReadOnlyList<object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql) || sql.Length > 100_000)
            throw new PersonalCloudK0ContractException("invalid_request", "The D1 statement is invalid.");
        _ = await QueryD1Async(accessToken, accountId, databaseId, sql, parameters, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> QueryD1Async(
        string accessToken,
        string accountId,
        string databaseId,
        string sql,
        IReadOnlyList<object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql) || sql.Length > 100_000)
            throw new PersonalCloudK0ContractException("invalid_request", "The D1 statement is invalid.");
        using var request = Authorized(HttpMethod.Post,
            $"accounts/{Segment(accountId)}/d1/database/{Segment(databaseId)}/query", accessToken);
        request.Content = JsonContent.Create(new { sql, @params = parameters ?? [] });
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var result = (await ReadEnvelopeAsync<JsonElement>(response, cancellationToken).ConfigureAwait(false)).Result;
        if (result.ValueKind != JsonValueKind.Array || result.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object ||
            item.TryGetProperty("success", out var success) && success.ValueKind != JsonValueKind.True))
            throw InvalidResponse();
        return result;
    }

    public async Task<CloudflareD1Export> ExportDatabaseAsync(
        string accessToken,
        string accountId,
        string databaseId,
        CancellationToken cancellationToken = default)
    {
        string? bookmark = null;
        for (var attempt = 0; attempt < 120; attempt++)
        {
            using var request = Authorized(HttpMethod.Post,
                $"accounts/{Segment(accountId)}/d1/database/{Segment(databaseId)}/export", accessToken);
            request.Content = bookmark is null
                ? JsonContent.Create(new { output_format = "polling" })
                : JsonContent.Create(new { output_format = "polling", current_bookmark = bookmark });
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var export = (await ReadEnvelopeAsync<ApiExport>(response, cancellationToken).ConfigureAwait(false)).Result;
            if (string.Equals(export.Status, "error", StringComparison.Ordinal))
                throw new PersonalCloudK0ContractException("cloudflare_export_failed", "Cloudflare could not export the Personal Cloud database.");
            if (string.Equals(export.Status, "complete", StringComparison.Ordinal))
            {
                if (export.Result is null || string.IsNullOrWhiteSpace(export.Result.Filename) ||
                    !Uri.TryCreate(export.Result.SignedUrl, UriKind.Absolute, out var download) ||
                    download.Scheme != Uri.UriSchemeHttps)
                    throw InvalidResponse();
                return new CloudflareD1Export(export.Result.Filename, download);
            }
            if (string.IsNullOrWhiteSpace(export.AtBookmark)) throw InvalidResponse();
            bookmark = export.AtBookmark;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        throw new PersonalCloudK0ContractException("cloudflare_export_timeout", "Cloudflare did not finish the Personal Cloud export in time.");
    }

    public Task DeleteWorkerAsync(
        string accessToken,
        string accountId,
        string workerName,
        CancellationToken cancellationToken = default) =>
        DeleteResourceAsync($"accounts/{Segment(accountId)}/workers/scripts/{Segment(workerName)}", accessToken, cancellationToken);

    public Task DeleteDatabaseAsync(
        string accessToken,
        string accountId,
        string databaseId,
        CancellationToken cancellationToken = default) =>
        DeleteResourceAsync($"accounts/{Segment(accountId)}/d1/database/{Segment(databaseId)}", accessToken, cancellationToken);

    private async Task DeleteResourceAsync(string relativePath, string accessToken, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Delete, relativePath, accessToken);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return;
        if (!response.IsSuccessStatusCode)
            throw new PersonalCloudK0ContractException("cloudflare_api_failed", "Cloudflare could not remove the Personal Cloud resource.");
        try
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return;
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                throw InvalidResponse();
        }
        catch (JsonException exception)
        {
            throw new PersonalCloudK0ContractException("invalid_response", "Cloudflare returned an invalid resource response.", exception);
        }
    }

    private async Task<IReadOnlyList<TOutput>> ListPagesAsync<TOutput>(
        string relativePath,
        string accessToken,
        Func<ApiResource, TOutput> map,
        CancellationToken cancellationToken)
    {
        var output = new List<TOutput>();
        for (var page = 1; page <= 100; page++)
        {
            using var request = Authorized(HttpMethod.Get, $"{relativePath}?page={page}&per_page=50", accessToken);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var envelope = await ReadEnvelopeAsync<List<ApiResource>>(response, cancellationToken).ConfigureAwait(false);
            output.AddRange(envelope.Result.Select(map));
            if (envelope.ResultInfo is null || page >= envelope.ResultInfo.TotalPages) return output;
        }
        throw new PersonalCloudK0ContractException("resource_limit", "Cloudflare returned too many resource pages.");
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

    private static async Task<ApiEnvelope<T>> ReadEnvelopeAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new PersonalCloudK0ContractException("cloudflare_api_failed", "Cloudflare could not complete the Personal Cloud resource operation.");
        try
        {
            var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(Json, cancellationToken).ConfigureAwait(false);
            if (envelope is null || !envelope.Success || envelope.Result is null) throw InvalidResponse();
            return envelope;
        }
        catch (JsonException exception)
        {
            throw new PersonalCloudK0ContractException("invalid_response", "Cloudflare returned an invalid resource response.", exception);
        }
    }

    private static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new PersonalCloudK0ContractException("invalid_request", "A Cloudflare resource identifier is invalid.");
        return Uri.EscapeDataString(value);
    }

    private static CloudflareAccount MapAccount(ApiResource item)
    {
        if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name)) throw InvalidResponse();
        return new CloudflareAccount(item.Id, item.Name);
    }

    private static CloudflareD1Database MapDatabase(ApiResource item)
    {
        var id = item.Uuid ?? item.Id;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(item.Name)) throw InvalidResponse();
        return new CloudflareD1Database(id, item.Name);
    }

    private static void ValidateName(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new PersonalCloudK0ContractException("invalid_request", $"{field} is invalid.");
    }

    private static PersonalCloudK0ContractException InvalidResponse() =>
        new("invalid_response", "Cloudflare returned an invalid resource response.");

    private sealed record ApiResource(
        string Id,
        string Name,
        [property: JsonPropertyName("uuid")] string? Uuid = null);

    private sealed record ApiSubdomain(string Subdomain);

    private sealed record ApiResultInfo(
        [property: JsonPropertyName("total_pages")] int TotalPages);

    private sealed record ApiEnvelope<T>(
        bool Success,
        T Result,
        [property: JsonPropertyName("result_info")] ApiResultInfo? ResultInfo);

    private sealed record ApiExport(
        [property: JsonPropertyName("at_bookmark")] string? AtBookmark,
        string? Status,
        ApiExportDownload? Result);

    private sealed record ApiExportDownload(
        string Filename,
        [property: JsonPropertyName("signed_url")] string SignedUrl);
}
