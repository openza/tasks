using System.Text.Json;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed partial class PersonalCloudBatchSyncTests
{
    private const string CatchupCapability = """{"catchupStatus":{"protocolVersion":1,"path":"/sync/catchup-status","staleAfterSeconds":900}}""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Captured_backlog_needs_fresh_busy_ACK_before_upload_while_pulls_and_exact_queue_survive(bool rejectedCounter)
    {
        var (store, _, handler) = await CreateAsync();
        handler.SchemaVersion = 26; handler.CapabilityOverride = CatchupCapability;
        await store.BeginPersonalCloudInitialSyncAsync(Instance, Device);
        var original = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        var clock = new QuotaClock();
        var policy = new PersonalCloudDispatchPolicy(Path.Combine(_directory, "catchup-ack-gate"), clock: clock, resumeJitter: () => TimeSpan.Zero);
        handler.CatchupServerSequence = rejectedCounter ? 8000 : 0;
        handler.CatchupFailure = !rejectedCounter;
        var client = await CreateClientAsync(store, handler, policy);
        Assert.Equal("catchup_status_unavailable", (await client.SyncAsync()).BlockedCode);
        Assert.Equal(0, handler.SinglePosts + handler.BatchPosts);
        Assert.Equal(1, handler.ChangeRequests);
        Assert.Equal(1, handler.CheckpointRequests);
        Assert.Equal(original, await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Single(handler.CatchupReports);
        Assert.Equal("catching_up", handler.CatchupReports[0].GetProperty("state").GetString());
        handler.CatchupFailure = false;
        clock.Utc = clock.Utc.AddMinutes(5);
        Assert.Equal(3, (await client.SyncAsync()).PushedMutations);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal(2, handler.ChangeRequests);
        Assert.Equal("catching_up", handler.CatchupReports[1].GetProperty("state").GetString());
        if (rejectedCounter) Assert.True(handler.CatchupReports[1].GetProperty("reportSequence").GetInt64() > 8000);
    }

    [Fact]
    public async Task Current_catchup_reports_are_capability_gated_content_free_and_do_not_reuse_initial_status()
    {
        var (store, _, handler) = await CreateAsync();
        handler.SchemaVersion = 26;
        handler.CapabilityOverride = CatchupCapability;
        var policy = new PersonalCloudDispatchPolicy(Path.Combine(_directory, "catchup-wire"), resumeJitter: () => TimeSpan.Zero);
        var client = await CreateClientAsync(store, handler, policy);
        Assert.Equal(3, (await client.SyncAsync()).PushedMutations);
        Assert.Equal(2, handler.CatchupReports.Count);
        Assert.Equal(new[] { 3, 0 }, handler.CatchupReports.Select(report => report.GetProperty("pending").GetInt32()));
        Assert.All(handler.CatchupReports, report => {
            Assert.Equal(new[] { "pending", "protocolVersion", "reportSequence", "state" }, report.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
            Assert.Equal("idle", report.GetProperty("state").GetString());
            Assert.DoesNotContain("Synthetic", report.GetRawText());
        });
        var (_, legacy, oldHandler) = await CreateAsync();
        Assert.Equal(3, (await legacy.SyncAsync()).PushedMutations);
        Assert.Empty(oldHandler.CatchupReports);
    }

    [Fact]
    public async Task Ignored_counter_report_recovers_server_max_then_resamples_next_pass_without_blocking_real_sync()
    {
        var (store, _, handler) = await CreateAsync();
        handler.SchemaVersion = 26; handler.CapabilityOverride = CatchupCapability;
        handler.CatchupServerSequence = 1000;
        var clock = new QuotaClock();
        var policy = new PersonalCloudDispatchPolicy(Path.Combine(_directory, "catchup-stale"), clock: clock, resumeJitter: () => TimeSpan.Zero);
        var client = await CreateClientAsync(store, handler, policy);
        Assert.Equal(3, (await client.SyncAsync()).PushedMutations);
        Assert.Single(handler.CatchupReports); // No blind same-pass retry of an old idle snapshot.
        Assert.True((await policy.GetStatusAsync(new("https://synthetic.example.test/"))).CatchupTelemetryUnknown);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        clock.Utc = clock.Utc.AddMinutes(5);
        Assert.Equal(0, (await client.SyncAsync()).PushedMutations);
        Assert.Equal(2, handler.CatchupReports.Count);
        Assert.Equal(1001, handler.CatchupReports[^1].GetProperty("reportSequence").GetInt64());
        Assert.Equal(0, handler.CatchupReports[^1].GetProperty("pending").GetInt32());
        Assert.False((await policy.GetStatusAsync(new("https://synthetic.example.test/"))).CatchupTelemetryUnknown);
    }

    [Theory]
    [InlineData("{\"protocolVersion\":2,\"path\":\"/sync/catchup-status\",\"staleAfterSeconds\":900}")]
    [InlineData("{\"protocolVersion\":1,\"path\":\"/other\",\"staleAfterSeconds\":900}")]
    [InlineData("{\"protocolVersion\":1,\"path\":\"/sync/catchup-status\",\"staleAfterSeconds\":0}")]
    public async Task Invalid_catchup_capability_is_not_used(string capability)
    {
        var handler = new Handler { SchemaVersion = 26, CapabilityOverride = "{\"catchupStatus\":" + capability + "}" };
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
            new PersonalCloudHealthClient(new HttpClient(handler)).InspectAsync(new("https://synthetic.example.test/")));
        Assert.Empty(handler.CatchupReports);
    }
}
