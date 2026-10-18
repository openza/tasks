using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

// Opt in only against an independently seeded disposable local Worker/D1 fixture.
// The normal suite reports this as skipped, never as a successful live acceptance.
public sealed class LocalProviderBridgeAcceptanceAttribute : FactAttribute
{
    public LocalProviderBridgeAcceptanceAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENZA_BRIDGE_ACCEPTANCE_ORIGIN")))
            Skip = "Requires the disposable local Worker/D1 provider bridge acceptance fixture.";
    }
}

public sealed class PersonalCloudProviderBridgeWorkerAcceptanceTests
{
    private const string Instance = "instance_bridge_acceptance_0001";
    private const string DeviceA = "device_bridge_acceptance_a";
    private const string DeviceB = "device_bridge_acceptance_b";

    [LocalProviderBridgeAcceptance]
    [Trait("Category", "LocalWorkerAcceptance")]
    public async Task Actual_Worker_and_Desktop_SQLite_preserve_provider_delivery_ownership_and_recovery()
    {
        var origin = new Uri(Environment.GetEnvironmentVariable("OPENZA_BRIDGE_ACCEPTANCE_ORIGIN")!);
        Assert.True(origin.IsLoopback && origin.Scheme == "http", "Acceptance must target the disposable loopback Worker.");
        var directory = Path.Combine(Path.GetTempPath(), "openza-worker-bridge-acceptance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var web = new HttpClient { BaseAddress = origin };
            web.DefaultRequestHeaders.Add("x-openza-test-session", "synthetic-local-bridge-acceptance");
            var actor = $"device_web_{Guid.NewGuid():N}"; long sequence = 0;
            var provider = new SyntheticProvider();
            var taskId = $"local_acceptance_{Guid.NewGuid():N}";
            var raw = RawSource("first"); provider.Tasks.Add(raw);
            var webTask = Linked(taskId, raw);
            await PostAsync(web, PersonalCloudK0Codec.Serialize(PersonalCloudK0Mapper.CreateTaskMutation(webTask,
                Instance, actor, MutationId(), ++sequence, DateTimeOffset.UtcNow)));
            await CompleteAsync(web, actor, ++sequence, taskId, 1);

            using var transportA = new AcceptanceTransport(DeviceA);
            using var transportB = new AcceptanceTransport(DeviceB);
            using var httpA = new HttpClient(transportA); using var httpB = new HttpClient(transportB);
            var a = await ReplicaAsync(directory, "a.db", httpA, origin, DeviceA, "publicfake-bridge-refresh-device-a-20261011");
            var b = await ReplicaAsync(directory, "b.db", httpB, origin, DeviceB, "publicfake-bridge-refresh-device-b-20261011");
            Assert.Equal(1, (await a.Bridge.RunAsync([])).Pending);
            Assert.Equal(JsonValueKind.Null, (await ReadTaskAsync(web, taskId)).GetProperty("externalCommand").GetProperty("deliveryState").ValueKind);
            Assert.Equal(1, (await a.Bridge.RunAsync([new(provider, "synthetic-account")])).Confirmed);
            Assert.Equal(1, (await b.Bridge.RunAsync([new(provider, "synthetic-account")])).Pending);
            Assert.Equal(1, provider.Actions);

            var immutable = Assert.Single(await a.Store.GetPendingPersonalCloudOutboxAsync(Instance, DeviceA), item => item.Operation == "externalCommand.confirm");
            transportA.LoseNextResultResponse = true;
            await Assert.ThrowsAsync<HttpRequestException>(() => a.Sync.SyncAsync());
            // HTTP200 can be a stopped batch prefix. Prove the confirmation committed before retrying it.
            Assert.Equal("confirmed", (await ReadTaskAsync(web, taskId)).GetProperty("externalCommand").GetProperty("state").GetString());
            Assert.Equal(immutable.CanonicalJson, Assert.Single(await a.Store.GetPendingPersonalCloudOutboxAsync(Instance, DeviceA), item => item.Operation == "externalCommand.confirm").CanonicalJson);
            Assert.Null((await a.Sync.SyncAsync()).BlockedCode);
            await a.Bridge.RunAsync([new(provider, "synthetic-account")]); Assert.Equal(1, provider.Actions);
            Assert.Equal("confirmed", (await ReadTaskAsync(web, taskId)).GetProperty("externalCommand").GetProperty("state").GetString());
            await b.Sync.SyncAsync();
            Assert.DoesNotContain(await b.Store.GetPersonalCloudProviderRequestStatusAsync(Instance), item => item.TaskId == taskId);

            var version = (await ReadTaskAsync(web, taskId)).GetProperty("entityVersion").GetInt64();
            await PostAsync(web, PersonalCloudK0Codec.Serialize(PersonalCloudReopenCodec.CreateMutation(Instance, actor,
                MutationId(), ++sequence, taskId, version, Now(), Now())));
            await a.Sync.SyncAsync();
            Assert.Equal(1, (await a.Bridge.RunAsync([new(provider, "synthetic-account")])).Confirmed);
            await a.Sync.SyncAsync(); Assert.Equal(2, provider.Actions); Assert.False(provider.LastCompleted);
            Assert.Equal("confirmed", (await ReadTaskAsync(web, taskId)).GetProperty("externalCommand").GetProperty("state").GetString());

            // A stale snapshot and subsequent user edit use real ordered receipts/feed over HTTP.
            await b.Sync.SyncAsync();
            var descriptor = Assert.Single(await a.Store.GetPersonalCloudProviderSourcesAsync(Instance), item => item.TaskId == taskId).Source;
            var time = DateTimeOffset.UtcNow.AddMinutes(1);
            await a.Store.EnqueuePersonalCloudSourceSnapshotAsync(Instance, DeviceA, taskId,
                descriptor with { RefreshedAt = PersonalCloudK0Mapper.FormatTimestamp(time), Title = "Older provider snapshot" });
            await b.Store.EnqueuePersonalCloudSourceSnapshotAsync(Instance, DeviceB, taskId,
                descriptor with { RefreshedAt = PersonalCloudK0Mapper.FormatTimestamp(time.AddSeconds(1)), Title = "Newer provider snapshot" });
            Assert.Null((await b.Sync.SyncAsync()).BlockedCode);
            await a.Store.UpdateTaskNotesWithPersonalCloudIntentAsync(taskId, "Later user edit survives the source race",
                (await a.Store.GetPersonalCloudEntityVersionAsync(Instance, taskId))!.Value, Instance, DeviceA);
            Assert.Null((await a.Sync.SyncAsync()).BlockedCode);
            var authoritative = await ReadTaskAsync(web, taskId);
            Assert.Equal("Later user edit survives the source race", authoritative.GetProperty("notes").GetString());
            Assert.Equal("Newer provider snapshot", authoritative.GetProperty("source").GetProperty("title").GetString());

            var rejectedId = $"local_rejected_{Guid.NewGuid():N}"; var rejectedSource = RawSource("rejected"); provider.Tasks.Add(rejectedSource);
            await PostAsync(web, PersonalCloudK0Codec.Serialize(PersonalCloudK0Mapper.CreateTaskMutation(Linked(rejectedId, rejectedSource),
                Instance, actor, MutationId(), ++sequence, DateTimeOffset.UtcNow)));
            await CompleteAsync(web, actor, ++sequence, rejectedId, 1); await a.Sync.SyncAsync();
            provider.RejectExternalId = rejectedSource.ExternalId;
            Assert.Equal(1, (await a.Bridge.RunAsync([new(provider, "synthetic-account")])).Rejected);
            await a.Sync.SyncAsync(); Assert.Equal("rejected", (await ReadTaskAsync(web, rejectedId)).GetProperty("externalCommand").GetProperty("state").GetString());
            transportA.LoseNextRetryResponse = true;
            await Assert.ThrowsAsync<HttpRequestException>(() => a.Bridge.RetryAsync(rejectedId));
            await a.Bridge.RetryAsync(rejectedId); await a.Sync.SyncAsync(); provider.RejectExternalId = null;
            Assert.Equal(1, (await a.Bridge.RunAsync([new(provider, "synthetic-account")])).Confirmed);
            await a.Sync.SyncAsync(); Assert.Equal("confirmed", (await ReadTaskAsync(web, rejectedId)).GetProperty("externalCommand").GetProperty("state").GetString());

            var deletedId = $"local_deleted_{Guid.NewGuid():N}"; var deletedSource = RawSource("deleted");
            await PostAsync(web, PersonalCloudK0Codec.Serialize(PersonalCloudK0Mapper.CreateTaskMutation(Linked(deletedId, deletedSource),
                Instance, actor, MutationId(), ++sequence, DateTimeOffset.UtcNow)));
            await a.Sync.SyncAsync(); await b.Sync.SyncAsync();
            var deletedDescriptor = Assert.Single(await a.Store.GetPersonalCloudProviderSourcesAsync(Instance), item => item.TaskId == deletedId).Source;
            await a.Store.EnqueuePersonalCloudSourceSnapshotAsync(Instance, DeviceA, deletedId,
                deletedDescriptor with { Title = "Changed provider fact before deletion",
                    RefreshedAt = PersonalCloudK0Mapper.FormatTimestamp(DateTimeOffset.UtcNow.AddMinutes(1)) });
            Assert.Single(await a.Store.GetPendingPersonalCloudOutboxAsync(Instance, DeviceA),
                item => item.EntityId == deletedId && item.Operation == "source.refreshSnapshot");
            await PostAsync(web, PersonalCloudK0Codec.Serialize(PersonalCloudDeleteCodec.CreateMutation(Instance, actor,
                MutationId(), ++sequence, deletedId, 1, Now(), Now())));
            await a.Store.UpdateTaskNotesWithPersonalCloudIntentAsync(taskId, "Later edit survives deleted-source refresh",
                (await a.Store.GetPersonalCloudEntityVersionAsync(Instance, taskId))!.Value, Instance, DeviceA);
            Assert.Null((await a.Sync.SyncAsync()).BlockedCode); Assert.Null((await b.Sync.SyncAsync()).BlockedCode);
            Assert.Empty(await a.Store.GetPendingPersonalCloudOutboxAsync(Instance, DeviceA));
            Assert.DoesNotContain(await a.Store.GetPersonalCloudProviderSourcesAsync(Instance), item => item.TaskId == deletedId);
            Assert.Equal("Later edit survives deleted-source refresh", (await ReadTaskAsync(web, taskId)).GetProperty("notes").GetString());

            var uncertainId = $"local_uncertain_{Guid.NewGuid():N}"; var uncertainSource = RawSource("uncertain"); provider.Tasks.Add(uncertainSource);
            await PostAsync(web, PersonalCloudK0Codec.Serialize(PersonalCloudK0Mapper.CreateTaskMutation(Linked(uncertainId, uncertainSource),
                Instance, actor, MutationId(), ++sequence, DateTimeOffset.UtcNow)));
            await CompleteAsync(web, actor, ++sequence, uncertainId, 1); await a.Sync.SyncAsync();
            provider.LoseResponseExternalId = uncertainSource.ExternalId;
            Assert.Equal(1, (await a.Bridge.RunAsync([new(provider, "synthetic-account")])).Uncertain);
            var actions = provider.Actions;
            var restarted = new PersonalCloudProviderBridge(new SqliteTaskStore(a.Store.DatabasePath),
                new PersonalCloudProviderDeliveryClient(httpA, a.Identity, a.Profile), a.Profile);
            Assert.Equal(1, (await restarted.RunAsync([new(provider, "synthetic-account")])).Uncertain);
            Assert.Equal(actions, provider.Actions);
            Assert.Equal("started", (await ReadTaskAsync(web, uncertainId)).GetProperty("externalCommand").GetProperty("deliveryState").GetString());
            Assert.False(Assert.Single(await a.Store.GetPersonalCloudProviderRequestStatusAsync(Instance), item => item.TaskId == uncertainId).CanRetry);
            Assert.True(transportA.BatchRequests > 0 && transportB.BatchRequests > 0,
                "Schema22 acceptance must exercise actual ordered batch HTTP delivery on both replicas.");
            Assert.Equal(1, transportA.LostResultResponses);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static TaskItem RawSource(string suffix) => new()
    {
        Id = $"todoist_synthetic-{suffix}", ExternalId = $"synthetic-{suffix}", IntegrationId = "todoist",
        ProviderConnectionId = "todoist_default", Title = $"Synthetic provider {suffix}", RecurrenceRule = "every day",
    };
    private static TaskItem Linked(string id, TaskItem raw) => new()
    {
        Id = id, SpaceId = SpaceIds.Default, Title = "Synthetic Openza task", SourceIntegrationId = raw.IntegrationId,
        SourceExternalId = raw.ExternalId, SourceTitle = raw.Title, CreatedAt = DateTimeOffset.UtcNow,
    };
    private static string MutationId() => $"mutation_{Guid.NewGuid():N}";
    private static string Now() => PersonalCloudK0Mapper.FormatTimestamp(DateTimeOffset.UtcNow);
    private static Task CompleteAsync(HttpClient http, string actor, long sequence, string id, long version) => PostAsync(http,
        PersonalCloudK0Codec.Serialize(PersonalCloudCompletionCodec.CreateMutation(Instance, actor, MutationId(), sequence, id, version, Now(), Now())));
    private static async Task PostAsync(HttpClient http, string json)
    {
        using var response = await http.PostAsync("/v1/tasks/mutations", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.True(response.IsSuccessStatusCode, $"Synthetic mutation returned HTTP {(int)response.StatusCode}.");
    }
    private static async Task<JsonElement> ReadTaskAsync(HttpClient http, string id)
    {
        using var response = await http.GetAsync("/v1/tasks"); response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("tasks").EnumerateArray().Single(item => item.GetProperty("id").GetString() == id).Clone();
    }
    private static async Task<(SqliteTaskStore Store, PersonalCloudProviderBridge Bridge, PersonalCloudSyncClient Sync,
        PersonalCloudDeviceIdentityClient Identity, PersonalCloudConnectionProfile Profile)> ReplicaAsync(
        string directory, string file, HttpClient http, Uri origin, string device, string refresh)
    {
        var credentials = new InMemoryCredentialStore();
        var key = $"personal-cloud-device-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin.AbsoluteUri))).ToLowerInvariant()}";
        await credentials.SaveAsync(key, PersonalCloudK0Codec.Serialize(new
        { serviceOrigin = origin.AbsoluteUri, instanceId = Instance, deviceId = device, refreshCredential = refresh }));
        var identity = new PersonalCloudDeviceIdentityClient(http, credentials);
        var profile = new PersonalCloudConnectionProfile { ServiceOrigin = origin, InstanceId = Instance, DeviceId = device, ArtifactVersion = "0.1.0-preview.9", ConnectedAt = DateTimeOffset.UtcNow };
        var store = new SqliteTaskStore(Path.Combine(directory, file)); await store.InitializeAsync();
        using var response = await http.GetAsync(new Uri(origin, "/sync/bootstrap")); response.EnsureSuccessStatusCode();
        await store.ReplacePersonalCloudReplicaFromBootstrapAsync(Instance, device, await response.Content.ReadAsStringAsync());
        return (store, new(store, new(http, identity, profile), profile), new(http, identity, store, profile), identity, profile);
    }
    private sealed class AcceptanceTransport(string device) : DelegatingHandler(new HttpClientHandler())
    {
        public bool LoseNextResultResponse { get; set; }
        public bool LoseNextRetryResponse { get; set; }
        public int BatchRequests { get; private set; }
        public int LostResultResponses { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("x-openza-test-device", device);
            if (request.RequestUri!.AbsolutePath == "/v1/tasks/mutations/batch") BatchRequests++;
            var result = LoseNextResultResponse && request.RequestUri.AbsolutePath is "/v1/tasks/mutations" or "/v1/tasks/mutations/batch" &&
                (await request.Content!.ReadAsStringAsync(cancellationToken)).Contains("externalCommand.", StringComparison.Ordinal);
            var retry = LoseNextRetryResponse && request.RequestUri!.AbsolutePath == "/v1/provider/commands/retry";
            var response = await base.SendAsync(request, cancellationToken);
            if ((result || retry) && response.IsSuccessStatusCode)
            {
                if (result) LostResultResponses++;
                LoseNextResultResponse = false; LoseNextRetryResponse = false; response.Dispose();
                throw new HttpRequestException("Synthetic local acceptance response lost after the Worker accepted it.");
            }
            return response;
        }
    }
    private sealed class SyntheticProvider : ISyncProvider
    {
        public string IntegrationId => "todoist";
        public List<TaskItem> Tasks { get; } = [];
        public int Actions { get; private set; }
        public bool LastCompleted { get; private set; }
        public string? RejectExternalId { get; set; }
        public string? LoseResponseExternalId { get; set; }
        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProviderSnapshot(Tasks.ToArray(), [], []));
        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default)
        {
            Actions++; LastCompleted = completion.Completed;
            if (completion.ProviderTaskId == RejectExternalId) throw new HttpRequestException("Synthetic known no-effect rejection.", null, HttpStatusCode.BadRequest);
            if (completion.ProviderTaskId == LoseResponseExternalId) throw new HttpRequestException("Synthetic provider effect occurred; response lost.", null, HttpStatusCode.ServiceUnavailable);
            return Task.CompletedTask;
        }
    }
}
