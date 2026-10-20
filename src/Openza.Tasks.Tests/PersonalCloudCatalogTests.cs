using System.Text.Json;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudCatalogTests : IDisposable
{
    private const string Instance = "instance_catalog_test";
    private const string Device = "device_catalog_test";
    private const string Stamp = "2026-10-13T00:00:00.000Z";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-catalog-tests", Guid.NewGuid().ToString("N"));
    private async Task<SqliteTaskStore> StoreAsync()
    {
        Directory.CreateDirectory(_directory); var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db")); await store.InitializeAsync();
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true); return store;
    }
    private static ProviderSourceItem Local() => new() { Id = "source_local_test", IntegrationId = IntegrationIds.Todoist, ProviderConnectionId = "todoist_default",
        ExternalId = "private-identifier-canary", ProviderTaskId = "private-provider-canary", Title = "Catalog example", SnapshotJson = "{\"credential\":\"private-secret-canary\"}" };
    private static K0SafeSourceDescriptor Source(ProviderSourceItem local) => new() { IntegrationKind = local.IntegrationId,
        ExternalIdHash = SqliteTaskStore.PersonalCloudOpaqueId("", $"{local.IntegrationId}:{local.ExternalId}"), Title = local.Title, RefreshedAt = Stamp, RecurrenceRule = "daily" };
    private static object Catalog(K0SafeSourceDescriptor source, long version = 1, string? scopeId = null) => new { id = "catalog_test", bridgeDeviceId = Device, scopeId, source, isSkipped = false, available = true,
        adoptedTaskId = (string?)null, labelNames = Array.Empty<string>(), projectChoices = Array.Empty<object>(), entityVersion = version, createdAt = Stamp, updatedAt = Stamp };
    private static JsonElement Intent(string state = "pending_desktop", long version = 1, string device = Device) => JsonSerializer.SerializeToElement(new
    {
        id = "intent_test", bridgeDeviceId = device, action = "adopt", catalogId = "catalog_test", spaceId = SpaceIds.Default, route = (object?)null,
        state, adoptedTaskId = (string?)null, errorCode = (string?)null, entityVersion = version, createdAt = Stamp, updatedAt = Stamp,
    });
    private static string Receipt(PersonalCloudK1OutboxEntry entry, long version) => PersonalCloudK0Codec.Serialize(new K0MutationReceipt
    { ProtocolVersion = 1, Product = "tasks", MutationId = entry.MutationId, Outcome = "accepted", EntityId = entry.EntityId, EntityVersion = version, ChangeSeq = 1, ServerAcceptedAt = Stamp });

    [Fact]
    public async Task Projection_excludes_raw_provider_ids_snapshot_and_local_account_binding()
    {
        var store = await StoreAsync(); var local = Local();
        await store.UpsertProviderSourceItemAsync(local);
        await store.EnqueuePersonalCloudCatalogItemAsync(Instance, Device, local, Source(local), true, ["Public label"], []);
        var entry = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal("provider.catalog.put", entry.Operation);
        DispatchCostAssertions.Known(entry);
        Assert.DoesNotContain("private-identifier-canary", entry.CanonicalJson); Assert.DoesNotContain("private-provider-canary", entry.CanonicalJson);
        Assert.DoesNotContain("private-secret-canary", entry.CanonicalJson); Assert.DoesNotContain("source_local_test", entry.CanonicalJson);
        Assert.Contains("Public label", entry.CanonicalJson);
        Assert.Equal(PersonalCloudK0Codec.ComputeCanonicalHash(entry.CanonicalJson), entry.CanonicalHash);
    }

    [Fact]
    public async Task Prepared_start_and_adoption_are_durable_across_repeated_actions_and_reopen()
    {
        var store = await StoreAsync(); var local = Local(); await store.UpsertProviderSourceItemAsync(local);
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(1, catalog: [Catalog(Source(local))]));
        var intent = Intent();
        await store.PreparePersonalCloudCatalogIntentAsync(Instance, Device, intent, "local-account-canary", "todoist_default", null);
        await store.PreparePersonalCloudCatalogIntentAsync(Instance, Device, intent, "local-account-canary", "todoist_default", null);
        var start = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.DoesNotContain("local-account-canary", start.CanonicalJson); Assert.Equal("provider.intent.start", start.Operation);
        DispatchCostAssertions.Known(start);
        await store.AcknowledgePersonalCloudMutationAsync(Instance, Device, Receipt(start, 2));
        await store.ApplyPersonalCloudSourceIntentAsync(Instance, Device, Intent("started", 2), local.Id);
        var action = (await store.GetPersonalCloudCatalogActionAsync(Instance, "intent_test"))!;
        var resumed = new SqliteTaskStore(store.DatabasePath); await resumed.InitializeAsync();
        await resumed.ApplyPersonalCloudSourceIntentAsync(Instance, Device, Intent("started", 2), local.Id);
        Assert.Equal(action.AdoptedTaskId, (await resumed.GetPersonalCloudCatalogActionAsync(Instance, "intent_test"))!.AdoptedTaskId);
        var task = (await resumed.GetTaskAsync(action.AdoptedTaskId!))!;
        Assert.Equal("daily", task.SourceRecurrenceRule); Assert.Null(task.RecurrenceRule);
        var create = Assert.Single(await resumed.GetPendingPersonalCloudOutboxAsync(Instance, Device)); Assert.Equal("task.createRich", create.Operation);
        Assert.Contains("externalIdHash", create.CanonicalJson); Assert.DoesNotContain("private-identifier-canary", create.CanonicalJson);
        Assert.Equal(action.AdoptedTaskId, create.EntityId);
        var blocked = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => resumed.EnsureNoPendingPersonalCloudIntentsAsync()); Assert.Equal("pending_intents", blocked.Code);
    }

    [Fact]
    public async Task Another_bridge_cannot_prepare_this_owned_intent()
    {
        var store = await StoreAsync();
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.PreparePersonalCloudCatalogIntentAsync(Instance, "device_other", Intent(), "account", null, null));
        Assert.Equal("invalid_request", error.Code); Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, "device_other"));
    }

    [Fact]
    public async Task Unverified_legacy_adoption_is_omitted_instead_of_offered_as_duplicate_adoption()
    {
        var store = await StoreAsync(); var local = Local() with { AdoptedTaskId = "task_legacy", AdoptionState = ProviderSourceAdoptionStates.Adopted };
        await store.UpsertTaskAsync(new() { Id = "task_legacy", Title = local.Title, SourceIntegrationId = local.IntegrationId, SourceExternalId = local.ExternalId });
        Assert.False(await store.EnqueuePersonalCloudCatalogItemAsync(Instance, Device, local, Source(local), true, [], []));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.NotNull(await store.GetTaskAsync("task_legacy"));
    }
    [Theory]
    [InlineData(7)]
    [InlineData(25)]
    [InlineData(48)]
    public async Task Unchanged_catalog_and_scope_do_not_publish_periodic_full_records(int hours)
    {
        var store = await StoreAsync(); var local = Local(); var source = Source(local); var baseline = DateTimeOffset.Parse(Stamp);
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true, providerCatalogProtocolVersion: 2);
        var scopeId = SqliteTaskStore.PersonalCloudOpaqueId("scope_", $"{Device}:{local.IntegrationId}:{local.ProviderConnectionId}");
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(1, catalog: [Catalog(source, scopeId: scopeId)], scopes: [new {
            id = scopeId, bridgeDeviceId = Device, name = "Todoist", integrationKind = IntegrationIds.Todoist, available = true, labelNames = Array.Empty<string>(), projectChoices = Array.Empty<object>(), entityVersion = 1, createdAt = Stamp, updatedAt = Stamp }]));
        var observed = source with { RefreshedAt = PersonalCloudK0Mapper.FormatTimestamp(baseline.AddHours(hours)) };
        await store.EnqueuePersonalCloudProviderScopeAsync(Instance, Device, local.IntegrationId, local.ProviderConnectionId, true, [], []);
        Assert.False(await store.EnqueuePersonalCloudCatalogItemAsync(Instance, Device, local, observed, true, [], [], scopeId: scopeId));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        var retained = Assert.Single(await store.GetPersonalCloudWorkspaceRecordsAsync(Instance, "catalog"));
        Assert.Equal(Stamp, retained.GetProperty("source").GetProperty("refreshedAt").GetString());
    }

    [Theory]
    [InlineData("content")]
    [InlineData("availability")]
    [InlineData("adoption")]
    [InlineData("skip")]
    [InlineData("labels")]
    [InlineData("projects")]
    [InlineData("scope")]
    public async Task Real_catalog_facts_publish_new_observation_and_preserve_any_queued_envelope(string changed)
    {
        var store = await StoreAsync(); var local = Local(); var source = Source(local);
        await store.UpsertProviderSourceItemAsync(local);
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true, providerCatalogProtocolVersion: 2);
        var scopeId = SqliteTaskStore.PersonalCloudOpaqueId("scope_", $"{Device}:{local.IntegrationId}:{local.ProviderConnectionId}");
        object Scope(string id) => new { id, bridgeDeviceId = Device, name = "Todoist", integrationKind = IntegrationIds.Todoist,
            available = true, labelNames = Array.Empty<string>(), projectChoices = Array.Empty<object>(), entityVersion = 1, createdAt = Stamp, updatedAt = Stamp };
        var task = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(PersonalCloudWorkspaceTests.TaskRecord("task_adopted_test", projectId: null)))!;
        task["source"] = source; task["sourceOwnerDeviceId"] = Device;
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(1,
            spaces: [PersonalCloudWorkspaceTests.Space()], tasks: changed == "adoption" ? [task] : [],
            catalog: [Catalog(source, scopeId: changed == "scope" ? null : scopeId)], scopes: [Scope(scopeId)]));
        var observed = source with { RefreshedAt = PersonalCloudK0Mapper.FormatTimestamp(DateTimeOffset.Parse(Stamp).AddHours(25)) };
        if (changed == "content") observed = observed with { Title = "Changed synthetic title" };
        if (changed == "adoption") local = local with { AdoptedTaskId = "task_adopted_test", AdoptionState = ProviderSourceAdoptionStates.Adopted };
        if (changed == "skip") local = local with { AdoptionState = ProviderSourceAdoptionStates.Ignored };
        Assert.True(await store.EnqueuePersonalCloudCatalogItemAsync(Instance, Device, local, observed, changed != "availability",
            changed == "labels" ? ["Changed label"] : [], changed == "projects" ? [new("project_choice", "Changed project")] : [],
            scopeId: scopeId));
        var queued = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal("provider.catalog.put", queued.Operation);
        Assert.Contains(observed.RefreshedAt, queued.CanonicalJson);
        Assert.False(await store.EnqueuePersonalCloudCatalogItemAsync(Instance, Device, local,
            observed with { Title = "Later observation while pending", RefreshedAt = PersonalCloudK0Mapper.FormatTimestamp(DateTimeOffset.Parse(Stamp).AddHours(49)) },
            true, [], [], scopeId: scopeId));
        Assert.Equal(queued, Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)));
    }

    [Fact]
    public async Task Changed_connection_cannot_rebind_a_published_catalog_scope()
    {
        var store = await StoreAsync(); var local = Local(); var source = Source(local);
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true, providerCatalogProtocolVersion: 2);
        var originalScope = SqliteTaskStore.PersonalCloudOpaqueId("scope_", $"{Device}:{local.IntegrationId}:{local.ProviderConnectionId}");
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(1, catalog: [Catalog(source, scopeId: originalScope)]));
        local = local with { ProviderConnectionId = "todoist_other_connection" };
        var differentScope = SqliteTaskStore.PersonalCloudOpaqueId("scope_", $"{Device}:{local.IntegrationId}:{local.ProviderConnectionId}");
        Assert.False(await store.EnqueuePersonalCloudCatalogItemAsync(Instance, Device, local,
            source with { RefreshedAt = PersonalCloudK0Mapper.FormatTimestamp(DateTimeOffset.Parse(Stamp).AddDays(2)) }, true, [], [], scopeId: differentScope));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal(originalScope, Assert.Single(await store.GetPersonalCloudWorkspaceRecordsAsync(Instance, "catalog")).GetProperty("scopeId").GetString());
    }
    [Fact]
    public async Task Manual_adoption_after_Web_request_cannot_create_duplicate_or_skip_existing_adoption()
    {
        var store = await StoreAsync(); var local = Local(); await store.UpsertProviderSourceItemAsync(local);
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(1, catalog: [Catalog(Source(local))]));
        await store.PreparePersonalCloudCatalogIntentAsync(Instance, Device, Intent(), "account", "todoist_default", null);
        await store.AcknowledgePersonalCloudMutationAsync(Instance, Device, Receipt(Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)), 2));
        var manual = (await store.AdoptProviderSourceItemAsync(local.Id))!;
        await store.ApplyPersonalCloudSourceIntentAsync(Instance, Device, Intent("started", 2), local.Id);
        Assert.Equal("local_conflict", (await store.GetPersonalCloudCatalogActionAsync(Instance, "intent_test"))!.Phase);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)); Assert.Equal(manual.Id, (await store.GetPersonalCloudCatalogSourceItemsAsync()).Single(s => s.Id == local.Id).AdoptedTaskId);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
