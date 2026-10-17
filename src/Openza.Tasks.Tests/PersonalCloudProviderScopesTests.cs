using System.Text.Json;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudProviderScopesTests : IDisposable
{
    private const string Instance = "instance_scopes_test", Device = "device_scopes_test", Stamp = "2026-10-13T00:00:00.000Z";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-scope-tests", Guid.NewGuid().ToString("N"));
    private async Task<SqliteTaskStore> StoreAsync(int protocol = 2)
    {
        Directory.CreateDirectory(_directory); var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db")); await store.InitializeAsync();
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true, providerCatalogProtocolVersion: protocol); return store;
    }
    private static object Scope(string id, string owner = Device, string kind = IntegrationIds.Todoist, bool available = true, long version = 1) => new {
        id, bridgeDeviceId = owner, name = "Todoist", integrationKind = kind, available, labelNames = new[] { "Example" }, projectChoices = Array.Empty<object>(), entityVersion = version, createdAt = Stamp, updatedAt = Stamp };
    private static JsonElement Intent(string scopeId, string action = "route.put", string state = "pending_desktop", long version = 1, bool legacy = false) =>
        JsonSerializer.SerializeToElement(new { id = "intent_scope_test", bridgeDeviceId = Device, action, catalogId = (string?)null, spaceId = (string?)null,
            route = legacy ? (object)new { id = "route_scope_test", label = (string?)null, spaceId = SpaceIds.Default, moveToProjectId = (string?)null }
                : new { id = "route_scope_test", scopeId, label = (string?)null, spaceId = SpaceIds.Default, moveToProjectId = (string?)null },
            state, adoptedTaskId = (string?)null, errorCode = (string?)null, entityVersion = version, createdAt = Stamp, updatedAt = Stamp });
    private static async Task AckAsync(SqliteTaskStore store)
    {
        foreach (var e in await store.GetPendingPersonalCloudOutboxAsync(Instance, Device))
        {
            using var envelope = JsonDocument.Parse(e.CanonicalJson);
            var basis = envelope.RootElement.GetProperty("baseEntityVersion");
            await store.AcknowledgePersonalCloudMutationAsync(Instance, Device, PersonalCloudK0Codec.Serialize(new K0MutationReceipt {
                ProtocolVersion = 1, Product = "tasks", MutationId = e.MutationId, Outcome = "accepted", EntityId = e.EntityId,
                EntityVersion = basis.ValueKind == JsonValueKind.Null ? 1 : basis.GetInt64() + 1, ChangeSeq = e.DeviceSequence, ServerAcceptedAt = Stamp }));
        }
    }
    [Fact]
    public async Task Empty_connection_publishes_safe_scope_once_and_legacy_cloud_publishes_no_new_keys()
    {
        var store = await StoreAsync(1);
        Assert.Null(await store.EnqueuePersonalCloudProviderScopeAsync(Instance, Device, IntegrationIds.Todoist, "private-connection-canary", true, [], []));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true, providerCatalogProtocolVersion: 2);
        var id = await store.EnqueuePersonalCloudProviderScopeAsync(Instance, Device, IntegrationIds.Todoist, "private-connection-canary", true, ["Example"], []);
        var entry = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)); Assert.Equal("provider.scope.put", entry.Operation);
        Assert.DoesNotContain("private-connection-canary", entry.CanonicalJson);
        await store.EnqueuePersonalCloudProviderScopeAsync(Instance, Device, IntegrationIds.Todoist, "private-connection-canary", true, ["Example"], []);
        Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        await AckAsync(store); await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(1, scopes: [Scope(id!)]));
        await store.EnqueuePersonalCloudProviderScopeAsync(Instance, Device, IntegrationIds.Todoist, "private-connection-canary", true, ["Example"], []);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }
    [Theory]
    [InlineData("unknown", Device, IntegrationIds.Todoist, false)]
    [InlineData("foreign", "device_other", IntegrationIds.Todoist, false)]
    [InlineData("microsoft", Device, IntegrationIds.MicrosoftToDo, false)]
    [InlineData("legacy", Device, IntegrationIds.Todoist, true)]
    public async Task Unsupported_routing_is_started_and_rejected_without_touching_local_rules(string id, string owner, string integration, bool legacy)
    {
        var store = await StoreAsync(); var scopeId = "scope_" + id;
        if (id != "unknown") await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(1, scopes: [Scope(scopeId, owner, integration)]));
        Assert.False(await store.ProcessPersonalCloudRouteIntentAsync(Instance, Device, Intent(scopeId, legacy: legacy), []));
        Assert.Equal("provider.intent.start", Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Operation);
        await AckAsync(store);
        Assert.False(await store.ProcessPersonalCloudRouteIntentAsync(Instance, Device, Intent(scopeId, state: "started", version: 2, legacy: legacy), []));
        var resolve = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal("provider.intent.resolve", resolve.Operation); Assert.Contains("unsupported", resolve.CanonicalJson);
        Assert.Empty(await store.GetSyncRoutesAsync());
    }
    [Fact]
    public async Task Scoped_rule_is_visible_in_existing_editor_and_delete_cleans_unavailable_connection()
    {
        var store = await StoreAsync(); var provider = new EmptyTodoist();
        var scopeId = (await store.EnqueuePersonalCloudProviderScopeAsync(Instance, Device, IntegrationIds.Todoist, "todoist_default", true, [], []))!;
        await AckAsync(store); await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(1, scopes: [Scope(scopeId)]));
        await store.ProcessPersonalCloudRouteIntentAsync(Instance, Device, Intent(scopeId), [new(provider, "local-account")]); await AckAsync(store);
        Assert.True(await store.ProcessPersonalCloudRouteIntentAsync(Instance, Device, Intent(scopeId, state: "started", version: 2), [new(provider, "local-account")]));
        var route = Assert.Single(await store.GetSyncRoutesAsync()); Assert.Equal(TodoistRoutingRuleCodec.RouteId, route.Id); Assert.Equal("todoist_default", route.SourceConnectionId);
        Assert.Equal(SpaceIds.Default, TodoistRoutingRuleCodec.Read(route.SettingsJson).UnlabeledRoute!.SpaceId);
        // Preserve the rule but use another request identity after its confirmed result.
        await AckAsync(store);
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(2, scopes: [Scope(scopeId, available: false, version: 2)]));
        var delete = JsonSerializer.SerializeToElement(Intent(scopeId, action: "route.delete")).GetRawText().Replace("intent_scope_test", "intent_delete_test", StringComparison.Ordinal);
        using var pending = JsonDocument.Parse(delete); await store.ProcessPersonalCloudRouteIntentAsync(Instance, Device, pending.RootElement, []); await AckAsync(store);
        using var started = JsonDocument.Parse(delete.Replace("pending_desktop", "started", StringComparison.Ordinal).Replace("\"entityVersion\":1", "\"entityVersion\":2", StringComparison.Ordinal));
        Assert.True(await store.ProcessPersonalCloudRouteIntentAsync(Instance, Device, started.RootElement, []));
        Assert.Null(TodoistRoutingRuleCodec.Read(Assert.Single(await store.GetSyncRoutesAsync()).SettingsJson).UnlabeledRoute);
    }
    private sealed class EmptyTodoist : ISyncProvider
    {
        public string IntegrationId => IntegrationIds.Todoist;
        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProviderSnapshot([], [], []));
        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Routing must not complete a provider task.");
    }
    [Fact]
    public async Task Desktop_rule_export_handles_new_nullable_mapping_and_preserves_one_pending_request_after_restart()
    {
        var store = await StoreAsync();
        var scopeId = (await store.EnqueuePersonalCloudProviderScopeAsync(Instance, Device, IntegrationIds.Todoist, "todoist_default", true, [], []))!;
        await AckAsync(store); await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(1, scopes: [Scope(scopeId)]));
        await store.UpsertSyncRouteAsync(new() { Id = TodoistRoutingRuleCodec.RouteId, Name = "Example", SourceConnectionId = "todoist_default", IsEnabled = true,
            SettingsJson = TodoistRoutingRuleCodec.Write(new([new("rule_desktop", "Typed label", SpaceIds.Default, null)], null)) });
        await store.EnqueuePersonalCloudTodoistRoutesAsync(Instance, Device, scopeId);
        var first = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)); Assert.Equal("provider.intent.request", first.Operation);
        var resumed = new SqliteTaskStore(store.DatabasePath); await resumed.InitializeAsync();
        await resumed.EnqueuePersonalCloudTodoistRoutesAsync(Instance, Device, scopeId);
        Assert.Equal(first, Assert.Single(await resumed.GetPendingPersonalCloudOutboxAsync(Instance, Device)));
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
