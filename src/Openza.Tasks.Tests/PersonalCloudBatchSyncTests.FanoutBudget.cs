using System.Net;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed partial class PersonalCloudBatchSyncTests
{
    private async Task<(SqliteTaskStore Store, Handler Handler)> FanoutStoreAsync(bool guarded)
    {
        var (store, _, handler) = await CreateAsync(0);
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true);
        await store.ApplyPersonalCloudChangePageAsync(Instance, Device, PersonalCloudWorkspaceTests.Page(1,
            [PersonalCloudWorkspaceTests.Space(), PersonalCloudWorkspaceTests.Space("space_destination", name: "Destination")],
            [PersonalCloudWorkspaceTests.Project()],
            [PersonalCloudWorkspaceTests.TaskRecord(), PersonalCloudWorkspaceTests.TaskRecord("task_second"), PersonalCloudWorkspaceTests.TaskRecord("task_third")]));
        await using var sql = new SqliteConnection($"Data Source={store.DatabasePath}");
        await sql.OpenAsync();
        var mark = sql.CreateCommand();
        mark.CommandText = "UPDATE personal_cloud_workspace_capabilities SET hydrated=1 WHERE instance_id=@i";
        mark.Parameters.AddWithValue("@i", Instance); await mark.ExecuteNonQueryAsync();
        handler.SchemaVersion = 25;
        handler.CurrentCheckpoint = 1;
        handler.CapabilityOverride = "{\"workspace\":{\"protocolVersion\":1,\"path\":\"/v1/tasks/mutations\",\"schemaVersion\":25},\"providerCatalog\":{\"protocolVersion\":1,\"path\":\"/v1/provider/catalog\",\"intentsPath\":\"/v1/provider/intents\"}" +
            (guarded ? ",\"fanoutBudget\":{\"protocolVersion\":1,\"requestHeader\":\"x-openza-fanout-bound\"}" : "") + "}";
        return (store, handler);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Negotiated_fanout_sends_bound_outside_exact_payload_and_legacy_retains_whole_day_charge(bool guarded)
    {
        var (store, handler) = await FanoutStoreAsync(guarded);
        await store.DeleteProjectWithPersonalCloudIntentAsync("project_test", Instance, Device);
        var original = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal(3, await store.GetPersonalCloudFanoutBoundAsync(original));
        var policy = new PersonalCloudDispatchPolicy(Path.Combine(_directory, "fanout-scaled"), resumeJitter: () => TimeSpan.Zero);
        var client = await CreateClientAsync(store, handler, policy);
        Assert.Equal(1, (await client.SyncAsync()).PushedMutations);
        Assert.Equal(original.CanonicalJson, Assert.Single(handler.AttemptBodies));
        Assert.Equal(guarded ? "3" : null, Assert.Single(handler.FanoutBounds));
        Assert.Equal(guarded ? 136 : 45000, (await policy.GetStatusAsync(new("https://synthetic.example.test/"))).ReservedWriteUnits);
        Assert.Equal(1, handler.ChangeRequests);
        Assert.Equal(1, handler.CheckpointRequests);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Fact]
    public async Task Stale_guarded_bound_pulls_then_reestimates_after_restart_without_replacing_ID_payload_or_FIFO()
    {
        var (store, handler) = await FanoutStoreAsync(true);
        await store.DeleteProjectWithPersonalCloudIntentAsync("project_test", Instance, Device);
        var original = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        handler.FanoutRejections = 1;
        handler.OnChanges = () => store.UpsertTaskAsync(new() { Id = "local_new_observation", Title = "Synthetic observed growth" });
        var clock = new QuotaClock();
        var directory = Path.Combine(_directory, "fanout-stale");
        PersonalCloudDispatchPolicy Policy() => new(directory, clock: clock, resumeJitter: () => TimeSpan.Zero);
        var first = await CreateClientAsync(store, handler, Policy());
        Assert.Equal("fanout_bound_exceeded", (await first.SyncAsync()).BlockedCode);
        Assert.Equal(original, Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)));
        Assert.Equal(1, handler.ChangeRequests);
        Assert.Equal("cloud_retry_backoff", (await Policy().GetStatusAsync(new("https://synthetic.example.test/"))).Code);
        clock.Utc = clock.Utc.AddMinutes(1);
        handler.OnChanges = null;
        var second = await CreateClientAsync(store, handler, Policy());
        Assert.Equal(1, (await second.SyncAsync()).PushedMutations);
        Assert.Equal(new[] { original.CanonicalJson, original.CanonicalJson }, handler.AttemptBodies);
        Assert.Equal("3", handler.FanoutBounds[0]);
        Assert.True(long.Parse(handler.FanoutBounds[1]!) >= 5);
        Assert.Equal(2, handler.ChangeRequests);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Theory]
    [InlineData("{\"protocolVersion\":2,\"requestHeader\":\"x-openza-fanout-bound\"}")]
    [InlineData("{\"protocolVersion\":1,\"requestHeader\":\"other-header\"}")]
    [InlineData("{\"protocolVersion\":\"1\",\"requestHeader\":\"x-openza-fanout-bound\"}")]
    [InlineData("{\"protocolVersion\":1,\"extra\":true}")]
    public async Task Malformed_fanout_capability_never_grants_scaled_cost(string capability)
    {
        var handler = new Handler { CapabilityOverride = "{\"fanoutBudget\":" + capability + "}" };
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
            new PersonalCloudHealthClient(new HttpClient(handler)).InspectAsync(new("https://synthetic.example.test/")));
        Assert.Equal("health_mismatch", error.Code);
    }

    [Fact]
    public async Task Upload_hold_does_not_stop_health_token_pull_checkpoint_or_rewrite_FIFO_after_restart()
    {
        var (store, _, handler) = await CreateAsync();
        var original = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        var directory = Path.Combine(_directory, "read-through-upload-hold");
        var policy = new PersonalCloudDispatchPolicy(directory, new(3000, 100, 25), resumeJitter: () => TimeSpan.Zero);
        var client = await CreateClientAsync(store, handler, policy);
        var result = await client.SyncAsync();
        Assert.Equal("local_operation_budget_exceeded", result.BlockedCode);
        Assert.Equal(0, result.PushedMutations);
        Assert.Equal(0, handler.BatchPosts + handler.SinglePosts);
        Assert.Equal(1, handler.ChangeRequests);
        Assert.Equal(1, handler.CheckpointRequests);
        Assert.Equal(original, await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        var restartedPolicy = new PersonalCloudDispatchPolicy(directory, new(3000, 100, 25), resumeJitter: () => TimeSpan.Zero);
        var restarted = await CreateClientAsync(store, handler, restartedPolicy);
        Assert.Equal("local_operation_budget_exceeded", (await restarted.SyncAsync()).BlockedCode);
        Assert.Equal(2, handler.ChangeRequests);
        Assert.Equal(original, await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.False((await restartedPolicy.GetStatusAsync(new("https://synthetic.example.test/"))).BlocksReads);
    }

    [Fact]
    public async Task Single_attempt_permission_sends_only_exact_FIFO_head_and_keeps_later_entries_pending()
    {
        var (store, _, handler) = await CreateAsync();
        var original = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        var policy = new PersonalCloudDispatchPolicy(Path.Combine(_directory, "single-attempt"), new(3000, 100, 25), resumeJitter: () => TimeSpan.Zero);
        var connection = new PersonalCloudConnectionProfile { ServiceOrigin = new("https://synthetic.example.test/"), InstanceId = Instance,
            DeviceId = Device, ArtifactVersion = "synthetic", ConnectedAt = DateTimeOffset.UtcNow };
        await policy.AuthorizeSingleAttemptAsync(connection, original[0], PersonalCloudDispatchPolicy.ExactEnvelopeDigest(original[0].CanonicalJson));
        var client = await CreateClientAsync(store, handler, policy);
        Assert.Equal(1, (await client.SyncAsync()).PushedMutations);
        Assert.Equal(1, handler.SinglePosts);
        Assert.Equal(0, handler.BatchPosts);
        Assert.Equal(original[0].CanonicalJson, Assert.Single(handler.Envelopes));
        Assert.Equal(original.Skip(1), await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal("local_budget_exceeded", (await client.SyncAsync()).BlockedCode);
        Assert.Equal(1, handler.SinglePosts);
    }
}
