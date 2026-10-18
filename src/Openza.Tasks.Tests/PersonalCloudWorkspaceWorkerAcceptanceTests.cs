using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudWorkspaceWorkerAcceptanceTests
{
    private const string Instance = "instance_bridge_acceptance_0001";
    private const string Device = "device_bridge_acceptance_a";
    private const string OtherDevice = "device_bridge_acceptance_b";

    [PagedWorkspaceAcceptance]
    [Trait("Category", "LocalWorkerAcceptance")]
    public async Task Retained_actual_schema24_paged_baseline_preserves_private_metadata_and_existing_device_sequence()
    {
        var origin = new Uri(Environment.GetEnvironmentVariable("OPENZA_BRIDGE_ACCEPTANCE_ORIGIN")!); Assert.True(origin.IsLoopback && origin.Scheme == "http");
        var directory = Environment.GetEnvironmentVariable("OPENZA_WORKSPACE_ACCEPTANCE_DATA_DIR")!;
        var path = Path.Combine(directory, "a.db"); Assert.True(File.Exists(path), "Use a verified backup of the retained synthetic client; never reconstruct its sequences.");
        using var transport = new ActualTransport(Device); using var http = new HttpClient(transport) { BaseAddress = origin };
        var replica = await ReplicaAsync(http, origin, path, Device, "publicfake-bridge-refresh-device-a-20261011");
        var task = (await replica.Store.GetTasksAsync(new() { Kind = TaskListKind.All, IncludeSubtasks = true })).First(t => t.IntegrationId == IntegrationIds.Local);
        await replica.Store.UpsertTaskAsync(task with { LocalMetadataJson = "{\"syntheticPagedRecovery\":true}" });
        var before = await ReadDeviceSequenceAsync(replica.Store);
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync(); var command = connection.CreateCommand(); command.CommandText = "UPDATE personal_cloud_workspace_capabilities SET hydrated=0 WHERE instance_id=@i";
            command.Parameters.AddWithValue("@i", Instance); await command.ExecuteNonQueryAsync();
        }
        var result = await replica.Sync.SyncAsync(); Assert.Null(result.BlockedCode);
        Assert.True(transport.PagedBootstrapRequests >= 12); Assert.True(transport.PagedChangeRequests > 0);
        var restored = (await new SqliteTaskStore(path).GetTaskAsync(task.Id))!;
        Assert.Equal("{\"syntheticPagedRecovery\":true}", restored.LocalMetadataJson);
        Assert.Equal(task.SourceExternalId, restored.SourceExternalId); Assert.Equal(task.SourceConnectionId, restored.SourceConnectionId);
        Assert.Equal(task.SourceProviderTaskId, restored.SourceProviderTaskId); Assert.Equal(task.PlannedAt, restored.PlannedAt); Assert.Equal(task.DeadlineAt, restored.DeadlineAt);
        Assert.Equal(before, await ReadDeviceSequenceAsync(replica.Store));
        Assert.Empty(await replica.Store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    private static async Task<long> ReadDeviceSequenceAsync(SqliteTaskStore store)
    {
        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Mode=ReadOnly"); await connection.OpenAsync();
        var read = connection.CreateCommand(); read.CommandText = "SELECT next_device_sequence FROM personal_cloud_device_state WHERE instance_id=@i AND device_id=@d";
        read.Parameters.AddWithValue("@i", Instance); read.Parameters.AddWithValue("@d", Device); return Convert.ToInt64(await read.ExecuteScalarAsync());
    }

    private sealed class PagedWorkspaceAcceptanceAttribute : FactAttribute
    {
        public PagedWorkspaceAcceptanceAttribute()
        {
            if (Environment.GetEnvironmentVariable("OPENZA_WORKSPACE_PAGED_BASELINE") != "1" || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENZA_BRIDGE_ACCEPTANCE_ORIGIN")))
                Skip = "Opt-in paged schema24 acceptance against preserved synthetic SQLite and local Worker clones.";
        }
    }

    [LocalProviderBridgeAcceptance]
    [Trait("Category", "LocalWorkerAcceptance")]
    public async Task Actual_Worker_and_Desktop_converge_workspace_metadata_and_owned_catalog_intents()
    {
        var origin = new Uri(Environment.GetEnvironmentVariable("OPENZA_BRIDGE_ACCEPTANCE_ORIGIN")!);
        Assert.True(origin.IsLoopback && origin.Scheme == "http");
        var directory = Environment.GetEnvironmentVariable("OPENZA_WORKSPACE_ACCEPTANCE_DATA_DIR")
            ?? throw new InvalidOperationException("Actual workspace acceptance requires a durable ignored fixture directory.");
        Assert.False(File.Exists(Path.Combine(directory, "a.db")), "Use this full runner only for a fresh fixture; preserved sequences belong to its continuation runner.");
        Directory.CreateDirectory(directory);
        {
            using var web = new HttpClient { BaseAddress = origin }; web.DefaultRequestHeaders.Add("x-openza-test-session", "synthetic-local-bridge-acceptance");
            using var transport = new ActualTransport(Device); using var http = new HttpClient(transport) { BaseAddress = origin };
            using var otherTransport = new ActualTransport(OtherDevice); using var otherHttp = new HttpClient(otherTransport) { BaseAddress = origin };
            var replica = await ReplicaAsync(http, origin, Path.Combine(directory, "a.db"), Device, "publicfake-bridge-refresh-device-a-20261011");
            var other = await ReplicaAsync(otherHttp, origin, Path.Combine(directory, "b.db"), OtherDevice, "publicfake-bridge-refresh-device-b-20261011");
            var actor = $"device_workspace_web_{Guid.NewGuid():N}"; long sequence = 0;
            var space = new SpaceItem { Id = $"space_{Guid.NewGuid():N}", Name = "Desktop space", Color = "#123456", Icon = "folder", SortOrder = 8 };
            var destination = new SpaceItem { Id = $"space_{Guid.NewGuid():N}", Name = "Destination" };
            await replica.Store.UpsertSpaceWithPersonalCloudIntentAsync(space, Instance, Device);
            await replica.Store.UpsertSpaceWithPersonalCloudIntentAsync(destination, Instance, Device);
            var project = new ProjectItem { Id = $"project_{Guid.NewGuid():N}", SpaceId = space.Id, Name = "Desktop project", Description = "Description", IsFavorite = true, Icon = "folder" };
            await replica.Store.UpsertProjectWithPersonalCloudIntentAsync(project, Instance, Device);
            var task = new TaskItem { Id = $"task_{Guid.NewGuid():N}", SpaceId = space.Id, ProjectId = project.Id, Title = "Desktop rich example", Priority = 3,
                Notes = "Rich notes", DeadlineAt = DateTimeOffset.Parse("2026-10-20T12:00:00Z"), ScheduledStart = DateTimeOffset.Parse("2026-10-19T12:00:00Z"), RecurrenceRule = "weekly" };
            await replica.Store.CreateRichTaskWithPersonalCloudIntentAsync(task, Instance, Device);
            var child = task with { Id = $"task_{Guid.NewGuid():N}", ParentId = task.Id, Title = "Child" };
            await replica.Store.CreateRichTaskWithPersonalCloudIntentAsync(child, Instance, Device);
            await SyncAllAsync(replica);
            var persistedSpace = (await replica.Store.GetSpacesAsync()).Single(s => s.Id == space.Id);
            var persistedProject = (await replica.Store.GetProjectsAsync()).Single(p => p.Id == project.Id);
            await replica.Store.UpsertSpaceWithPersonalCloudIntentAsync(persistedSpace with { Name = "Desktop renamed space" }, Instance, Device);
            await replica.Store.UpsertProjectWithPersonalCloudIntentAsync(persistedProject with { Description = "Desktop updated description" }, Instance, Device);
            await SyncAllAsync(replica); // Real server enforces immutable millisecond creation timestamps after SQLite read.
            Assert.Equal("weekly", (await ReadTaskAsync(web, task.Id)).GetProperty("recurrenceRule").GetString());
            await PostAsync(web, actor, ++sequence, "space.put", space.Id, 2, new { space = new { name = "Web renamed space", color = space.Color, icon = space.Icon, sortOrder = space.SortOrder, isArchived = false, createdAt = PersonalCloudK0Mapper.FormatTimestamp(space.CreatedAt) } });
            await PostAsync(web, actor, ++sequence, "task.patch", task.Id, 1, new { fields = new { deadlineAt = "2026-10-22T12:00:00.000Z", scheduledStart = "2026-10-21T12:00:00.000Z", recurrenceRule = "monthly" } });
            await replica.Sync.SyncAsync(); var pulled = (await replica.Store.GetTaskAsync(task.Id))!;
            Assert.Equal("monthly", pulled.RecurrenceRule); Assert.Equal(DateTimeOffset.Parse("2026-10-22T12:00:00Z"), pulled.DeadlineAt);
            Assert.Equal("Web renamed space", (await replica.Store.GetSpacesAsync()).Single(s => s.Id == space.Id).Name);
            await replica.Store.PatchTaskWithPersonalCloudIntentAsync(task.Id, new Dictionary<string, object?> { ["deadlineOn"] = "2026-10-23", ["scheduledEnd"] = "2026-10-21T13:00:00.000Z", ["durationMinutes"] = 60 }, pulled.Revision, Instance, Device);
            await SyncAllAsync(replica);
            var fromDesktop = await ReadTaskAsync(web, task.Id); Assert.Equal(JsonValueKind.Null, fromDesktop.GetProperty("deadlineAt").ValueKind); Assert.Equal("2026-10-23", fromDesktop.GetProperty("deadlineOn").GetString());
            await replica.Store.MoveTaskTreeToSpaceWithPersonalCloudIntentAsync(task.Id, destination.Id, destination.Name,
                new Dictionary<string, long> { [task.Id] = 3 }, Instance, Device);
            await SyncAllAsync(replica); await other.Sync.SyncAsync();
            Assert.Equal(destination.Id, (await other.Store.GetTaskAsync(child.Id))!.SpaceId); Assert.Equal(task.Id, (await other.Store.GetTaskAsync(child.Id))!.ParentId);

            var provider = new CatalogProvider();
            foreach (var source in provider.Tasks) await replica.Store.UpsertProviderSourceItemAsync(TaskSyncEngine.ToProviderSourceItem(source, provider.ProviderConnectionId, provider.Projects.ToDictionary(p => p.Id, p => p.Name)));
            await replica.Bridge.RunAsync([new(provider, "synthetic-workspace-account")]); await SyncAllAsync(replica);
            var catalog = (await ReadCatalogAsync(web)).GetProperty("catalog").EnumerateArray().Single(c => c.GetProperty("source").GetProperty("title").GetString() == "Synthetic catalog adoption").Clone();
            var catalogId = catalog.GetProperty("id").GetString()!;
            await other.Sync.SyncAsync();
            // A second bridge with the same synthetic account neither republishes nor adopts this owned source.
            foreach (var source in provider.Tasks) await other.Store.UpsertProviderSourceItemAsync(TaskSyncEngine.ToProviderSourceItem(source, provider.ProviderConnectionId, provider.Projects.ToDictionary(p => p.Id, p => p.Name)));
            await other.Bridge.RunAsync([new(provider, "synthetic-workspace-account")]);
            Assert.DoesNotContain(await other.Store.GetPendingPersonalCloudOutboxAsync(Instance, OtherDevice), e => e.Operation == "provider.catalog.put");
            var skipId = $"intent_{Guid.NewGuid():N}";
            await PostAsync(web, actor, ++sequence, "provider.intent.request", skipId, null, new { intent = new { bridgeDeviceId = Device, action = "skip", catalogId, spaceId = (string?)null, route = (object?)null } });
            await replica.Sync.SyncAsync(); await replica.Bridge.RunAsync([new(provider, "synthetic-workspace-account")]);
            var start = Assert.Single(await replica.Store.GetPendingPersonalCloudOutboxAsync(Instance, Device), e => e.Operation == "provider.intent.start");
            transport.LoseNextStartResponse = true; await Assert.ThrowsAsync<HttpRequestException>(() => replica.Sync.SyncAsync());
            var started = (await ReadCatalogAsync(web)).GetProperty("intents").EnumerateArray().Single(i => i.GetProperty("id").GetString() == skipId);
            Assert.Equal("started", started.GetProperty("state").GetString());
            Assert.Equal(start.CanonicalJson, Assert.Single(await replica.Store.GetPendingPersonalCloudOutboxAsync(Instance, Device), e => e.Operation == "provider.intent.start").CanonicalJson);
            await SyncAllAsync(replica); await replica.Bridge.RunAsync([new(provider, "synthetic-workspace-account")]); await SyncAllAsync(replica);
            Assert.True((await ReadCatalogAsync(web)).GetProperty("catalog").EnumerateArray().Single(c => c.GetProperty("id").GetString() == catalogId).GetProperty("isSkipped").GetBoolean());
            var restoreId = $"intent_{Guid.NewGuid():N}";
            await PostAsync(web, actor, ++sequence, "provider.intent.request", restoreId, null, new { intent = new { bridgeDeviceId = Device, action = "restore", catalogId, spaceId = (string?)null, route = (object?)null } });
            await DeliverAsync(replica, provider);
            Assert.False((await ReadCatalogAsync(web)).GetProperty("catalog").EnumerateArray().Single(c => c.GetProperty("id").GetString() == catalogId).GetProperty("isSkipped").GetBoolean());
            var adoptId = $"intent_{Guid.NewGuid():N}";
            await PostAsync(web, actor, ++sequence, "provider.intent.request", adoptId, null, new { intent = new { bridgeDeviceId = Device, action = "adopt", catalogId, spaceId = destination.Id, route = (object?)null } });
            await replica.Sync.SyncAsync(); await replica.Bridge.RunAsync([new(provider, "synthetic-workspace-account")]); await SyncAllAsync(replica);
            await replica.Bridge.RunAsync([new(provider, "synthetic-workspace-account")]);
            var action = (await replica.Store.GetPersonalCloudCatalogActionAsync(Instance, adoptId))!; Assert.NotNull(action.AdoptedTaskId);
            var restarted = new PersonalCloudProviderBridge(replica.Store, replica.Delivery, replica.Profile);
            await restarted.RunAsync([new(provider, "synthetic-workspace-account")]);
            Assert.Single(await replica.Store.GetPendingPersonalCloudOutboxAsync(Instance, Device), e => e.Operation == "task.createRich");
            await SyncAllAsync(replica); await restarted.RunAsync([new(provider, "synthetic-workspace-account")]); await SyncAllAsync(replica);
            var adopted = (await ReadCatalogAsync(web)).GetProperty("catalog").EnumerateArray().Single(c => c.GetProperty("id").GetString() == catalogId);
            Assert.Equal(action.AdoptedTaskId, adopted.GetProperty("adoptedTaskId").GetString());
            Assert.Equal(Device, (await replica.Store.GetPersonalCloudWorkspaceRecordsAsync(Instance, "tasks")).Single(t => t.GetProperty("id").GetString() == action.AdoptedTaskId).GetProperty("sourceOwnerDeviceId").GetString());
            Assert.Equal(0, provider.Actions); Assert.True(transport.BatchRequests > 0);
            Assert.DoesNotContain("private-provider-canary", adopted.GetProperty("projectChoices").GetRawText()); Assert.DoesNotContain("private-secret-canary", adopted.GetRawText());
        }
    }

    [LocalProviderBridgeAcceptance]
    [Trait("Category", "LocalWorkerAcceptance")]
    public async Task Retained_actual_workspace_records_prove_source_owner_and_safe_projection()
    {
        var origin = new Uri(Environment.GetEnvironmentVariable("OPENZA_BRIDGE_ACCEPTANCE_ORIGIN")!); Assert.True(origin.IsLoopback && origin.Scheme == "http");
        var directory = Environment.GetEnvironmentVariable("OPENZA_WORKSPACE_ACCEPTANCE_DATA_DIR")!;
        var store = new SqliteTaskStore(Path.Combine(directory, "a.db")); Assert.True(File.Exists(store.DatabasePath));
        using var web = new HttpClient { BaseAddress = origin }; web.DefaultRequestHeaders.Add("x-openza-test-session", "synthetic-local-bridge-acceptance");
        var catalog = (await ReadCatalogAsync(web)).GetProperty("catalog").EnumerateArray().Single(c => c.GetProperty("source").GetProperty("title").GetString() == "Synthetic catalog adoption");
        var id = catalog.GetProperty("adoptedTaskId").GetString(); Assert.NotNull(id);
        var record = (await store.GetPersonalCloudWorkspaceRecordsAsync(Instance, "tasks")).Single(t => t.GetProperty("id").GetString() == id);
        Assert.Equal(Device, record.GetProperty("sourceOwnerDeviceId").GetString());
        Assert.Equal(catalog.GetProperty("source").GetProperty("externalIdHash").GetString(), record.GetProperty("source").GetProperty("externalIdHash").GetString());
        Assert.DoesNotContain("private-provider-canary", catalog.GetProperty("projectChoices").GetRawText()); Assert.DoesNotContain("private-secret-canary", catalog.GetRawText());
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [LocalProviderBridgeAcceptance]
    [Trait("Category", "LocalWorkerAcceptance")]
    public async Task Retained_second_device_routes_zero_task_Todoist_connection_with_scoped_project_choice()
    {
        var origin = new Uri(Environment.GetEnvironmentVariable("OPENZA_BRIDGE_ACCEPTANCE_ORIGIN")!); Assert.True(origin.IsLoopback && origin.Scheme == "http");
        var directory = Environment.GetEnvironmentVariable("OPENZA_WORKSPACE_ACCEPTANCE_DATA_DIR")!; Assert.True(File.Exists(Path.Combine(directory, "b.db")));
        using var web = new HttpClient { BaseAddress = origin }; web.DefaultRequestHeaders.Add("x-openza-test-session", "synthetic-local-bridge-acceptance");
        using var transport = new ActualTransport(OtherDevice); using var http = new HttpClient(transport) { BaseAddress = origin };
        var replica = await ReplicaAsync(http, origin, Path.Combine(directory, "b.db"), OtherDevice, "publicfake-bridge-refresh-device-b-20261011");
        var provider = new CatalogProvider(empty: true);
        await replica.Bridge.RunAsync([new(provider, "synthetic-empty-account")]); await SyncAllAsync(replica);
        var scope = (await ReadCatalogAsync(web)).GetProperty("scopes").EnumerateArray().Single(s => s.GetProperty("bridgeDeviceId").GetString() == OtherDevice && s.GetProperty("integrationKind").GetString() == IntegrationIds.Todoist);
        var choice = Assert.Single(scope.GetProperty("projectChoices").EnumerateArray());
        var actor = $"device_zero_task_web_{Guid.NewGuid():N}"; var intentId = $"intent_{Guid.NewGuid():N}";
        await PostAsync(web, actor, 1, "provider.intent.request", intentId, null, new { intent = new { bridgeDeviceId = OtherDevice, action = "route.put", catalogId = (string?)null, spaceId = (string?)null,
            route = new { id = $"route_{Guid.NewGuid():N}", scopeId = scope.GetProperty("id").GetString(), label = "Typed label", spaceId = SpaceIds.Default, moveToProjectId = choice.GetProperty("id").GetString() } } });
        for (var pass = 0; pass < 3; pass++)
        {
            await replica.Sync.SyncAsync(); await replica.Bridge.RunAsync([new(provider, "synthetic-empty-account")]); await SyncAllAsync(replica);
        }
        Assert.Equal("confirmed", (await ReadCatalogAsync(web)).GetProperty("intents").EnumerateArray().Single(i => i.GetProperty("id").GetString() == intentId).GetProperty("state").GetString());
        var rule = Assert.Single(TodoistRoutingRuleCodec.Read((await replica.Store.GetSyncRoutesAsync()).Single(r => r.Id == TodoistRoutingRuleCodec.RouteId).SettingsJson).LabelRoutes);
        Assert.Equal("Typed label", rule.Label); Assert.Equal("private-provider-canary", rule.PostImport!.MoveToProjectId); Assert.Equal(0, provider.Actions);
    }

    [BrowserWorkspaceAcceptance]
    [Trait("Category", "LocalWorkerAcceptance")]
    public async Task Preserved_Desktop_consumes_new_browser_intents_without_resetting_device_sequence()
    {
        var origin = new Uri(Environment.GetEnvironmentVariable("OPENZA_BRIDGE_ACCEPTANCE_ORIGIN")!);
        Assert.True(origin.IsLoopback && origin.Scheme == "http");
        var directory = Environment.GetEnvironmentVariable("OPENZA_WORKSPACE_ACCEPTANCE_DATA_DIR")!;
        Assert.True(File.Exists(Path.Combine(directory, "a.db")));
        using var web = new HttpClient { BaseAddress = origin }; web.DefaultRequestHeaders.Add("x-openza-test-session", "synthetic-local-bridge-acceptance");
        var before = (await ReadCatalogAsync(web)).GetProperty("intents").EnumerateArray().Where(i => i.GetProperty("bridgeDeviceId").GetString() == Device && i.GetProperty("state").GetString() == "pending_desktop").Select(i => i.Clone()).ToArray();
        Assert.NotEmpty(before);
        using var transport = new ActualTransport(Device); using var http = new HttpClient(transport) { BaseAddress = origin };
        var replica = await ReplicaAsync(http, origin, Path.Combine(directory, "a.db"), Device, "publicfake-bridge-refresh-device-a-20261011");
        var provider = new CatalogProvider();
        for (var pass = 0; pass < 5; pass++) await DeliverAsync(replica, provider);
        var after = await ReadCatalogAsync(web);
        foreach (var expected in before)
            Assert.Equal("confirmed", after.GetProperty("intents").EnumerateArray().Single(i => i.GetProperty("id").GetString() == expected.GetProperty("id").GetString()).GetProperty("state").GetString());
        Assert.Equal(0, provider.Actions);
        var settings = TodoistRoutingRuleCodec.Read((await replica.Store.GetSyncRoutesAsync()).Single(r => r.Id == TodoistRoutingRuleCodec.RouteId).SettingsJson);
        var put = before.LastOrDefault(i => i.GetProperty("action").GetString() == "route.put");
        if (put.ValueKind != JsonValueKind.Undefined)
        {
            var route = put.GetProperty("route"); var label = route.GetProperty("label").GetString();
            Assert.True(label is null ? settings.UnlabeledRoute is not null : settings.LabelRoutes.Any(r => r.Label == label));
        }
    }

    [LocalProviderBridgeAcceptance]
    [Trait("Category", "LocalWorkerAcceptance")]
    public async Task Retained_actual_bridge_reconciles_committed_filing_loss_and_preserves_completion_reopen()
    {
        var origin = new Uri(Environment.GetEnvironmentVariable("OPENZA_BRIDGE_ACCEPTANCE_ORIGIN")!); Assert.True(origin.IsLoopback && origin.Scheme == "http");
        var directory = Environment.GetEnvironmentVariable("OPENZA_WORKSPACE_ACCEPTANCE_DATA_DIR")!; Assert.True(File.Exists(Path.Combine(directory, "a.db")));
        using var web = new HttpClient { BaseAddress = origin }; web.DefaultRequestHeaders.Add("x-openza-test-session", "synthetic-local-bridge-acceptance");
        using var transport = new ActualTransport(Device); using var http = new HttpClient(transport) { BaseAddress = origin };
        var replica = await ReplicaAsync(http, origin, Path.Combine(directory, "a.db"), Device, "publicfake-bridge-refresh-device-a-20261011");
        var provider = new FilingProvider();
        await replica.Bridge.RunAsync([new(provider, "synthetic-workspace-account")]); await SyncAllAsync(replica);
        await replica.Store.UpsertSyncRouteAsync(new() { Id = TodoistRoutingRuleCodec.RouteId, Name = "Synthetic filing rule", SourceConnectionId = "todoist_default", IsEnabled = true,
            SettingsJson = TodoistRoutingRuleCodec.Write(new([new("todoist_rule_filing_test", "Synthetic label", SpaceIds.Default, new(FilingProvider.DestinationRaw))], null)) });
        var catalog = (await ReadCatalogAsync(web)).GetProperty("catalog").EnumerateArray().Single(c => c.GetProperty("source").GetProperty("title").GetString() == "Synthetic browser source 2");
        Assert.Equal(JsonValueKind.Null, catalog.GetProperty("adoptedTaskId").ValueKind);
        var actor = $"device_filing_web_{Guid.NewGuid():N}"; long sequence = 0;
        var existing = (await ReadCatalogAsync(web)).GetProperty("intents").EnumerateArray().Where(i => i.GetProperty("bridgeDeviceId").GetString() == Device && i.GetProperty("action").GetString() == "adopt" && i.GetProperty("catalogId").GetString() == catalog.GetProperty("id").GetString() && i.GetProperty("state").GetString() is "pending_desktop" or "started" or "uncertain").ToArray();
        var id = existing.Length == 0 ? $"intent_{Guid.NewGuid():N}" : Assert.Single(existing).GetProperty("id").GetString()!;
        if (existing.Length == 0)
            await PostAsync(web, actor, ++sequence, "provider.intent.request", id, null, new { intent = new { bridgeDeviceId = Device, action = "adopt", catalogId = catalog.GetProperty("id").GetString(), spaceId = SpaceIds.Default, route = (object?)null } });
        for (var pass = 0; pass < 4 && provider.Moves == 0; pass++)
        {
            await replica.Sync.SyncAsync(); await replica.Bridge.RunAsync([new(provider, "synthetic-workspace-account")]); await SyncAllAsync(replica);
        }
        Assert.Equal(1, provider.Moves);
        Assert.Equal("uncertain", (await ReadCatalogAsync(web)).GetProperty("intents").EnumerateArray().Single(i => i.GetProperty("id").GetString() == id).GetProperty("state").GetString());
        var action = (await replica.Store.GetPersonalCloudCatalogActionAsync(Instance, id))!; Assert.NotNull(action.AdoptedTaskId);
        var resumed = await ReplicaAsync(http, origin, Path.Combine(directory, "a.db"), Device, "publicfake-bridge-refresh-device-a-20261011");
        await resumed.Bridge.RunAsync([new(provider, "synthetic-workspace-account")]); await SyncAllAsync(resumed);
        Assert.Equal(1, provider.Moves); // Fresh provider state proves the effect; restart must not repeat it.
        Assert.Equal("confirmed", (await ReadCatalogAsync(web)).GetProperty("intents").EnumerateArray().Single(i => i.GetProperty("id").GetString() == id).GetProperty("state").GetString());
        Assert.Equal(action.AdoptedTaskId, (await resumed.Store.GetPersonalCloudCatalogActionAsync(Instance, id))!.AdoptedTaskId);
        var legacy = (await ReadCatalogAsync(web)).GetProperty("catalog").EnumerateArray().Single(c => c.GetProperty("source").GetProperty("title").GetString() == "Synthetic catalog adoption").GetProperty("adoptedTaskId").GetString()!;
        var task = await ReadTaskAsync(web, legacy);
        await PostAsync(web, actor, ++sequence, "task.complete", legacy, task.GetProperty("entityVersion").GetInt64(), new { completedAt = PersonalCloudK0Mapper.FormatTimestamp(DateTimeOffset.UtcNow) });
        await resumed.Sync.SyncAsync(); await resumed.Bridge.RunAsync([new(provider, "synthetic-workspace-account")]); await SyncAllAsync(resumed);
        Assert.Equal(1, provider.Completions); Assert.Equal("completed", (await ReadTaskAsync(web, legacy)).GetProperty("completionState").GetString());
        task = await ReadTaskAsync(web, legacy);
        await PostAsync(web, actor, ++sequence, "task.reopen", legacy, task.GetProperty("entityVersion").GetInt64(), new { reopenedAt = PersonalCloudK0Mapper.FormatTimestamp(DateTimeOffset.UtcNow) });
        await resumed.Sync.SyncAsync(); await resumed.Bridge.RunAsync([new(provider, "synthetic-workspace-account")]); await SyncAllAsync(resumed);
        Assert.Equal(2, provider.Completions); Assert.Equal("open", (await ReadTaskAsync(web, legacy)).GetProperty("completionState").GetString()); Assert.Equal(1, provider.Moves);
    }

    private sealed class BrowserWorkspaceAcceptanceAttribute : FactAttribute
    {
        public BrowserWorkspaceAcceptanceAttribute()
        {
            if (Environment.GetEnvironmentVariable("OPENZA_WORKSPACE_CONSUME_BROWSER_INTENTS") != "1") Skip = "Opt-in continuation against retained synthetic SQLite and new browser intents.";
        }
    }

    private sealed record Replica(SqliteTaskStore Store, PersonalCloudSyncClient Sync, PersonalCloudProviderBridge Bridge, PersonalCloudProviderDeliveryClient Delivery, PersonalCloudConnectionProfile Profile);
    private static async Task<Replica> ReplicaAsync(HttpClient http, Uri origin, string path, string device, string refresh)
    {
        var credentials = new InMemoryCredentialStore(); var key = $"personal-cloud-device-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin.AbsoluteUri))).ToLowerInvariant()}";
        await credentials.SaveAsync(key, PersonalCloudK0Codec.Serialize(new { serviceOrigin = origin.AbsoluteUri, instanceId = Instance, deviceId = device, refreshCredential = refresh }));
        var identity = new PersonalCloudDeviceIdentityClient(http, credentials); var store = new SqliteTaskStore(path); await store.InitializeAsync();
        var profile = new PersonalCloudConnectionProfile { ServiceOrigin = origin, InstanceId = Instance, DeviceId = device, ArtifactVersion = "0.1.0-preview.9", ConnectedAt = DateTimeOffset.UtcNow };
        var sync = new PersonalCloudSyncClient(http, identity, store, profile); await sync.SyncAsync();
        var delivery = new PersonalCloudProviderDeliveryClient(http, identity, profile);
        return new(store, sync, new(store, delivery, profile), delivery, profile);
    }
    private static async Task SyncAllAsync(Replica replica)
    {
        for (var pass = 0; pass < 50; pass++)
        {
            Assert.Null((await replica.Sync.SyncAsync()).BlockedCode);
            if ((await replica.Store.GetPendingPersonalCloudOutboxAsync(Instance, replica.Profile.DeviceId)).Count == 0) return;
        }
        Assert.Fail("Synthetic pending outbox did not finish.");
    }
    private static async Task DeliverAsync(Replica replica, CatalogProvider provider)
    {
        await replica.Sync.SyncAsync(); await replica.Bridge.RunAsync([new(provider, "synthetic-workspace-account")]); await SyncAllAsync(replica);
        await replica.Bridge.RunAsync([new(provider, "synthetic-workspace-account")]); await SyncAllAsync(replica);
    }
    private static async Task PostAsync(HttpClient http, string actor, long sequence, string operation, string id, long? version, object payload)
    {
        var json = PersonalCloudK0Codec.Serialize(new { protocolVersion = 1, product = "tasks", instanceId = Instance, deviceId = actor, mutationId = $"mutation_{Guid.NewGuid():N}",
            deviceSequence = sequence, operation, entityId = id, baseEntityVersion = version, payload, clientCreatedAt = PersonalCloudK0Mapper.FormatTimestamp(DateTimeOffset.UtcNow) });
        using var response = await http.PostAsync("/v1/tasks/mutations", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.True(response.IsSuccessStatusCode, $"Synthetic {operation} returned HTTP {(int)response.StatusCode}.");
    }
    private static async Task<JsonElement> ReadCatalogAsync(HttpClient http)
    {
        using var response = await http.GetAsync("/v1/provider/catalog"); response.EnsureSuccessStatusCode(); using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return document.RootElement.Clone();
    }
    private static async Task<JsonElement> ReadTaskAsync(HttpClient http, string id)
    {
        using var response = await http.GetAsync("/v1/tasks"); response.EnsureSuccessStatusCode(); using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("tasks").EnumerateArray().Single(t => t.GetProperty("id").GetString() == id).Clone();
    }
    private sealed class ActualTransport(string device) : DelegatingHandler(new HttpClientHandler())
    {
        public bool LoseNextStartResponse { get; set; }
        public int BatchRequests { get; private set; }
        public int PagedBootstrapRequests { get; private set; }
        public int PagedChangeRequests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("x-openza-test-device", device);
            if (request.RequestUri!.AbsolutePath == "/v1/tasks/mutations/batch") BatchRequests++;
            if (request.RequestUri.AbsolutePath == "/sync/bootstrap" && request.RequestUri.Query.Contains("paged=1", StringComparison.Ordinal)) PagedBootstrapRequests++;
            if (request.RequestUri.AbsolutePath == "/sync/changes" && request.RequestUri.Query.Contains("paged=1", StringComparison.Ordinal)) PagedChangeRequests++;
            var lose = LoseNextStartResponse && request.Content is not null && (await request.Content.ReadAsStringAsync(cancellationToken)).Contains("provider.intent.start", StringComparison.Ordinal);
            var response = await base.SendAsync(request, cancellationToken);
            if (lose && response.IsSuccessStatusCode) { LoseNextStartResponse = false; response.Dispose(); throw new HttpRequestException("Synthetic response lost after Worker acceptance."); }
            return response;
        }
    }
    private sealed class CatalogProvider(bool empty = false) : ISyncProvider
    {
        public string IntegrationId => IntegrationIds.Todoist;
        public string ProviderConnectionId => "todoist_default";
        public int Actions { get; private set; }
        public IReadOnlyList<ProjectItem> Projects { get; } = [new() { Id = "todoist_private-provider-canary", ExternalId = "private-provider-canary", IntegrationId = IntegrationIds.Todoist, Name = "Synthetic filing project" }];
        public IReadOnlyList<TaskItem> Tasks { get; } = Enumerable.Range(0, empty ? 0 : 3).Select(i => new TaskItem { Id = $"todoist_synthetic_{i}", ExternalId = $"private-provider-canary-{i}", IntegrationId = IntegrationIds.Todoist,
            ProviderConnectionId = "todoist_default", Title = i == 0 ? "Synthetic catalog adoption" : $"Synthetic browser source {i}", Priority = 3,
            ProjectId = "todoist_private-provider-canary", ProviderMetadataJson = "{\"sourceTask\":{\"labels\":[\"Synthetic label\"]},\"credential\":\"private-secret-canary\"}" }).ToArray();
        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProviderSnapshot(Tasks, Projects, []));
        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default) { Actions++; return Task.CompletedTask; }
    }

    private sealed class FilingProvider : ITaskProjectMoveProvider
    {
        public const string DestinationRaw = "private-destination-canary";
        public string IntegrationId => IntegrationIds.Todoist;
        public string ProviderConnectionId => "todoist_default";
        public int Moves { get; private set; }
        public int Completions { get; private set; }
        private readonly List<TaskItem> _tasks = new CatalogProvider().Tasks.ToList();
        private readonly IReadOnlyList<ProjectItem> _projects = new CatalogProvider().Projects.Concat([new ProjectItem { Id = "todoist_destination", ExternalId = DestinationRaw, IntegrationId = IntegrationIds.Todoist, Name = "Synthetic second filing project" }]).ToArray();
        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProviderSnapshot(_tasks.Where(t => !t.IsCompleted).ToArray(), _projects, []) { CompletedTasks = _tasks.Where(t => t.IsCompleted).ToArray() });
        public Task MoveTaskAsync(string taskId, string projectId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(DestinationRaw, projectId); var index = _tasks.FindIndex(t => t.Id == taskId || t.ExternalId == taskId); Assert.True(index >= 0);
            Moves++; _tasks[index] = _tasks[index] with { ProjectId = "todoist_destination" };
            throw new HttpRequestException("Synthetic response lost after provider committed filing.");
        }
        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default)
        {
            var index = _tasks.FindIndex(t => t.Id == completion.ProviderTaskId || t.ExternalId == completion.ProviderTaskId); Assert.True(index >= 0);
            Completions++; _tasks[index] = _tasks[index] with { CompletionState = completion.Completed ? TaskCompletionState.Completed : TaskCompletionState.Open }; return Task.CompletedTask;
        }
    }
}
