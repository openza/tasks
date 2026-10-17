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
    [Fact]
    public async Task Combined_catalog_144_unchanged_observations_emit_two_actual_time_heartbeats()
    {
        var store = await StoreAsync(); var local = Local(); var source = Source(local); var baseline = DateTimeOffset.Parse(Stamp);
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true, providerCatalogProtocolVersion: 2);
        var scopeId = SqliteTaskStore.PersonalCloudOpaqueId("scope_", $"{Device}:{local.IntegrationId}:{local.ProviderConnectionId}");
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(1, catalog: [Catalog(source, scopeId: scopeId)], scopes: [new {
            id = scopeId, bridgeDeviceId = Device, name = "Todoist", integrationKind = IntegrationIds.Todoist, available = true, labelNames = Array.Empty<string>(), projectChoices = Array.Empty<object>(), entityVersion = 1, createdAt = Stamp, updatedAt = Stamp }]));
        var count = 0; long version = 1;
        for (var minutes = 5; minutes <= 720; minutes += 5)
        {
            var observed = source with { RefreshedAt = PersonalCloudK0Mapper.FormatTimestamp(baseline.AddMinutes(minutes)) };
            await store.EnqueuePersonalCloudProviderScopeAsync(Instance, Device, local.IntegrationId, local.ProviderConnectionId, true, [], []);
            if (!await store.EnqueuePersonalCloudCatalogItemAsync(Instance, Device, local, observed, true, [], [], scopeId: scopeId)) continue;
            count++; var entry = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
            Assert.True(minutes is 360 or 720); Assert.Contains(observed.RefreshedAt, entry.CanonicalJson);
            await store.AcknowledgePersonalCloudMutationAsync(Instance, Device, Receipt(entry, ++version));
            await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(version, catalog: [Catalog(observed, version, scopeId)]));
        }
        Assert.Equal(2, count); Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
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
