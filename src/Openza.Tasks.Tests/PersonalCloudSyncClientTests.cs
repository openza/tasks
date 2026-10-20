using System.Net;
using System.Text;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudSyncClientTests : IDisposable
{
    private const string InstanceId = "instance_sync_0000000000000000000000000001";
    private const string DeviceId = "device_sync_000000000000000000000000000001";
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "openza-personal-cloud-sync-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Permanent_rejection_preserves_local_intent_and_downloads_unrelated_changes()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "conflict.db"));
        await store.InitializeAsync();
        var handler = new SyncHandler { ErrorCode = "version_conflict" };
        var client = await CreateClientAsync(store, handler);
        var local = new TaskItem { Id = "local_conflict", Title = "Keep local intent", SpaceId = SpaceIds.Default };
        var outbox = await store.CreateTaskWithPersonalCloudIntentAsync(local, InstanceId, DeviceId);
        K0TaskCreatedChange Change(TaskItem task, long sequence) => new()
        {
            ProtocolVersion = 1, Product = "tasks", ChangeSeq = sequence, MutationId = $"mutation_remote_{sequence}",
            Operation = "task.created", EntityKind = "task", EntityId = task.Id, EntityVersion = 1,
            Payload = PersonalCloudK0Mapper.CreateTaskMutation(task, InstanceId, DeviceId, $"mutation_remote_{sequence}", sequence, DateTimeOffset.UtcNow).Payload,
            ServerAcceptedAt = "2026-09-14T04:30:01.000Z",
        };
        handler.PullBody = PersonalCloudK0Codec.Serialize(new
        {
            ProtocolVersion = 1, Product = "tasks", NextCheckpoint = 2,
            Changes = new[] { Change(local with { Title = "Remote title" }, 1), Change(local with { Id = "local_unrelated", Title = "Unrelated remote task" }, 2) },
        });

        var result = await client.SyncAsync();

        Assert.Equal("version_conflict", result.BlockedCode);
        Assert.Equal(0, result.PushedMutations);
        Assert.Equal(2, result.PulledChanges);
        Assert.Equal(2, result.Checkpoint);
        Assert.Equal(local.Title, (await store.GetTaskAsync(local.Id))!.Title);
        Assert.Equal("Unrelated remote task", (await store.GetTaskAsync("local_unrelated"))!.Title);
        Assert.Equal(outbox.CanonicalJson, Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId)).CanonicalJson);
        Assert.Equal(1, await store.GetDeferredPersonalCloudChangeCountAsync(InstanceId));
        await client.SyncAsync();
        Assert.Equal(1, await store.GetDeferredPersonalCloudChangeCountAsync(InstanceId));
    }

    [Fact]
    public async Task Expired_checkpoint_rebuilds_only_when_no_unresolved_intent_exists()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "expired.db"));
        await store.InitializeAsync();
        var handler = new SyncHandler
        {
            ExpireCheckpointOnce = true,
            PullBody = """{"protocolVersion":1,"product":"tasks","changes":[],"nextCheckpoint":5}""",
        };
        var client = await CreateClientAsync(store, handler);

        var result = await client.SyncAsync();

        Assert.Equal(5, result.Checkpoint);
        Assert.Contains("/sync/bootstrap", handler.Paths);
        Assert.Equal(5, await store.GetPersonalCloudCheckpointAsync(InstanceId, DeviceId));
    }

    [Fact]
    public async Task Expired_checkpoint_with_pending_intent_keeps_it_and_does_not_request_bootstrap()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "expired-pending.db"));
        await store.InitializeAsync();
        var handler = new SyncHandler { ErrorCode = "version_conflict", ExpireCheckpointOnce = true };
        var client = await CreateClientAsync(store, handler);
        var outbox = await store.CreateTaskWithPersonalCloudIntentAsync(new TaskItem
        {
            Id = "local_pending_expired", Title = "Preserve pending", SpaceId = SpaceIds.Default,
        }, InstanceId, DeviceId);

        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.SyncAsync());

        Assert.Equal("checkpoint_expired_pending", error.Code);
        Assert.DoesNotContain("/sync/bootstrap", handler.Paths);
        Assert.Equal(outbox.CanonicalJson, Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId)).CanonicalJson);
        Assert.Equal(0, await store.GetPersonalCloudCheckpointAsync(InstanceId, DeviceId));
    }

    private static async Task<PersonalCloudSyncClient> CreateClientAsync(SqliteTaskStore store, SyncHandler handler)
    {
        var http = new HttpClient(handler);
        var identity = new PersonalCloudDeviceIdentityClient(http, new InMemoryCredentialStore());
        var origin = new Uri("https://personal-cloud.example.test/");
        var paired = await identity.PairAsync(origin, "pair-one-time-secret-123456789", "Test desktop", false);
        return new PersonalCloudSyncClient(http, identity, store, new PersonalCloudConnectionProfile
        {
            ServiceOrigin = origin, InstanceId = paired.InstanceId, DeviceId = paired.DeviceId,
            ArtifactVersion = "0.1.0-preview.8", ConnectedAt = DateTimeOffset.UtcNow,
        });
    }

    [Fact]
    public async Task Sync_pushes_pending_work_pulls_own_echo_once_and_acknowledges_checkpoint()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        var handler = new SyncHandler();
        var http = new HttpClient(handler);
        var identity = new PersonalCloudDeviceIdentityClient(http, new InMemoryCredentialStore());
        var origin = new Uri("https://personal-cloud.example.test/");
        var paired = await identity.PairAsync(
            origin,
            "pair-one-time-secret-123456789",
            "Test desktop",
            providerBridge: false);
        var original = new TaskItem
        {
            Id = "local_sync_000000000000000000000000000001",
            SpaceId = SpaceIds.Default,
            Title = "Sync this task once",
            CreatedAt = DateTimeOffset.Parse("2026-09-14T04:30:00Z"),
        };
        await store.CreateTaskWithPersonalCloudIntentAsync(original, paired.InstanceId, paired.DeviceId);
        var client = new PersonalCloudSyncClient(http, identity, store, new PersonalCloudConnectionProfile
        {
            ServiceOrigin = origin,
            InstanceId = paired.InstanceId,
            DeviceId = paired.DeviceId,
            ArtifactVersion = "0.1.0-preview.1",
            ConnectedAt = DateTimeOffset.Parse("2026-09-14T04:29:00Z"),
        });

        var result = await client.SyncAsync();

        Assert.Equal(new PersonalCloudSyncResult(1, 1, 1), result);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId));
        var stored = Assert.IsType<TaskItem>(await store.GetTaskAsync(original.Id));
        Assert.Equal(original.Title, stored.Title);
        Assert.Equal(0, stored.Revision);
        Assert.Equal(1, await store.GetPersonalCloudCheckpointAsync(InstanceId, DeviceId));
        Assert.Equal(
            ["/auth/pair", "/health", "/auth/device/token", "/v1/tasks/mutations", "/sync/changes", "/sync/checkpoints"],
            handler.Paths);
        Assert.All(handler.AuthorizationSchemes, scheme => Assert.Equal("Bearer", scheme));
        Assert.Equal(1, handler.AcknowledgedCheckpoint);
    }

    [Fact]
    public async Task Sync_rejects_server_details_and_leaves_pending_work_for_retry()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "retry.db"));
        await store.InitializeAsync();
        var handler = new SyncHandler(rejectMutation: true);
        var http = new HttpClient(handler);
        var identity = new PersonalCloudDeviceIdentityClient(http, new InMemoryCredentialStore());
        var origin = new Uri("https://personal-cloud.example.test/");
        var paired = await identity.PairAsync(origin, "pair-one-time-secret-123456789", "Test desktop", false);
        await store.CreateTaskWithPersonalCloudIntentAsync(new TaskItem
        {
            Id = "local_retry_00000000000000000000000000001",
            SpaceId = SpaceIds.Default,
            Title = "Retry safely",
        }, paired.InstanceId, paired.DeviceId);
        var client = new PersonalCloudSyncClient(http, identity, store, new PersonalCloudConnectionProfile
        {
            ServiceOrigin = origin,
            InstanceId = paired.InstanceId,
            DeviceId = paired.DeviceId,
            ArtifactVersion = "0.1.0-preview.1",
            ConnectedAt = DateTimeOffset.UtcNow,
        });

        var exception = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.SyncAsync());

        Assert.Equal("sync_failed", exception.Code);
        Assert.DoesNotContain("private database detail", exception.Message, StringComparison.Ordinal);
        Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(InstanceId, DeviceId));
    }

    public void Dispose() => TestDirectory.Delete(_directory);

    private sealed class SyncHandler(bool rejectMutation = false) : HttpMessageHandler
    {
        private K0TaskCreateMutation? _mutation;
        private bool _expired;
        public string? ErrorCode { get; init; }
        public string? PullBody { get; set; }
        public bool ExpireCheckpointOnce { get; init; }
        public List<string> Paths { get; } = [];
        public List<string?> AuthorizationSchemes { get; } = [];
        public long? AcknowledgedCheckpoint { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath is not "/auth/pair" and not "/auth/device/token" and not "/health")
                AuthorizationSchemes.Add(request.Headers.Authorization?.Scheme);
            return request.RequestUri.AbsolutePath switch
            {
                "/health" => Json(HttpStatusCode.OK, """{"status":"ok","protocolVersion":1,"product":"tasks","artifactVersion":"test","schemaVersion":20}"""),
                "/auth/pair" => Json(HttpStatusCode.Created, $$"""
                    {"protocolVersion":1,"instanceId":"{{InstanceId}}","deviceId":"{{DeviceId}}","refreshCredential":"refresh-secret-value-1234567890","capabilities":["desktop_sync"],"refreshExpiresAt":"2099-01-01T00:00:00Z"}
                    """),
                "/auth/device/token" => Json(HttpStatusCode.OK, """
                    {"protocolVersion":1,"accessToken":"access-secret-value-1234567890","expiresAt":"2099-01-01T00:10:00Z","capabilities":["desktop_sync"]}
                    """),
                "/v1/tasks/mutations" => await AcceptMutationAsync(request, cancellationToken),
                "/sync/changes" => ChangePage(),
                "/sync/bootstrap" => Json(HttpStatusCode.OK, """{"protocolVersion":1,"product":"tasks","checkpoint":5,"snapshot":{"spaces":[],"projects":[],"tasks":[],"labels":[],"taskLabels":[],"tombstones":[]}}"""),
                "/sync/checkpoints" => await AcknowledgeCheckpointAsync(request, cancellationToken),
                _ => throw new InvalidOperationException($"Unexpected request path: {request.RequestUri.AbsolutePath}"),
            };
        }

        private async Task<HttpResponseMessage> AcceptMutationAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (ErrorCode is not null)
                return Json(HttpStatusCode.Conflict, $$$"""{"error":{"code":"{{{ErrorCode}}}","message":"private database detail"}}""");
            if (rejectMutation)
                return Json(HttpStatusCode.InternalServerError, "{\"error\":\"private database detail\"}");
            _mutation = PersonalCloudK0Codec.ParseTaskCreateMutation(
                await request.Content!.ReadAsStringAsync(cancellationToken),
                InstanceId);
            return Json(HttpStatusCode.Created, PersonalCloudK0Codec.Serialize(new K0MutationReceipt
            {
                ProtocolVersion = 1,
                Product = "tasks",
                MutationId = _mutation.MutationId,
                Outcome = "accepted",
                EntityId = _mutation.EntityId,
                EntityVersion = 1,
                ChangeSeq = 1,
                ServerAcceptedAt = "2026-09-14T04:30:01.000Z",
            }));
        }

        private HttpResponseMessage ChangePage()
        {
            if (ExpireCheckpointOnce && !_expired)
            {
                _expired = true;
                return Json(HttpStatusCode.Conflict, """{"error":{"code":"checkpoint_expired"}}""");
            }
            if (PullBody is not null) return Json(HttpStatusCode.OK, PullBody);
            if (_mutation is null) throw new InvalidOperationException("The mutation must be accepted before changes are pulled.");
            var change = new K0TaskCreatedChange
            {
                ProtocolVersion = 1,
                Product = "tasks",
                ChangeSeq = 1,
                MutationId = _mutation.MutationId,
                Operation = PersonalCloudK0Protocol.TaskCreatedOperation,
                EntityKind = "task",
                EntityId = _mutation.EntityId,
                EntityVersion = 1,
                Payload = _mutation.Payload,
                ServerAcceptedAt = "2026-09-14T04:30:01.000Z",
            };
            return Json(HttpStatusCode.OK, PersonalCloudK0Codec.Serialize(new PersonalCloudK3ChangePage
            {
                ProtocolVersion = 1,
                Product = "tasks",
                Changes = [PersonalCloudK0Codec.Deserialize<System.Text.Json.JsonElement>(PersonalCloudK0Codec.Serialize(change))],
                NextCheckpoint = 1,
            }));
        }

        private async Task<HttpResponseMessage> AcknowledgeCheckpointAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var acknowledgement = PersonalCloudK0Codec.Deserialize<PersonalCloudK4CheckpointAcknowledgement>(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            AcknowledgedCheckpoint = acknowledgement.Checkpoint;
            return Json(HttpStatusCode.OK, PersonalCloudK0Codec.Serialize(new PersonalCloudK4CheckpointReceipt
            {
                ProtocolVersion = 1,
                Product = "tasks",
                DeviceId = DeviceId,
                AcknowledgedCheckpoint = acknowledgement.Checkpoint,
            }));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
