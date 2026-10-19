using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed partial class PersonalCloudBatchSyncTests : IDisposable
{
    private const string Instance = "instance_batch_synthetic";
    private const string Device = "device_batch_synthetic";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-batch-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Schema9_upgrade_preserves_pending_envelopes_and_creates_observation_fencing_and_initial_marker_tables()
    {
        var (store, _, _) = await CreateAsync();
        var original = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE personal_cloud_initial_sync; DROP TABLE personal_cloud_source_observations; DROP INDEX idx_personal_cloud_outbox_source_history; PRAGMA user_version=9;";
            await command.ExecuteNonQueryAsync();
        }
        var upgraded = new SqliteTaskStore(store.DatabasePath);
        await upgraded.InitializeAsync();
        Assert.Equal(original, await upgraded.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Null(await upgraded.GetPersonalCloudInitialSyncProgressAsync(Instance, Device));
        await upgraded.BeginPersonalCloudInitialSyncAsync(Instance, Device);
        Assert.NotNull(await upgraded.GetPersonalCloudInitialSyncProgressAsync(Instance, Device));
        await using var read = new SqliteConnection($"Data Source={store.DatabasePath};Mode=ReadOnly");
        await read.OpenAsync();
        var inspect = read.CreateCommand();
        inspect.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('personal_cloud_source_observations','idx_personal_cloud_outbox_source_history')";
        Assert.Equal(2L, await inspect.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Ordered_batches_acknowledge_exact_intents_and_keep_the_existing_hundred_change_pass()
    {
        var (store, client, handler) = await CreateAsync(103);
        var original = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        var first = await client.SyncAsync();
        Assert.Equal(100, first.PushedMutations);
        Assert.Equal(50, handler.BatchPosts);
        Assert.Equal(0, handler.SinglePosts);
        Assert.Equal(original.Take(100).Select(item => item.CanonicalJson), handler.Envelopes);
        Assert.Equal(3, (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Count);
        Assert.Equal(3, (await client.SyncAsync()).PushedMutations);
        Assert.Equal(52, handler.BatchPosts);
        Assert.Equal(2, handler.HealthRequests);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.All(handler.BatchSizes, size => Assert.InRange(size, 1, 2));
        Assert.All(handler.BatchBytes, bytes => Assert.InRange(bytes, 1, 16384));
    }

    [Theory]
    [InlineData(404)] [InlineData(405)] [InlineData(501)]
    public async Task Unsupported_route_falls_back_once_to_exact_persisted_single_envelopes(int status)
    {
        var (store, client, handler) = await CreateAsync();
        var original = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        handler.RouteStatus = (HttpStatusCode)status;
        Assert.Equal(3, (await client.SyncAsync()).PushedMutations);
        Assert.Equal(1, handler.BatchPosts);
        Assert.Equal(3, handler.SinglePosts);
        Assert.Equal(original.Select(item => item.CanonicalJson), handler.Envelopes);
    }

    [Fact]
    public async Task Missing_capability_uses_single_delivery_even_on_schema22()
    {
        var (_, client, handler) = await CreateAsync();
        handler.AdvertiseBatch = false;
        Assert.Equal(3, (await client.SyncAsync()).PushedMutations);
        Assert.Equal(0, handler.BatchPosts);
        Assert.Equal(3, handler.SinglePosts);
    }
    [Fact]
    public async Task Scoped_envelopes_are_preserved_when_current_cloud_only_advertises_legacy_catalog()
    {
        var (store, client, handler) = await CreateAsync(0);
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true, providerCatalogProtocolVersion: 2);
        await store.EnqueuePersonalCloudProviderScopeAsync(Instance, Device, IntegrationIds.Todoist, "todoist_default", true, [], []);
        var original = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        handler.SchemaVersion = 23;
        handler.CapabilityOverride = """{"workspace":{"protocolVersion":1,"path":"/v1/tasks/mutations","schemaVersion":23},"providerCatalog":{"protocolVersion":1,"path":"/v1/provider/catalog","intentsPath":"/v1/provider/intents"}}""";
        Assert.Equal("health_mismatch", (await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.SyncAsync())).Code);
        Assert.True(await store.SupportsPersonalCloudProviderScopesAsync(Instance));
        Assert.Equal(original, Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device))); Assert.Equal(0, handler.SinglePosts); Assert.Equal(0, handler.BatchPosts);
    }

    [Fact]
    public async Task Oversized_compound_rejection_preserves_the_exact_ordered_outbox_and_reports_the_bound()
    {
        var (store, client, handler) = await CreateAsync(); handler.AdvertiseBatch = false; handler.SingleError = "response_too_large";
        var original = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        var result = await client.SyncAsync();
        Assert.Equal("response_too_large", result.BlockedCode); Assert.Equal(0, result.PushedMutations);
        Assert.Equal(original.Select(e => e.CanonicalJson), (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Select(e => e.CanonicalJson));
    }

    [Theory]
    [InlineData(400)] [InlineData(401)] [InlineData(500)]
    public async Task Ambiguous_or_auth_failure_preserves_exact_intents_and_never_blindly_falls_back(int status)
    {
        var (store, client, handler) = await CreateAsync();
        var original = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        handler.RouteStatus = (HttpStatusCode)status;
        if (status == 400)
            Assert.Equal("invalid_request", (await client.SyncAsync()).BlockedCode);
        else
            await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.SyncAsync());
        Assert.Equal(0, handler.SinglePosts);
        Assert.Equal(original, await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        handler.RouteStatus = null;
        Assert.Equal(3, (await client.SyncAsync()).PushedMutations);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Theory]
    [InlineData("version_conflict")] [InlineData("mutation_id_reused")] [InlineData("mutation_id_reuse")]
    public async Task Partial_semantic_rejection_acknowledges_only_the_durable_ordered_prefix(string code)
    {
        var (store, client, handler) = await CreateAsync();
        var original = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        handler.PartialFailure = code;
        var blocked = await client.SyncAsync();
        Assert.Equal(1, blocked.PushedMutations);
        Assert.Equal(code, blocked.BlockedCode);
        Assert.Equal(original.Skip(1), await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal(0, handler.SinglePosts);
        handler.PartialFailure = null;
        var restarted = await CreateClientAsync(new SqliteTaskStore(store.DatabasePath), handler);
        Assert.Equal(2, (await restarted.SyncAsync()).PushedMutations);
    }

    [Fact]
    public async Task Single_delivery_recognizes_public_mutation_id_reused_and_preserves_the_original_content()
    {
        var (store, client, handler) = await CreateAsync();
        var original = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        handler.AdvertiseBatch = false;
        handler.SingleError = "mutation_id_reused";
        var result = await client.SyncAsync();
        Assert.Equal("mutation_id_reused", result.BlockedCode);
        Assert.Equal(0, result.PushedMutations);
        Assert.Equal(original, await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal(1, handler.SinglePosts);
    }

    [Fact]
    public async Task Partial_transient_failure_keeps_the_failed_suffix_and_replays_it_exactly()
    {
        var (store, client, handler) = await CreateAsync();
        var original = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        handler.PartialFailure = "sync_failed";
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.SyncAsync());
        Assert.Equal(original.Skip(1), await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        handler.PartialFailure = null;
        Assert.Equal(2, (await client.SyncAsync()).PushedMutations);
    }

    [Fact]
    public async Task Lost_response_after_server_commit_replays_identical_ids_sequences_and_receives_duplicates()
    {
        var (store, client, handler) = await CreateAsync();
        var original = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        handler.LoseResponse = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SyncAsync());
        Assert.Equal(original, await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        handler.LoseResponse = false;
        var restarted = await CreateClientAsync(new SqliteTaskStore(store.DatabasePath), handler);
        Assert.Equal(3, (await restarted.SyncAsync()).PushedMutations);
        Assert.Equal(2, handler.DuplicateReceipts);
        Assert.Equal(original.Take(2).Select(item => item.CanonicalJson), handler.Envelopes.Take(2));
        Assert.Equal(original.Select(item => item.CanonicalJson), handler.Envelopes.Skip(2));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Theory]
    [InlineData("order")] [InlineData("version")] [InlineData("count")] [InlineData("instance")]
    [InlineData("protocol")] [InlineData("stopped")] [InlineData("unknown")] [InlineData("json")]
    public async Task Invalid_batch_response_acknowledges_nothing_even_when_the_first_receipt_is_valid(string defect)
    {
        var (store, client, handler) = await CreateAsync();
        var original = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        handler.ResponseDefect = defect;
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.SyncAsync());
        Assert.Equal("invalid_response", error.Code);
        Assert.Equal(original, await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal(0, handler.SinglePosts);
        handler.ResponseDefect = null;
        Assert.Equal(3, (await client.SyncAsync()).PushedMutations);
    }

    [Fact]
    public async Task Entire_utf8_wrapper_bound_splits_batches_and_single_fallback_never_rewrites_large_intent()
    {
        var (store, client, handler) = await CreateAsync(3, description: new string('界', 1000));
        var original = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        handler.MaxBytes = Encoding.UTF8.GetByteCount(original[0].CanonicalJson) + 40;
        Assert.Equal(3, (await client.SyncAsync()).PushedMutations);
        Assert.Equal(3, handler.BatchPosts);
        Assert.All(handler.BatchSizes, size => Assert.Equal(1, size));

        var (other, singleClient, singleHandler) = await CreateAsync(1, description: new string('界', 1000));
        var envelope = Assert.Single(await other.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        singleHandler.MaxBytes = Encoding.UTF8.GetByteCount(envelope.CanonicalJson);
        Assert.Equal(1, (await singleClient.SyncAsync()).PushedMutations);
        Assert.Equal(0, singleHandler.BatchPosts);
        Assert.Equal(envelope.CanonicalJson, Assert.Single(singleHandler.Envelopes));
    }

    [Theory]
    [InlineData("\"1\"", "2", "16384")]
    [InlineData("1", "\"2\"", "16384")]
    [InlineData("1", "2", "\"16384\"")]
    [InlineData("1", "3", "16384")]
    [InlineData("1", "2", "16385")]
    public async Task Malformed_capability_is_rejected_without_runtime_type_exception(string protocol, string items, string bytes)
    {
        var handler = new Handler { CapabilityOverride = $$$"""{"mutationBatch":{"protocolVersion":{{{protocol}}},"path":"/v1/tasks/mutations/batch","maxMutations":{{{items}}},"maxRequestUtf8Bytes":{{{bytes}}}}}""" };
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => new PersonalCloudHealthClient(new HttpClient(handler)).InspectAsync(new("https://synthetic.example.test/")));
        Assert.Equal("health_mismatch", error.Code);
    }

    private async Task<(SqliteTaskStore Store, PersonalCloudSyncClient Client, Handler Handler)> CreateAsync(int count = 3, string? description = null)
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, $"{Guid.NewGuid():N}.db"));
        await store.InitializeAsync();
        for (var index = 0; index < count; index++)
            await store.CreateTaskWithPersonalCloudIntentAsync(new TaskItem { Id = $"local_batch_{index:D8}", Title = "Synthetic", Description = description, SpaceId = SpaceIds.Default }, Instance, Device);
        var handler = new Handler();
        return (store, await CreateClientAsync(store, handler), handler);
    }

    private static async Task<PersonalCloudSyncClient> CreateClientAsync(SqliteTaskStore store, Handler handler, PersonalCloudDispatchPolicy? dispatch = null)
    {
        var http = dispatch is null ? new HttpClient(handler) : PersonalCloudDispatchHttp.Create(dispatch, handler);
        var identity = new PersonalCloudDeviceIdentityClient(http, new InMemoryCredentialStore());
        var origin = new Uri("https://synthetic.example.test/");
        await identity.PairAsync(origin, "synthetic-one-time-grant-123456789", "Synthetic desktop", false);
        return new PersonalCloudSyncClient(http, identity, store, new() { ServiceOrigin = origin, InstanceId = Instance, DeviceId = Device, ArtifactVersion = "test", ConnectedAt = DateTimeOffset.UtcNow });
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int SchemaVersion { get; set; } = 22;
        public bool AdvertiseBatch { get; set; } = true;
        public int MaxBytes { get; set; } = 16384;
        public string? CapabilityOverride { get; set; }
        public HttpStatusCode? RouteStatus { get; set; }
        public string? PartialFailure { get; set; }
        public string? SingleError { get; set; }
        public string? ResponseDefect { get; set; }
        public bool LoseResponse { get; set; }
        public DateTimeOffset? ResponseDate { get; set; }
        public int BatchPosts { get; private set; }
        public int SinglePosts { get; private set; }
        public int HealthRequests { get; private set; }
        public HttpStatusCode HealthStatus { get; set; } = HttpStatusCode.OK;
        public string? HealthBody { get; set; }
        public bool HealthOffline { get; set; }
        public int DuplicateReceipts { get; private set; }
        public List<int> BatchSizes { get; } = [];
        public List<int> BatchBytes { get; } = [];
        public List<string> Envelopes { get; } = [];
        private readonly Dictionary<string, object> _receipts = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/health")
            {
                HealthRequests++;
                if (HealthOffline) throw new HttpRequestException("Synthetic health transport failure");
                var capabilities = CapabilityOverride ?? (AdvertiseBatch ? $$$"""{"mutationBatch":{"protocolVersion":1,"path":"/v1/tasks/mutations/batch","maxMutations":2,"maxRequestUtf8Bytes":{{{MaxBytes}}}}}""" : "{}");
                return Json(HealthStatus, HealthBody ?? $$"""{"status":"ok","protocolVersion":1,"product":"tasks","artifactVersion":"test","schemaVersion":{{SchemaVersion}},"capabilities":{{capabilities}}}""");
            }
            if (path == "/auth/pair") return Json(HttpStatusCode.Created, $$"""{"protocolVersion":1,"instanceId":"{{Instance}}","deviceId":"{{Device}}","refreshCredential":"synthetic-refresh-123456789012345","capabilities":["desktop_sync"],"refreshExpiresAt":"2099-01-01T00:00:00Z"}""");
            if (path == "/auth/device/token") return Json(HttpStatusCode.OK, """{"protocolVersion":1,"accessToken":"synthetic-access-123456789012345","expiresAt":"2099-01-01T00:00:00Z","capabilities":["desktop_sync"]}""");
            if (path == "/sync/changes") return Json(HttpStatusCode.OK, """{"protocolVersion":1,"product":"tasks","changes":[],"nextCheckpoint":0}""");
            if (path == "/sync/checkpoints") return Json(HttpStatusCode.OK, $$"""{"protocolVersion":1,"product":"tasks","deviceId":"{{Device}}","acknowledgedCheckpoint":0}""");
            if (path is not ("/v1/tasks/mutations/batch" or "/v1/tasks/mutations")) throw new InvalidOperationException(path);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            if (path == "/v1/tasks/mutations")
            {
                SinglePosts++;
                if (SingleError is { } code) return Json(HttpStatusCode.Conflict, JsonSerializer.Serialize(new { error = new { code } }));
                return Json(HttpStatusCode.Created, JsonSerializer.Serialize(Accept(document.RootElement)));
            }
            BatchPosts++;
            if (RouteStatus is { } status) return Json(status, JsonSerializer.Serialize(new
            {
                error = new { code = status == HttpStatusCode.InternalServerError ? "sync_failed" : status == HttpStatusCode.Unauthorized ? "unauthorized" : "invalid_request" },
            }));
            var mutations = document.RootElement.GetProperty("mutations").EnumerateArray().ToArray();
            BatchSizes.Add(mutations.Length);
            BatchBytes.Add(Encoding.UTF8.GetByteCount(body));
            Assert.InRange(mutations.Length, 1, 2);
            Assert.True(BatchBytes[^1] <= MaxBytes);
            for (var index = 1; index < mutations.Length; index++)
                Assert.Equal(mutations[index - 1].GetProperty("deviceSequence").GetInt64() + 1, mutations[index].GetProperty("deviceSequence").GetInt64());
            var receipts = mutations.Take(PartialFailure is null ? mutations.Length : 1).Select(Accept).ToList();
            if (LoseResponse) throw new HttpRequestException("Synthetic lost response after durable commit");
            if (ResponseDefect == "order") receipts.Reverse();
            if (ResponseDefect == "version")
            {
                var receipt = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(receipts[^1]))!;
                receipt["entityVersion"] = JsonSerializer.SerializeToElement(999);
                receipts[^1] = receipt;
            }
            if (ResponseDefect == "json") return Json(HttpStatusCode.OK, "{");
            var result = new Dictionary<string, object?>
            {
                ["protocolVersion"] = ResponseDefect == "protocol" ? 2 : 1, ["product"] = "tasks",
                ["instanceId"] = ResponseDefect == "instance" ? "wrong" : Instance, ["receipts"] = receipts,
                ["processedCount"] = ResponseDefect == "count" ? 0 : receipts.Count,
                ["stoppedAt"] = PartialFailure is not null ? receipts.Count : ResponseDefect == "stopped" ? 0 : null,
                ["error"] = PartialFailure is null ? null : new Dictionary<string, object?>
                {
                    ["code"] = PartialFailure, ["message"] = "Synthetic", ["retryable"] = PartialFailure is "sync_failed" or "daily_quota_exceeded", ["requestId"] = "request_synthetic",
                },
            };
            if (PartialFailure == "daily_quota_exceeded")
                ((Dictionary<string, object?>)result["error"]!)["quota"] = new { protocolVersion = 1, resource = "d1_rows_written",
                    resetAt = PersonalCloudDispatchPolicy.NextMidnight(ResponseDate ?? DateTimeOffset.UtcNow).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") };
            if (ResponseDefect == "unknown") result["extra"] = 1;
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(result));
        }

        private object Accept(JsonElement mutation)
        {
            Envelopes.Add(mutation.GetRawText());
            var id = mutation.GetProperty("mutationId").GetString()!;
            var duplicate = _receipts.ContainsKey(id);
            if (duplicate) DuplicateReceipts++;
            var receipt = new { protocolVersion = 1, product = "tasks", mutationId = id, outcome = duplicate ? "duplicate" : "accepted",
                entityId = mutation.GetProperty("entityId").GetString(), entityVersion = 1,
                changeSeq = mutation.GetProperty("deviceSequence").GetInt64(), serverAcceptedAt = "2026-10-13T00:00:00.000Z" };
            _receipts[id] = receipt;
            return receipt;
        }
        private HttpResponseMessage Json(HttpStatusCode status, string body)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            response.Headers.Date = ResponseDate;
            return response;
        }
    }
    public void Dispose() => TestDirectory.Delete(_directory);
}
