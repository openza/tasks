using System.Net;
using System.Text;
using System.Text.Json;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudInitialSyncProgressTests : IDisposable
{
    [Theory]
    [InlineData(false, 915)]
    [InlineData(true, 458)]
    public async Task Combined_workspace_913_task_seed_measures_exact_posts_acknowledgements_and_checkpoint(bool batch, int expectedPosts)
    {
        var (store, service, handler, _, _) = await CreateAsync(); handler.BatchEnabled = batch; handler.WorkspaceEnabled = true;
        await store.UpsertSpaceAsync(new() { Id = "space_measurement", Name = "Synthetic" });
        await store.UpsertProjectAsync(new() { Id = "project_measurement", Name = "Synthetic", SpaceId = "space_measurement", Description = "Synthetic description" });
        await store.UpsertLabelAsync(new() { Id = "label_measurement", Name = "Synthetic" });
        for (var index = 0; index < 913; index++)
            await store.UpsertTaskAsync(new() { Id = $"local_measure_{index:D8}", Title = "Synthetic task", SpaceId = "space_measurement", ProjectId = "project_measurement",
                Labels = [new() { Id = "label_measurement", Name = "Synthetic" }], Priority = 4, RecurrenceRule = "weekly", DeadlineAt = DateTimeOffset.Parse("2026-11-01T01:00:00Z") });
        service.InitialSyncProgressChanged += item => { if (item.IsComplete) Assert.Equal(915, handler.LastCheckpointMutationCount); };
        var result = await service.SyncAsync();
        Assert.Equal(915, result.PushedMutations); Assert.Equal(915, handler.Mutations); Assert.Equal(expectedPosts, handler.MutationPosts);
        Assert.True(result.InitialSyncProgress!.IsComplete); Assert.Equal(915L, result.InitialSyncProgress.Total); Assert.Equal(915L, result.InitialSyncProgress.Acknowledged);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)); Assert.True(handler.Checkpoints > 1);
    }
    [Theory]
    [InlineData(false, 4565)]
    [InlineData(true, 2283)]
    public async Task Realistic_4565_change_seed_measures_mutation_posts_and_requires_final_catch_up(bool batch, int expectedPosts)
    {
        var (store, service, handler, _, _) = await CreateAsync();
        handler.BatchEnabled = batch;
        await store.UpsertSpaceAsync(new() { Id = "space_measurement", Name = "Synthetic" });
        await store.UpsertProjectAsync(new() { Id = "project_measurement", Name = "Synthetic", SpaceId = "space_measurement" });
        await store.UpsertLabelAsync(new() { Id = "label_measurement", Name = "Synthetic" });
        for (var index = 0; index < 913; index++)
            await store.UpsertTaskAsync(new() { Id = $"local_measure_{index:D8}", Title = "Synthetic task", SpaceId = "space_measurement",
                ProjectId = "project_measurement", Labels = [new() { Id = "label_measurement", Name = "Synthetic" }], Priority = 4 });
        var progress = new List<PersonalCloudInitialSyncProgress>();
        service.InitialSyncProgressChanged += item =>
        {
            if (item.IsComplete) Assert.True(handler.Checkpoints > 1 && handler.LastCheckpointMutationCount == 4565);
            progress.Add(item);
        };
        var result = await service.SyncAsync();
        Assert.Equal(4565, result.PushedMutations);
        Assert.Equal(4565, handler.Mutations);
        Assert.Equal(expectedPosts, handler.MutationPosts);
        Assert.True(result.InitialSyncProgress!.IsComplete);
        Assert.Equal(4565L, result.InitialSyncProgress.Total);
        Assert.Equal(4565L, result.InitialSyncProgress.Acknowledged);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Contains(progress, item => item.Phase == PersonalCloudInitialSyncPhase.Uploading && item.Acknowledged > 0 && item.Acknowledged < 4565);
        Assert.Equal("complete", handler.Reports[^1].GetProperty("state").GetString());
        Assert.Equal(1, handler.HealthRequests);
    }

    private const string Instance = "instance_initial_000000000000000000000001";
    private const string Device = "device_initial_00000000000000000000000001";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-initial-progress", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Full_seed_count_and_receipts_survive_interruption_restart_and_later_edits()
    {
        var (store, service, handler, identity, connections) = await CreateAsync();
        await store.UpsertSpaceAsync(new() { Id = "space_example", Name = "Example" });
        await store.UpsertProjectAsync(new() { Id = "project_example", Name = "Example", SpaceId = "space_example" });
        await store.UpsertLabelAsync(new() { Id = "label_example", Name = "Example" });
        for (var index = 0; index < 80; index++)
            await store.UpsertTaskAsync(new() { Id = $"local_initial_{index:D8}", Title = "Synthetic task", SpaceId = "space_example",
                ProjectId = "project_example", Labels = [new() { Id = "label_example", Name = "Example" }], Priority = 4 });
        var events = new List<PersonalCloudInitialSyncProgress>();
        service.InitialSyncProgressChanged += events.Add;
        handler.FailAfterMutations = 150;
        await Assert.ThrowsAsync<HttpRequestException>(() => service.SyncAsync());
        var interrupted = (await service.GetInitialSyncProgressAsync())!;
        Assert.Equal(PersonalCloudInitialSyncPhase.Interrupted, interrupted.Phase);
        Assert.Equal(150L, interrupted.Acknowledged);
        Assert.Equal(400L, interrupted.Total); // Creations, moves, projects, labels and priority changes all belong to the seed.
        Assert.DoesNotContain(events, item => item.IsComplete);
        Assert.Contains(events, item => item.Total is null);
        Assert.Contains(events, item => item.Summary.Contains("Spaces, projects and labels"));
        var frozenTotal = interrupted.Total;
        var lastSequence = handler.Reports.Max(item => item.GetProperty("reportSequence").GetInt64());
        var restarted = new PersonalCloudSetupService(null!, "test", connectionStore: connections, deviceIdentity: identity,
            syncHttp: new HttpClient(handler), taskStore: store);
        Assert.Equal(interrupted, await restarted.GetInitialSyncProgressAsync());
        handler.FailAfterMutations = null;
        var result = await restarted.SyncAsync();
        Assert.Equal(frozenTotal, result.InitialSyncProgress!.Total);
        Assert.True(result.InitialSyncProgress.IsComplete);
        Assert.Equal(frozenTotal, result.InitialSyncProgress.Acknowledged);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.True(handler.Reports.First(item => item.GetProperty("reportSequence").GetInt64() > lastSequence)
            .GetProperty("reportSequence").GetInt64() > lastSequence);
        Assert.Equal("complete", handler.Reports[^1].GetProperty("state").GetString());
        await store.CreateTaskWithPersonalCloudIntentAsync(new() { Id = "local_later_00000001", Title = "Later", SpaceId = SpaceIds.Default }, Instance, Device);
        var later = await restarted.SyncAsync();
        Assert.True(later.InitialSyncProgress!.IsComplete);
        Assert.Equal(frozenTotal, later.InitialSyncProgress.Total);
        Assert.Null(await store.GetPersonalCloudInitialSyncProgressAsync("different-instance", Device));
        Assert.Null(await store.GetPersonalCloudInitialSyncProgressAsync(Instance, "replacement-device"));
        Assert.All(handler.Reports, report => Assert.DoesNotContain("Synthetic task", report.GetRawText()));
        Assert.Equal(handler.Reports.Count, handler.Reports.Select(item => item.GetProperty("reportSequence").GetInt64()).Distinct().Count());
    }

    [Theory]
    [InlineData(0)] [InlineData(400)] [InlineData(404)] [InlineData(405)] [InlineData(501)] [InlineData(401)] [InlineData(500)]
    public async Task Optional_status_failures_do_not_break_sync_or_discard_intents(int status)
    {
        var (store, service, handler, _, _) = await CreateAsync();
        handler.ReportStatus = (HttpStatusCode)status;
        await store.UpsertTaskAsync(new() { Id = "local_optional_00000001", Title = "Synthetic", SpaceId = SpaceIds.Default });
        var result = await service.SyncAsync();
        Assert.True(result.InitialSyncProgress!.IsComplete);
        Assert.True(result.PushedMutations > 0);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.All(handler.ReportAuthorization, scheme => Assert.Equal("Bearer", scheme));
    }

    [Fact]
    public async Task Zero_tasks_requires_checkpoint_acknowledgement_and_completed_state_never_reopens()
    {
        var (store, service, handler, _, _) = await CreateAsync();
        var complete = await service.SyncAsync();
        Assert.True(complete.InitialSyncProgress!.IsComplete);
        Assert.Equal(0L, complete.InitialSyncProgress.Total);
        Assert.Equal(1, handler.Checkpoints);
        handler.FailCatchUp = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => service.SyncAsync());
        Assert.True((await service.GetInitialSyncProgressAsync())!.IsComplete);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Acknowledged_upload_is_not_complete_until_catch_up_succeeds(bool batch)
    {
        var (store, service, handler, _, _) = await CreateAsync();
        handler.BatchEnabled = batch;
        await store.UpsertTaskAsync(new() { Id = "local_catchup_00000001", Title = "Synthetic", SpaceId = SpaceIds.Default });
        handler.FailCatchUpAfterMutation = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => service.SyncAsync());
        var state = (await service.GetInitialSyncProgressAsync())!;
        Assert.Equal(state.Total, state.Acknowledged);
        Assert.False(state.IsComplete);
        Assert.DoesNotContain(handler.Reports, item => item.GetProperty("state").GetString() == "complete");
        handler.FailCatchUpAfterMutation = false;
        Assert.True((await service.SyncAsync()).InitialSyncProgress!.IsComplete);
    }

    [Theory]
    [InlineData("version_conflict")] [InlineData("mutation_id_reused")]
    public async Task Conflict_preserves_seed_and_reports_blocked_without_completion(string code)
    {
        var (store, service, handler, _, _) = await CreateAsync();
        await store.UpsertTaskAsync(new() { Id = "local_conflict_00000001", Title = "Synthetic", SpaceId = SpaceIds.Default });
        handler.Conflict = true;
        handler.ConflictCode = code;
        var result = await service.SyncAsync();
        Assert.Equal(code, result.BlockedCode);
        Assert.Equal(PersonalCloudInitialSyncPhase.Blocked, result.InitialSyncProgress!.Phase);
        Assert.NotEmpty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal("conflict", handler.Reports[^1].GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Existing_cloud_pairing_downloads_existing_tasks_without_reseeding_them()
    {
        var (store, service, handler, _, _) = await CreateAsync();
        handler.RemoteTask = new() { Id = "local_remote_00000001", Title = "Synthetic existing cloud task", SpaceId = SpaceIds.Default };
        var result = await service.SyncAsync();
        Assert.True(result.InitialSyncProgress!.IsComplete);
        Assert.Equal(0L, result.InitialSyncProgress.Total);
        Assert.Equal(1, result.PulledChanges);
        Assert.Equal(0, handler.Mutations);
        Assert.NotNull(await store.GetTaskAsync(handler.RemoteTask.Id));
    }

    [Fact]
    public async Task Fully_acknowledged_seed_with_deferred_catch_up_stays_blocked()
    {
        var (store, service, handler, _, _) = await CreateAsync();
        var task = new TaskItem { Id = "local_deferred_00000001", Title = "Synthetic", SpaceId = SpaceIds.Default };
        await store.UpsertTaskAsync(task);
        await store.StageUnpublishedTasksForPersonalCloudAsync(Instance, Device);
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, Handler.ChangePage(task));
        Assert.Equal(1, await store.GetDeferredPersonalCloudChangeCountAsync(Instance));
        handler.Checkpoint = 1;
        var result = await service.SyncAsync();
        Assert.Equal(result.InitialSyncProgress!.Total, result.InitialSyncProgress.Acknowledged);
        Assert.Equal("pending_changes", result.BlockedCode);
        Assert.False(result.InitialSyncProgress.IsComplete);
        Assert.Equal(1, await store.GetDeferredPersonalCloudChangeCountAsync(Instance));
        Assert.DoesNotContain(handler.Reports, item => item.GetProperty("state").GetString() == "complete");
    }

    [Fact]
    public async Task Legacy_pending_ordinary_edits_do_not_claim_initial_sync()
    {
        var (store, service, handler, _, _) = await CreateAsync(beginInitialSync: false);
        await store.CreateTaskWithPersonalCloudIntentAsync(new() { Id = "local_legacy_00000001", Title = "Synthetic", SpaceId = SpaceIds.Default }, Instance, Device);
        var result = await service.SyncAsync();
        Assert.Null(result.InitialSyncProgress);
        Assert.Null(await service.GetInitialSyncProgressAsync());
        Assert.Empty(handler.Reports);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Fact]
    public async Task Pairing_marker_exists_before_profile_persistence_and_survives_save_failure()
    {
        var (store, service, _, _, connections) = await CreateAsync(beginInitialSync: false);
        connections.BeforeSave = async profile =>
        {
            Assert.NotNull(await store.GetPersonalCloudInitialSyncProgressAsync(profile.InstanceId, profile.DeviceId));
            throw new IOException("Synthetic profile save interruption");
        };
        await Assert.ThrowsAsync<IOException>(() => service.PairExistingAsync(connections.Load()!.ServiceOrigin,
            "synthetic-new-pair-code-123456789", "Synthetic desktop", false));
        var marker = (await store.GetPersonalCloudInitialSyncProgressAsync(Instance, Device))!;
        Assert.Equal(PersonalCloudInitialSyncPhase.Preparing, marker.Phase);
        Assert.Null(marker.Total);
        Assert.Null(await store.GetPersonalCloudInitialSyncProgressAsync(Instance, "other-pairing"));
    }

    [Fact]
    public async Task Active_persisted_state_is_interrupted_on_restart_with_fixed_seed_counts()
    {
        var (store, service, _, _, _) = await CreateAsync();
        await store.StageUnpublishedTasksForPersonalCloudAsync(Instance, Device);
        await store.SetPersonalCloudInitialSyncPhaseAsync(Instance, Device, PersonalCloudInitialSyncPhase.Uploading);
        Assert.Equal(PersonalCloudInitialSyncPhase.Interrupted, (await service.GetInitialSyncProgressAsync())!.Phase);
        Assert.Equal(0L, (await service.GetInitialSyncProgressAsync())!.Total);
    }

    private async Task<(SqliteTaskStore, PersonalCloudSetupService, Handler, PersonalCloudDeviceIdentityClient, Connections)> CreateAsync(bool beginInitialSync = true)
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        var handler = new Handler();
        var http = new HttpClient(handler);
        var identity = new PersonalCloudDeviceIdentityClient(http, new InMemoryCredentialStore());
        var origin = new Uri("https://synthetic.example.test/");
        await identity.PairAsync(origin, "synthetic-one-time-grant-123456789", "Synthetic desktop", false);
        var connections = new Connections(new() { ServiceOrigin = origin, InstanceId = Instance, DeviceId = Device, ArtifactVersion = "test", ConnectedAt = DateTimeOffset.UtcNow });
        if (beginInitialSync) await store.BeginPersonalCloudInitialSyncAsync(Instance, Device);
        var service = new PersonalCloudSetupService(null!, "test", connectionStore: connections, deviceIdentity: identity, syncHttp: http, taskStore: store);
        return (store, service, handler, identity, connections);
    }

    private sealed class Connections(PersonalCloudConnectionProfile profile) : IPersonalCloudConnectionStore
    {
        public Func<PersonalCloudConnectionProfile, Task>? BeforeSave { get; set; }
        public PersonalCloudConnectionProfile? Load() => profile;
        public Task SaveAsync(PersonalCloudConnectionProfile connection, CancellationToken cancellationToken = default) => BeforeSave?.Invoke(connection) ?? Task.CompletedTask;
        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public bool WorkspaceEnabled { get; set; }
        public int? FailAfterMutations { get; set; }
        public bool FailCatchUp { get; set; }
        public bool FailCatchUpAfterMutation { get; set; }
        public bool Conflict { get; set; }
        public string ConflictCode { get; set; } = "version_conflict";
        public int Mutations { get; private set; }
        public bool BatchEnabled { get; set; }
        public int MutationPosts { get; private set; }
        public int HealthRequests { get; private set; }
        public int LastCheckpointMutationCount { get; private set; }
        public int Checkpoints { get; private set; }
        public long Checkpoint { get; set; }
        public TaskItem? RemoteTask { get; set; }
        private bool _remoteDelivered;
        public HttpStatusCode ReportStatus { get; set; } = HttpStatusCode.OK;
        public List<JsonElement> Reports { get; } = [];
        public List<string?> ReportAuthorization { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/health")
            {
                HealthRequests++;
                if (WorkspaceEnabled)
                {
                    var capabilities = new Dictionary<string, object> { ["workspace"] = new { protocolVersion = 1, path = "/v1/tasks/mutations", schemaVersion = 23 } };
                    if (BatchEnabled) capabilities["mutationBatch"] = new { protocolVersion = 1, path = "/v1/tasks/mutations/batch", maxMutations = 2, maxRequestUtf8Bytes = 16384 };
                    return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { status = "ok", protocolVersion = 1, product = "tasks", artifactVersion = "test", schemaVersion = 23, capabilities }));
                }
                return Json(HttpStatusCode.OK, BatchEnabled
                    ? """{"status":"ok","protocolVersion":1,"product":"tasks","artifactVersion":"test","schemaVersion":22,"capabilities":{"mutationBatch":{"protocolVersion":1,"path":"/v1/tasks/mutations/batch","maxMutations":2,"maxRequestUtf8Bytes":16384}}}"""
                    : """{"status":"ok","protocolVersion":1,"product":"tasks","artifactVersion":"test","schemaVersion":21}""");
            }
            if (path == "/sync/bootstrap" && WorkspaceEnabled) return Json(HttpStatusCode.OK, PersonalCloudK0Codec.Serialize(new {
                protocolVersion = 1, product = "tasks", checkpoint = 0, snapshot = new {
                    spaces = new[] { new { id = SpaceIds.Default, name = "Default", color = "#808080", icon = (string?)null, sortOrder = 0, isArchived = false, createdAt = "2026-01-01T00:00:00.000Z", updatedAt = "2026-01-01T00:00:00.000Z", deletedAt = (string?)null, entityVersion = 1 } },
                    projects = Array.Empty<object>(), tasks = Array.Empty<object>(), labels = Array.Empty<object>(), taskLabels = Array.Empty<object>(), tombstones = Array.Empty<object>(), sourceDescriptors = Array.Empty<object>(), externalCommands = Array.Empty<object>(), scopes = Array.Empty<object>(), catalog = Array.Empty<object>(), intents = Array.Empty<object>(), routes = Array.Empty<object>() } }));
            if (path == "/auth/pair") return Json(HttpStatusCode.Created, $$"""{"protocolVersion":1,"instanceId":"{{Instance}}","deviceId":"{{Device}}","refreshCredential":"synthetic-refresh-123456789012345","capabilities":["desktop_sync"],"refreshExpiresAt":"2099-01-01T00:00:00Z"}""");
            if (path == "/auth/device/token") return Json(HttpStatusCode.OK, """{"protocolVersion":1,"accessToken":"synthetic-access-123456789012345","expiresAt":"2099-01-01T00:00:00Z","capabilities":["desktop_sync"]}""");
            if (path == "/sync/initial-status")
            {
                if ((int)ReportStatus == 0) throw new HttpRequestException("Synthetic optional report failure");
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Reports.Add(document.RootElement.Clone());
                ReportAuthorization.Add(request.Headers.Authorization?.Scheme);
                return Json(ReportStatus, """{"protocolVersion":1,"accepted":true,"updatedAt":"2026-10-12T00:00:00Z"}""");
            }
            if (path == "/v1/tasks/mutations")
            {
                MutationPosts++;
                if (Conflict) return Json(HttpStatusCode.Conflict, JsonSerializer.Serialize(new { error = new { code = ConflictCode } }));
                if (FailAfterMutations is { } limit && Mutations >= limit) throw new HttpRequestException("Synthetic offline");
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                return Json(HttpStatusCode.Created, JsonSerializer.Serialize(Accept(document.RootElement)));
            }
            if (path == "/v1/tasks/mutations/batch")
            {
                MutationPosts++;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                Assert.True(Encoding.UTF8.GetByteCount(body) <= 16384);
                using var document = JsonDocument.Parse(body);
                var mutations = document.RootElement.GetProperty("mutations").EnumerateArray().ToArray();
                Assert.InRange(mutations.Length, 1, 2);
                var receipts = mutations.Select(Accept).ToArray();
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { protocolVersion = 1, product = "tasks", instanceId = Instance,
                    receipts, processedCount = receipts.Length, stoppedAt = (int?)null, error = (object?)null }));
            }
            if (path == "/sync/changes")
            {
                if (FailCatchUp || (FailCatchUpAfterMutation && Mutations > 0)) throw new HttpRequestException("Synthetic catch-up offline");
                if (RemoteTask is not null && !_remoteDelivered)
                {
                    _remoteDelivered = true;
                    Checkpoint = 1;
                    return Json(HttpStatusCode.OK, ChangePage(RemoteTask));
                }
                return Json(HttpStatusCode.OK, $$"""{"protocolVersion":1,"product":"tasks","changes":[],"nextCheckpoint":{{Checkpoint}}}""");
            }
            if (path == "/sync/checkpoints")
            {
                Checkpoints++;
                LastCheckpointMutationCount = Mutations;
                return Json(HttpStatusCode.OK, $$"""{"protocolVersion":1,"product":"tasks","deviceId":"{{Device}}","acknowledgedCheckpoint":{{Checkpoint}}}""");
            }
            throw new InvalidOperationException($"Unexpected synthetic route {path}");
        }
        private object Accept(JsonElement mutation)
        {
            Mutations++;
            return new { protocolVersion = 1, product = "tasks", mutationId = mutation.GetProperty("mutationId").GetString(),
                outcome = "accepted", entityId = mutation.GetProperty("entityId").GetString(),
                entityVersion = (mutation.GetProperty("baseEntityVersion").ValueKind == JsonValueKind.Null ? 0 : mutation.GetProperty("baseEntityVersion").GetInt64()) + 1,
                changeSeq = Mutations, serverAcceptedAt = "2026-10-12T00:00:00.000Z" };
        }
        public static string ChangePage(TaskItem task)
        {
            var mutation = PersonalCloudK0Mapper.CreateTaskMutation(task, Instance, Device, "mutation_remote_initial_00000001", 1, DateTimeOffset.UtcNow);
            return PersonalCloudK0Codec.Serialize(new { protocolVersion = 1, product = "tasks", nextCheckpoint = 1,
                changes = new[] { new K0TaskCreatedChange { ProtocolVersion = 1, Product = "tasks", ChangeSeq = 1,
                    MutationId = mutation.MutationId, Operation = "task.created", EntityKind = "task", EntityId = task.Id,
                    EntityVersion = 1, Payload = mutation.Payload, ServerAcceptedAt = "2026-10-12T00:00:00.000Z" } } });
        }
        private static HttpResponseMessage Json(HttpStatusCode status, string value) => new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }
    public void Dispose() => TestDirectory.Delete(_directory);
}
