using System.Net;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed partial class PersonalCloudBatchSyncTests
{
    [Theory]
    [InlineData(503, false)] [InlineData(503, true)]
    [InlineData(500, true)] [InlineData(401, true)] [InlineData(429, true)]
    public async Task Negative_readiness_never_dispatches_or_changes_negotiated_state(int status, bool negotiated)
    {
        var (store, client, handler) = await CreateAsync();
        if (negotiated) await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true, providerCatalogProtocolVersion: 2);
        var pending = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        var checkpoint = await store.GetPersonalCloudCheckpointAsync(Instance, Device);
        handler.HealthStatus = (HttpStatusCode)status;
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.SyncAsync());
        Assert.Equal("health_failed", error.Code);
        Assert.Equal(0, handler.SinglePosts + handler.BatchPosts);
        Assert.Equal(pending, await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.Equal(checkpoint, await store.GetPersonalCloudCheckpointAsync(Instance, Device));
        Assert.Equal(negotiated, await store.SupportsPersonalCloudWorkspaceAsync(Instance));
        Assert.Equal(negotiated ? 2 : 0, await store.GetPersonalCloudProviderCatalogProtocolVersionAsync(Instance));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"status\":\"ok\",\"protocolVersion\":1,\"product\":\"tasks\",\"artifactVersion\":\"test\",\"schemaVersion\":26}")]
    [InlineData("{\"status\":\"ok\",\"protocolVersion\":2,\"product\":\"tasks\",\"artifactVersion\":\"test\",\"schemaVersion\":25}")]
    [InlineData("{\"status\":\"ok\",\"protocolVersion\":1,\"product\":\"tasks\",\"artifactVersion\":\"test\",\"schemaVersion\":25,\"capabilities\":{\"mutationBatch\":{}}}")]
    public async Task Malformed_or_incompatible_success_never_falls_back_or_erases_capabilities(string body)
    {
        var (store, client, handler) = await CreateAsync();
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true, providerCatalogProtocolVersion: 2);
        var pending = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        handler.HealthBody = body;
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.SyncAsync());
        Assert.Equal(0, handler.SinglePosts + handler.BatchPosts);
        Assert.Equal(pending, await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.True(await store.SupportsPersonalCloudWorkspaceAsync(Instance));
        Assert.Equal(2, await store.GetPersonalCloudProviderCatalogProtocolVersionAsync(Instance));
        Assert.Equal(0, await store.GetPersonalCloudCheckpointAsync(Instance, Device));
    }

    [Theory]
    [InlineData(404, false)] [InlineData(405, false)] [InlineData(501, false)]
    [InlineData(404, true)] [InlineData(405, true)] [InlineData(501, true)]
    public async Task Only_explicit_missing_health_without_prior_negotiation_allows_legacy_delivery(int status, bool negotiated)
    {
        var (store, client, handler) = await CreateAsync();
        if (negotiated) await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true, providerCatalogProtocolVersion: 1);
        var pending = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        handler.HealthStatus = (HttpStatusCode)status;
        if (negotiated)
        {
            var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.SyncAsync());
            Assert.Equal("health_unsupported", error.Code);
            Assert.Equal(pending, await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
            Assert.Equal(0, handler.SinglePosts + handler.BatchPosts);
            Assert.True(await store.SupportsPersonalCloudWorkspaceAsync(Instance));
            Assert.Equal(1, await store.GetPersonalCloudProviderCatalogProtocolVersionAsync(Instance));
        }
        else
        {
            Assert.Equal(3, (await client.SyncAsync()).PushedMutations);
            Assert.Equal(3, handler.SinglePosts);
            Assert.Equal(0, handler.BatchPosts);
            Assert.Equal(pending.Select(item => item.CanonicalJson), handler.Envelopes);
        }
    }

    [Fact]
    public async Task Transport_error_and_capability_regression_preserve_pending_work()
    {
        var (store, client, handler) = await CreateAsync();
        await store.SavePersonalCloudWorkspaceCapabilitiesAsync(Instance, true, true, providerCatalogProtocolVersion: 2);
        var pending = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        handler.HealthOffline = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SyncAsync());
        handler.HealthOffline = false; // A compatible legacy 200 still cannot downgrade prior workspace negotiation.
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.SyncAsync());
        Assert.Equal("health_mismatch", error.Code);
        Assert.Equal(0, handler.SinglePosts + handler.BatchPosts);
        Assert.Equal(pending, await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        Assert.True(await store.SupportsPersonalCloudProviderScopesAsync(Instance));
    }

    [Fact]
    public async Task Same_client_rechecks_health_before_a_later_pass_and_recovers_without_rewriting_intents()
    {
        var (store, client, handler) = await CreateAsync(1);
        Assert.Equal(1, (await client.SyncAsync()).PushedMutations);
        await store.CreateTaskWithPersonalCloudIntentAsync(new TaskItem { Id = "local_later", Title = "Synthetic later edit" }, Instance, Device);
        var pending = await store.GetPendingPersonalCloudOutboxAsync(Instance, Device);
        var dispatched = handler.BatchPosts + handler.SinglePosts;
        handler.HealthStatus = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.SyncAsync());
        Assert.Equal(dispatched, handler.BatchPosts + handler.SinglePosts);
        Assert.Equal(pending, await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
        handler.HealthStatus = HttpStatusCode.OK;
        Assert.Equal(1, (await client.SyncAsync()).PushedMutations);
        Assert.Equal(3, handler.HealthRequests);
        Assert.Contains(pending.Single().CanonicalJson, handler.Envelopes);
    }
}
