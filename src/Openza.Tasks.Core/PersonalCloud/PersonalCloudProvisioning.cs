using System.Text.Json;

namespace Openza.Tasks.Core.PersonalCloud;

public enum PersonalCloudProvisioningStage
{
    NotStarted,
    AccountResolved,
    DatabaseReady,
    ApplicationDeployed,
    DatabaseMigrated,
    HealthVerified,
    DesktopPaired,
    Complete,
}

public enum PersonalCloudSetupStep
{
    AwaitingAuthorization,
    ResolvingAccount,
    CreatingDatabase,
    DeployingApplication,
    PreparingDatabase,
    VerifyingConnection,
    PairingDesktop,
    CreatingOwnerAccess,
    Complete,
}

public sealed record PersonalCloudSetupProgress(PersonalCloudSetupStep Step);

public sealed record PersonalCloudProvisioningState
{
    public required string AttemptId { get; init; }
    public required string ArtifactVersion { get; init; }
    public required PersonalCloudProvisioningStage Stage { get; init; }
    public string? AccountId { get; init; }
    public string? DatabaseId { get; init; }
    public string? WorkerName { get; init; }
    public Uri? ServiceOrigin { get; init; }
    public string? LastErrorCode { get; init; }
}

public sealed record CloudflareProvisioningAccount(string AccountId);
public sealed record CloudflareProvisioningDatabase(string DatabaseId);
public sealed record CloudflareProvisioningApplication(string WorkerName, Uri ServiceOrigin);
public sealed record PersonalCloudInitialPairing(string Grant, string DeviceName, bool ProviderBridge);

