using System.Net;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudProviderBridgeTests : IDisposable
{
    [Fact]
    public async Task Openza_completion_does_not_create_provider_delivery_and_legacy_phantoms_are_hidden()
    {
        var store = await StoreAsync("local-completion.db");
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM personal_cloud_provider_sources; UPDATE tasks SET completion_state='open', completed_at=NULL";
            await command.ExecuteNonQueryAsync();
        }
        // The bootstrap above deliberately leaves a historical phantom command behind.
        Assert.Empty(await store.GetPersonalCloudProviderRequestStatusAsync(Instance));
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudK0Codec.Serialize(new
        {
            protocolVersion = 1, product = "tasks", nextCheckpoint = 43,
            changes = new[] { new PersonalCloudTaskCompletedChange
            {
                ProtocolVersion = 1, Product = "tasks", ChangeSeq = 43, MutationId = "local_complete",
                Operation = "task.completed", EntityKind = "task", EntityId = TaskId, EntityVersion = 3,
                ServerAcceptedAt = "2026-10-11T07:00:00.000Z",
                Payload = new() { Task = new() { Id = TaskId, CompletionState = "completed", CompletedAt = "2026-10-11T07:00:00.000Z" } },
            } },
        }));
        Assert.Empty(await store.GetPersonalCloudProviderRequestStatusAsync(Instance));
        await using var verify = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        await verify.OpenAsync();
        var read = verify.CreateCommand();
        read.CommandText = "SELECT requested_seq FROM personal_cloud_provider_commands";
        Assert.Equal(42L, Convert.ToInt64(await read.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Unchanged_newer_observations_do_not_churn_and_fence_delayed_fields_across_restart()
    {
        var store = await StoreAsync("dedup-observation.db");
        var current = Assert.Single(await store.GetPersonalCloudProviderSourcesAsync(Instance)).Source;
        var time = DateTimeOffset.Parse(current.RefreshedAt);
        for (var index = 1; index <= 12; index++)
            await store.EnqueuePersonalCloudSourceSnapshotAsync(Instance, Device, TaskId,
                current with { RefreshedAt = PersonalCloudK0Mapper.FormatTimestamp(time.AddMinutes(index * 5)) });
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        var restarted = new SqliteTaskStore(store.DatabasePath);
        await restarted.InitializeAsync();
        await restarted.EnqueuePersonalCloudSourceSnapshotAsync(Instance, Device, TaskId,
            current with { Title = "Delayed changed title", RefreshedAt = PersonalCloudK0Mapper.FormatTimestamp(time.AddMinutes(30)) });
        Assert.Empty(await restarted.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        await restarted.EnqueuePersonalCloudSourceSnapshotAsync(Instance, Device, TaskId,
            current with { Title = "Current changed title", RefreshedAt = PersonalCloudK0Mapper.FormatTimestamp(time.AddMinutes(61)) });
        Assert.Equal(1, Assert.Single(await restarted.GetPendingPersonalCloudOutboxAsync(Instance, Device)).DeviceSequence);
        Assert.Equal(current.RefreshedAt, Assert.Single(await restarted.GetPersonalCloudProviderSourcesAsync(Instance)).Source.RefreshedAt);
    }

    [Fact]
    public async Task Pending_inflight_and_acknowledged_without_echo_snapshots_are_deduplicated_without_rewriting_intents()
    {
        var store = await StoreAsync("dedup-pending.db");
        var current = Assert.Single(await store.GetPersonalCloudProviderSourcesAsync(Instance)).Source;
        var time = DateTimeOffset.Parse(current.RefreshedAt);
        var desired = current with { Description = "Changed description", RefreshedAt = PersonalCloudK0Mapper.FormatTimestamp(time.AddMinutes(1)) };
        await store.EnqueuePersonalCloudSourceSnapshotAsync(Instance, Device, TaskId, desired);
        var inFlight = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        await store.EnqueuePersonalCloudSourceSnapshotAsync(Instance, Device, TaskId,
            desired with { RefreshedAt = PersonalCloudK0Mapper.FormatTimestamp(time.AddMinutes(2)) });
        Assert.Equal(inFlight, Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)));
        await store.AcknowledgePersonalCloudMutationAsync(Instance, Device, Receipt(inFlight.MutationId));
        var restarted = new SqliteTaskStore(store.DatabasePath);
        await restarted.InitializeAsync();
        await restarted.EnqueuePersonalCloudSourceSnapshotAsync(Instance, Device, TaskId,
            desired with { RefreshedAt = PersonalCloudK0Mapper.FormatTimestamp(time.AddMinutes(3)) });
        Assert.Empty(await restarted.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        await restarted.EnqueuePersonalCloudSourceSnapshotAsync(Instance, Device, TaskId,
            current with { RefreshedAt = PersonalCloudK0Mapper.FormatTimestamp(time.AddMinutes(4)) });
        // A real reversion must follow the pending/accepted newer state even when it matches the confirmed older state.
        Assert.Equal(2, Assert.Single(await restarted.GetPendingPersonalCloudOutboxAsync(Instance, Device)).DeviceSequence);
    }

    [Theory]
    [InlineData("title")] [InlineData("description")] [InlineData("url")] [InlineData("project")]
    [InlineData("priority")] [InlineData("plannedOn")] [InlineData("plannedAt")]
    [InlineData("deadlineOn")] [InlineData("deadlineAt")] [InlineData("recurrence")]
    public async Task Every_source_fact_change_is_published_without_allocating_sequences_for_duplicate_observations(string field)
    {
        var store = await StoreAsync($"dedup-{field}.db");
        var current = Assert.Single(await store.GetPersonalCloudProviderSourcesAsync(Instance)).Source;
        var time = DateTimeOffset.Parse(current.RefreshedAt);
        var changed = field switch
        {
            "title" => current with { Title = "Changed" }, "description" => current with { Description = "Changed" },
            "url" => current with { SourceUrl = "https://example.test/item" }, "project" => current with { ProjectName = "Changed" },
            "priority" => current with { Priority = 4 }, "plannedOn" => current with { PlannedOn = "2030-01-01" },
            "plannedAt" => current with { PlannedAt = "2030-01-01T00:00:00.000Z" },
            "deadlineOn" => current with { DeadlineOn = "2030-01-02" }, "deadlineAt" => current with { DeadlineAt = "2030-01-02T00:00:00.000Z" },
            _ => current with { RecurrenceRule = "every day" },
        };
        await store.EnqueuePersonalCloudSourceSnapshotAsync(Instance, Device, TaskId,
            changed with { RefreshedAt = PersonalCloudK0Mapper.FormatTimestamp(time.AddMinutes(1)) });
        await store.EnqueuePersonalCloudSourceSnapshotAsync(Instance, Device, TaskId,
            changed with { RefreshedAt = PersonalCloudK0Mapper.FormatTimestamp(time.AddMinutes(2)) });
        Assert.Equal(1, Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).DeviceSequence);
    }

    [Fact]
    public async Task Unchanged_freshness_heartbeat_is_bounded_and_carries_actual_observation_time()
    {
        var store = await StoreAsync("dedup-heartbeat.db");
        var current = Assert.Single(await store.GetPersonalCloudProviderSourcesAsync(Instance)).Source;
        var time = DateTimeOffset.Parse(current.RefreshedAt);
        for (var minutes = 5; minutes <= 720; minutes += 5)
            await store.EnqueuePersonalCloudSourceSnapshotAsync(Instance, Device, TaskId,
                current with { RefreshedAt = PersonalCloudK0Mapper.FormatTimestamp(time.AddMinutes(minutes)) });
        var pending = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        Assert.Equal(2, pending.Count); // Two six-hour heartbeats, rather than144 full snapshots.
        using var document = JsonDocument.Parse(pending[^1].CanonicalJson);
        Assert.Equal(PersonalCloudK0Mapper.FormatTimestamp(time.AddHours(12)),
            document.RootElement.GetProperty("payload").GetProperty("source").GetProperty("refreshedAt").GetString());
    }

    [Fact]
    public async Task Wrong_source_identity_and_deletion_cannot_enqueue_refresh_or_consume_a_sequence()
    {
        var store = await StoreAsync("dedup-identity-delete.db");
        var current = Assert.Single(await store.GetPersonalCloudProviderSourcesAsync(Instance)).Source;
        var newer = current with { RefreshedAt = "2030-01-01T00:00:00.000Z" };
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.EnqueuePersonalCloudSourceSnapshotAsync(
            Instance, Device, TaskId, newer with { ExternalIdHash = new string('a', 64) }));
        Assert.Equal("source_identity_conflict", error.Code);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudK0Codec.Serialize(new
        {
            protocolVersion = 1, product = "tasks", nextCheckpoint = 43,
            changes = new[] { new PersonalCloudTaskDeletedChange
            {
                ProtocolVersion = 1, Product = "tasks", ChangeSeq = 43, MutationId = "mutation_dedup_deleted",
                Operation = "task.deleted", EntityKind = "task", EntityId = TaskId, EntityVersion = 3,
                Payload = new() { Task = new() { Id = TaskId, DeletedAt = "2026-10-13T00:00:00.000Z" } },
                ServerAcceptedAt = "2026-10-13T00:00:01.000Z",
            } },
        }));
        await store.EnqueuePersonalCloudSourceSnapshotAsync(Instance, Device, TaskId, newer);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    private const string Instance = "instance_bridge_tests";
    private const string Device = "device_bridge_tests_a";
    private const string TaskId = "local_bridge_task";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-bridge-tests", Guid.NewGuid().ToString("N"));
    private static readonly TaskItem ProviderTask = new()
    {
        Id = "todoist_private-id", ExternalId = "private-id", IntegrationId = IntegrationIds.Todoist,
        ProviderConnectionId = "todoist_default", Title = "Provider title", RecurrenceRule = "every day",
    };
    private static K0SafeSourceDescriptor Source => new()
    {
        IntegrationKind = "todoist", ExternalIdHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("todoist:private-id"))).ToLowerInvariant(),
        Title = ProviderTask.Title, RefreshedAt = PersonalCloudK0Mapper.FormatTimestamp(DateTimeOffset.UtcNow.AddMinutes(-5)),
    };

    [Fact]
    public void Claim_start_and_result_match_shared_goldens()
    {
        const string instance = "instance_00000000000000000000000000000001";
        const string device = "device_00000000000000000000000000000001";
        const string claim = "claim_00000000000000000000000000000001";
        const string task = "local_00000000000000000000000000000001";
        Assert.Equal(Golden("provider-delivery-claim-request.json"), PersonalCloudProviderBridgeCodec.ClaimRequest(instance, device, claim, task, 42, "todoist", new string('a', 64)));
        Assert.Equal(Golden("provider-delivery-start-request.json"), PersonalCloudProviderBridgeCodec.StartRequest(instance, device, claim));
        Assert.Equal(Golden("valid-external-command-confirm.json"), PersonalCloudProviderBridgeCodec.ResultMutation(instance, device,
            "mutation_00000000000000000000000000000042", 42, task, claim, 42, "2026-10-11T06:00:00.000Z", null));
    }

    [Fact]
    public async Task Success_stores_immutable_result_once_and_restarts_without_repeating_recurring_completion()
    {
        var store = await StoreAsync("success.db"); var handler = new DeliveryHandler();
        var provider = new FakeProvider(); var bridge = await BridgeAsync(store, handler);
        var result = await bridge.RunAsync([new(provider, "account-a")]);
        Assert.Equal(1, result.Confirmed); Assert.Equal(1, provider.Actions);
        var intent = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device), row => row.Operation == "externalCommand.confirm");
        Assert.DoesNotContain("private-id", intent.CanonicalJson); Assert.DoesNotContain("account-a", intent.CanonicalJson);
        var snapshot = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device), row => row.Operation == "source.refreshSnapshot");
        DispatchCostAssertions.Known(intent);
        DispatchCostAssertions.Known(snapshot);
        using (var sourceJson = JsonDocument.Parse(snapshot.CanonicalJson))
            Assert.True(DateTimeOffset.Parse(sourceJson.RootElement.GetProperty("payload").GetProperty("source").GetProperty("refreshedAt").GetString()!) <= provider.ActionAt);
        Assert.Equal(2, await store.GetPersonalCloudEntityVersionAsync(Instance, TaskId));
        await (await BridgeAsync(store, handler)).RunAsync([new(provider, "account-a")]);
        Assert.Equal(1, provider.Actions);
        Assert.Equal(intent.CanonicalJson, Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device), row => row.Operation == "externalCommand.confirm").CanonicalJson);
        await store.AcknowledgePersonalCloudMutationAsync(Instance, Device, PersonalCloudK0Codec.Serialize(new
        {
            protocolVersion = 1, product = "tasks", mutationId = intent.MutationId, outcome = "accepted", entityId = TaskId,
            entityVersion = 5, changeSeq = 43, serverAcceptedAt = "2026-10-11T06:00:01.000Z",
        }));
        // A source/result receipt cannot manufacture a newer base for unsynchronized user fields.
        Assert.Equal(2, await store.GetPersonalCloudEntityVersionAsync(Instance, TaskId));
        await bridge.RunAsync([new(provider, "account-a")]); Assert.Equal(1, provider.Actions);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, HttpStatusCode.ServiceUnavailable)]
    public async Task Lost_provider_response_or_start_response_is_uncertain_and_never_replayed(bool loseStart, HttpStatusCode? failureStatus)
    {
        var store = await StoreAsync("uncertain.db");
        var handler = new DeliveryHandler { LoseStartResponse = loseStart };
        var provider = new FakeProvider { FailureAfterEffect = !loseStart, FailureStatus = failureStatus };
        var bridge = await BridgeAsync(store, handler);
        var first = await bridge.RunAsync([new(provider, "account-a")]);
        Assert.Equal(1, first.Uncertain); Assert.Equal(loseStart ? 0 : 1, provider.Actions);
        handler.LoseStartResponse = false; provider.FailureAfterEffect = false;
        var restarted = await (await BridgeAsync(store, handler)).RunAsync([new(provider, "account-a")]);
        Assert.Equal(1, restarted.Uncertain); Assert.Equal(loseStart ? 0 : 1, provider.Actions);
        Assert.DoesNotContain(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device), row => row.Operation.StartsWith("externalCommand.", StringComparison.Ordinal));
        var status = Assert.Single(await store.GetPersonalCloudProviderRequestStatusAsync(Instance));
        Assert.Contains("uncertain", status.Status); Assert.False(status.CanRetry);
        var blocked = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.EnsureNoPendingPersonalCloudIntentsAsync());
        Assert.Equal("pending_intents", blocked.Code);
    }

    [Fact]
    public async Task No_configuration_wrong_source_duplicate_source_or_account_change_never_dispatches()
    {
        var store = await StoreAsync("no-config.db"); var handler = new DeliveryHandler();
        var bridge = await BridgeAsync(store, handler); var provider = new FakeProvider();
        Assert.Equal(1, (await bridge.RunAsync([])).Pending); Assert.Equal(0, handler.Claims);
        provider.Tasks = [ProviderTask with { ExternalId = "different" }];
        await bridge.RunAsync([new(provider, "account-a")]); Assert.Equal(0, handler.Claims);
        provider.Tasks = [ProviderTask, ProviderTask];
        await bridge.RunAsync([new(provider, "account-a")]); Assert.Equal(0, handler.Claims);
        var request = Assert.Single(await store.GetPersonalCloudProviderCommandsAsync(Instance));
        await store.PreparePersonalCloudProviderAttemptAsync(Instance, Device, request, "account-a");
        provider.Tasks = [ProviderTask];
        await bridge.RunAsync([new(provider, "account-b")]); Assert.Equal(0, handler.Claims); Assert.Equal(0, provider.Actions);
    }

    [Fact]
    public async Task Two_replicas_contend_for_one_claim_and_only_one_provider_action_runs()
    {
        var first = await StoreAsync("first.db"); var second = await StoreAsync("second.db", "device_bridge_tests_b");
        var handler = new DeliveryHandler(); var provider = new FakeProvider();
        var a = await BridgeAsync(first, handler); var b = await BridgeAsync(second, handler, "device_bridge_tests_b");
        await a.RunAsync([new(provider, "account-a")]);
        var result = await b.RunAsync([new(provider, "account-a")]);
        Assert.Equal(1, provider.Actions); Assert.Equal(1, result.Pending);
    }

    [Fact]
    public async Task Device_without_bridge_capability_never_reserves_and_legacy_completion_cannot_bypass_claim()
    {
        var store = await StoreAsync("capability.db"); var handler = new DeliveryHandler { BridgeCapability = false };
        var provider = new FakeProvider(); var bridge = await BridgeAsync(store, handler);
        Assert.Equal(1, (await bridge.RunAsync([new(provider, "account-a")])).Pending);
        Assert.Equal(0, handler.Claims); Assert.Equal(0, provider.Actions);
        await store.EnsureNoPendingPersonalCloudIntentsAsync();
        await store.QueueCompletionAsync(new PendingCompletion { Id = "legacy-cloud-completion", TaskId = TaskId, Provider = "todoist", ProviderTaskId = "private-id", Completed = true });
        Assert.Equal(0, await new TaskSyncEngine(store).SyncPendingCompletionsAsync(provider));
        Assert.Single(await store.GetPendingCompletionsAsync("todoist")); Assert.Equal(0, provider.Actions);
        Assert.Equal("pending_intents", (await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.EnsureNoPendingPersonalCloudIntentsAsync())).Code);
    }

    [Fact]
    public async Task Multiple_live_cloud_tasks_for_one_provider_item_do_not_claim_or_dispatch()
    {
        var store = await StoreAsync("aliases.db"); var handler = new DeliveryHandler(); var provider = new FakeProvider();
        var task = new TaskItem { Id = "local_alias", Title = "Another copy", SpaceId = SpaceIds.Default,
            SourceIntegrationId = "todoist", SourceExternalId = "private-id", SourceTitle = ProviderTask.Title };
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudK0Codec.Serialize(new
        {
            protocolVersion = 1, product = "tasks", nextCheckpoint = 43,
            changes = new[] { new K0TaskCreatedChange
            {
                ProtocolVersion = 1, Product = "tasks", ChangeSeq = 43, MutationId = "mutation_alias", Operation = "task.created",
                EntityKind = "task", EntityId = task.Id, EntityVersion = 1, ServerAcceptedAt = "2026-10-11T06:00:01.000Z",
                Payload = PersonalCloudK0Mapper.CreateTaskMutation(task, Instance, Device, "mutation_alias", 1, DateTimeOffset.UtcNow).Payload,
            } },
        }));
        var bridge = await BridgeAsync(store, handler);
        Assert.Equal(1, (await bridge.RunAsync([new(provider, "account-a")])).Pending);
        Assert.Equal(0, handler.Claims); Assert.Equal(0, provider.Actions);
        Assert.Contains("Multiple Openza tasks", Assert.Single(await store.GetPersonalCloudProviderRequestStatusAsync(Instance)).Status);
    }

    [Fact]
    public async Task Lost_claim_response_reuses_persisted_identity_before_one_provider_dispatch()
    {
        var store = await StoreAsync("claim-loss.db"); var handler = new DeliveryHandler { LoseClaimResponse = true };
        var provider = new FakeProvider(); var bridge = await BridgeAsync(store, handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => bridge.RunAsync([new(provider, "account-a")]));
        Assert.Equal(0, provider.Actions);
        handler.LoseClaimResponse = false;
        Assert.Equal(1, (await (await BridgeAsync(store, handler)).RunAsync([new(provider, "account-a")])).Confirmed);
        Assert.Equal(2, handler.Claims); Assert.Equal(1, provider.Actions);
    }

    [Fact]
    public async Task Repeated_deleted_source_feed_after_compacted_bootstrap_preserves_tombstone_without_resurrection()
    {
        var store = await StoreAsync("deleted.db");
        await store.ReplacePersonalCloudReplicaFromBootstrapAsync(Instance, Device, PersonalCloudK0Codec.Serialize(new PersonalCloudBootstrap
        {
            ProtocolVersion = 1, Product = "tasks", Checkpoint = 42,
            Snapshot = new PersonalCloudBootstrapSnapshot { Spaces = [], Projects = [], Tasks = [], Labels = [], TaskLabels = [],
                Tombstones = [new("task", TaskId, 2, "2026-10-11T06:00:00.000Z", null)] },
        }));
        for (var sequence = 43; sequence <= 44; sequence++)
        {
            await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudK0Codec.Serialize(new
            {
                protocolVersion = 1, product = "tasks", nextCheckpoint = sequence,
                changes = new[] { new PersonalCloudTaskDeletedChange
                {
                    ProtocolVersion = 1, Product = "tasks", ChangeSeq = sequence, MutationId = $"mutation_deleted_{sequence}",
                    Operation = "task.deleted", EntityKind = "task", EntityId = TaskId, EntityVersion = 2,
                    Payload = new() { Task = new() { Id = TaskId, DeletedAt = "2026-10-11T06:00:00.000Z" } },
                    ServerAcceptedAt = "2026-10-11T06:00:01.000Z",
                } },
            }));
        }
        Assert.Null(await store.GetTaskAsync(TaskId)); Assert.Equal(44, await store.GetPersonalCloudCheckpointAsync(Instance, Device));
    }

    [Fact]
    public async Task Known_no_effect_rejection_is_durable_and_explicit_retry_reuses_id_after_response_loss()
    {
        var store = await StoreAsync("rejected.db"); var handler = new DeliveryHandler();
        var provider = new FakeProvider { Rejection = HttpStatusCode.BadRequest }; var bridge = await BridgeAsync(store, handler);
        Assert.Equal(1, (await bridge.RunAsync([new(provider, "account-a")])).Rejected);
        var result = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device), row => row.Operation == "externalCommand.reject");
        DispatchCostAssertions.Known(result);
        using var json = JsonDocument.Parse(result.CanonicalJson);
        Assert.Equal("provider_rejected", json.RootElement.GetProperty("payload").GetProperty("errorCode").GetString());
        await store.AcknowledgePersonalCloudMutationAsync(Instance, Device, Receipt(result.MutationId));
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, CommandPage(43, "rejected", "provider_rejected", 42));
        handler.LoseRetryResponse = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => bridge.RetryAsync(TaskId));
        handler.LoseRetryResponse = false;
        await bridge.RetryAsync(TaskId);
        Assert.Equal(2, handler.RetryIds.Count); Assert.Equal(handler.RetryIds[0], handler.RetryIds[1]);
        Assert.Equal(1, provider.Actions);
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, CommandPage(44, "pending_desktop", null, 44));
        handler.Release(); provider.Rejection = null;
        Assert.Equal(1, (await bridge.RunAsync([new(provider, "account-a")])).Confirmed);
        Assert.Equal(2, provider.Actions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_facts_and_own_echo_do_not_defer_or_overwrite_pending_user_fields(bool ownEcho)
    {
        var store = await StoreAsync("source-independent.db");
        var original = (await store.GetTaskAsync(TaskId))!;
        await store.UpsertTaskAsync(original with { RecurrenceRule = "every week" });
        var notes = await store.UpdateTaskNotesWithPersonalCloudIntentAsync(TaskId, "Keep local notes", 2, Instance, Device);
        var optimisticVersion = await store.GetPersonalCloudEntityVersionAsync(Instance, TaskId);
        var fresh = Source with { Description = "Fresh source description", RecurrenceRule = "every day",
            RefreshedAt = "2030-01-01T00:00:00.000Z" };
        var mutation = "mutation_remote_source";
        if (ownEcho)
        {
            await store.EnqueuePersonalCloudSourceSnapshotAsync(Instance, Device, TaskId, fresh);
            var snapshot = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device), row => row.Operation == "source.refreshSnapshot");
            mutation = snapshot.MutationId;
            await store.AcknowledgePersonalCloudMutationAsync(Instance, Device, Receipt(mutation));
        }
        var refresh = new PersonalCloudSourceRefreshedChange
        {
            ProtocolVersion = 1, Product = "tasks", ChangeSeq = 43, MutationId = mutation,
            Operation = "task.updatedOpenzaFields", EntityKind = "task", EntityId = TaskId, EntityVersion = 99,
            ServerAcceptedAt = "2026-10-11T06:00:03.000Z", Payload = new() { Task = new() { Id = TaskId, Source = fresh } },
        };
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudK0Codec.Serialize(new
        { protocolVersion = 1, product = "tasks", changes = new[] { refresh }, nextCheckpoint = 43 }));
        var task = (await store.GetTaskAsync(TaskId))!;
        Assert.Equal("Keep local notes", task.Notes); Assert.Equal("Fresh source description", task.SourceDescription);
        Assert.Equal("every day", task.SourceRecurrenceRule);
        Assert.Equal("every week", task.RecurrenceRule); Assert.True(task.IsCompleted);
        Assert.Equal(fresh, Assert.Single(await store.GetPersonalCloudProviderSourcesAsync(Instance)).Source);
        Assert.Equal(0, await store.GetDeferredPersonalCloudChangeCountAsync(Instance));
        Assert.Equal(optimisticVersion, await store.GetPersonalCloudEntityVersionAsync(Instance, TaskId));
        await store.AcknowledgePersonalCloudMutationAsync(Instance, Device, PersonalCloudK0Codec.Serialize(new
        { protocolVersion = 1, product = "tasks", mutationId = notes.MutationId, outcome = "accepted", entityId = TaskId,
            entityVersion = 3, changeSeq = 44, serverAcceptedAt = "2026-10-11T06:00:04.000Z" }));
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, CommandPage(44, "confirmed", null, 42));
        Assert.Equal(0, await store.GetDeferredPersonalCloudChangeCountAsync(Instance));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        await store.EnsureNoPendingPersonalCloudIntentsAsync();
        await store.EnqueuePersonalCloudSourceSnapshotAsync(Instance, Device, TaskId,
            fresh with { RefreshedAt = "2030-01-01T00:05:00.000Z" });
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal(fresh.RefreshedAt, Assert.Single(await store.GetPersonalCloudProviderSourcesAsync(Instance)).Source.RefreshedAt);
    }

    [Fact]
    public async Task Source_echo_after_cloud_delete_does_not_resurrect_or_block_the_delete()
    {
        var store = await StoreAsync("source-echo-deleted.db");
        var fresh = Source with { Description = "Fresh source description", RefreshedAt = "2030-01-01T00:00:00.000Z" };
        await store.EnqueuePersonalCloudSourceSnapshotAsync(Instance, Device, TaskId, fresh);
        var snapshot = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        await store.AcknowledgePersonalCloudMutationAsync(Instance, Device, Receipt(snapshot.MutationId));
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudK0Codec.Serialize(new
        {
            protocolVersion = 1, product = "tasks", nextCheckpoint = 43,
            changes = new[] { new PersonalCloudTaskDeletedChange
            {
                ProtocolVersion = 1, Product = "tasks", ChangeSeq = 43, MutationId = "mutation_source_deleted",
                Operation = "task.deleted", EntityKind = "task", EntityId = TaskId, EntityVersion = 3,
                Payload = new() { Task = new() { Id = TaskId, DeletedAt = "2026-10-11T06:00:00.000Z" } },
                ServerAcceptedAt = "2026-10-11T06:00:01.000Z",
            } },
        }));
        var refresh = new PersonalCloudSourceRefreshedChange
        {
            ProtocolVersion = 1, Product = "tasks", ChangeSeq = 44, MutationId = snapshot.MutationId,
            Operation = "task.updatedOpenzaFields", EntityKind = "task", EntityId = TaskId, EntityVersion = 2,
            ServerAcceptedAt = "2026-10-11T06:00:03.000Z", Payload = new() { Task = new() { Id = TaskId, Source = fresh } },
        };
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudK0Codec.Serialize(new
        { protocolVersion = 1, product = "tasks", changes = new[] { refresh }, nextCheckpoint = 44 }));
        Assert.Null((await store.GetTaskAsync(TaskId))?.SourceDescription);
        Assert.Empty(await store.GetPersonalCloudProviderSourcesAsync(Instance));
        Assert.Equal(0, await store.GetDeferredPersonalCloudChangeCountAsync(Instance));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal(3, await store.GetPersonalCloudEntityVersionAsync(Instance, TaskId));
        await store.EnqueuePersonalCloudSourceSnapshotAsync(Instance, Device, TaskId,
            fresh with { RefreshedAt = "2030-01-01T00:05:00.000Z" });
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("pending_desktop")]
    public async Task Earlier_rejections_or_unconfigured_requests_do_not_starve_later_delivery(string state)
    {
        var store = await StoreAsync("queue-page.db");
        await using (var sql = new SqliteConnection("Data Source=" + store.DatabasePath))
        {
            await sql.OpenAsync();
            var move = sql.CreateCommand(); move.CommandText = """
                UPDATE personal_cloud_provider_commands SET requested_seq=501 WHERE task_id=@t;
                UPDATE personal_cloud_checkpoints SET last_change_seq=501
                """;
            move.Parameters.AddWithValue("@t", TaskId); await move.ExecuteNonQueryAsync();
            for (var n = 1; n <= 100; n++)
            {
                var command = sql.CreateCommand(); command.CommandText = """
                    INSERT INTO personal_cloud_provider_sources VALUES(@i,@t,@s);
                    INSERT INTO personal_cloud_provider_commands(instance_id,task_id,requested_seq,command,state)
                    VALUES(@i,@t,@q,'complete',@state)
                    """;
                command.Parameters.AddWithValue("@i", Instance); command.Parameters.AddWithValue("@t", $"local_older_{n}");
                command.Parameters.AddWithValue("@q", n); command.Parameters.AddWithValue("@state", state);
                command.Parameters.AddWithValue("@s", PersonalCloudK0Codec.Serialize(Source with
                { IntegrationKind = "msToDo", ExternalIdHash = n.ToString("x64") }));
                await command.ExecuteNonQueryAsync();
            }
        }
        var handler = new DeliveryHandler(); var provider = new FakeProvider();
        var bridge = await BridgeAsync(store, handler);
        Assert.Equal(1, (await bridge.RunAsync([new(provider, "account-a")])).Confirmed);
        Assert.Equal(1, provider.Actions); Assert.Equal(1, handler.Claims);
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, CommandPage(502, "rejected", "provider_rejected", 501));
        handler.Release();
        await bridge.RetryAsync(TaskId);
        Assert.Single(handler.RetryIds);
    }

    private async Task<SqliteTaskStore> StoreAsync(string name, string device = Device)
    {
        Directory.CreateDirectory(_directory); var store = new SqliteTaskStore(Path.Combine(_directory, name)); await store.InitializeAsync();
        var source = Source;
        var bootstrap = new PersonalCloudBootstrap
        {
            ProtocolVersion = 1, Product = "tasks", Checkpoint = 42,
            Snapshot = new PersonalCloudBootstrapSnapshot
            {
                Spaces = [new(SpaceIds.Default, "My space", null)], Projects = [], Labels = [], TaskLabels = [], Tombstones = [],
                Tasks = [new(TaskId, SpaceIds.Default, null, null, "Openza title", "completed", "inbox", 3, null, null, null, null,
                    "2026-10-11T06:00:00.000Z", "2026-10-11T05:00:00.000Z", 2, null)],
                SourceDescriptors = [new(TaskId, source.IntegrationKind, source.ExternalIdHash, null, source.Title, null, null, null,
                    null, null, null, null, null, source.RefreshedAt)],
                ExternalCommands = [new(TaskId, "complete", "pending_desktop", 42, "2026-10-11T06:00:00.000Z", null)],
            },
        };
        await store.ReplacePersonalCloudReplicaFromBootstrapAsync(Instance, device, PersonalCloudK0Codec.Serialize(bootstrap));
        return store;
    }

    private static async Task<PersonalCloudProviderBridge> BridgeAsync(SqliteTaskStore store, DeliveryHandler handler, string device = Device)
    {
        var http = new HttpClient(new PerDeviceHandler(handler, device)); var credentials = new InMemoryCredentialStore();
        var origin = new Uri("https://bridge.example.test/");
        var key = $"personal-cloud-device-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin.AbsoluteUri))).ToLowerInvariant()}";
        await credentials.SaveAsync(key, PersonalCloudK0Codec.Serialize(new
        { serviceOrigin = origin.AbsoluteUri, instanceId = Instance, deviceId = device, refreshCredential = "publicfake-refresh-for-unit-tests" }));
        var profile = new PersonalCloudConnectionProfile { ServiceOrigin = origin, InstanceId = Instance, DeviceId = device, ArtifactVersion = "0.1.0-preview.9", ConnectedAt = DateTimeOffset.UtcNow };
        return new(store, new PersonalCloudProviderDeliveryClient(http, new(http, credentials), profile), profile);
    }

    private static string Golden(string name) => PersonalCloudK0Codec.Canonicalize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PersonalCloudK0", name)));
    private static string Receipt(string id) => PersonalCloudK0Codec.Serialize(new { protocolVersion = 1, product = "tasks", mutationId = id,
        outcome = "accepted", entityId = TaskId, entityVersion = 2, changeSeq = 43, serverAcceptedAt = "2026-10-11T06:00:01.000Z" });
    private static string CommandPage(long sequence, string state, string? error, long requested) => PersonalCloudK0Codec.Serialize(new
    {
        protocolVersion = 1, product = "tasks", nextCheckpoint = sequence,
        changes = new[] { new PersonalCloudExternalCommandChangedChange
        {
            ProtocolVersion = 1, Product = "tasks", ChangeSeq = sequence, MutationId = $"mutation_result_{sequence}", Operation = "task.updatedOpenzaFields",
            EntityKind = "task", EntityId = TaskId, EntityVersion = 2, ServerAcceptedAt = "2026-10-11T06:00:02.000Z",
            Payload = new(new(TaskId, new("complete", state, error) { RequestedChangeSeq = requested })),
        } },
    });

    private sealed class FakeProvider : ISyncProvider
    {
        public string IntegrationId => "todoist";
        public TaskItem[] Tasks { get; set; } = [ProviderTask];
        public int Actions { get; private set; }
        public DateTimeOffset ActionAt { get; private set; }
        public bool FailureAfterEffect { get; set; }
        public HttpStatusCode? FailureStatus { get; set; }
        public HttpStatusCode? Rejection { get; set; }
        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProviderSnapshot(Tasks, [], []));
        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default)
        {
            Assert.Equal("private-id", completion.ProviderTaskId); Actions++; ActionAt = DateTimeOffset.UtcNow;
            if (FailureAfterEffect) throw new HttpRequestException("Synthetic effect occurred, response lost.", null, FailureStatus);
            if (Rejection is { } status) throw new HttpRequestException("Synthetic known rejection.", null, status);
            return Task.CompletedTask;
        }
    }

    private sealed class PerDeviceHandler(DeliveryHandler server, string device) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => server.SendAsync(request, device, cancellationToken);
    }

    private sealed class DeliveryHandler
    {
        private PersonalCloudProviderClaim? _claim;
        public int Claims { get; private set; }
        public bool LoseStartResponse { get; set; }
        public bool LoseClaimResponse { get; set; }
        public bool LoseRetryResponse { get; set; }
        public bool BridgeCapability { get; set; } = true;
        public List<string> RetryIds { get; } = [];
        public void Release() => _claim = null;
        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string device, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/health") return Json(new { status = "ok", protocolVersion = 1,
                product = "tasks", artifactVersion = "0.1.0-preview.9", schemaVersion = 20 });
            if (request.RequestUri!.AbsolutePath == "/auth/device/token") return Json(new { protocolVersion = 1,
                accessToken = "publicfake-access-for-unit-tests", expiresAt = DateTimeOffset.UtcNow.AddMinutes(5), capabilities = BridgeCapability ? new[] { "desktop_sync", "provider_bridge" } : new[] { "desktop_sync" } });
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)); var body = document.RootElement;
            switch (request.RequestUri.AbsolutePath)
            {
                case "/v1/provider/commands/claim":
                    Claims++;
                    if (_claim is not null && _claim.ClaimId != body.GetProperty("claimId").GetString()) return Error("delivery_busy");
                    _claim ??= new(body.GetProperty("claimId").GetString()!, TaskId, body.GetProperty("requestedChangeSeq").GetInt64(), device,
                        "complete", "todoist", Source.ExternalIdHash, "reserved");
                    if (LoseClaimResponse) throw new HttpRequestException("Synthetic claim response lost.");
                    break;
                case "/v1/provider/commands/start":
                    _claim = _claim! with { State = "started" };
                    if (LoseStartResponse) throw new HttpRequestException("Synthetic start response lost.");
                    break;
                case "/v1/provider/commands/retry":
                    var retry = body.GetProperty("retryId").GetString()!; RetryIds.Add(retry);
                    if (LoseRetryResponse) throw new HttpRequestException("Synthetic retry response lost.");
                    return Json(new { protocolVersion = 1, product = "tasks", instanceId = Instance, retryId = retry, taskId = TaskId, requestedChangeSeq = body.GetProperty("requestedChangeSeq").GetInt64() + 1 });
                default: throw new InvalidOperationException("Unexpected synthetic route.");
            }
            return Json(new { protocolVersion = 1, product = "tasks", instanceId = Instance, claim = _claim });
        }
        private static HttpResponseMessage Error(string code) => new(HttpStatusCode.Conflict) { Content = new StringContent(PersonalCloudK0Codec.Serialize(new { error = new { code } })) };
        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(PersonalCloudK0Codec.Serialize(body), Encoding.UTF8, "application/json") };
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
