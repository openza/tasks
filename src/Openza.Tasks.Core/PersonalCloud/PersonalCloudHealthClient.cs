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
    // Cloud releases ship independently and may be newer than this Desktop
    // (personal-cloud/docs/cloud-releases.md): unknown health fields, unknown
    // capabilities, extra capability keys and higher schema versions are
    // accepted, while the values this Desktop relies on are still checked exactly.
    private const int MinimumSchemaVersion = 17;

    // This Desktop's version, compared with a cloud's desktopCompatibility capability.
    // Release builds stamp the same Version on every assembly.
    public static Version DesktopVersion { get; set; } = Normalize(typeof(PersonalCloudHealthClient).Assembly.GetName().Version);

    private static Version Normalize(Version? value) => value is null ? new Version(0, 0, 0) : new Version(value.Major, value.Minor, Math.Max(0, value.Build));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
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
                body.Product != PersonalCloudK0Protocol.TasksProduct || body.SchemaVersion < MinimumSchemaVersion ||
                string.IsNullOrWhiteSpace(body.ArtifactVersion) || body.ArtifactVersion.Length > 128)
                throw new PersonalCloudK0ContractException("health_mismatch", "The deployed Personal Cloud is not compatible with this Openza build.");
            RequireSupportedDesktop(body.Capabilities);
            var batch = ParseMutationBatch(body.Capabilities);
            var workspace = ParseWorkspace(body.Capabilities, body.SchemaVersion);
            var catalogProtocol = ParseProviderCatalog(body.Capabilities);
            var catalog = catalogProtocol > 0;
            var paging = ParseWorkspacePaging(body.Capabilities);
            if ((workspace || catalog) && body.SchemaVersion < 23 || catalog && !workspace || paging && (!workspace || body.SchemaVersion < 24))
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

    // A cloud release that needs a newer Desktop says so here, so this Desktop stops
    // safely even when another computer installed that release.
    private static void RequireSupportedDesktop(JsonElement? capabilities)
    {
        if (capabilities is null || capabilities.Value.ValueKind != JsonValueKind.Object ||
            !capabilities.Value.TryGetProperty("desktopCompatibility", out var value)) return;
        if (!ExactCapability(value, "protocolVersion", "minDesktopVersion") || !Integer(value, "protocolVersion", 1) ||
            value.GetProperty("minDesktopVersion").ValueKind != JsonValueKind.String ||
            !Version.TryParse(value.GetProperty("minDesktopVersion").GetString(), out var minimum) || minimum.Revision != -1 || minimum.Build < 0)
            throw new PersonalCloudK0ContractException("health_mismatch", "Invalid desktop compatibility capability.");
        if (DesktopVersion < minimum)
            throw new PersonalCloudK0ContractException("desktop_update_required",
                $"This Personal Cloud needs Openza Desktop {minimum} or newer. Update Openza Desktop to keep syncing; your tasks stay on this computer.");
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

    // Newer clouds may add keys to a capability; the keys this Desktop uses must be present.
    private static bool ExactCapability(JsonElement value, params string[] keys) => value.ValueKind == JsonValueKind.Object &&
        keys.All(key => value.TryGetProperty(key, out _));
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
        if (batch.ValueKind != JsonValueKind.Object ||
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
