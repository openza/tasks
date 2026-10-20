using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed partial class PersonalCloudBatchSyncTests
{
    [Theory]
    [InlineData(404)]
    [InlineData(405)]
    [InlineData(501)]
    public async Task Dispatch_policy_preserves_explicit_legacy_route_fallback_and_exact_envelopes(int status)
    {
        var (store, _, handler) = await CreateAsync();
        handler.RouteStatus = (System.Net.HttpStatusCode)status;
        var original = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        var client = await CreateClientAsync(store, handler, new(Path.Combine(_directory, "unsupported-batch")));
        Assert.Equal(3, (await client.SyncAsync()).PushedMutations);
        Assert.Equal(1, handler.BatchPosts);
        Assert.Equal(3, handler.SinglePosts);
        Assert.Equal(original.Select(item => item.CanonicalJson), handler.Envelopes);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(405)]
    [InlineData(501)]
    public async Task Dispatch_policy_preserves_unnegotiated_unsupported_health_fallback(int status)
    {
        var (store, _, handler) = await CreateAsync();
        handler.HealthStatus = (System.Net.HttpStatusCode)status;
        handler.HealthBody = "{}";
        var client = await CreateClientAsync(store, handler, new(Path.Combine(_directory, "unsupported-health")));
        Assert.Equal(3, (await client.SyncAsync()).PushedMutations);
        Assert.Equal(0, handler.BatchPosts);
        Assert.Equal(3, handler.SinglePosts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typed_partial_quota_validates_prefix_before_ACK_and_retains_exact_suffix(bool invalidPrefix)
    {
        var (store, _, handler) = await CreateAsync();
        var original = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        var clock = new QuotaClock();
        handler.ResponseDate = clock.Utc;
        handler.PartialFailure = "daily_quota_exceeded";
        handler.ResponseDefect = invalidPrefix ? "version" : null;
        var directory = Path.Combine(_directory, "quota-partial");
        var policy = new PersonalCloudDispatchPolicy(directory, clock: clock, resumeJitter: () => TimeSpan.Zero);
        var client = await CreateClientAsync(store, handler, policy);
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.SyncAsync());
        Assert.Equal(invalidPrefix ? "invalid_response" : "daily_quota_exceeded", error.Code);
        Assert.Equal(original.Skip(invalidPrefix ? 0 : 1).Select(item => item.CanonicalJson),
            (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Select(item => item.CanonicalJson));
        Assert.Equal(0, await store.GetPersonalCloudCheckpointAsync(Instance, Device));
        var restarted = new PersonalCloudDispatchPolicy(directory, clock: clock, resumeJitter: () => TimeSpan.Zero);
        Assert.Equal("daily_quota_exceeded", (await restarted.GetStatusAsync(new("https://synthetic.example.test/"))).Code);
        var posts = handler.BatchPosts;
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.SyncAsync());
        Assert.Equal(posts, handler.BatchPosts);
    }

    [Fact]
    public async Task Daily_write_credit_budget_sends_only_allowed_prefix_without_an_initial_seed_marker()
    {
        var (store, _, handler) = await CreateAsync();
        Assert.Null(await store.GetPersonalCloudInitialSyncProgressAsync(Instance, Device));
        var original = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        var policy = new PersonalCloudDispatchPolicy(Path.Combine(_directory, "budget-prefix"), new(3000, 100, 75), resumeJitter: () => TimeSpan.Zero);
        var client = await CreateClientAsync(store, handler, policy);
        var result = await client.SyncAsync();
        Assert.Equal("local_budget_exceeded", result.BlockedCode);
        var status = await policy.GetStatusAsync(new("https://synthetic.example.test/"));
        Assert.Equal(new[] { 1 }, handler.BatchSizes);
        Assert.Equal(original.Take(1).Select(item => item.CanonicalJson), handler.Envelopes);
        Assert.Equal(original.Skip(1).Select(item => item.CanonicalJson),
            (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Select(item => item.CanonicalJson));
        Assert.Equal(1, status.AttemptedEnvelopes);
        Assert.Equal(50, status.ReservedWriteUnits);
        Assert.Equal(25, status.RemainingWriteUnits);
        Assert.False(status.BlocksReads);
    }

    [Fact]
    public async Task Unknown_batch_outcome_consumes_budget_and_replays_same_IDs_next_day_until_backlog_drains()
    {
        var (store, _, handler) = await CreateAsync();
        var original = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        var clock = new QuotaClock();
        var directory = Path.Combine(_directory, "uncertain-budget");
        PersonalCloudDispatchPolicy Policy() => new(directory, new(3000, 100, 100), clock, () => TimeSpan.Zero);
        handler.ResponseDate = clock.Utc;
        handler.LoseResponse = true;
        var client = await CreateClientAsync(store, handler, Policy());
        Assert.Equal("cloud_retry_backoff", (await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.SyncAsync())).Code);
        Assert.Equal(original.Select(item => item.CanonicalJson), (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Select(item => item.CanonicalJson));

        clock.Utc = clock.Utc.AddDays(1);
        handler.ResponseDate = clock.Utc;
        handler.LoseResponse = false;
        client = await CreateClientAsync(store, handler, Policy());
        Assert.Equal("local_budget_exceeded", (await client.SyncAsync()).BlockedCode);
        Assert.Equal(2, handler.DuplicateReceipts);
        Assert.Equal(original.Skip(2).Select(item => item.CanonicalJson), (await store.GetPendingPersonalCloudOutboxAsync(Instance, Device)).Select(item => item.CanonicalJson));

        clock.Utc = clock.Utc.AddDays(1);
        handler.ResponseDate = clock.Utc;
        client = await CreateClientAsync(store, handler, Policy());
        Assert.Equal(1, (await client.SyncAsync()).PushedMutations);
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal(original.Take(2).Concat(original).Select(item => item.CanonicalJson), handler.Envelopes);
    }

    private sealed class QuotaClock : TimeProvider
    {
        public DateTimeOffset Utc = DateTimeOffset.Parse("2026-10-19T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Utc;
    }
}
