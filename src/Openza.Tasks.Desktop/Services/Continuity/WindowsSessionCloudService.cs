using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Desktop.Services.Continuity;

// The composed service is the barrier, so callers outside the window also stay offline.
internal sealed class WindowsSessionCloudService(WindowsContinuitySession session, IPersonalCloudSetupService inner)
    : IPersonalCloudSetupService, IPersonalCloudProviderBridgeService, IPersonalCloudInitialSyncProgressService
{
    internal WindowsContinuitySession Session => session;
    public bool IsConfigured => inner.IsConfigured;
    // Keep local authoring attached to the existing identity and its durable outbox.
    public PersonalCloudConnectionProfile? CurrentConnection => inner.CurrentConnection;
    private T Run<T>(Func<T> operation) { session.EnsureCloudAccess(); return operation(); }
    public Task OpenAsync(CancellationToken cancellationToken = default) => Run(() => inner.OpenAsync(cancellationToken));
    public Task<PersonalCloudProvisioningResult> ConnectAsync(CancellationToken cancellationToken = default, IProgress<PersonalCloudSetupProgress>? progress = null) => Run(() => inner.ConnectAsync(cancellationToken, progress));
    public Task<PersonalCloudConnectionProfile> PairExistingAsync(Uri serviceOrigin, string grant, string deviceName, bool providerBridge, CancellationToken cancellationToken = default) => Run(() => inner.PairExistingAsync(serviceOrigin, grant, deviceName, providerBridge, cancellationToken));
    public Task<PersonalCloudSyncResult> SyncAsync(CancellationToken cancellationToken = default) => Run(() => inner.SyncAsync(cancellationToken));
    public Task<CloudflareD1Export> ExportAsync(CancellationToken cancellationToken = default) => Run(() => inner.ExportAsync(cancellationToken));
    public Task<PersonalCloudUpgradeResult> UpgradeAsync(CancellationToken cancellationToken = default) => Run(() => inner.UpgradeAsync(cancellationToken));
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Run(() => inner.DisconnectAsync(cancellationToken));
    public Task RemoveAsync(CancellationToken cancellationToken = default) => Run(() => inner.RemoveAsync(cancellationToken));
    public Task<PersonalCloudProviderBridgeResult> RunProviderBridgeAsync(IReadOnlyList<PersonalCloudConfiguredProvider> providers, CancellationToken cancellationToken = default) =>
        Run(() => ((IPersonalCloudProviderBridgeService)inner).RunProviderBridgeAsync(providers, cancellationToken));
    public Task RetryProviderCommandAsync(string taskId, CancellationToken cancellationToken = default) =>
        Run(() => ((IPersonalCloudProviderBridgeService)inner).RetryProviderCommandAsync(taskId, cancellationToken));
    public event Action<PersonalCloudInitialSyncProgress>? InitialSyncProgressChanged
    {
        add { if (inner is IPersonalCloudInitialSyncProgressService progress) progress.InitialSyncProgressChanged += value; }
        remove { if (inner is IPersonalCloudInitialSyncProgressService progress) progress.InitialSyncProgressChanged -= value; }
    }
    public Task<PersonalCloudInitialSyncProgress?> GetInitialSyncProgressAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        session.EnsureActive();
        if (session.CloudAccessInhibited) return Task.FromResult<PersonalCloudInitialSyncProgress?>(null);
        return Run(() => inner is IPersonalCloudInitialSyncProgressService progress ? progress.GetInitialSyncProgressAsync(cancellationToken) : Task.FromResult<PersonalCloudInitialSyncProgress?>(null));
    }
}
