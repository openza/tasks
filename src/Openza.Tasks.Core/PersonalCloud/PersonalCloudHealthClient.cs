using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.PersonalCloud;

public sealed class PersonalCloudHealthClient(HttpClient http)
{
    // Schema 18 adds parent-cycle invariants and 19 adds provider delivery
    // ownership; 20 guards ambiguous provider aliases; 21 adds optional initial upload feedback.
    // Schema22 adds bounded sequential batches; schema23 adds workspace/catalog;
    // schema24 adds optional byte-bounded workspace paging.
    // Schema26 adds optional current catch-up metadata; schema25 stays supported.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task VerifyAsync(Uri serviceOrigin, string artifactVersion, CancellationToken cancellationToken = default)
    {
        var body = await InspectAsync(serviceOrigin, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(body.ArtifactVersion, artifactVersion, StringComparison.Ordinal))
            throw new PersonalCloudK0ContractException("health_mismatch", "The deployed Personal Cloud version does not match the requested artifact.");
    }

    public async Task<PersonalCloudHealth> InspectAsync(Uri serviceOrigin, CancellationToken cancellationToken = default)
    {
        if (!serviceOrigin.IsAbsoluteUri ||
            (serviceOrigin.Scheme != Uri.UriSchemeHttps && !(serviceOrigin.Scheme == Uri.UriSchemeHttp && serviceOrigin.IsLoopback)) ||
            !string.IsNullOrEmpty(serviceOrigin.Query) || !string.IsNullOrEmpty(serviceOrigin.Fragment))
            throw new PersonalCloudK0ContractException("invalid_request", "A secure Personal Cloud origin is required for health verification.");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(serviceOrigin, "/health"));
        using var response = await PersonalCloudDispatchHttp.SendAsync(http, request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed or System.Net.HttpStatusCode.NotImplemented)
            throw new PersonalCloudK0ContractException("health_unsupported", "This Personal Cloud does not expose a supported health endpoint.");
        if (!response.IsSuccessStatusCode)
            throw new PersonalCloudK0ContractException("health_failed", "The deployed Personal Cloud did not pass its health check.");
        try
        {
            var body = await response.Content.ReadFromJsonAsync<HealthResponse>(Json, cancellationToken).ConfigureAwait(false);
            if (body is null || body.Status != "ok" || body.ProtocolVersion != PersonalCloudK0Protocol.Version ||
                body.Product != PersonalCloudK0Protocol.TasksProduct || body.SchemaVersion is not (17 or 18 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26) ||
                string.IsNullOrWhiteSpace(body.ArtifactVersion) || body.ArtifactVersion.Length > 128)
                throw new PersonalCloudK0ContractException("health_mismatch", "The deployed Personal Cloud is not compatible with this Openza build.");
            var batch = ParseMutationBatch(body.Capabilities);
            var workspace = ParseWorkspace(body.Capabilities, body.SchemaVersion);
            var catalogProtocol = ParseProviderCatalog(body.Capabilities);
            var catalog = catalogProtocol > 0;
            var paging = ParseWorkspacePaging(body.Capabilities);
            if ((workspace || catalog) && body.SchemaVersion is not (23 or 24 or 25 or 26) || catalog && !workspace || paging && (!workspace || body.SchemaVersion is not (24 or 25 or 26)))
                throw new PersonalCloudK0ContractException("health_mismatch", "The workspace capabilities do not match the cloud schema.");
            return new PersonalCloudHealth(body.ArtifactVersion, body.SchemaVersion)
            {
                MutationBatch = batch, SupportsWorkspace = workspace, SupportsProviderCatalog = catalog, ProviderCatalogProtocolVersion = catalogProtocol,
                SupportsWorkspacePaging = paging,
                SupportsFanoutBudget = ParseFanoutBudget(body.Capabilities),
                SupportsCatchupStatus = ParseCatchupStatus(body.Capabilities),
            };
        }
        catch (JsonException exception)
        {
            throw new PersonalCloudK0ContractException("invalid_response", "Personal Cloud returned an invalid health response.", exception);
        }
    }

    private static bool ParseWorkspace(JsonElement? capabilities, int schemaVersion)
    {
        if (capabilities is null || capabilities.Value.ValueKind != JsonValueKind.Object ||
            !capabilities.Value.TryGetProperty("workspace", out var value)) return false;
        if (!ExactCapability(value, "protocolVersion", "path", "schemaVersion") ||
            !Integer(value, "protocolVersion", 1) || !Text(value, "path", "/v1/tasks/mutations") || !Integer(value, "schemaVersion", schemaVersion))
            throw new PersonalCloudK0ContractException("health_mismatch", "Invalid workspace capability.");
        return true;
    }

    private static bool ParseFanoutBudget(JsonElement? capabilities)
    {
        if (capabilities is null || capabilities.Value.ValueKind != JsonValueKind.Object ||
            !capabilities.Value.TryGetProperty("fanoutBudget", out var value)) return false;
        if (!ExactCapability(value, "protocolVersion", "requestHeader") ||
            !Integer(value, "protocolVersion", 1) || !Text(value, "requestHeader", PersonalCloudMutationCost.FanoutHeader))
            throw new PersonalCloudK0ContractException("health_mismatch", "Invalid fanout budget capability.");
        return true;
    }

    private static bool ParseCatchupStatus(JsonElement? capabilities)
    {
        if (capabilities is null || capabilities.Value.ValueKind != JsonValueKind.Object ||
            !capabilities.Value.TryGetProperty("catchupStatus", out var value)) return false;
        if (!ExactCapability(value, "protocolVersion", "path", "staleAfterSeconds") ||
            !Integer(value, "protocolVersion", 1) || !Text(value, "path", "/sync/catchup-status") || !Integer(value, "staleAfterSeconds", 900))
            throw new PersonalCloudK0ContractException("health_mismatch", "Invalid catch-up status capability.");
        return true;
    }

    private static int ParseProviderCatalog(JsonElement? capabilities)
    {
        if (capabilities is null || capabilities.Value.ValueKind != JsonValueKind.Object ||
            !capabilities.Value.TryGetProperty("providerCatalog", out var value)) return 0;
        if (!ExactCapability(value, "protocolVersion", "path", "intentsPath") ||
            !(Integer(value, "protocolVersion", 1) || Integer(value, "protocolVersion", 2)) || !Text(value, "path", "/v1/provider/catalog") || !Text(value, "intentsPath", "/v1/provider/intents"))
            throw new PersonalCloudK0ContractException("health_mismatch", "Invalid provider catalog capability.");
        return value.GetProperty("protocolVersion").GetInt32();
    }

    private static bool ParseWorkspacePaging(JsonElement? capabilities)
    {
        if (capabilities is null || capabilities.Value.ValueKind != JsonValueKind.Object || !capabilities.Value.TryGetProperty("workspacePaging", out var value)) return false;
        if (!ExactCapability(value, "protocolVersion", "bootstrapPath", "changesPath", "maxResponseUtf8Bytes", "maxPageRecords") ||
            !Integer(value, "protocolVersion", 1) || !Text(value, "bootstrapPath", "/sync/bootstrap") || !Text(value, "changesPath", "/sync/changes") ||
            !Integer(value, "maxResponseUtf8Bytes", 2097152) || !Integer(value, "maxPageRecords", 100))
            throw new PersonalCloudK0ContractException("health_mismatch", "Invalid workspace paging capability.");
        return true;
    }

    private static bool ExactCapability(JsonElement value, params string[] keys) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Count() == keys.Length && keys.All(key => value.TryGetProperty(key, out _));
    private static bool Integer(JsonElement value, string key, int expected) => value.TryGetProperty(key, out var item) &&
        item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number) && number == expected;
    private static bool Text(JsonElement value, string key, string expected) => value.TryGetProperty(key, out var item) &&
        item.ValueKind == JsonValueKind.String && item.GetString() == expected;

    private static PersonalCloudMutationBatchCapability? ParseMutationBatch(JsonElement? capabilities)
    {
        if (capabilities is null || capabilities.Value.ValueKind == JsonValueKind.Null) return null;
        if (capabilities.Value.ValueKind != JsonValueKind.Object)
            throw new PersonalCloudK0ContractException("health_mismatch", "Invalid cloud capabilities.");
        if (!capabilities.Value.TryGetProperty("mutationBatch", out var batch)) return null;
        if (batch.ValueKind != JsonValueKind.Object || batch.EnumerateObject().Count() != 4 ||
            !batch.TryGetProperty("protocolVersion", out var protocol) || protocol.ValueKind != JsonValueKind.Number || !protocol.TryGetInt32(out var version) || version != 1 ||
            !batch.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String || path.GetString() != "/v1/tasks/mutations/batch" ||
            !batch.TryGetProperty("maxMutations", out var items) || items.ValueKind != JsonValueKind.Number || !items.TryGetInt32(out var count) || count is < 1 or > 2 ||
            !batch.TryGetProperty("maxRequestUtf8Bytes", out var bytes) || bytes.ValueKind != JsonValueKind.Number || !bytes.TryGetInt32(out var limit) || limit is < 1 or > 16384)
            throw new PersonalCloudK0ContractException("health_mismatch", "Invalid mutation batch capability.");
        return new(count, limit);
    }

    private sealed record HealthResponse(string Status, int ProtocolVersion, string Product, string ArtifactVersion, int SchemaVersion, JsonElement? Capabilities = null);
}

public sealed record PersonalCloudMutationBatchCapability(int MaxMutations, int MaxRequestUtf8Bytes);
public sealed record PersonalCloudHealth(string ArtifactVersion, int SchemaVersion)
{
    public PersonalCloudMutationBatchCapability? MutationBatch { get; init; }
    public bool SupportsWorkspace { get; init; }
    public bool SupportsProviderCatalog { get; init; }
    public int ProviderCatalogProtocolVersion { get; init; }
    public bool SupportsWorkspacePaging { get; init; }
    public bool SupportsFanoutBudget { get; init; }
    public bool SupportsCatchupStatus { get; init; }
}
