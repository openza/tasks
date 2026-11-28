using System.Net;
using System.Text;
using System.Text.Json;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudProviderObservedReopenSyncTests : IDisposable
{
    private const string Instance = "instance_observed_reopen_0000000000000001";
    private const string Device = "device_observed_reopen_00000000000001";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-observed-reopen-http-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Old_cloud_defers_materialized_observation_without_blocking_unrelated_mutations_and_retries_after_capability_returns()
    {
        var (store, client, handler, taskId) = await CreateAsync();
        await store.PrepareProviderObservedReopensAsync(Instance, Device, "supported:test");
        await AddUnrelatedTaskAsync(store, "unrelated_old_cloud");
        handler.SupportsObserved = false;

        var first = await client.SyncAsync();
        Assert.Null(first.BlockedCode);
        Assert.Equal(1, first.PushedMutations);
        Assert.Equal(0, handler.ObservedPosts);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal(TaskCompletionState.Open, (await store.GetTaskAsync(taskId))!.CompletionState);

        await client.SyncAsync();
        Assert.Equal(0, handler.ObservedPosts);

        handler.SupportsObserved = true;
        var upgraded = await client.SyncAsync();
        Assert.Null(upgraded.BlockedCode);
        Assert.Equal(1, handler.ObservedPosts);
        Assert.Equal(0, await CountAsync(store, "SELECT count(*) FROM personal_cloud_provider_observed_reopens WHERE task_id=@task_id", taskId));
        await client.SyncAsync();
        Assert.Equal(1, handler.ObservedPosts);
    }

    [Fact]
    public async Task Definitive_unknown_operation_rejection_is_deferred_without_retry_spam_and_retries_after_new_protocol_check()
    {
        var (store, client, handler, taskId) = await CreateAsync();
        await store.PrepareProviderObservedReopensAsync(Instance, Device, "supported:test");
        await AddUnrelatedTaskAsync(store, "unrelated_rejection");
        handler.SupportsObserved = true;
        handler.RejectObserved = true;

        var rejected = await client.SyncAsync();
        Assert.Null(rejected.BlockedCode);
        Assert.Equal(1, handler.ObservedPosts);
        Assert.Contains("unrelated_rejection", handler.SingleEntityIds);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal(TaskCompletionState.Open, (await store.GetTaskAsync(taskId))!.CompletionState);

        await client.SyncAsync();
        Assert.Equal(1, handler.ObservedPosts);

        handler.RejectObserved = false;
        handler.ArtifactVersion = "test-next";
        await client.SyncAsync();
        Assert.Equal(2, handler.ObservedPosts);
        Assert.Equal(0, await CountAsync(store, "SELECT count(*) FROM personal_cloud_provider_observed_reopens WHERE task_id=@task_id", taskId));
        await client.SyncAsync();
        Assert.Equal(2, handler.ObservedPosts);
    }

    [Fact]
    public async Task Batch_stops_before_provider_observed_mutation_even_when_it_is_not_first()
    {
        var (store, client, handler, _) = await CreateAsync();
        await AddUnrelatedTaskAsync(store, "unrelated_before_observation");
        handler.SupportsObserved = true;
        handler.SupportsBatch = true;

        await client.SyncAsync();

        Assert.Contains("task.create", handler.BatchOperations);
        Assert.DoesNotContain(PersonalCloudK0Protocol.ProviderObservedReopenOperation, handler.BatchOperations);
        Assert.Equal(1, handler.ObservedPosts);
        Assert.Contains(PersonalCloudK0Protocol.ProviderObservedReopenOperation, handler.SingleOperations);
    }

    [Fact]
    public async Task Reconciles_synthetic_199_missing_sources_and_63_cancelled_attempts_and_clears_settings_requests()
    {
        var (store, _, _, _) = await CreateAsync();
        var sourceHash = new string('a', 64);
        var sourceJson = $$"""{"integrationKind":"synthetic","externalIdHash":"{{sourceHash}}","sourceUrl":null,"title":"Synthetic source","description":null,"projectName":null,"priority":null,"plannedOn":null,"plannedAt":null,"deadlineOn":null,"deadlineAt":null,"recurrenceRule":null,"refreshedAt":"2026-11-27T12:00:00.000Z"}""";
        await using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath}");
        await db.OpenAsync();
        for (var index = 0; index < 262; index++)
        {
            var taskId = $"synthetic_historical_command_{index:D3}";
            var sequence = 1000 + index;
            await using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO personal_cloud_provider_commands(instance_id,task_id,requested_seq,command,state) VALUES(@i,@t,@q,'complete','pending_desktop')";
            command.Parameters.AddWithValue("@i", Instance); command.Parameters.AddWithValue("@t", taskId); command.Parameters.AddWithValue("@q", sequence);
            await command.ExecuteNonQueryAsync();
            if (index < 199) continue;
            await using var source = db.CreateCommand();
            source.CommandText = "INSERT INTO personal_cloud_provider_sources(instance_id,task_id,source_json) VALUES(@i,@t,@j)";
            source.Parameters.AddWithValue("@i", Instance); source.Parameters.AddWithValue("@t", taskId); source.Parameters.AddWithValue("@j", sourceJson);
            await source.ExecuteNonQueryAsync();
            await using var attempt = db.CreateCommand();
            attempt.CommandText = "INSERT INTO personal_cloud_provider_attempts(claim_id,instance_id,task_id,requested_seq,device_id,account_binding,command,source_json,state) VALUES(@c,@i,@t,@q,@d,'synthetic-account','complete',@j,'cancelled')";
            attempt.Parameters.AddWithValue("@c", $"synthetic_cancelled_claim_{index:D3}"); attempt.Parameters.AddWithValue("@i", Instance);
            attempt.Parameters.AddWithValue("@t", taskId); attempt.Parameters.AddWithValue("@q", sequence); attempt.Parameters.AddWithValue("@d", Device);
            attempt.Parameters.AddWithValue("@j", sourceJson); await attempt.ExecuteNonQueryAsync();
        }

        Assert.Equal(262, await store.ReconcileUndeliverableProviderCommandsAsync(Instance, Device));
        Assert.Equal(0, await CountAsync(store, "SELECT count(*) FROM personal_cloud_provider_commands WHERE state='pending_desktop'", ""));
        Assert.Equal(262, await CountAsync(store, "SELECT count(*) FROM personal_cloud_provider_command_cancellations", ""));
        Assert.Empty(await store.GetPendingPersonalCloudProviderCommandsAsync(Instance));
        Assert.Empty(await store.GetPersonalCloudProviderRequestStatusAsync(Instance));
    }

    [Fact]
    public async Task Lost_acknowledgement_for_seed_completion_preserves_original_idempotency_envelope()
    {
        var (store, client, handler, taskId) = await CreateAsync();
        await using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath}");
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = """
            UPDATE personal_cloud_outbox SET acknowledged_at=NULL,server_change_seq=NULL
              WHERE entity_id=@t AND operation='task.complete';
            INSERT INTO personal_cloud_initial_sync(instance_id,device_id,seed_end_sequence,phase)
              SELECT @i,@d,MAX(device_sequence),'Complete' FROM personal_cloud_outbox
              WHERE instance_id=@i AND device_id=@d AND entity_id=@t AND operation='task.complete';
            """;
        command.Parameters.AddWithValue("@i", Instance);
        command.Parameters.AddWithValue("@d", Device);
        command.Parameters.AddWithValue("@t", taskId);
        await command.ExecuteNonQueryAsync();
        var original = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device),
            item => item.Operation == PersonalCloudK0Protocol.CompleteTaskOperation);

        await store.ReconcileUndeliverableProviderCommandsAsync(Instance, Device);

        var replay = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device),
            item => item.MutationId == original.MutationId);
        Assert.Equal(original.Operation, replay.Operation);
        Assert.Equal(original.CanonicalJson, replay.CanonicalJson);
        Assert.Equal(original.CanonicalHash, replay.CanonicalHash);
        Assert.Empty(await store.GetPendingPersonalCloudProviderCommandsAsync(Instance));
        Assert.Null((await client.SyncAsync()).BlockedCode);
        Assert.Equal(1, handler.SingleOperations.Count(operation => operation == PersonalCloudK0Protocol.CompleteTaskOperation));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Fact]
    public async Task Fresh_live_source_without_attempt_remains_deliverable_and_old_cancelled_attempt_does_not_cancel_new_sequence()
    {
        var (store, _, _, _) = await CreateAsync();
        var taskId = "synthetic_fresh_provider_command";
        var sourceHash = new string('b', 64);
        var sourceJson = $$"""{"integrationKind":"synthetic","externalIdHash":"{{sourceHash}}","sourceUrl":null,"title":"Synthetic fresh source","description":null,"projectName":null,"priority":null,"plannedOn":null,"plannedAt":null,"deadlineOn":null,"deadlineAt":null,"recurrenceRule":null,"refreshedAt":"2026-11-27T12:00:00.000Z"}""";
        await using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath}");
        await db.OpenAsync();
        await using (var setup = db.CreateCommand())
        {
            setup.CommandText = """
                INSERT INTO personal_cloud_provider_sources(instance_id,task_id,source_json) VALUES(@i,@t,@j);
                INSERT INTO personal_cloud_provider_commands(instance_id,task_id,requested_seq,command,state) VALUES(@i,@t,900,'complete','pending_desktop');
                INSERT INTO personal_cloud_provider_attempts(claim_id,instance_id,task_id,requested_seq,device_id,account_binding,command,source_json,state)
                  VALUES('synthetic_superseded_attempt',@i,@t,899,@d,'synthetic-account','complete',@j,'cancelled');
                """;
            setup.Parameters.AddWithValue("@i", Instance); setup.Parameters.AddWithValue("@t", taskId);
            setup.Parameters.AddWithValue("@d", Device); setup.Parameters.AddWithValue("@j", sourceJson);
            await setup.ExecuteNonQueryAsync();
        }
        Assert.Equal(0, await store.ReconcileUndeliverableProviderCommandsAsync(Instance, Device));
        Assert.Equal(900, Assert.Single(await store.GetPendingPersonalCloudProviderCommandsAsync(Instance)).RequestedChangeSeq);
        Assert.Single(await store.GetPersonalCloudProviderRequestStatusAsync(Instance));
        await using var attempt = db.CreateCommand();
        attempt.CommandText = "INSERT INTO personal_cloud_provider_attempts(claim_id,instance_id,task_id,requested_seq,device_id,account_binding,command,source_json,state) VALUES('synthetic_current_cancelled_attempt',@i,@t,900,@d,'synthetic-account','complete',@j,'cancelled')";
        attempt.Parameters.AddWithValue("@i", Instance); attempt.Parameters.AddWithValue("@t", taskId); attempt.Parameters.AddWithValue("@d", Device); attempt.Parameters.AddWithValue("@j", sourceJson);
        await attempt.ExecuteNonQueryAsync();
        Assert.Equal(1, await store.ReconcileUndeliverableProviderCommandsAsync(Instance, Device));
        Assert.Empty(await store.GetPendingPersonalCloudProviderCommandsAsync(Instance));
    }

    [Fact]
    public async Task Old_cloud_defers_cancellation_without_spam_then_sends_exact_sequence_after_capability_check()
    {
        var (store, client, handler, _) = await CreateAsync();
        const string taskId = "synthetic_missing_source_cancel";
        await using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            await db.OpenAsync();
            await using var insert = db.CreateCommand();
            insert.CommandText = "INSERT INTO personal_cloud_provider_commands(instance_id,task_id,requested_seq,command,state) VALUES(@i,@t,701,'complete','pending_desktop')";
            insert.Parameters.AddWithValue("@i", Instance); insert.Parameters.AddWithValue("@t", taskId);
            await insert.ExecuteNonQueryAsync();
        }
        await AddUnrelatedTaskAsync(store, "unrelated_while_cancel_deferred");

        Assert.Null((await client.SyncAsync()).BlockedCode);
        Assert.Contains("unrelated_while_cancel_deferred", handler.SingleEntityIds);
        Assert.Equal(0, handler.CancellationPosts);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Empty(await store.GetPersonalCloudProviderRequestStatusAsync(Instance));
        Assert.Null((await client.SyncAsync()).BlockedCode);
        Assert.Equal(0, handler.CancellationPosts);

        handler.SupportsCancellation = true;
        Assert.Null((await client.SyncAsync()).BlockedCode);
        Assert.Equal(1, handler.CancellationPosts);
        Assert.Equal(701, handler.CancelledSequence);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        await client.SyncAsync();
        Assert.Equal(1, handler.CancellationPosts);
    }

    [Fact]
    public async Task Historical_seed_completion_never_falls_back_to_task_complete_on_old_cloud()
    {
        var (store, client, handler, _) = await CreateAsync();
        var task = new TaskItem
        {
            Id = "synthetic_historical_seed_task", Title = "Synthetic historical completion", SpaceId = SpaceIds.Default,
            CompletionState = TaskCompletionState.Completed, CompletedAt = DateTimeOffset.Parse("2026-11-20T12:00:00Z"),
            CreatedAt = DateTimeOffset.Parse("2026-11-19T12:00:00Z"),
        };
        await store.UpsertTaskAsync(task);
        await store.StageUnpublishedTasksForPersonalCloudAsync(Instance, Device);
        Assert.Contains(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device), item => item.Operation == PersonalCloudK0Protocol.TaskHistoricalCompletionImportOperation);
        Assert.DoesNotContain(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device), item => item.Operation == PersonalCloudK0Protocol.CompleteTaskOperation);
        await AddUnrelatedTaskAsync(store, "unrelated_while_history_deferred");

        Assert.Equal("upgrade_required", (await client.SyncAsync()).BlockedCode);
        Assert.True((await client.SyncAsync()).CanContinueOtherWork);
        Assert.Equal(0, handler.HistoricalCompletionPosts);
        Assert.Contains("unrelated_while_history_deferred", handler.SingleEntityIds);
        Assert.True(await store.HasDeferredHistoricalCompletionImportsAsync(Instance));
        Assert.DoesNotContain(PersonalCloudK0Protocol.CompleteTaskOperation, handler.SingleOperations);
        Assert.Equal(TaskCompletionState.Completed, (await store.GetTaskAsync(task.Id))!.CompletionState);

        Assert.Equal("upgrade_required", (await client.SyncAsync()).BlockedCode);
        Assert.Equal(0, handler.HistoricalCompletionPosts);

        handler.SupportsHistoricalCompletionImport = true;
        handler.ArtifactVersion = "test-v2";
        Assert.Null((await client.SyncAsync()).BlockedCode);
        Assert.Equal(1, handler.HistoricalCompletionPosts);
        Assert.DoesNotContain(PersonalCloudK0Protocol.CompleteTaskOperation, handler.SingleOperations);
        Assert.False(await store.HasDeferredHistoricalCompletionImportsAsync(Instance));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Fact]
    public async Task Definitive_unsupported_historical_operation_is_deferred_and_retried_without_blocking_unrelated_mutations()
    {
        var (store, client, handler, _) = await CreateAsync();
        var task = new TaskItem
        {
            Id = "synthetic_historical_rejection_task", Title = "Synthetic deferred history", SpaceId = SpaceIds.Default,
            CompletionState = TaskCompletionState.Completed, CompletedAt = DateTimeOffset.Parse("2026-11-20T12:00:00Z"),
            CreatedAt = DateTimeOffset.Parse("2026-11-19T12:00:00Z"),
        };
        await store.UpsertTaskAsync(task);
        await store.StageUnpublishedTasksForPersonalCloudAsync(Instance, Device);
        await AddUnrelatedTaskAsync(store, "unrelated_after_historical_rejection");
        handler.SupportsHistoricalCompletionImport = true;
        handler.SupportsBatch = true;
        handler.RejectHistoricalCompletionAsUnknown = true;

        var rejected = await client.SyncAsync();
        Assert.NotNull(rejected.BlockedCode);
        Assert.True(rejected.CanContinueOtherWork);
        Assert.Equal(1, handler.HistoricalCompletionPosts);
        Assert.True(await store.HasDeferredHistoricalCompletionImportsAsync(Instance));
        Assert.Contains("unrelated_after_historical_rejection", handler.SingleEntityIds.Concat(handler.BatchEntityIds));
        Assert.DoesNotContain(PersonalCloudK0Protocol.TaskHistoricalCompletionImportOperation, handler.BatchOperations);
        Assert.DoesNotContain(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device), item => item.Operation == PersonalCloudK0Protocol.TaskHistoricalCompletionImportOperation);

        await client.SyncAsync();
        Assert.Equal(1, handler.HistoricalCompletionPosts);

        handler.RejectHistoricalCompletionAsUnknown = false;
        handler.ArtifactVersion = "test-history-retry";
        Assert.Null((await client.SyncAsync()).BlockedCode);
        Assert.Equal(2, handler.HistoricalCompletionPosts);
        Assert.Equal(1, handler.AcceptedHistoricalCompletionPosts);
        Assert.False(await store.HasDeferredHistoricalCompletionImportsAsync(Instance));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.DoesNotContain(PersonalCloudK0Protocol.CompleteTaskOperation, handler.SingleOperations);
    }

    [Fact]
    public async Task Seed_sequence_without_exact_server_change_does_not_cancel_a_later_web_completion()
    {
        var (store, _, _, taskId) = await CreateAsync();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE personal_cloud_outbox SET server_change_seq=NULL WHERE entity_id='{taskId}' AND operation='task.complete'; UPDATE personal_cloud_provider_commands SET requested_seq=900,state='pending_desktop' WHERE task_id='{taskId}'; INSERT INTO personal_cloud_initial_sync(instance_id,device_id,seed_end_sequence,phase) VALUES('{Instance}','{Device}',2,'uploading');";
        await command.ExecuteNonQueryAsync();

        Assert.Equal(0, await store.ReconcileUndeliverableProviderCommandsAsync(Instance, Device));
        Assert.Equal(1, await CountAsync(store, "SELECT count(*) FROM personal_cloud_provider_commands WHERE task_id=@task_id AND state='pending_desktop'", taskId));
    }

    [Fact]
    public async Task Later_web_completion_after_older_local_intent_confirms_with_mixed_timestamps()
    {
        var (store, _, _, taskId) = await CreateAsync();
        var current = DateTimeOffset.UtcNow.AddSeconds(-5);
        var old = current.AddMinutes(-1);
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE personal_cloud_provider_commands SET requested_seq=3 WHERE instance_id=@i AND task_id=@t;
            INSERT INTO personal_cloud_outbox(mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at,acknowledged_at,server_change_seq)
              VALUES('web_completion_current','tasks',@i,@d,4,'externalCommand.confirm',@t,@current_json,@hash,@current_at,@current_at,4);
            INSERT INTO provider_completion_confirmations(confirmation_id,instance_id,task_id,requested_change_seq,confirmed_at) VALUES('web_completion_current',@i,@t,3,@current_at);
            INSERT INTO personal_cloud_outbox(mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at,acknowledged_at,server_change_seq)
              VALUES('web_completion_old_text','tasks',@i,@d,5,'externalCommand.confirm',@t,@old_json,@old_hash,@old_at,@old_at,5);
            INSERT INTO provider_completion_confirmations(confirmation_id,instance_id,task_id,requested_change_seq,confirmed_at) VALUES('web_completion_old_text',@i,@t,3,@old_at);
            UPDATE personal_cloud_device_state SET next_device_sequence=6 WHERE instance_id=@i AND device_id=@d;
            """;
        command.Parameters.AddWithValue("@i", Instance); command.Parameters.AddWithValue("@d", Device); command.Parameters.AddWithValue("@t", taskId);
        command.Parameters.AddWithValue("@current_json", JsonSerializer.Serialize(new { payload = new { requestedChangeSeq = 3, resolvedAt = current.ToString("O") } }));
        command.Parameters.AddWithValue("@hash", new string('b', 64)); command.Parameters.AddWithValue("@current_at", current.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@old_json", JsonSerializer.Serialize(new { payload = new { requestedChangeSeq = 3, resolvedAt = old.ToString("O") } }));
        command.Parameters.AddWithValue("@old_hash", new string('c', 64)); command.Parameters.AddWithValue("@old_at", old.ToString("O"));
        await command.ExecuteNonQueryAsync();

        await store.PrepareProviderObservedReopensAsync(Instance, Device, "supported:test");
        Assert.Contains(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device),
            entry => entry.Operation == PersonalCloudK0Protocol.ProviderObservedReopenOperation && entry.EntityId == taskId);
    }

    [Fact]
    public async Task Deferred_historical_parent_rebases_seed_version_and_rebinds_after_repair()
    {
        var (store, client, handler, _) = await CreateAsync();
        const string parent = "synthetic_history_parent";
        const string child = "synthetic_history_child";
        await store.UpsertTaskAsync(new TaskItem { Id = parent, Title = "Parent", SpaceId = SpaceIds.Default });
        await store.UpsertTaskAsync(new TaskItem { Id = child, Title = "Completed child", SpaceId = SpaceIds.Default,
            ParentId = parent, CompletionState = TaskCompletionState.Completed,
            CompletedAt = DateTimeOffset.Parse("2026-11-20T12:00:00Z") });
        await store.BeginPersonalCloudInitialSyncAsync(Instance, Device);
        await store.StageUnpublishedTasksForPersonalCloudAsync(Instance, Device);
        var seedEnd = await ScalarLongAsync(store, $"SELECT seed_end_sequence FROM personal_cloud_initial_sync WHERE instance_id='{Instance}' AND device_id='{Device}'");

        var result = await client.SyncAsync();
        Assert.Equal("upgrade_required", result.BlockedCode);
        Assert.True(result.CanContinueOtherWork);
        Assert.True(await store.HasDeferredHistoricalCompletionImportsAsync(Instance));
        Assert.Equal(2, await ScalarLongAsync(store, $"SELECT entity_version FROM personal_cloud_entity_versions WHERE instance_id='{Instance}' AND product='tasks' AND entity_id='{child}'"));
        Assert.Equal(seedEnd - 1, await ScalarLongAsync(store, $"SELECT seed_end_sequence FROM personal_cloud_initial_sync WHERE instance_id='{Instance}' AND device_id='{Device}'"));
        Assert.DoesNotContain(PersonalCloudK0Protocol.CompleteTaskOperation, handler.SingleOperations);

        const string newDevice = "device_repaired_00000000000001";
        await store.PrepareDeferredHistoricalCompletionImportsAsync(Instance, newDevice, "test-v2");
        var pending = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, newDevice));
        Assert.Equal(PersonalCloudK0Protocol.TaskHistoricalCompletionImportOperation, pending.Operation);
        using var mutation = JsonDocument.Parse(pending.CanonicalJson);
        Assert.Equal(2, mutation.RootElement.GetProperty("baseEntityVersion").GetInt64());
        Assert.False(await store.HasDeferredHistoricalCompletionImportsAsync(Instance));
    }

    [Fact]
    public async Task Superseded_cancellation_is_retired_and_unrelated_mutation_progresses()
    {
        var (store, client, handler, _) = await CreateAsync();
        const string taskId = "synthetic_superseded_cancellation";
        await using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            await db.OpenAsync();
            await using var insert = db.CreateCommand();
            insert.CommandText = "INSERT INTO personal_cloud_provider_commands(instance_id,task_id,requested_seq,command,state) VALUES(@i,@t,703,'complete','pending_desktop')";
            insert.Parameters.AddWithValue("@i", Instance); insert.Parameters.AddWithValue("@t", taskId);
            await insert.ExecuteNonQueryAsync();
        }
        handler.SupportsCancellation = true;
        handler.RejectCancellationAsSuperseded = true;
        Assert.Equal(1, await store.ReconcileUndeliverableProviderCommandsAsync(Instance, Device));
        await store.PrepareProviderCommandCancellationsAsync(Instance, Device, handler.ArtifactVersion);
        await AddUnrelatedTaskAsync(store, "unrelated_after_superseded_cancellation");

        Assert.Null((await client.SyncAsync()).BlockedCode);
        Assert.Equal(1, handler.CancellationPosts);
        Assert.Contains("unrelated_after_superseded_cancellation", handler.SingleEntityIds);
        Assert.False(await store.HasDeferredProviderCommandCancellationsAsync(Instance));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        await client.SyncAsync();
        Assert.Equal(1, handler.CancellationPosts);
    }

    [Fact]
    public async Task Started_cloud_claim_is_reported_and_cancellation_is_not_retried_silently()
    {
        var (store, client, handler, _) = await CreateAsync();
        const string taskId = "synthetic_started_cloud_claim";
        await using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            await db.OpenAsync();
            await using var insert = db.CreateCommand();
            insert.CommandText = "INSERT INTO personal_cloud_provider_commands(instance_id,task_id,requested_seq,command,state) VALUES(@i,@t,702,'complete','pending_desktop')";
            insert.Parameters.AddWithValue("@i", Instance); insert.Parameters.AddWithValue("@t", taskId);
            await insert.ExecuteNonQueryAsync();
        }
        handler.SupportsCancellation = true;
        handler.RejectCancellationAsBusy = true;
        Assert.Equal(1, await store.ReconcileUndeliverableProviderCommandsAsync(Instance, Device));
        await store.PrepareProviderCommandCancellationsAsync(Instance, Device, handler.ArtifactVersion);
        await AddUnrelatedTaskAsync(store, "unrelated_while_busy_cancellation");

        var deferred = await client.SyncAsync();
        Assert.Null(deferred.BlockedCode);
        Assert.Equal(1, handler.CancellationPosts);
        Assert.Equal(1, handler.CreatePosts);
        Assert.Empty(await store.GetPendingPersonalCloudProviderCommandsAsync(Instance));
        Assert.Empty(await store.GetPersonalCloudProviderRequestStatusAsync(Instance));
        Assert.DoesNotContain(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device), item => item.Operation == PersonalCloudK0Protocol.ExternalCommandCancelOperation);
        Assert.True(await store.HasDeferredProviderCommandCancellationsAsync(Instance));

        await client.SyncAsync();
        Assert.Equal(1, handler.CancellationPosts);
    }

    [Fact]
    public async Task Deferred_provider_open_survives_workspace_baseline_and_change_feed_replay()
    {
        var (store, _, _, taskId) = await CreateAsync();
        var completedAt = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        var taskJson = PersonalCloudK0Codec.Serialize(PersonalCloudWorkspaceTests.TaskRecord(taskId, 2, null))
            .Replace("\"completionState\":\"open\"", "\"completionState\":\"completed\"", StringComparison.Ordinal)
            .Replace("\"completedAt\":null", $"\"completedAt\":\"{completedAt}\"", StringComparison.Ordinal);
        using var taskDocument = JsonDocument.Parse(taskJson);
        var task = taskDocument.RootElement.Clone();
        var spaces = new[] { PersonalCloudWorkspaceTests.Space() };
        var snapshot = new
        {
            spaces, projects = Array.Empty<object>(), tasks = new[] { task }, tombstones = Array.Empty<object>(),
            labels = Array.Empty<object>(), taskLabels = Array.Empty<object>(), sourceDescriptors = Array.Empty<object>(),
            externalCommands = Array.Empty<object>(), catalog = Array.Empty<object>(), intents = Array.Empty<object>(), routes = Array.Empty<object>(),
        };
        var bootstrap = PersonalCloudK0Codec.Serialize(new { protocolVersion = 1, product = "tasks", checkpoint = 5, snapshot });
        await store.ReplacePersonalCloudWorkspaceFromBootstrapAsync(Instance, Device, bootstrap);
        Assert.Equal(TaskCompletionState.Open, (await store.GetTaskAsync(taskId))!.CompletionState);
        Assert.Null((await store.GetTaskAsync(taskId))!.CompletedAt);

        var page = PersonalCloudWorkspaceTests.Page(6, spaces: spaces, tasks: [task]);
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, page);
        Assert.Equal(TaskCompletionState.Open, (await store.GetTaskAsync(taskId))!.CompletionState);
        Assert.Null((await store.GetTaskAsync(taskId))!.CompletedAt);
    }

    [Fact]
    public async Task Explicit_completion_supersedes_unmaterialized_provider_observation()
    {
        var (store, _, _, taskId) = await CreateAsync();
        await store.CompleteTaskWithPersonalCloudIntentAsync(taskId, DateTimeOffset.UtcNow, 2, Instance, Device);
        await store.PrepareProviderObservedReopensAsync(Instance, Device, "supported:artifact-v1");

        Assert.Equal(TaskCompletionState.Completed, (await store.GetTaskAsync(taskId))!.CompletionState);
        Assert.Equal(0, await CountAsync(store, "SELECT count(*) FROM personal_cloud_provider_observed_reopens WHERE task_id=@task_id", taskId));
        Assert.DoesNotContain(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device),
            entry => entry.Operation == PersonalCloudK0Protocol.ProviderObservedReopenOperation);
    }

    private async Task<(SqliteTaskStore Store, PersonalCloudSyncClient Client, TestHandler Handler, string TaskId)> CreateAsync()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"));
        await store.InitializeAsync();
        const string taskId = "task_observed_reopen_000000000001";
        var create = await store.CreateTaskWithPersonalCloudIntentAsync(new TaskItem
        {
            Id = taskId, Title = "Synthetic provider-linked task", SpaceId = SpaceIds.Default,
        }, Instance, Device);
        await store.AcknowledgePersonalCloudMutationAsync(Instance, Device, PersonalCloudK0Codec.Serialize(new K0MutationReceipt
        {
            ProtocolVersion = 1, Product = "tasks", MutationId = create.MutationId, Outcome = "accepted",
            EntityId = taskId, EntityVersion = 1, ChangeSeq = 1, ServerAcceptedAt = "2026-11-27T12:00:00.000Z",
        }));
        var completedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var complete = await store.CompleteTaskWithPersonalCloudIntentAsync(taskId, completedAt, 1, Instance, Device);
        await store.AcknowledgePersonalCloudMutationAsync(Instance, Device, PersonalCloudK0Codec.Serialize(new K0MutationReceipt
        {
            ProtocolVersion = 1, Product = "tasks", MutationId = complete.MutationId, Outcome = "accepted",
            EntityId = taskId, EntityVersion = 2, ChangeSeq = 2,
            ServerAcceptedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
        }));
        var confirmedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO personal_cloud_provider_sources(instance_id,task_id,source_json) VALUES(@i,@t,@source_json);
                INSERT INTO personal_cloud_provider_commands(instance_id,task_id,requested_seq,command,state) VALUES(@i,@t,2,'complete','confirmed');
                INSERT INTO personal_cloud_outbox
                    (mutation_id,product,instance_id,device_id,device_sequence,operation,entity_id,canonical_json,canonical_hash,created_at,acknowledged_at,server_change_seq)
                VALUES('confirmation_http_test','tasks',@i,@d,3,'externalCommand.confirm',@t,@result_json,@hash,@confirmed,@confirmed,3);
                INSERT INTO provider_completion_confirmations(confirmation_id,instance_id,task_id,requested_change_seq,confirmed_at)
                VALUES('confirmation_http_test',@i,@t,2,@confirmed);
                UPDATE personal_cloud_device_state SET next_device_sequence=4 WHERE instance_id=@i AND device_id=@d;
                UPDATE tasks SET completion_state='open',completed_at=NULL WHERE id=@t;
                INSERT INTO personal_cloud_provider_observed_reopens(instance_id,task_id,observation_id,observed_at) VALUES(@i,@t,'mutation_observed_http_test',@observed);
                """;
            command.Parameters.AddWithValue("@i", Instance);
            command.Parameters.AddWithValue("@d", Device);
            command.Parameters.AddWithValue("@t", taskId);
            command.Parameters.AddWithValue("@source_json", $$"""{"integrationKind":"synthetic","externalIdHash":"{{new string('c', 64)}}","sourceUrl":null,"title":"Synthetic linked source","description":null,"projectName":null,"priority":null,"plannedOn":null,"plannedAt":null,"deadlineOn":null,"deadlineAt":null,"recurrenceRule":null,"refreshedAt":"2026-11-27T12:00:00.000Z"}""");
            command.Parameters.AddWithValue("@confirmed", confirmedAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@result_json", JsonSerializer.Serialize(new
            {
                payload = new { requestedChangeSeq = 2, resolvedAt = confirmedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") },
            }));
            command.Parameters.AddWithValue("@hash", new string('a', 64));
            command.Parameters.AddWithValue("@observed", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync();
        }
        var handler = new TestHandler();
        var http = new HttpClient(handler);
        var identity = new PersonalCloudDeviceIdentityClient(http, new InMemoryCredentialStore());
        var origin = new Uri("https://synthetic-observed-reopen.example.test/");
        await identity.PairAsync(origin, "synthetic-pair-secret-123456789", "Synthetic desktop", false);
        return (store, new PersonalCloudSyncClient(http, identity, store, new()
        {
            ServiceOrigin = origin, InstanceId = Instance, DeviceId = Device, ArtifactVersion = "test", ConnectedAt = DateTimeOffset.UtcNow,
        }), handler, taskId);
    }

    private static Task<PersonalCloudK1OutboxEntry> AddUnrelatedTaskAsync(SqliteTaskStore store, string id) =>
        store.CreateTaskWithPersonalCloudIntentAsync(new TaskItem { Id = id, Title = "Synthetic unrelated task", SpaceId = SpaceIds.Default }, Instance, Device);

    private async Task<int> CountAsync(SqliteTaskStore store, string sql, string taskId)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@task_id", taskId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<long> ScalarLongAsync(SqliteTaskStore store, string sql)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public void Dispose() => TestDirectory.Delete(_directory);

    private sealed class TestHandler : HttpMessageHandler
    {
        private long _changeSequence = 1;
        public string ArtifactVersion { get; set; } = "test";
        public bool SupportsObserved { get; set; }
        public bool SupportsBatch { get; set; }
        public bool SupportsCancellation { get; set; }
        public bool SupportsHistoricalCompletionImport { get; set; }
        public bool RejectCancellationAsBusy { get; set; }
        public bool RejectCancellationAsSuperseded { get; set; }
        public bool RejectHistoricalCompletionAsUnknown { get; set; }
        public bool RejectObserved { get; set; }
        public int ObservedPosts { get; private set; }
        public int CreatePosts { get; private set; }
        public int CancellationPosts { get; private set; }
        public long CancelledSequence { get; private set; }
        public int HistoricalCompletionPosts { get; private set; }
        public int AcceptedHistoricalCompletionPosts { get; private set; }
        public List<string> BatchOperations { get; } = [];
        public List<string> BatchEntityIds { get; } = [];
        public List<string> SingleOperations { get; } = [];
        public List<string> SingleEntityIds { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/auth/pair") return Json(HttpStatusCode.Created, $$"""{"protocolVersion":1,"instanceId":"{{Instance}}","deviceId":"{{Device}}","refreshCredential":"synthetic-refresh-secret-123456789","capabilities":["desktop_sync"],"refreshExpiresAt":"2099-01-01T00:00:00Z"}""");
            if (path == "/auth/device/token") return Json(HttpStatusCode.OK, """{"protocolVersion":1,"accessToken":"synthetic-access-secret-123456789","expiresAt":"2099-01-01T00:10:00Z","capabilities":["desktop_sync"]}""");
            if (path == "/health")
            {
                var capabilities = new Dictionary<string, object>();
                if (SupportsObserved) capabilities["providerObservedReopen"] = new { protocolVersion = 1 };
                if (SupportsBatch) capabilities["mutationBatch"] = new { protocolVersion = 1, path = "/v1/tasks/mutations/batch", maxMutations = 2, maxRequestUtf8Bytes = 16384 };
                if (SupportsCancellation) capabilities["providerCommandCancellation"] = new { protocolVersion = 1 };
                if (SupportsHistoricalCompletionImport)
                {
                    capabilities["historicalCompletionImport"] = new { protocolVersion = 1 };
                    capabilities["workspace"] = new { protocolVersion = 1, path = "/v1/tasks/mutations", schemaVersion = 27 };
                }
                var schemaVersion = SupportsCancellation || SupportsHistoricalCompletionImport ? 27 : 26;
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { status = "ok", protocolVersion = 1, product = "tasks", artifactVersion = ArtifactVersion, schemaVersion, capabilities }));
            }
            if (path == "/sync/bootstrap") return Json(HttpStatusCode.OK, """{"protocolVersion":1,"product":"tasks","checkpoint":0,"snapshot":{"spaces":[],"projects":[],"tasks":[],"labels":[],"taskLabels":[],"tombstones":[],"sourceDescriptors":[],"externalCommands":[],"scopes":[],"catalog":[],"intents":[],"routes":[]}}""");
            if (path == "/sync/changes") return Json(HttpStatusCode.OK, """{"protocolVersion":1,"product":"tasks","changes":[],"nextCheckpoint":0}""");
            if (path == "/sync/checkpoints") return Json(HttpStatusCode.OK, $$"""{"protocolVersion":1,"product":"tasks","deviceId":"{{Device}}","acknowledgedCheckpoint":0}""");
            if (path is "/v1/tasks/mutations" or "/v1/tasks/mutations/batch")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (path.EndsWith("/batch", StringComparison.Ordinal))
                {
                    var operations = body.RootElement.GetProperty("mutations").EnumerateArray().ToArray();
                    foreach (var mutation in operations)
                    {
                        BatchOperations.Add(mutation.GetProperty("operation").GetString()!);
                        BatchEntityIds.Add(mutation.GetProperty("entityId").GetString()!);
                    }
                    var receipts = operations.Select(Accept).ToArray();
                    return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { protocolVersion = 1, product = "tasks", instanceId = Instance, receipts, processedCount = receipts.Length, stoppedAt = (int?)null, error = (object?)null }));
                }
                var operation = body.RootElement.GetProperty("operation").GetString()!;
                SingleOperations.Add(operation);
                SingleEntityIds.Add(body.RootElement.GetProperty("entityId").GetString()!);
                if (operation == PersonalCloudK0Protocol.ProviderObservedReopenOperation)
                {
                    ObservedPosts++;
                    if (RejectObserved) return Json(HttpStatusCode.BadRequest, """{"error":{"code":"invalid_request","retryable":false}}""");
                }
                else if (operation == "task.create") CreatePosts++;
                else if (operation == PersonalCloudK0Protocol.ExternalCommandCancelOperation)
                {
                    CancellationPosts++;
                    CancelledSequence = body.RootElement.GetProperty("payload").GetProperty("requestedChangeSeq").GetInt64();
                    if (RejectCancellationAsBusy) return Json(HttpStatusCode.Conflict, """{"error":{"code":"command_busy","retryable":false}}""");
                    if (RejectCancellationAsSuperseded) return Json(HttpStatusCode.Conflict, """{"error":{"code":"command_superseded","retryable":false}}""");
                }
                else if (operation == PersonalCloudK0Protocol.TaskHistoricalCompletionImportOperation)
                {
                    HistoricalCompletionPosts++;
                    if (RejectHistoricalCompletionAsUnknown) return Json(HttpStatusCode.BadRequest, """{"error":{"code":"invalid_request","retryable":false}}""");
                    AcceptedHistoricalCompletionPosts++;
                }
                return Json(HttpStatusCode.Created, JsonSerializer.Serialize(Accept(body.RootElement)));
            }
            throw new InvalidOperationException(path);
        }

        private object Accept(JsonElement mutation)
        {
            var operation = mutation.GetProperty("operation").GetString();
            var sequence = checked(++_changeSequence);
            var entityVersion = operation switch
            {
                PersonalCloudK0Protocol.CompleteTaskOperation or PersonalCloudK0Protocol.ReopenTaskOperation or
                    PersonalCloudK0Protocol.ProviderObservedReopenOperation => mutation.GetProperty("baseEntityVersion").GetInt64() + 1,
                PersonalCloudK0Protocol.TaskHistoricalCompletionImportOperation =>
                    mutation.GetProperty("baseEntityVersion").ValueKind == JsonValueKind.Number
                        ? mutation.GetProperty("baseEntityVersion").GetInt64() + 1 : 1,
                "task.setParent" => mutation.GetProperty("baseEntityVersion").GetInt64() + 1,
                _ => 1,
            };
            return new
            {
                protocolVersion = 1, product = "tasks", mutationId = mutation.GetProperty("mutationId").GetString(), outcome = "accepted",
                entityId = mutation.GetProperty("entityId").GetString(), entityVersion,
                changeSeq = sequence, serverAcceptedAt = "2026-11-27T12:00:01.000Z",
            };
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
