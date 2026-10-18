using Openza.Tasks.Application.Runtime;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Application.Sync;

public interface IConnectedSyncProviderResolver
{
    Task<PersonalCloudConfiguredProvider?> ResolveAsync(string integrationId, CancellationToken cancellationToken = default);
}

public sealed record ConnectedSyncResult(
    IReadOnlyList<SyncSummary> Providers,
    bool CloudAttempted,
    PersonalCloudSyncResult? PersonalCloud,
    PersonalCloudProviderBridgeResult? ProviderBridge,
    string? PersonalCloudError);

/// <summary>Runs connected providers followed by cloud/bridge reconciliation, without presentation state.</summary>
public sealed class ConnectedSyncApplicationService(
    ITaskStore store,
    IConnectedSyncProviderResolver providers,
    OpenzaRuntimeContext runtime,
    IPersonalCloudSetupService? personalCloud = null)
{
    private readonly TaskSyncEngine _syncEngine = new(store);

    public async Task<ConnectedSyncResult> RunAsync(
        IProgress<PersonalCloudInitialSyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var bridgeProviders = new List<PersonalCloudConfiguredProvider>();
        // Preserve the caller's context for progress publication. Provider and bridge
        // work retain their separate background passes, outside the host's UI busy gate.
        var summaries = await Task.Run(async () =>
        {
            using var databaseLease = ChannelRuntimeLease.AcquireDatabaseRead(runtime);
            var results = new List<SyncSummary>();
            async Task SyncProvider(string integrationId)
            {
                try
                {
                    using var lease = ChannelRuntimeLease.AcquireProviderSync(runtime, integrationId);
                    var configuration = await providers.ResolveAsync(integrationId, cancellationToken).ConfigureAwait(false);
                    if (configuration is not null)
                    {
                        var summary = await _syncEngine.SyncAsync(configuration.Provider, cancellationToken).ConfigureAwait(false);
                        results.Add(summary);
                        if (summary.Success) bridgeProviders.Add(configuration);
                    }
                }
                catch (Exception exception)
                {
                    results.Add(new SyncSummary(integrationId, false, 0, 0, 0, 0, 0, 0, exception.Message));
                }
            }
            await SyncProvider(IntegrationIds.Todoist);
            await SyncProvider(IntegrationIds.MicrosoftToDo);
            return results;
        });

        PersonalCloudSyncResult? cloudResult = null;
        PersonalCloudProviderBridgeResult? bridgeResult = null;
        string? cloudError = null;
        var cloudAttempted = personalCloud?.CurrentConnection is not null;
        if (cloudAttempted)
        {
            try
            {
                cloudResult = await SyncCloudAsync();
                if (cloudResult.BlockedCode is null && personalCloud is IPersonalCloudProviderBridgeService bridge)
                {
                    bridgeResult = await Task.Run(() => bridge.RunProviderBridgeAsync(bridgeProviders, cancellationToken));
                    var resultPass = await SyncCloudAsync();
                    cloudResult = new PersonalCloudSyncResult(cloudResult.PushedMutations + resultPass.PushedMutations,
                        cloudResult.PulledChanges + resultPass.PulledChanges, resultPass.Checkpoint)
                    { BlockedCode = resultPass.BlockedCode, InitialSyncProgress = resultPass.InitialSyncProgress };
                }
            }
            catch (Exception exception) { cloudError = exception.Message; }
        }
        return new(summaries, cloudAttempted, cloudResult, bridgeResult, cloudError);

        async Task<PersonalCloudSyncResult> SyncCloudAsync()
        {
            var result = await personalCloud!.SyncAsync(cancellationToken);
            if (result.InitialSyncProgress is { } value) progress?.Report(value);
            return result;
        }
    }
}
