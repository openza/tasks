using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class MicrosoftRecurrenceTests : IDisposable
{
    private const string Weekly = """
        {"pattern":{"type":"weekly","interval":2,"daysOfWeek":["monday","thursday"],"firstDayOfWeek":"monday"},"range":{"type":"numbered","startDate":"2026-06-01","numberOfOccurrences":8,"recurrenceTimeZone":"Pacific Standard Time"}}
        """;
    private const string Monthly = """
        {"pattern":{"type":"relativeMonthly","interval":3,"daysOfWeek":["monday"],"index":"last"},"range":{"type":"endDate","startDate":"2026-06-01","endDate":"2027-06-01","recurrenceTimeZone":"UTC"}}
        """;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "openza-repeat", Guid.NewGuid().ToString("N"));

    private static ProviderSourceItem Source(string? rule, bool observed = true) => new()
    {
        Id = "source_repeat", ExternalId = "external_repeat", ProviderTaskId = "list|external_repeat",
        IntegrationId = IntegrationIds.MicrosoftToDo, ProviderConnectionId = "mstodo_default",
        Title = "Synthetic repeating task", RecurrenceRule = rule,
        SnapshotJson = observed ? """{"msToDo":{"recurrenceObserved":true}}""" : """{"msToDo":{}}""",
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adoption_saves_local_rule_and_completion_does_not_create_an_occurrence(bool dated)
    {
        var store = await CreateStoreAsync();
        var source = Source(Weekly) with { DeadlineOn = dated ? new DateOnly(2026, 6, 1) : null };
        var task = await AdoptAsync(store, source);
        Assert.Equal(Weekly, task.RecurrenceRule);
        Assert.Equal(Weekly, task.EffectiveRecurrenceRule);
        Assert.Equal(dated ? TaskWorkflowStatus.Someday : TaskWorkflowStatus.Inbox, task.WorkflowStatus);

        await store.DetachProviderSourceItemAsync(source.Id);
        await store.CompleteTaskAsync(task.Id);
        var completed = Assert.Single(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All }));
        Assert.True(completed.IsCompleted);
        Assert.Equal(source.DeadlineOn, completed.DeadlineOn);
        Assert.Equal(Weekly, completed.RecurrenceRule);
        Assert.Null(completed.SourceRecurrenceRule);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("My local repeat rule")]
    public async Task Refresh_and_relink_do_not_restore_a_cleared_rule_or_overwrite_a_local_edit(string? localRule)
    {
        var store = await CreateStoreAsync();
        var source = Source(Weekly);
        var task = await AdoptAsync(store, source);
        await store.UpsertTaskAsync(task with { RecurrenceRule = localRule });
        await store.UpsertProviderSourceItemAsync(source with { RecurrenceRule = Monthly });
        var refreshed = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(Monthly, refreshed.SourceRecurrenceRule);
        Assert.Equal(localRule, refreshed.EffectiveRecurrenceRule);

        await store.DetachProviderSourceItemAsync(source.Id);
        await store.UpsertProviderSourceItemAsync(source with
        {
            AdoptedTaskId = task.Id, AdoptionState = ProviderSourceAdoptionStates.Adopted, RecurrenceRule = Monthly,
        });
        var relinked = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(Monthly, relinked.SourceRecurrenceRule);
        Assert.Equal(localRule, relinked.RecurrenceRule);
        Assert.Equal(localRule, relinked.EffectiveRecurrenceRule);
    }

    [Theory]
    [InlineData(null, Monthly)]
    [InlineData("Keep my rule", "Keep my rule")]
    public async Task Previously_adopted_task_waits_for_fresh_data_and_backfills_once(string? localRule, string expected)
    {
        var store = await CreateStoreAsync();
        var task = await AdoptAsync(store, Source("weekly", observed: false));
        Assert.Equal("weekly", task.SourceRecurrenceRule);
        Assert.Null(task.RecurrenceRule);
        await store.UpsertTaskAsync(task with { RecurrenceRule = localRule });

        await store.UpsertProviderSourceItemAsync(Source(Monthly) with { DeadlineOn = new DateOnly(2026, 6, 1) });
        await store.UpsertProviderSourceItemAsync(Source(Weekly) with { DeadlineOn = new DateOnly(2026, 6, 1) });
        await store.InitializeAsync();
        var reloaded = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(expected, reloaded.RecurrenceRule);
        Assert.Equal(TaskWorkflowStatus.Inbox, reloaded.WorkflowStatus);
    }

    [Fact]
    public async Task Explicit_non_repeating_import_does_not_acquire_a_provider_rule_later()
    {
        var store = await CreateStoreAsync();
        var task = await AdoptAsync(store, Source(null));
        await store.UpsertProviderSourceItemAsync(Source(Weekly));
        var updated = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(Weekly, updated.SourceRecurrenceRule);
        Assert.Null(updated.RecurrenceRule);
        Assert.Null(updated.EffectiveRecurrenceRule);
    }

    [Fact]
    public async Task Workspace_replica_without_a_device_import_marker_does_not_use_source_recurrence()
    {
        var store = await CreateStoreAsync();
        const string instance = "instance_repeat";
        const string device = "device_replica";
        var source = PersonalCloudK0Mapper.CreateSafeSourceDescriptor(new TaskItem
        {
            Id = "task_test", Title = "Synthetic repeating task", SourceIntegrationId = IntegrationIds.MicrosoftToDo,
            SourceExternalId = "external_repeat", SourceMetadataJson = JsonSerializer.Serialize(new { recurrenceRule = Weekly }),
        });
        var remote = JsonSerializer.SerializeToNode(PersonalCloudWorkspaceTests.TaskRecord(projectId: null))!.AsObject();
        remote["recurrenceRule"] = null;
        remote["source"] = JsonSerializer.SerializeToNode(source, JsonSerializerOptions.Web);
        remote["sourceOwnerDeviceId"] = "device_owner";
        await store.ApplyPersonalCloudChangePageAsync(instance, device,
            PersonalCloudWorkspaceTests.Page(1, [PersonalCloudWorkspaceTests.Space()], tasks: [remote]));

        var task = (await store.GetTaskAsync("task_test"))!;
        Assert.DoesNotContain("msToDoRecurrenceApplied", task.SourceMetadataJson!);
        Assert.Equal(Weekly, task.SourceRecurrenceRule);
        Assert.Null(task.EffectiveRecurrenceRule);

        remote["recurrenceRule"] = Weekly;
        remote["workflowStatus"] = "inbox";
        remote["entityVersion"] = 2;
        await store.ApplyPersonalCloudChangePageAsync(instance, device, PersonalCloudWorkspaceTests.Page(2, tasks: [remote]));
        await store.InitializeAsync();
        var reloaded = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(Weekly, reloaded.EffectiveRecurrenceRule);
        Assert.Equal(TaskWorkflowStatus.Inbox, reloaded.WorkflowStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clear_before_a_full_observation_does_not_backfill(bool workspace)
    {
        var store = await CreateStoreAsync();
        var task = await AdoptAsync(store, Source("weekly", observed: false));
        if (!workspace)
        {
            await store.UpsertTaskAsync(task with { RecurrenceRule = "My local rule" });
            await store.UpsertTaskAsync((await store.GetTaskAsync(task.Id))! with { RecurrenceRule = null });
            await store.UpsertProviderSourceItemAsync(Source(Weekly));
        }
        else
        {
            const string instance = "instance_repeat";
            const string device = "device_repeat";
            var cloud = new PersonalCloudConnectionProfile
            {
                InstanceId = instance, DeviceId = device, ServiceOrigin = new Uri("https://cloud.example/"),
                ArtifactVersion = "test", ConnectedAt = DateTimeOffset.UtcNow,
            };
            store.PersonalCloudConnection = () => cloud;
            await SetPublishedVersionAsync(store, instance, task.Id);
            await store.SavePersonalCloudWorkspaceCapabilitiesAsync(instance, workspace: true, providerCatalog: false);
            var edit = await store.PatchTaskWithPersonalCloudIntentAsync(task.Id,
                new Dictionary<string, object?> { ["recurrenceRule"] = null }, task.Revision, instance, device);
            await store.AcknowledgePersonalCloudMutationAsync(instance, device, PersonalCloudK0Codec.Serialize(new K0MutationReceipt
            {
                ProtocolVersion = 1, Product = "tasks", MutationId = edit.MutationId, Outcome = "accepted", EntityId = task.Id,
                EntityVersion = 5, ChangeSeq = 1, ServerAcceptedAt = "2026-06-01T00:00:00.000Z",
            }));
            await store.UpsertProviderSourceItemAsync(Source(Weekly));
            Assert.Equal(0, await store.ApplyPendingMicrosoftRecurrenceAfterPullAsync(cloud, default));
            Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(instance, device));
        }

        var reloaded = (await store.GetTaskAsync(task.Id))!;
        Assert.Null(reloaded.RecurrenceRule);
        Assert.Null(reloaded.EffectiveRecurrenceRule);
    }

    [Fact]
    public async Task Workspace_backfill_uses_the_confirmed_version_and_stages_only_once()
    {
        const string instance = "instance_repeat";
        const string device = "device_repeat";
        var store = await CreateStoreAsync();
        var task = await AdoptAsync(store, Source("weekly", observed: false));
        await SetPublishedVersionAsync(store, instance, task.Id);
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(instance, workspace: true, providerCatalog: false);
        var cloud = new PersonalCloudConnectionProfile
        {
            InstanceId = instance, DeviceId = device, ServiceOrigin = new Uri("https://cloud.example/"),
            ArtifactVersion = "test", ConnectedAt = DateTimeOffset.UtcNow,
        };
        store.PersonalCloudConnection = () => cloud;

        await store.UpsertProviderSourceItemAsync(Source(Weekly));
        Assert.Null((await store.GetTaskAsync(task.Id))!.RecurrenceRule);
        Assert.Equal(1, await store.ApplyPendingMicrosoftRecurrenceAfterPullAsync(cloud, default));
        Assert.Equal(0, await store.ApplyPendingMicrosoftRecurrenceAfterPullAsync(cloud, default));
        Assert.Equal(Weekly, (await store.GetTaskAsync(task.Id))!.RecurrenceRule);

        var change = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(instance, device));
        using var document = JsonDocument.Parse(change.CanonicalJson);
        Assert.Equal("task.patch", change.Operation);
        Assert.Equal(4, document.RootElement.GetProperty("baseEntityVersion").GetInt64());
        Assert.Equal(Weekly, document.RootElement.GetProperty("payload").GetProperty("fields").GetProperty("recurrenceRule").GetString());
    }

    [Fact]
    public async Task Disconnect_allows_backfill_of_a_previously_published_task()
    {
        const string instance = "instance_repeat";
        const string device = "device_repeat";
        var store = await CreateStoreAsync();
        var task = await AdoptAsync(store, Source("weekly", observed: false));
        await SetPublishedVersionAsync(store, instance, task.Id);
        store.PersonalCloudConnection = () => null;

        await store.UpsertProviderSourceItemAsync(Source(Weekly));

        Assert.Equal(Weekly, (await store.GetTaskAsync(task.Id))!.RecurrenceRule);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(instance, device));
    }

    [Theory]
    [InlineData(null, "Keep my local rule", 1)]
    [InlineData(Monthly, Monthly, 0)]
    public async Task First_workspace_baseline_preserves_legacy_recurrence_unless_cloud_already_has_a_rule(
        string? remoteRule, string expected, int staged)
    {
        const string instance = "instance_repeat";
        const string device = "device_repeat";
        var store = await CreateStoreAsync();
        var task = await AdoptAsync(store, Source("weekly", observed: false));
        await SetPublishedVersionAsync(store, instance, task.Id);
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(instance, workspace: false, providerCatalog: false);
        var cloud = new PersonalCloudConnectionProfile
        {
            InstanceId = instance, DeviceId = device, ServiceOrigin = new Uri("https://cloud.example/"),
            ArtifactVersion = "test", ConnectedAt = DateTimeOffset.UtcNow,
        };
        store.PersonalCloudConnection = () => cloud;
        await store.UpsertProviderSourceItemAsync(Source(Weekly));
        Assert.Equal(0, await store.ApplyPendingMicrosoftRecurrenceAfterPullAsync(cloud, default));
        var legacy = (await store.GetTaskAsync(task.Id))!;
        Assert.Equal(Weekly, legacy.RecurrenceRule);
        await store.UpsertTaskAsync(legacy with { RecurrenceRule = "Keep my local rule" });

        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(instance, workspace: true, providerCatalog: false);
        var remote = JsonSerializer.SerializeToNode(PersonalCloudWorkspaceTests.TaskRecord(task.Id, 5, null))!.AsObject();
        remote["spaceId"] = task.SpaceId;
        remote["recurrenceRule"] = remoteRule;
        remote["source"] = JsonSerializer.SerializeToNode(PersonalCloudK0Mapper.CreateSafeSourceDescriptor(legacy), JsonSerializerOptions.Web);
        remote["sourceOwnerDeviceId"] = device;
        var bootstrap = PersonalCloudK0Codec.Serialize(new
        {
            protocolVersion = 1, product = "tasks", checkpoint = 5, snapshot = new
            {
                spaces = new[] { PersonalCloudWorkspaceTests.Space(task.SpaceId) }, tasks = new[] { remote },
                projects = Array.Empty<object>(), labels = Array.Empty<object>(), taskLabels = Array.Empty<object>(),
                tombstones = Array.Empty<object>(), sourceDescriptors = Array.Empty<object>(), externalCommands = Array.Empty<object>(),
                catalog = Array.Empty<object>(), intents = Array.Empty<object>(), routes = Array.Empty<object>(),
            },
        });
        await store.ReplacePersonalCloudWorkspaceFromBootstrapAsync(instance, device, bootstrap);

        Assert.Equal(expected, (await store.GetTaskAsync(task.Id))!.RecurrenceRule);
        var changes = await store.GetPendingPersonalCloudOutboxAsync(instance, device);
        Assert.Equal(staged, changes.Count);
        if (staged == 1)
        {
            var change = Assert.Single(changes);
            Assert.Equal("task.patch", change.Operation);
            using var payload = JsonDocument.Parse(change.CanonicalJson);
            Assert.Equal(5, payload.RootElement.GetProperty("baseEntityVersion").GetInt64());
            Assert.Equal(expected, payload.RootElement.GetProperty("payload").GetProperty("fields").GetProperty("recurrenceRule").GetString());
        }
    }

    private static async Task SetPublishedVersionAsync(SqliteTaskStore store, string instance, string taskId)
    {
        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO personal_cloud_entity_versions(instance_id,product,entity_id,entity_version,updated_at)
            VALUES(@instance,'tasks',@id,4,0);
            """;
        command.Parameters.AddWithValue("@instance", instance);
        command.Parameters.AddWithValue("@id", taskId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<SqliteTaskStore> CreateStoreAsync()
    {
        var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
        await store.InitializeAsync();
        return store;
    }

    private static async Task<TaskItem> AdoptAsync(SqliteTaskStore store, ProviderSourceItem source)
    {
        await store.UpsertProviderSourceItemAsync(source);
        return (await store.AdoptProviderSourceItemAsync(source.Id))!;
    }

    public void Dispose() => TestDirectory.Delete(directory);
}
