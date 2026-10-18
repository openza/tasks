using Openza.Tasks.Application.Runtime;
using Openza.Tasks.Application.Sync;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Tests;

public sealed class ConnectedSyncApplicationServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-connected-sync", Guid.NewGuid().ToString("N"));
    private readonly List<string> _trace = [];

    private async Task<ConnectedSyncApplicationService> CreateAsync(Resolver resolver, Cloud? cloud = null)
    {
        var runtime = OpenzaRuntimeContext.Create(OpenzaChannel.Dev, _directory);
        var store = new SqliteTaskStore(runtime.DatabasePath);
        await store.InitializeAsync();
        return new(store, resolver, runtime, cloud);
    }

    [Fact]
    public async Task Providers_then_cloud_bridge_cloud_keep_order_and_combine_only_successful_passes()
    {
        var cloud = new Cloud(_trace);
        var service = await CreateAsync(new(_trace), cloud);
        var progress = new ProgressLog();
        var result = await service.RunAsync(progress);
        Assert.Equal(new[] { "resolve:todoist", "fetch:todoist", "resolve:msToDo", "fetch:msToDo", "cloud:1", "bridge", "cloud:2" }, _trace);
        Assert.All(result.Providers, x => Assert.True(x.Success));
        Assert.Equal(new[] { IntegrationIds.Todoist, IntegrationIds.MicrosoftToDo }, cloud.BridgeProviders.Select(x => x.Provider.IntegrationId));
        Assert.True(result.CloudAttempted);
        Assert.Null(result.PersonalCloudError);
        Assert.Equal((3L, 5L, 20L), ((long)result.PersonalCloud!.PushedMutations, (long)result.PersonalCloud.PulledChanges, result.PersonalCloud.Checkpoint));
        Assert.Equal(new long?[] { 1, 2 }, progress.Values.Select(x => x.Acknowledged));
        Assert.Equal(2, result.PersonalCloud.InitialSyncProgress!.Acknowledged);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolver_or_provider_failure_does_not_skip_the_other_provider_or_bridge_failed_configuration(bool resolverFails)
    {
        var resolver = new Resolver(_trace) { ResolverFailure = resolverFails, FetchFailure = !resolverFails };
        var cloud = new Cloud(_trace);
        var service = await CreateAsync(resolver, cloud);
        var result = await service.RunAsync();
        Assert.False(result.Providers[0].Success);
        Assert.Contains("Synthetic provider failure", result.Providers[0].Error);
        Assert.True(result.Providers[1].Success);
        Assert.Equal(IntegrationIds.MicrosoftToDo, Assert.Single(cloud.BridgeProviders).Provider.IntegrationId);
        Assert.Equal(new[] { "cloud:1", "bridge", "cloud:2" }, _trace.Where(x => x.StartsWith("cloud:") || x == "bridge"));
        Assert.Null(result.PersonalCloudError);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Cloud_failures_preserve_partial_results_and_do_not_replace_the_saved_connection(int failureStage)
    {
        var cloud = new Cloud(_trace) { FailureStage = failureStage };
        var connection = cloud.CurrentConnection;
        var service = await CreateAsync(new(_trace), cloud);
        var result = await service.RunAsync();
        Assert.Equal("Synthetic cloud failure", result.PersonalCloudError);
        Assert.Same(connection, cloud.CurrentConnection);
        Assert.True(result.CloudAttempted);
        Assert.All(result.Providers, x => Assert.True(x.Success));
        Assert.Equal(failureStage == 1 ? null : 10L, result.PersonalCloud?.Checkpoint);
        Assert.Equal(failureStage == 3, result.ProviderBridge is not null);
        Assert.Equal(failureStage == 3 ? 2 : 1, cloud.SyncCalls);
    }

    [Fact]
    public async Task Blocked_cloud_pass_does_not_dispatch_bridge_or_post_pass()
    {
        var cloud = new Cloud(_trace) { Blocked = true };
        var service = await CreateAsync(new(_trace), cloud);
        var result = await service.RunAsync();
        Assert.Equal("synthetic_block", result.PersonalCloud!.BlockedCode);
        Assert.Null(result.PersonalCloudError);
        Assert.Null(result.ProviderBridge);
        Assert.DoesNotContain("bridge", _trace);
        Assert.Equal(1, cloud.SyncCalls);
    }

    [Fact]
    public async Task Missing_providers_and_unpaired_cloud_remain_a_quiet_empty_result()
    {
        var cloud = new Cloud(_trace) { Paired = false };
        var service = await CreateAsync(new(_trace) { Missing = true }, cloud);
        var result = await service.RunAsync();
        Assert.Empty(result.Providers);
        Assert.False(result.CloudAttempted);
        Assert.Null(result.PersonalCloud);
        Assert.Equal(new[] { "resolve:todoist", "resolve:msToDo" }, _trace);
    }

    [Fact]
    public async Task Provider_lease_contention_is_isolated_before_credentials_and_other_providers_still_run()
    {
        var runtime = OpenzaRuntimeContext.Create(OpenzaChannel.Dev, _directory);
        var cloud = new Cloud(_trace);
        var service = await CreateAsync(new(_trace), cloud);
        using (ChannelRuntimeLease.AcquireProviderSync(runtime, IntegrationIds.Todoist))
        {
            var result = await service.RunAsync();
            Assert.False(result.Providers[0].Success);
            Assert.Contains("already running", result.Providers[0].Error);
            Assert.DoesNotContain("resolve:todoist", _trace);
            Assert.True(result.Providers[1].Success);
            Assert.Equal(IntegrationIds.MicrosoftToDo, Assert.Single(cloud.BridgeProviders).Provider.IntegrationId);
        }
    }

    [Fact]
    public async Task Database_replacement_blocks_the_run_before_resolving_credentials_and_releases_for_retry()
    {
        var runtime = OpenzaRuntimeContext.Create(OpenzaChannel.Dev, _directory);
        var service = await CreateAsync(new(_trace));
        using (ChannelRuntimeLease.AcquireDatabaseReplacement(runtime))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync());
            Assert.Empty(_trace);
        }
        var result = await service.RunAsync();
        Assert.All(result.Providers, x => Assert.True(x.Success));
        Assert.Equal(2, result.Providers.Count);
    }

    [Fact]
    public async Task Cancellation_keeps_the_existing_per_provider_fault_isolation_and_cloud_error_result()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cloud = new Cloud(_trace) { Cancel = true };
        var resolver = new Resolver(_trace) { Cancel = true };
        var service = await CreateAsync(resolver, cloud);
        var result = await service.RunAsync(cancellationToken: cancellation.Token);
        Assert.Equal(2, result.Providers.Count);
        Assert.All(result.Providers, x => Assert.False(x.Success));
        Assert.Equal(cancellation.Token, resolver.LastToken);
        Assert.Equal(cancellation.Token, cloud.LastToken);
        Assert.Equal("Synthetic cancellation", result.PersonalCloudError);
        Assert.Equal(new[] { "resolve:todoist", "resolve:msToDo", "cloud:1" }, _trace);
    }

    private sealed class ProgressLog : IProgress<PersonalCloudInitialSyncProgress>
    {
        public List<PersonalCloudInitialSyncProgress> Values { get; } = [];
        public void Report(PersonalCloudInitialSyncProgress value) => Values.Add(value);
    }

    private sealed class Resolver(List<string> trace) : IConnectedSyncProviderResolver
    {
        public bool ResolverFailure { get; init; }
        public bool FetchFailure { get; init; }
        public bool Missing { get; init; }
        public bool Cancel { get; init; }
        public CancellationToken LastToken { get; private set; }
        public Task<PersonalCloudConfiguredProvider?> ResolveAsync(string integrationId, CancellationToken cancellationToken = default)
        {
            trace.Add("resolve:" + integrationId);
            LastToken = cancellationToken;
            if (Cancel) throw new OperationCanceledException("Synthetic cancellation", cancellationToken);
            if (ResolverFailure && integrationId == IntegrationIds.Todoist) throw new InvalidOperationException("Synthetic provider failure");
            return Task.FromResult<PersonalCloudConfiguredProvider?>(Missing ? null : new(new Provider(integrationId, trace, FetchFailure), "synthetic-binding"));
        }
    }

    private sealed class Provider(string integrationId, List<string> trace, bool fail) : ISyncProvider
    {
        public string IntegrationId => integrationId;
        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default)
        {
            trace.Add("fetch:" + integrationId);
            if (fail && integrationId == IntegrationIds.Todoist) throw new InvalidOperationException("Synthetic provider failure");
            return Task.FromResult(new ProviderSnapshot([], [], []));
        }
        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Cloud(List<string> trace) : IPersonalCloudSetupService, IPersonalCloudProviderBridgeService
    {
        public bool IsConfigured => true;
        public bool Paired { get; init; } = true;
        public bool Blocked { get; init; }
        public bool Cancel { get; init; }
        public int FailureStage { get; init; }
        public int SyncCalls { get; private set; }
        public CancellationToken LastToken { get; private set; }
        private readonly PersonalCloudConnectionProfile _connection = new()
        { ServiceOrigin = new("https://synthetic.invalid/"), InstanceId = "synthetic-instance", DeviceId = "synthetic-device", ArtifactVersion = "1.0.0", ConnectedAt = DateTimeOffset.Parse("2026-10-18T00:00:00Z") };
        public PersonalCloudConnectionProfile? CurrentConnection => Paired ? _connection : null;
        public IReadOnlyList<PersonalCloudConfiguredProvider> BridgeProviders { get; private set; } = [];
        public Task<PersonalCloudSyncResult> SyncAsync(CancellationToken cancellationToken = default)
        {
            trace.Add("cloud:" + ++SyncCalls);
            LastToken = cancellationToken;
            if (Cancel) throw new OperationCanceledException("Synthetic cancellation", cancellationToken);
            if (FailureStage == 1 || (FailureStage == 3 && SyncCalls == 2)) throw new InvalidOperationException("Synthetic cloud failure");
            return Task.FromResult(new PersonalCloudSyncResult(SyncCalls, SyncCalls + 1, SyncCalls * 10)
            { BlockedCode = Blocked ? "synthetic_block" : null,
              InitialSyncProgress = new("synthetic-instance", "synthetic-device", PersonalCloudInitialSyncPhase.Uploading, SyncCalls, 2) });
        }
        public Task<PersonalCloudProviderBridgeResult> RunProviderBridgeAsync(IReadOnlyList<PersonalCloudConfiguredProvider> providers, CancellationToken cancellationToken = default)
        {
            trace.Add("bridge");
            BridgeProviders = providers;
            if (FailureStage == 2) throw new InvalidOperationException("Synthetic cloud failure");
            return Task.FromResult(new PersonalCloudProviderBridgeResult(0, 0, 0, 0));
        }
        public Task RetryProviderCommandAsync(string taskId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task OpenAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PersonalCloudProvisioningResult> ConnectAsync(CancellationToken cancellationToken = default, IProgress<PersonalCloudSetupProgress>? progress = null) => throw new NotSupportedException();
        public Task<PersonalCloudConnectionProfile> PairExistingAsync(Uri origin, string grant, string deviceName, bool providerBridge, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CloudflareD1Export> ExportAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PersonalCloudUpgradeResult> UpgradeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    public void Dispose() => TestDirectory.Delete(_directory);
}
