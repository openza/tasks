using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudWorkspacePagingTests : IDisposable
{
    private const string Instance = "instance_paging_example";
    private const string Device = "device_paging_example";
    private const string Stamp = "2026-10-13T00:00:00.000Z";
    private static readonly string[] Sections = "spaces projects tasks labels taskLabels tombstones sourceDescriptors externalCommands scopes catalog intents routes".Split(' ');
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-workspace-paging", Guid.NewGuid().ToString("N"));

    private static object With(object value, params (string Name, object? Value)[] fields)
    {
        using var document = JsonDocument.Parse(PersonalCloudK0Codec.Serialize(value));
        var result = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        foreach (var field in fields) result[field.Name] = field.Value;
        return result;
    }

    private static string Baseline(long checkpoint, string? cursor = null, string section = "spaces", params object[] records) =>
        PersonalCloudK0Codec.Serialize(new { protocolVersion = 1, product = "tasks", checkpoint,
            snapshot = Sections.ToDictionary(k => k, k => k == section ? records : Array.Empty<object>()), hasMore = cursor is not null, nextCursor = cursor });

    private static string Feed(long sequence, bool more, params object[] tasks)
    {
        using var document = JsonDocument.Parse(PersonalCloudWorkspaceTests.Page(sequence, tasks: tasks));
        return PersonalCloudK0Codec.Serialize(With(document.RootElement, ("hasMore", more)));
    }

    private async Task<(SqliteTaskStore Store, PersonalCloudSyncClient Client, Handler Handler)> CreateAsync()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db")); await store.InitializeAsync();
        var handler = new Handler(); var http = new HttpClient(handler);
        var identity = new PersonalCloudDeviceIdentityClient(http, new InMemoryCredentialStore()); var origin = new Uri("https://synthetic.example.test/");
        await identity.PairAsync(origin, "synthetic-one-time-grant-123456789", "Example", false);
        var client = new PersonalCloudSyncClient(http, identity, store, new() { ServiceOrigin = origin, InstanceId = Instance, DeviceId = Device, ArtifactVersion = "test", ConnectedAt = DateTimeOffset.UtcNow });
        return (store, client, handler);
    }

    [Fact]
    public async Task Dense_baseline_larger_than_two_MiB_is_staged_in_bounded_pages_and_committed_once()
    {
        var (store, client, handler) = await CreateAsync();
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(1,
            [PersonalCloudWorkspaceTests.Space()], [PersonalCloudWorkspaceTests.Project()], [PersonalCloudWorkspaceTests.TaskRecord()]));
        var original = (await store.GetTaskAsync("task_test"))!;
        await store.UpsertTaskAsync(original with { LocalMetadataJson = "{\"localExample\":true}" });
        var records = Enumerable.Range(0, 600).Select(i => With(PersonalCloudWorkspaceTests.TaskRecord(i == 0 ? "task_test" : $"task_dense_{i:D6}"),
            ("notes", new string('x', 4096)), ("entityVersion", 2), ("deadlineAt", "2026-10-20T04:30:00.123Z"))).ToArray();
        var pages = new List<string> { Baseline(9, "opaque+/space=", "spaces", PersonalCloudWorkspaceTests.Space()),
            Baseline(9, "opaque_tasks_0", "projects", PersonalCloudWorkspaceTests.Project()) };
        for (var index = 0; index < 6; index++) pages.Add(Baseline(9, index == 5 ? null : $"opaque_tasks_{index + 1}", "tasks", records.Skip(index * 100).Take(100).ToArray()));
        Assert.True(pages.Sum(Encoding.UTF8.GetByteCount) > 2097152);
        Assert.All(pages, page => Assert.InRange(Encoding.UTF8.GetByteCount(page), 1, 2097152));
        handler.Bootstrap = async (request, index) =>
        {
            Assert.Contains("workspace=1&paged=1", request.RequestUri!.Query);
            if (index == 1) Assert.Contains("cursor=opaque%2B%2Fspace%3D", request.RequestUri.Query);
            Assert.Equal(1, await store.GetPersonalCloudCheckpointAsync(Instance, Device));
            Assert.Null(await store.GetTaskAsync("task_dense_000001"));
            return Json(pages[index]);
        };
        handler.Checkpoint = 9;
        var result = await client.SyncAsync();
        Assert.Equal(9, result.Checkpoint); Assert.Equal(8, handler.BootstrapRequests); Assert.Equal(1, handler.Acknowledgements);
        var task = (await store.GetTaskAsync("task_test"))!;
        Assert.Equal("{\"localExample\":true}", task.LocalMetadataJson);
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T04:30:00.123Z"), task.DeadlineAt);
        Assert.Equal(4096, task.Notes!.Length); Assert.Equal(2, await store.GetPersonalCloudEntityVersionAsync(Instance, task.Id));
        Assert.Equal(600, (await store.GetTasksAsync(new() { SpaceId = "space_test", Kind = TaskListKind.All, IncludeSubtasks = true })).Count);
        Assert.Equal(0, await StagedCountAsync(store));
    }

    [Theory]
    [InlineData("network")]
    [InlineData("cancel")]
    [InlineData("response_too_large")]
    [InlineData("invalid_request")]
    public async Task Incomplete_baseline_never_changes_replica_metadata_or_acknowledges(string failure)
    {
        var (store, client, handler) = await CreateAsync();
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(1, [PersonalCloudWorkspaceTests.Space()], [PersonalCloudWorkspaceTests.Project()], [PersonalCloudWorkspaceTests.TaskRecord()]));
        var before = (await store.GetTaskAsync("task_test"))!; await store.UpsertTaskAsync(before with { LocalMetadataJson = "local-example" });
        handler.Bootstrap = (_, index) => index == 0 ? Task.FromResult(Json(Baseline(7, "next", "spaces", PersonalCloudWorkspaceTests.Space(name: "Changed")))) : failure switch
        {
            "network" => throw new HttpRequestException("Synthetic offline"),
            "cancel" => throw new OperationCanceledException(),
            _ => Task.FromResult(Error(failure)),
        };
        await Assert.ThrowsAnyAsync<Exception>(() => client.SyncAsync());
        Assert.Equal(1, await store.GetPersonalCloudCheckpointAsync(Instance, Device));
        Assert.Equal("local-example", (await store.GetTaskAsync(before.Id))!.LocalMetadataJson);
        Assert.Equal("Example", (await store.GetSpacesAsync()).Single(s => s.Id == "space_test").Name);
        Assert.Equal(0, handler.Acknowledgements); Assert.Equal(0, await StagedCountAsync(store));
    }

    [Fact]
    public async Task Changed_head_restarts_from_first_page_at_most_three_times_without_partial_commit()
    {
        var (store, client, handler) = await CreateAsync();
        handler.Bootstrap = (request, index) =>
        {
            Assert.Equal(index % 2 == 1, request.RequestUri!.Query.Contains("cursor=", StringComparison.Ordinal));
            return Task.FromResult(index % 2 == 0 ? Json(Baseline(7, "next", "spaces", PersonalCloudWorkspaceTests.Space())) : Error("bootstrap_changed"));
        };
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.SyncAsync());
        Assert.Equal("bootstrap_changed", error.Code); Assert.Equal(6, handler.BootstrapRequests);
        Assert.Equal(0, await store.GetPersonalCloudCheckpointAsync(Instance, Device));
        Assert.DoesNotContain(await store.GetSpacesAsync(), s => s.Id == "space_test"); Assert.Equal(0, handler.Acknowledgements);
        Assert.Equal(0, await StagedCountAsync(store));
    }

    [Fact]
    public async Task Inconsistent_page_checkpoint_is_discarded_then_a_consistent_retry_can_finish()
    {
        var (store, client, handler) = await CreateAsync();
        handler.Bootstrap = (_, index) => Task.FromResult(index switch
        {
            0 => Json(Baseline(7, "next", "spaces", PersonalCloudWorkspaceTests.Space(name: "Discarded"))),
            1 => Json(Baseline(8)),
            _ => Json(Baseline(8, section: "spaces", records: [PersonalCloudWorkspaceTests.Space(name: "Consistent")])),
        });
        handler.Checkpoint = 8;
        Assert.Equal(8, (await client.SyncAsync()).Checkpoint); Assert.Equal(3, handler.BootstrapRequests);
        Assert.Equal("Consistent", (await store.GetSpacesAsync()).Single(s => s.Id == "space_test").Name);
    }

    [Fact]
    public async Task Short_byte_bounded_feed_continues_while_hasMore_and_acknowledges_only_its_final_checkpoint()
    {
        var (store, client, handler) = await CreateAsync();
        handler.Bootstrap = (_, _) => Task.FromResult(Json(Baseline(0, section: "spaces", records: [PersonalCloudWorkspaceTests.Space()])));
        handler.Changes = (request, index) =>
        {
            Assert.Contains("workspace=1&paged=1", request.RequestUri!.Query); Assert.Contains($"after={index}", request.RequestUri.Query);
            return Json(Feed(index + 1, index == 0, PersonalCloudWorkspaceTests.TaskRecord($"task_feed_{index}", projectId: null)));
        };
        handler.Checkpoint = 2;
        Assert.Equal(2, (await client.SyncAsync()).PulledChanges); Assert.Equal(2, handler.ChangeRequests); Assert.Equal(1, handler.Acknowledgements);
        Assert.NotNull(await store.GetTaskAsync("task_feed_1"));
    }

    [Fact]
    public async Task Oversized_single_change_preserves_committed_prefix_without_ack_and_retry_resumes_it()
    {
        var (store, client, handler) = await CreateAsync();
        handler.Bootstrap = (_, _) => Task.FromResult(Json(Baseline(0, section: "spaces", records: [PersonalCloudWorkspaceTests.Space()])));
        handler.Changes = (_, index) => index == 0 ? Json(Feed(1, true, PersonalCloudWorkspaceTests.TaskRecord("task_prefix", projectId: null))) : Error("response_too_large");
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.SyncAsync());
        Assert.Equal("response_too_large", error.Code); Assert.Equal(1, await store.GetPersonalCloudCheckpointAsync(Instance, Device)); Assert.Equal(0, handler.Acknowledgements);
        handler.Changes = (request, _) => { Assert.Contains("after=1", request.RequestUri!.Query); return Json(Feed(2, false, PersonalCloudWorkspaceTests.TaskRecord("task_suffix", projectId: null))); };
        handler.Checkpoint = 2;
        Assert.Equal(2, (await client.SyncAsync()).Checkpoint); Assert.Equal(1, handler.BootstrapRequests); Assert.Equal(1, handler.Acknowledgements);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty_more")]
    public async Task Invalid_paged_feed_never_acknowledges(string defect)
    {
        var (store, client, handler) = await CreateAsync();
        handler.Changes = (_, _) => Json(defect == "missing" ? "{\"protocolVersion\":1,\"product\":\"tasks\",\"changes\":[],\"nextCheckpoint\":0}" :
            "{\"protocolVersion\":1,\"product\":\"tasks\",\"changes\":[],\"nextCheckpoint\":0,\"hasMore\":true}");
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.SyncAsync()); Assert.Equal(0, handler.Acknowledgements);
        Assert.Equal(0, await store.GetPersonalCloudCheckpointAsync(Instance, Device));
    }

    [Fact]
    public async Task Repeated_expired_feed_cannot_reset_the_three_baseline_attempt_budget()
    {
        var (_, client, handler) = await CreateAsync();
        handler.Changes = (_, _) => Error("checkpoint_expired");
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.SyncAsync());
        Assert.Equal("bootstrap_changed", error.Code); Assert.Equal(3, handler.BootstrapRequests); Assert.Equal(0, handler.Acknowledgements);
    }

    [Fact]
    public async Task HasMore_page_limit_preserves_the_prefix_and_does_not_acknowledge_incomplete_catchup()
    {
        var (store, client, handler) = await CreateAsync();
        handler.Bootstrap = (_, _) => Task.FromResult(Json(Baseline(0, section: "spaces", records: [PersonalCloudWorkspaceTests.Space()])));
        handler.Changes = (_, index) => Json(Feed(index + 1, true, PersonalCloudWorkspaceTests.TaskRecord("task_continuous", index + 1, projectId: null)));
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.SyncAsync());
        Assert.Equal("sync_incomplete", error.Code); Assert.Equal(100, handler.ChangeRequests); Assert.Equal(0, handler.Acknowledgements);
        Assert.Equal(100, await store.GetPersonalCloudCheckpointAsync(Instance, Device)); Assert.NotNull(await store.GetTaskAsync("task_continuous"));
    }

    [Theory]
    [InlineData("page_count=10000")]
    [InlineData("record_count=100000")]
    [InlineData("json_bytes=134217728")]
    public async Task Accumulation_limits_fail_before_domain_replacement(string exhausted)
    {
        var (store, _, _) = await CreateAsync();
        var transfer = await store.BeginPersonalCloudWorkspaceBootstrapAsync(Instance, Device);
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            await connection.OpenAsync(); var command = connection.CreateCommand();
            command.CommandText = "UPDATE personal_cloud_workspace_bootstraps SET " + exhausted; await command.ExecuteNonQueryAsync();
        }
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.StagePersonalCloudWorkspaceBootstrapPageAsync(transfer, null, Baseline(1, section: "spaces", records: [PersonalCloudWorkspaceTests.Space()])));
        Assert.Equal("sync_incomplete", error.Code); Assert.DoesNotContain(await store.GetSpacesAsync(), s => s.Id == "space_test");
        await store.DiscardPersonalCloudWorkspaceBootstrapAsync(transfer);
    }

    [Theory]
    [InlineData("cursor")]
    [InlineData("duplicate")]
    [InlineData("section")]
    public async Task Staging_rejects_repeated_cursors_records_and_reversed_sections(string defect)
    {
        var (store, _, _) = await CreateAsync(); var transfer = await store.BeginPersonalCloudWorkspaceBootstrapAsync(Instance, Device);
        await store.StagePersonalCloudWorkspaceBootstrapPageAsync(transfer, null, Baseline(1, "next", "projects", PersonalCloudWorkspaceTests.Project()));
        var page = defect switch
        {
            "cursor" => Baseline(1, "next"),
            "duplicate" => Baseline(1, section: "projects", records: [PersonalCloudWorkspaceTests.Project()]),
            _ => Baseline(1, section: "spaces", records: [PersonalCloudWorkspaceTests.Space()]),
        };
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.StagePersonalCloudWorkspaceBootstrapPageAsync(transfer, "next", page));
        Assert.Equal(0, await store.GetPersonalCloudCheckpointAsync(Instance, Device)); await store.DiscardPersonalCloudWorkspaceBootstrapAsync(transfer);
    }

    [Fact]
    public async Task Valid_colon_containing_task_label_pairs_do_not_collide_in_staging()
    {
        var (store, _, _) = await CreateAsync(); var transfer = await store.BeginPersonalCloudWorkspaceBootstrapAsync(Instance, Device);
        await store.StagePersonalCloudWorkspaceBootstrapPageAsync(transfer, null, Baseline(1, "next", "taskLabels", new { taskId = "a:b", labelId = "c" }));
        await store.StagePersonalCloudWorkspaceBootstrapPageAsync(transfer, "next", Baseline(1, section: "taskLabels", records: [new { taskId = "a", labelId = "b:c" }]));
        Assert.Equal(1, await store.CommitPersonalCloudWorkspaceBootstrapAsync(transfer, Instance, Device));
        Assert.Equal(0, await StagedCountAsync(store));
        transfer = await store.BeginPersonalCloudWorkspaceBootstrapAsync(Instance, Device);
        await store.StagePersonalCloudWorkspaceBootstrapPageAsync(transfer, null, Baseline(2, "next", "taskLabels", new { taskId = "a:b", labelId = "c" }));
        var duplicate = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.StagePersonalCloudWorkspaceBootstrapPageAsync(transfer, "next",
            Baseline(2, section: "taskLabels", records: [new { taskId = "a:b", labelId = "c" }])));
        Assert.Equal("invalid_response", duplicate.Code); Assert.Equal(1, await store.GetPersonalCloudCheckpointAsync(Instance, Device));
        await store.DiscardPersonalCloudWorkspaceBootstrapAsync(transfer);
    }

    [Fact]
    public async Task Pending_edit_during_download_and_late_apply_error_both_leave_prior_replica_atomic()
    {
        var (store, _, _) = await CreateAsync(); await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true);
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(1, [PersonalCloudWorkspaceTests.Space()], [PersonalCloudWorkspaceTests.Project()], [PersonalCloudWorkspaceTests.TaskRecord()]));
        var transfer = await store.BeginPersonalCloudWorkspaceBootstrapAsync(Instance, Device);
        await store.StagePersonalCloudWorkspaceBootstrapPageAsync(transfer, null, Baseline(9, "next", "spaces", PersonalCloudWorkspaceTests.Space(name: "Must roll back")));
        await store.StagePersonalCloudWorkspaceBootstrapPageAsync(transfer, "next", Baseline(9, section: "tombstones", records: [new { entityKind = "task", entityId = "task_test", entityVersion = 0, deletedAt = Stamp, purgeAfter = (string?)null }]));
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.CommitPersonalCloudWorkspaceBootstrapAsync(transfer, Instance, Device));
        Assert.NotNull(await store.GetTaskAsync("task_test")); Assert.Equal(1, await store.GetPersonalCloudCheckpointAsync(Instance, Device));
        Assert.Equal("Example", (await store.GetSpacesAsync()).Single(s => s.Id == "space_test").Name);
        await store.CreateTaskWithPersonalCloudIntentAsync(new() { Id = "task_new_pending", Title = "Preserve edit" }, Instance, Device);
        var pending = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.CommitPersonalCloudWorkspaceBootstrapAsync(transfer, Instance, Device));
        Assert.Equal("pending_intents", pending.Code); Assert.NotNull(await store.GetTaskAsync("task_new_pending"));
        await store.DiscardPersonalCloudWorkspaceBootstrapAsync(transfer);
    }

    private static async Task<long> StagedCountAsync(SqliteTaskStore store)
    {
        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Mode=ReadOnly"); await connection.OpenAsync();
        var read = connection.CreateCommand(); read.CommandText = "SELECT COUNT(*) FROM personal_cloud_workspace_bootstraps"; return Convert.ToInt64(await read.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Restart_discards_abandoned_staging_and_successive_baselines_keep_device_ownership_and_local_metadata()
    {
        var (store, _, _) = await CreateAsync(); await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true);
        var abandoned = await store.BeginPersonalCloudWorkspaceBootstrapAsync(Instance, Device);
        await store.StagePersonalCloudWorkspaceBootstrapPageAsync(abandoned, null, Baseline(1, "abandoned", "spaces", PersonalCloudWorkspaceTests.Space(name: "Abandoned")));
        store = new SqliteTaskStore(store.DatabasePath); await store.InitializeAsync();
        var transfer = await store.BeginPersonalCloudWorkspaceBootstrapAsync(Instance, Device);
        Assert.Equal(1, await StagedCountAsync(store));
        await store.StagePersonalCloudWorkspaceBootstrapPageAsync(transfer, null, Baseline(2, section: "spaces", records: [PersonalCloudWorkspaceTests.Space(name: "First")]));
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.CommitPersonalCloudWorkspaceBootstrapAsync(transfer, Instance, "device_other_example"));
        Assert.Equal(0, await store.GetPersonalCloudCheckpointAsync(Instance, Device));
        await store.CommitPersonalCloudWorkspaceBootstrapAsync(transfer, Instance, Device);
        transfer = await store.BeginPersonalCloudWorkspaceBootstrapAsync(Instance, Device);
        await store.StagePersonalCloudWorkspaceBootstrapPageAsync(transfer, null, Baseline(3, section: "spaces", records: [PersonalCloudWorkspaceTests.Space(name: "Second")]));
        await store.CommitPersonalCloudWorkspaceBootstrapAsync(transfer, Instance, Device);
        Assert.Equal("Second", (await store.GetSpacesAsync()).Single(s => s.Id == "space_test").Name);
        Assert.Equal(3, await store.GetPersonalCloudCheckpointAsync(Instance, Device)); Assert.Equal(0, await StagedCountAsync(store));
    }

    [Fact]
    public async Task Workspace_instants_keep_milliseconds_across_restart_and_notes_only_patch_with_legacy_integer_compatibility()
    {
        var (store, _, _) = await CreateAsync(); await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true);
        var instant = "2026-10-20T04:30:00.123Z";
        var record = With(PersonalCloudWorkspaceTests.TaskRecord(), ("plannedAt", instant), ("deadlineAt", instant), ("scheduledStart", instant), ("scheduledEnd", instant));
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(1, [PersonalCloudWorkspaceTests.Space()], [PersonalCloudWorkspaceTests.Project()], [record]));
        store = new SqliteTaskStore(store.DatabasePath); await store.InitializeAsync();
        var task = (await store.GetTaskAsync("task_test"))!;
        Assert.All(new[] { task.PlannedAt, task.DeadlineAt, task.ScheduledStart, task.ScheduledEnd }, value => Assert.Equal(DateTimeOffset.Parse(instant), value));
        await store.PatchTaskWithPersonalCloudIntentAsync(task.Id, new Dictionary<string, object?> { ["notes"] = "Notes only" }, task.Revision, Instance, Device);
        task = (await new SqliteTaskStore(store.DatabasePath).GetTaskAsync(task.Id))!;
        Assert.All(new[] { task.PlannedAt, task.DeadlineAt, task.ScheduledStart, task.ScheduledEnd }, value => Assert.Equal(DateTimeOffset.Parse(instant), value));
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            await connection.OpenAsync(); var command = connection.CreateCommand();
            command.CommandText = "UPDATE tasks SET planned_at=@seconds,deadline_at=@milliseconds,scheduled_start=@seconds,scheduled_end=@seconds WHERE id='task_test'";
            command.Parameters.AddWithValue("@seconds", DateTimeOffset.Parse(Stamp).ToUnixTimeSeconds());
            command.Parameters.AddWithValue("@milliseconds", DateTimeOffset.Parse(instant).ToUnixTimeMilliseconds()); await command.ExecuteNonQueryAsync();
        }
        task = (await new SqliteTaskStore(store.DatabasePath).GetTaskAsync(task.Id))!;
        Assert.Equal(DateTimeOffset.Parse(Stamp), task.PlannedAt); Assert.Equal(DateTimeOffset.Parse(instant), task.DeadlineAt);
        Assert.Equal(DateTimeOffset.Parse(Stamp), task.ScheduledStart); Assert.Equal(DateTimeOffset.Parse(Stamp), task.ScheduledEnd);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Error(string code) => new(HttpStatusCode.Conflict) { Content = new StringContent(JsonSerializer.Serialize(new { error = new { code } }), Encoding.UTF8, "application/json") };

    private sealed class Handler : HttpMessageHandler
    {
        public const string Health = """{"status":"ok","protocolVersion":1,"product":"tasks","artifactVersion":"test","schemaVersion":24,"capabilities":{"workspace":{"protocolVersion":1,"path":"/v1/tasks/mutations","schemaVersion":24},"providerCatalog":{"protocolVersion":2,"path":"/v1/provider/catalog","intentsPath":"/v1/provider/intents"},"workspacePaging":{"protocolVersion":1,"bootstrapPath":"/sync/bootstrap","changesPath":"/sync/changes","maxResponseUtf8Bytes":2097152,"maxPageRecords":100}}}""";
        public string HealthBody { get; set; } = Health;
        public Func<HttpRequestMessage, int, Task<HttpResponseMessage>> Bootstrap { get; set; } = (_, _) => Task.FromResult(Json(Baseline(0)));
        public Func<HttpRequestMessage, int, HttpResponseMessage>? Changes { get; set; }
        public long Checkpoint { get; set; }
        public int BootstrapRequests { get; private set; }
        public int ChangeRequests { get; private set; }
        public int Acknowledgements { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/auth/pair": return Json($$"""{"protocolVersion":1,"instanceId":"{{Instance}}","deviceId":"{{Device}}","refreshCredential":"synthetic-refresh-123456789012345","capabilities":["desktop_sync"],"refreshExpiresAt":"2099-01-01T00:00:00Z"}""");
                case "/auth/device/token": return Json("""{"protocolVersion":1,"accessToken":"synthetic-access-123456789012345","expiresAt":"2099-01-01T00:00:00Z","capabilities":["desktop_sync"]}""");
                case "/health": return Json(HealthBody);
                case "/sync/bootstrap": return await Bootstrap(request, BootstrapRequests++);
                case "/sync/changes": return Changes?.Invoke(request, ChangeRequests++) ?? Json($$"""{"protocolVersion":1,"product":"tasks","changes":[],"nextCheckpoint":{{Checkpoint}},"hasMore":false}""");
                case "/sync/checkpoints":
                    using (var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))) Assert.Equal(Checkpoint, document.RootElement.GetProperty("checkpoint").GetInt64());
                    Acknowledgements++; return Json($$"""{"protocolVersion":1,"product":"tasks","deviceId":"{{Device}}","acknowledgedCheckpoint":{{Checkpoint}}}""");
                default: throw new InvalidOperationException(request.RequestUri.AbsolutePath);
            }
        }
    }

    [Theory]
    [InlineData("2097152", "4194304")]
    [InlineData("maxPageRecords\":100", "maxPageRecords\":101")]
    [InlineData("bootstrapPath\":\"/sync/bootstrap", "bootstrapPath\":\"https://untrusted.example.test/")]
    [InlineData("schemaVersion\":24", "schemaVersion\":23")]
    public async Task Health_rejects_unrecognized_paging_limits_paths_and_schema(string before, string after)
    {
        var handler = new Handler { HealthBody = Handler.Health.Replace(before, after, StringComparison.Ordinal) };
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => new PersonalCloudHealthClient(new HttpClient(handler)).InspectAsync(new("https://synthetic.example.test/")));
        Assert.Equal("health_mismatch", error.Code);
    }

    public void Dispose() => TestDirectory.Delete(_directory);
}