public interface IPersonalCloudProvisioningStateStore
{
    Task<PersonalCloudProvisioningState?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(PersonalCloudProvisioningState state, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface ICloudflareProvisioningGateway
{
    Task<CloudflareProvisioningAccount> ResolveAccountAsync(string accessToken, CancellationToken cancellationToken = default);
    Task<CloudflareProvisioningDatabase> EnsureDatabaseAsync(string accessToken, string accountId, string attemptId, CancellationToken cancellationToken = default);
    Task<CloudflareProvisioningApplication> DeployApplicationAsync(string accessToken, string accountId, string databaseId, string attemptId, string artifactVersion, CancellationToken cancellationToken = default);
    Task ApplyMigrationsAsync(string accessToken, string accountId, string databaseId, string artifactVersion, CancellationToken cancellationToken = default);
    Task VerifyHealthAsync(Uri serviceOrigin, string artifactVersion, CancellationToken cancellationToken = default);
    Task<PersonalCloudInitialPairing> CreateInitialPairingAsync(string accessToken, string accountId, string databaseId, Uri serviceOrigin, CancellationToken cancellationToken = default);
    Task<Uri> CreateOwnerBootstrapAsync(string accessToken, string accountId, string databaseId, Uri serviceOrigin, CancellationToken cancellationToken = default);
}

public sealed record PersonalCloudProvisioningResult(Uri ServiceOrigin, string WorkerName, string DatabaseId, string ArtifactVersion, Uri OwnerBootstrapUri)
{
    public string? InstanceId { get; init; }
    public string? DeviceId { get; init; }
}

public interface IPersonalCloudSetupService
{
    bool IsConfigured { get; }
    PersonalCloudConnectionProfile? CurrentConnection { get; }
    Task OpenAsync(CancellationToken cancellationToken = default);
    Task<PersonalCloudProvisioningResult> ConnectAsync(
        CancellationToken cancellationToken = default,
        IProgress<PersonalCloudSetupProgress>? progress = null);
    Task<PersonalCloudConnectionProfile> PairExistingAsync(
        Uri serviceOrigin,
        string grant,
        string deviceName,
        bool providerBridge,
        CancellationToken cancellationToken = default);
    Task<PersonalCloudSyncResult> SyncAsync(CancellationToken cancellationToken = default);
    Task<CloudflareD1Export> ExportAsync(CancellationToken cancellationToken = default);
    Task<PersonalCloudUpgradeResult> UpgradeAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task RemoveAsync(CancellationToken cancellationToken = default);
}

public sealed partial class PersonalCloudSetupService(
    PersonalCloudProvisioningCoordinator coordinator,
    string artifactVersion,
    Func<Uri, CancellationToken, Task>? openOwnerBootstrap = null,
    IPersonalCloudConnectionStore? connectionStore = null,
    PersonalCloudDeviceIdentityClient? deviceIdentity = null,
    HttpClient? syncHttp = null,
    Openza.Tasks.Core.Data.SqliteTaskStore? taskStore = null,
    PersonalCloudManagementCoordinator? management = null,
    PersonalCloudArtifactSlot? artifactSlot = null) : IPersonalCloudSetupService, IPersonalCloudProviderBridgeService, IPersonalCloudInitialSyncProgressService, IPersonalCloudDispatchStatusService, IPersonalCloudReleaseStatusService
{
    // Setup and upgrade share the artifact slot, so they never run concurrently.
    private readonly SemaphoreSlim _artifactOperation = new(1, 1);

    public Task<PersonalCloudReleaseStatus> CheckForCloudUpdateAsync(CancellationToken cancellationToken = default) =>
        artifactSlot is not null
            ? artifactSlot.CheckAsync(CurrentConnection, cancellationToken)
            : Task.FromResult(new PersonalCloudReleaseStatus(artifactVersion,
                !string.Equals(CurrentConnection?.ArtifactVersion, artifactVersion, StringComparison.Ordinal)));

    private PersonalCloudProviderBridge CreateProviderBridge()
    {
        if (CurrentConnection is not { } current || syncHttp is null || deviceIdentity is null || taskStore is null)
            throw new PersonalCloudK0ContractException("invalid_state", "Personal Cloud is not connected for provider delivery.");
        return new PersonalCloudProviderBridge(taskStore,
            new PersonalCloudProviderDeliveryClient(syncHttp, deviceIdentity, current), current);
    }

    public async Task<PersonalCloudProviderBridgeResult> RunProviderBridgeAsync(IReadOnlyList<PersonalCloudConfiguredProvider> providers,
        CancellationToken cancellationToken = default)
    {
        using var lease = taskStore is null ? null : PersonalCloudConnectionLease.AcquireAuthoring(taskStore.DatabasePath);
        await EnsureDispatchAvailableAsync(cancellationToken).ConfigureAwait(false);
        return await CreateProviderBridge().RunAsync(providers, cancellationToken).ConfigureAwait(false);
    }

    public async Task RetryProviderCommandAsync(string taskId, CancellationToken cancellationToken = default)
    {
        using var lease = taskStore is null ? null : PersonalCloudConnectionLease.AcquireAuthoring(taskStore.DatabasePath);
        await EnsureDispatchAvailableAsync(cancellationToken).ConfigureAwait(false);
        await CreateProviderBridge().RetryAsync(taskId, cancellationToken).ConfigureAwait(false);
    }
    public bool IsConfigured => artifactSlot is not null || !string.IsNullOrWhiteSpace(artifactVersion);
    public PersonalCloudConnectionProfile? CurrentConnection { get; private set; } = connectionStore?.Load();

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = CurrentConnection
            ?? throw new PersonalCloudK0ContractException("not_connected", "Personal Cloud is not connected.");
        return openOwnerBootstrap is null ? Task.CompletedTask : openOwnerBootstrap(connection.ServiceOrigin, cancellationToken);
    }

    public async Task<PersonalCloudProvisioningResult> ConnectAsync(
        CancellationToken cancellationToken = default,
        IProgress<PersonalCloudSetupProgress>? progress = null)
    {
        if (!await _artifactOperation.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new PersonalCloudK0ContractException("operation_in_progress", "Another Personal Cloud setup or update is already running.");
        try
        {
            return await ConnectCoreAsync(cancellationToken, progress).ConfigureAwait(false);
        }
        finally
        {
            _artifactOperation.Release();
        }
    }

    private async Task<PersonalCloudProvisioningResult> ConnectCoreAsync(
        CancellationToken cancellationToken,
        IProgress<PersonalCloudSetupProgress>? progress)
    {
        using var connectionLease = taskStore is null ? null : PersonalCloudConnectionLease.AcquireReplacement(taskStore.DatabasePath);
        if (taskStore is not null)
            await taskStore.EnsureNoPendingPersonalCloudIntentsAsync(cancellationToken).ConfigureAwait(false);
        var version = artifactVersion;
        if (artifactSlot is not null)
        {
            await artifactSlot.PrepareForSetupAsync(cancellationToken).ConfigureAwait(false);
            version = artifactSlot.Version;
        }
        var result = await coordinator.ConnectAsync(version, cancellationToken, progress).ConfigureAwait(false);
        if (connectionStore is not null)
        {
            if (result.InstanceId is null || result.DeviceId is null)
                throw new PersonalCloudK0ContractException("invalid_state", "Personal Cloud pairing did not return a device identity.");
            var connection = new PersonalCloudConnectionProfile
            {
                ServiceOrigin = result.ServiceOrigin,
                InstanceId = result.InstanceId,
                DeviceId = result.DeviceId,
                ArtifactVersion = result.ArtifactVersion,
                ConnectedAt = DateTimeOffset.UtcNow,
            };
            if (taskStore is not null)
                await taskStore.BeginPersonalCloudInitialSyncAsync(connection.InstanceId, connection.DeviceId, CancellationToken.None).ConfigureAwait(false);
            await connectionStore.SaveAsync(connection, cancellationToken).ConfigureAwait(false);
            CurrentConnection = connection;
            await coordinator.ConfirmConnectionSavedAsync(connection, cancellationToken).ConfigureAwait(false);
            if (artifactSlot is not null) await artifactSlot.RecordInstalledAsync(connection).ConfigureAwait(false);
        }
        if (openOwnerBootstrap is not null)
            await openOwnerBootstrap(result.OwnerBootstrapUri, cancellationToken).ConfigureAwait(false);
        progress?.Report(new PersonalCloudSetupProgress(PersonalCloudSetupStep.Complete));
        return result;
    }

    public async Task<PersonalCloudConnectionProfile> PairExistingAsync(
        Uri serviceOrigin,
        string grant,
        string deviceName,
        bool providerBridge,
        CancellationToken cancellationToken = default)
    {
        if (connectionStore is null || deviceIdentity is null || syncHttp is null || taskStore is null)
            throw new PersonalCloudK0ContractException("sync_unavailable", "Personal Cloud pairing is not available in this build.");

        using var connectionLease = PersonalCloudConnectionLease.AcquireReplacement(taskStore.DatabasePath);
        await taskStore.EnsureNoPendingPersonalCloudIntentsAsync(cancellationToken).ConfigureAwait(false);

        var health = await new PersonalCloudHealthClient(syncHttp)
            .InspectAsync(serviceOrigin, cancellationToken).ConfigureAwait(false);
        var paired = await deviceIdentity.PairAsync(serviceOrigin, grant, deviceName, providerBridge, cancellationToken)
            .ConfigureAwait(false);
        var connection = new PersonalCloudConnectionProfile
        {
            ServiceOrigin = paired.ServiceOrigin,
            InstanceId = paired.InstanceId,
            DeviceId = paired.DeviceId,
            ArtifactVersion = health.ArtifactVersion,
            ConnectedAt = DateTimeOffset.UtcNow,
        };
        try
        {
            await taskStore.BeginPersonalCloudInitialSyncAsync(connection.InstanceId, connection.DeviceId, CancellationToken.None).ConfigureAwait(false);
            await connectionStore.SaveAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await deviceIdentity.ForgetAsync(paired.ServiceOrigin, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        CurrentConnection = connection;
        return connection;
    }

    public async Task<CloudflareD1Export> ExportAsync(CancellationToken cancellationToken = default)
    {
        var current = RequireConnection();
        if (management is null)
            throw new PersonalCloudK0ContractException("management_unavailable", "Personal Cloud management is not available in this build.");
        return await management.ExportAsync(current, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PersonalCloudUpgradeResult> UpgradeAsync(CancellationToken cancellationToken = default)
    {
        if (!await _artifactOperation.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new PersonalCloudK0ContractException("operation_in_progress", "Another Personal Cloud setup or update is already running.");
        try
        {
            return await UpgradeCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _artifactOperation.Release();
        }
    }

    private async Task<PersonalCloudUpgradeResult> UpgradeCoreAsync(CancellationToken cancellationToken)
    {
        using var connectionLease = taskStore is null ? null : PersonalCloudConnectionLease.AcquireAuthoring(taskStore.DatabasePath);
        var current = RequireConnection();
        if (management is null || connectionStore is null)
            throw new PersonalCloudK0ContractException("management_unavailable", "Personal Cloud management is not available in this build.");
        if (artifactSlot is not null)
            await artifactSlot.PrepareForUpgradeAsync(await DeployedAsync(current, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        var result = await management.UpgradeAsync(current, cancellationToken).ConfigureAwait(false);
        var updated = current with { ArtifactVersion = result.ArtifactVersion };
        await connectionStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        CurrentConnection = updated;
        if (artifactSlot is not null) await artifactSlot.RecordInstalledAsync(updated).ConfigureAwait(false);
        return result;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        using var connectionLease = taskStore is null ? null : PersonalCloudConnectionLease.AcquireReplacement(taskStore.DatabasePath);
        var current = RequireConnection();
        if (deviceIdentity is null || connectionStore is null)
            throw new PersonalCloudK0ContractException("management_unavailable", "Personal Cloud management is not available in this build.");
        if (taskStore is not null)
            await taskStore.EnsureNoPendingPersonalCloudIntentsAsync(cancellationToken).ConfigureAwait(false);
        await deviceIdentity.RevokeAsync(current.ServiceOrigin, cancellationToken).ConfigureAwait(false);
        await connectionStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        CurrentConnection = null;
    }

    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        var current = RequireConnection();
        if (management is null || deviceIdentity is null || connectionStore is null)
            throw new PersonalCloudK0ContractException("management_unavailable", "Personal Cloud management is not available in this build.");
        await management.RemoveAsync(current, cancellationToken).ConfigureAwait(false);
        await deviceIdentity.ForgetAsync(current.ServiceOrigin, cancellationToken).ConfigureAwait(false);
        await connectionStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        CurrentConnection = null;
    }

    // Another computer may have upgraded this cloud, so the version it actually runs
    // sets the rollback floor. An unhealthy cloud falls back to the saved version so
    // that reinstalling can repair it; a cloud needing a newer Desktop stops here.
    private async Task<PersonalCloudConnectionProfile> DeployedAsync(PersonalCloudConnectionProfile current, CancellationToken cancellationToken)
    {
        if (syncHttp is null) return current;
        try
        {
            var health = await new PersonalCloudHealthClient(syncHttp).InspectAsync(current.ServiceOrigin, cancellationToken).ConfigureAwait(false);
            return current with { ArtifactVersion = health.ArtifactVersion };
        }
        catch (PersonalCloudK0ContractException exception) when (exception.Code != "desktop_update_required" && exception.DispatchStatus is null)
        {
            return current;
        }
    }

    private PersonalCloudConnectionProfile RequireConnection() => CurrentConnection
        ?? throw new PersonalCloudK0ContractException("not_connected", "Personal Cloud is not connected.");
}

public sealed class JsonPersonalCloudProvisioningStateStore(string path) : IPersonalCloudProvisioningStateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<PersonalCloudProvisioningState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PersonalCloudProvisioningState>(stream, Json, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(PersonalCloudProvisioningState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(stream, state, Json, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}

public sealed class PersonalCloudProvisioningCoordinator(
    CloudflareOAuthClient oauth,
    ICloudflareAuthorizationBroker authorizationBroker,
    ICloudflareProvisioningGateway gateway,
    IPersonalCloudProvisioningStateStore stateStore,
    PersonalCloudDeviceIdentityClient deviceIdentity)
{
    public async Task<PersonalCloudProvisioningResult> ConnectAsync(
        string artifactVersion,
        CancellationToken cancellationToken = default,
        IProgress<PersonalCloudSetupProgress>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(artifactVersion) || artifactVersion.Length > 128)
            throw new PersonalCloudK0ContractException("invalid_request", "A versioned Personal Cloud artifact is required.");

        var state = await stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (state is not null && !string.Equals(state.ArtifactVersion, artifactVersion, StringComparison.Ordinal))
            throw new PersonalCloudK0ContractException("setup_resume_required",
                "An interrupted Personal Cloud setup must be finished with its original artifact before starting another setup.");
        if (state is null)
        {
            state = new PersonalCloudProvisioningState
            {
                AttemptId = $"pc_{Guid.NewGuid():N}",
                ArtifactVersion = artifactVersion,
                Stage = PersonalCloudProvisioningStage.NotStarted,
            };
            await stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new PersonalCloudSetupProgress(PersonalCloudSetupStep.AwaitingAuthorization));
        CloudflareOAuthAccess access = await oauth.AuthorizeAsync(authorizationBroker, cancellationToken).ConfigureAwait(false);
        var revokeAttempted = false;
        try
        {
            if (state.Stage < PersonalCloudProvisioningStage.AccountResolved)
            {
                progress?.Report(new PersonalCloudSetupProgress(PersonalCloudSetupStep.ResolvingAccount));
                var account = await gateway.ResolveAccountAsync(access.AccessToken, cancellationToken).ConfigureAwait(false);
                state = state with { Stage = PersonalCloudProvisioningStage.AccountResolved, AccountId = account.AccountId, LastErrorCode = null };
                await stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }
            if (state.Stage < PersonalCloudProvisioningStage.DatabaseReady)
            {
                progress?.Report(new PersonalCloudSetupProgress(PersonalCloudSetupStep.CreatingDatabase));
                var database = await gateway.EnsureDatabaseAsync(access.AccessToken, Required(state.AccountId), state.AttemptId, cancellationToken).ConfigureAwait(false);
                state = state with { Stage = PersonalCloudProvisioningStage.DatabaseReady, DatabaseId = database.DatabaseId, LastErrorCode = null };
                await stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }
            if (state.Stage < PersonalCloudProvisioningStage.ApplicationDeployed)
            {
                progress?.Report(new PersonalCloudSetupProgress(PersonalCloudSetupStep.DeployingApplication));
                var app = await gateway.DeployApplicationAsync(access.AccessToken, Required(state.AccountId), Required(state.DatabaseId), state.AttemptId, artifactVersion, cancellationToken).ConfigureAwait(false);
                state = state with { Stage = PersonalCloudProvisioningStage.ApplicationDeployed, WorkerName = app.WorkerName, ServiceOrigin = app.ServiceOrigin, LastErrorCode = null };
                await stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }
            if (state.Stage < PersonalCloudProvisioningStage.DatabaseMigrated)
            {
                progress?.Report(new PersonalCloudSetupProgress(PersonalCloudSetupStep.PreparingDatabase));
                await gateway.ApplyMigrationsAsync(access.AccessToken, Required(state.AccountId), Required(state.DatabaseId), artifactVersion, cancellationToken).ConfigureAwait(false);
                state = state with { Stage = PersonalCloudProvisioningStage.DatabaseMigrated, LastErrorCode = null };
                await stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }
            if (state.Stage < PersonalCloudProvisioningStage.HealthVerified)
            {
                progress?.Report(new PersonalCloudSetupProgress(PersonalCloudSetupStep.VerifyingConnection));
                await gateway.VerifyHealthAsync(Required(state.ServiceOrigin), artifactVersion, cancellationToken).ConfigureAwait(false);
                state = state with { Stage = PersonalCloudProvisioningStage.HealthVerified, LastErrorCode = null };
                await stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }
            PersonalCloudStoredDevice pairedDevice;
            if (state.Stage < PersonalCloudProvisioningStage.DesktopPaired)
            {
                progress?.Report(new PersonalCloudSetupProgress(PersonalCloudSetupStep.PairingDesktop));
                var pairing = await gateway.CreateInitialPairingAsync(access.AccessToken, Required(state.AccountId), Required(state.DatabaseId), Required(state.ServiceOrigin), cancellationToken).ConfigureAwait(false);
                pairedDevice = await deviceIdentity.PairAsync(Required(state.ServiceOrigin), pairing.Grant, pairing.DeviceName, pairing.ProviderBridge, cancellationToken).ConfigureAwait(false);
                state = state with { Stage = PersonalCloudProvisioningStage.DesktopPaired, LastErrorCode = null };
                await stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                pairedDevice = await deviceIdentity.GetStoredDeviceAsync(Required(state.ServiceOrigin), cancellationToken).ConfigureAwait(false);
            }
            progress?.Report(new PersonalCloudSetupProgress(PersonalCloudSetupStep.CreatingOwnerAccess));
            var ownerBootstrap = await gateway.CreateOwnerBootstrapAsync(
                access.AccessToken, Required(state.AccountId), Required(state.DatabaseId), Required(state.ServiceOrigin), cancellationToken).ConfigureAwait(false);
            var result = new PersonalCloudProvisioningResult(
                Required(state.ServiceOrigin), Required(state.WorkerName), Required(state.DatabaseId), artifactVersion, ownerBootstrap)
            {
                InstanceId = pairedDevice.InstanceId,
                DeviceId = pairedDevice.DeviceId,
            };
            revokeAttempted = true;
            await oauth.RevokeAsync(access.AccessToken, CancellationToken.None).ConfigureAwait(false);
            state = state with { Stage = PersonalCloudProvisioningStage.Complete, LastErrorCode = null };
            await stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            var code = exception is PersonalCloudK0ContractException contract ? contract.Code : "provisioning_failed";
            await stateStore.SaveAsync(state with { LastErrorCode = code }, CancellationToken.None).ConfigureAwait(false);
            if (!revokeAttempted)
            {
                try { await oauth.RevokeAsync(access.AccessToken, CancellationToken.None).ConfigureAwait(false); }
                catch { /* Preserve the original failure and non-secret resume state. */ }
            }
            throw;
        }
    }

    public async Task ConfirmConnectionSavedAsync(
        PersonalCloudConnectionProfile connection, CancellationToken cancellationToken = default)
    {
        var state = await stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (state is null || state.Stage != PersonalCloudProvisioningStage.Complete ||
            state.ServiceOrigin != connection.ServiceOrigin || state.ArtifactVersion != connection.ArtifactVersion)
            throw new PersonalCloudK0ContractException("invalid_state", "The completed setup does not match the saved Personal Cloud connection.");
        await stateStore.ClearAsync(cancellationToken).ConfigureAwait(false);
    }

    private static T Required<T>(T? value) where T : class => value ?? throw new PersonalCloudK0ContractException("invalid_state", "Personal Cloud setup state is incomplete.");
}
