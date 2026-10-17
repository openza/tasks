using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private PersonalCloudInitialSyncProgress? _initialSyncProgress;
    private bool _isPairingInitialSync;
    public bool IsPairingInitialSync
    {
        get => _isPairingInitialSync;
        private set
        {
            if (!SetProperty(ref _isPairingInitialSync, value)) return;
            OnPropertyChanged(nameof(PersonalCloudPairActionText));
            OnPropertyChanged(nameof(PersonalCloudPairTitle));
        }
    }
    public string PersonalCloudPairActionText => IsConnectingPersonalCloud ? IsPairingInitialSync ? "Syncing…" : "Pairing…" : "Pair desktop";
    public string PersonalCloudPairTitle => IsPairingInitialSync ? "This desktop is paired" : "Pair this desktop";
    public PersonalCloudInitialSyncProgress? InitialSyncProgress => _initialSyncProgress;
    public string InitialSyncSummary => _initialSyncProgress?.Summary ?? string.Empty;
    public bool HasInitialSyncProgress => IsPersonalCloudConnected && _initialSyncProgress is not null;
    public bool CanRetryInitialSync => HasInitialSyncProgress && _initialSyncProgress is { IsComplete: false } && !IsSyncing && !IsConnectingPersonalCloud;

    private void ApplyInitialSyncProgress(PersonalCloudInitialSyncProgress progress)
    {
        if (_personalCloudSetup?.CurrentConnection is not { } current || current.InstanceId != progress.InstanceId || current.DeviceId != progress.DeviceId) return;
        if (_initialSyncProgress is { IsComplete: true } completed && completed.InstanceId == progress.InstanceId && completed.DeviceId == progress.DeviceId && !progress.IsComplete) return;
        _initialSyncProgress = progress;
        OnPropertyChanged(nameof(InitialSyncProgress));
        OnPropertyChanged(nameof(InitialSyncSummary));
        OnPropertyChanged(nameof(HasInitialSyncProgress));
        OnPropertyChanged(nameof(CanRetryInitialSync));
        PersonalCloudSummaryText = progress.Summary;
        PersonalCloudStatusText = progress.Phase switch
        {
            PersonalCloudInitialSyncPhase.Complete => "Connected",
            PersonalCloudInitialSyncPhase.Blocked => "Sync blocked",
            PersonalCloudInitialSyncPhase.Interrupted => "Sync interrupted",
            _ => "Initial sync",
        };
    }

    private async Task RefreshInitialSyncProgressAsync()
    {
        if (_personalCloudSetup is IPersonalCloudInitialSyncProgressService service)
        {
            if (await service.GetInitialSyncProgressAsync() is { } progress) ApplyInitialSyncProgress(progress);
            else if (IsPersonalCloudConnected)
                PersonalCloudSummaryText = "This desktop is paired. Initial sync progress is unavailable for this connection.";
        }
    }

    private async Task<PersonalCloudSyncResult> SyncPersonalCloudWithProgressAsync(CancellationToken cancellationToken = default)
    {
        var result = await _personalCloudSetup!.SyncAsync(cancellationToken);
        if (result.InitialSyncProgress is { } progress) ApplyInitialSyncProgress(progress);
        return result;
    }
    public System.Collections.ObjectModel.ObservableCollection<Openza.Tasks.Core.Data.PersonalCloudProviderRequestStatus> ProviderRequests { get; } = [];

    private async Task UpsertSpaceWithCloudAsync(Openza.Tasks.Core.Models.SpaceItem space)
    {
        if (_store is Openza.Tasks.Core.Data.SqliteTaskStore sqlite && _personalCloudSetup?.CurrentConnection is { } cloud &&
            await sqlite.SupportsPersonalCloudWorkspaceAsync(cloud.InstanceId))
            await sqlite.UpsertSpaceWithPersonalCloudIntentAsync(space, cloud.InstanceId, cloud.DeviceId);
        else await _store.UpsertSpaceAsync(space);
    }

    private async Task UpsertProjectWithCloudAsync(Openza.Tasks.Core.Models.ProjectItem project)
    {
        if (project.IntegrationId == Openza.Tasks.Core.Models.IntegrationIds.Local && _store is Openza.Tasks.Core.Data.SqliteTaskStore sqlite &&
            _personalCloudSetup?.CurrentConnection is { } cloud && await sqlite.SupportsPersonalCloudWorkspaceAsync(cloud.InstanceId))
            await sqlite.UpsertProjectWithPersonalCloudIntentAsync(project, cloud.InstanceId, cloud.DeviceId);
        else await _store.UpsertProjectAsync(project);
    }

    private async Task RefreshProviderRequestStatusAsync()
    {
        ProviderRequests.Clear();
        if (_store is not Openza.Tasks.Core.Data.SqliteTaskStore store || _personalCloudSetup?.CurrentConnection is not { } current) return;
        foreach (var item in await store.GetPersonalCloudProviderRequestStatusAsync(current.InstanceId)) ProviderRequests.Add(item);
    }

    public async Task RetryProviderRequestAsync(string taskId)
    {
        if (_personalCloudSetup is not IPersonalCloudProviderBridgeService bridge || !await _syncGate.WaitAsync(0)) return;
        IsSyncing = true;
        try
        {
            await bridge.RetryProviderCommandAsync(taskId);
            await SyncPersonalCloudWithProgressAsync();
            await RefreshProviderRequestStatusAsync();
            StatusMessage = "Provider retry requested. Sync to deliver it through the connected account.";
        }
        catch (Exception exception) { SetPersistentStatusMessage(exception.Message); }
        finally { IsSyncing = false; _syncGate.Release(); }
    }
    public string PersonalCloudStatusText
    {
        get => _personalCloudStatusText;
        private set => SetProperty(ref _personalCloudStatusText, value);
    }

    public string PersonalCloudSummaryText
    {
        get => _personalCloudSummaryText;
        private set => SetProperty(ref _personalCloudSummaryText, value);
    }

    public string PersonalCloudActionText
    {
        get => _personalCloudActionText;
        private set => SetProperty(ref _personalCloudActionText, value);
    }

    public bool IsConnectingPersonalCloud
    {
        get => _isConnectingPersonalCloud;
        private set
        {
            if (SetProperty(ref _isConnectingPersonalCloud, value))
            {
                OnPropertyChanged(nameof(CanConnectPersonalCloud));
                OnPropertyChanged(nameof(CanRetryInitialSync));
                OnPropertyChanged(nameof(PersonalCloudPairActionText));
            }
        }
    }

    public bool CanConnectPersonalCloud => _personalCloudSetup?.IsConfigured == true && !IsConnectingPersonalCloud;
    public bool IsPersonalCloudConnected => _personalCloudSetup?.CurrentConnection is not null;

    public async Task<PersonalCloudProvisioningResult?> ConnectPersonalCloudAsync(
        CancellationToken cancellationToken = default,
        IProgress<PersonalCloudSetupProgress>? progress = null)
    {
        if (_personalCloudSetup?.IsConfigured != true)
        {
            PersonalCloudStatusText = "Not available in this build";
            PersonalCloudSummaryText = "Cloudflare authorization must be configured before Personal Cloud can create account resources.";
            return null;
        }

        IsConnectingPersonalCloud = true;
        PersonalCloudStatusText = "Connecting";
        PersonalCloudSummaryText = "Authorize Cloudflare in your browser. Openza will then create and verify your Personal Cloud.";
        var previousConnection = _personalCloudSetup.CurrentConnection;
        try
        {
            var result = await _personalCloudSetup.ConnectAsync(cancellationToken, progress);
            PersonalCloudStatusText = "Initial sync";
            PersonalCloudSummaryText = "Your cloud is ready. Your existing tasks are now syncing.";
            PersonalCloudActionText = "Manage Personal Cloud";
            OnPropertyChanged(nameof(IsPersonalCloudConnected));
            PersonalCloudSyncResult sync;
            try { sync = await SyncPersonalCloudWithProgressAsync(cancellationToken); }
            catch
            {
                await RefreshInitialSyncProgressAsync();
                if (_initialSyncProgress is null)
                    PersonalCloudSummaryText = "Your cloud is ready. Initial sync was interrupted. Retry Sync; no new setup is needed.";
                return result;
            }
            PersonalCloudStatusText = "Connected";
            PersonalCloudSummaryText = sync.BlockedCode is null
                ? $"This desktop is paired with {result.ServiceOrigin.Host}. Cloud connection verified at checkpoint {sync.Checkpoint}."
                : $"This desktop is paired. Sync is blocked ({sync.BlockedCode}); pending edits are preserved and require resolution.";
            if (sync.InitialSyncProgress is { } progressState) ApplyInitialSyncProgress(progressState);
            PersonalCloudActionText = "Manage Personal Cloud";
            OnPropertyChanged(nameof(IsPersonalCloudConnected));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_personalCloudSetup.CurrentConnection is { } saved && saved != previousConnection)
            {
                ReportPairedSyncIncomplete(saved);
                await RefreshInitialSyncProgressAsync();
                return null;
            }
            PersonalCloudStatusText = "Not connected";
            PersonalCloudSummaryText = "Personal Cloud setup was cancelled. You can start again whenever you are ready.";
            return null;
        }
        catch (PersonalCloudK0ContractException exception)
        {
            if (_personalCloudSetup.CurrentConnection is { } saved && saved != previousConnection)
            {
                ReportPairedSyncIncomplete(saved);
                await RefreshInitialSyncProgressAsync();
                return null;
            }
            PersonalCloudStatusText = "Needs attention";
            PersonalCloudSummaryText = exception.Code switch
            {
                "oauth_denied" => "Cloudflare authorization was cancelled. No new setup step was started.",
                "oauth_state_mismatch" => "Cloudflare authorization did not match this setup attempt. Start again from Openza.",
                "oauth_revoke_failed" => "Personal Cloud was created, but Cloudflare authorization could not be discarded. Revoke Openza from Cloudflare before retrying.",
                _ => "Personal Cloud setup stopped safely. Choose Connect Cloudflare to resume from the last verified step.",
            };
            return null;
        }
        catch
        {
            if (_personalCloudSetup.CurrentConnection is { } saved && saved != previousConnection)
            {
                ReportPairedSyncIncomplete(saved);
                await RefreshInitialSyncProgressAsync();
                return null;
            }
            PersonalCloudStatusText = "Needs attention";
            PersonalCloudSummaryText = "Personal Cloud setup stopped safely. Choose Set up Personal Cloud to resume from the last verified step.";
            return null;
        }
        finally
        {
            IsConnectingPersonalCloud = false;
        }
    }

    public Task OpenPersonalCloudAsync(CancellationToken cancellationToken = default) =>
        _personalCloudSetup?.OpenAsync(cancellationToken) ?? Task.CompletedTask;

    public async Task<PersonalCloudConnectionProfile?> PairExistingPersonalCloudAsync(
        string serviceAddress,
        string pairingGrant,
        bool providerBridge,
        CancellationToken cancellationToken = default)
    {
        if (_personalCloudSetup?.IsConfigured != true ||
            !Uri.TryCreate(serviceAddress.Trim(), UriKind.Absolute, out var serviceOrigin))
        {
            PersonalCloudStatusText = "Needs attention";
            PersonalCloudSummaryText = "Enter the HTTPS address shown by your Personal Cloud.";
            return null;
        }

        IsConnectingPersonalCloud = true;
        PersonalCloudStatusText = "Pairing";
        PersonalCloudSummaryText = "Verifying this Personal Cloud and pairing this desktop.";
        PersonalCloudConnectionProfile? pairedConnection = null;
        try
        {
            var connection = await _personalCloudSetup.PairExistingAsync(
                serviceOrigin,
                pairingGrant.Trim(),
                Environment.MachineName,
                providerBridge,
                cancellationToken);
            pairedConnection = connection;
            IsPairingInitialSync = true;
            PersonalCloudStatusText = "Initial sync";
            PersonalCloudSummaryText = "This desktop is paired. Your existing tasks are now syncing.";
            PersonalCloudActionText = "Manage Personal Cloud";
            OnPropertyChanged(nameof(IsPersonalCloudConnected));
            var sync = await SyncPersonalCloudWithProgressAsync(cancellationToken);
            PersonalCloudStatusText = "Connected";
            PersonalCloudSummaryText = sync.BlockedCode is null
                ? $"This desktop is paired with {connection.ServiceOrigin.Host}. Cloud connection verified at checkpoint {sync.Checkpoint}."
                : $"This desktop is paired. Sync is blocked ({sync.BlockedCode}); pending edits are preserved and require resolution.";
            if (sync.InitialSyncProgress is { } progressState) ApplyInitialSyncProgress(progressState);
            PersonalCloudActionText = "Manage Personal Cloud";
            OnPropertyChanged(nameof(IsPersonalCloudConnected));
            return connection;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (pairedConnection is not null) return ReportPairedSyncIncomplete(pairedConnection);
            PersonalCloudStatusText = "Not connected";
            PersonalCloudSummaryText = "Pairing was cancelled. The one-time code can be retried until it expires.";
            return null;
        }
        catch (PersonalCloudK0ContractException exception)
        {
            if (pairedConnection is not null) return ReportPairedSyncIncomplete(pairedConnection);
            PersonalCloudStatusText = "Needs attention";
            PersonalCloudSummaryText = exception.Code switch
            {
                "health_mismatch" => "This Personal Cloud must be updated before this desktop can pair.",
                "pending_intents" => "Pending edits or deferred cloud changes must be synced or explicitly resolved before replacing this connection. Your edits have been preserved.",
                _ => "The Personal Cloud address or one-time pairing code was rejected.",
            };
            return null;
        }
        catch
        {
            if (pairedConnection is not null) return ReportPairedSyncIncomplete(pairedConnection);
            PersonalCloudStatusText = "Needs attention";
            PersonalCloudSummaryText = "Pairing stopped safely. Create a new code in Personal Cloud Settings and try again.";
            return null;
        }
        finally
        {
            IsPairingInitialSync = false;
            IsConnectingPersonalCloud = false;
        }
    }

    private PersonalCloudConnectionProfile ReportPairedSyncIncomplete(PersonalCloudConnectionProfile connection)
    {
        PersonalCloudStatusText = "Needs attention";
        PersonalCloudSummaryText = "This desktop is paired, but its first sync did not finish. Retry Sync; no new pairing code is needed.";
        PersonalCloudActionText = "Manage Personal Cloud";
        OnPropertyChanged(nameof(IsPersonalCloudConnected));
        if (_initialSyncProgress is { } progress) ApplyInitialSyncProgress(progress);
        return connection;
    }

    public Task OpenPersonalCloudSettingsAsync(CancellationToken cancellationToken = default)
    {
        var current = _personalCloudSetup?.CurrentConnection
            ?? throw new PersonalCloudK0ContractException("not_connected", "Personal Cloud is not connected.");
        return OpenUriAsync(new Uri(current.ServiceOrigin, "/settings"), cancellationToken);
    }

    public async Task<bool> ExportPersonalCloudAsync(
        Func<CloudflareD1Export, CancellationToken, Task> saveBackup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(saveBackup);
        if (_personalCloudSetup is null) return false;
        return await RunManagementAsync(
            "Waiting for Cloudflare authorization",
            "Complete Cloudflare authorization in your browser to prepare a backup. You can cancel this attempt in the management window; authorization expires after five minutes.",
            async () =>
            {
                var result = await _personalCloudSetup.ExportAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                PersonalCloudStatusText = "Saving backup";
                PersonalCloudSummaryText = "Choose a local destination for the Personal Cloud backup.";
                await saveBackup(result, cancellationToken);
                PersonalCloudSummaryText = "Personal Cloud backup saved.";
            });
    }

    public async Task<bool> UpgradePersonalCloudAsync(CancellationToken cancellationToken = default)
    {
        if (_personalCloudSetup is null) return false;
        return await RunManagementAsync(
            "Updating",
            "Authorize Cloudflare to deploy the version bundled with this Openza build.",
            async () =>
            {
                var result = await _personalCloudSetup.UpgradeAsync(cancellationToken);
                PersonalCloudSummaryText = $"Personal Cloud is updated to {result.ArtifactVersion}.";
            });
    }

    public async Task<bool> DisconnectPersonalCloudAsync(CancellationToken cancellationToken = default)
    {
        if (_personalCloudSetup is null) return false;
        return await RunManagementAsync(
            "Disconnecting",
            "Revoking this desktop without deleting local tasks or the Personal Cloud.",
            async () =>
            {
                await _personalCloudSetup.DisconnectAsync(cancellationToken);
                SetDisconnected("This desktop is disconnected. Your local tasks and Personal Cloud remain available.");
            });
    }

    public async Task<bool> RemovePersonalCloudAsync(CancellationToken cancellationToken = default)
    {
        if (_personalCloudSetup is null) return false;
        return await RunManagementAsync(
            "Removing",
            "Authorize Cloudflare to permanently delete the Personal Cloud Web app and database.",
            async () =>
            {
                await _personalCloudSetup.RemoveAsync(cancellationToken);
                SetDisconnected("Personal Cloud was removed. Your local Openza tasks remain on this desktop.");
            });
    }

    private async Task<bool> RunManagementAsync(string status, string summary, Func<Task> action)
    {
        IsConnectingPersonalCloud = true;
        PersonalCloudStatusText = status;
        PersonalCloudSummaryText = summary;
        try
        {
            await action();
            if (IsPersonalCloudConnected) PersonalCloudStatusText = "Connected";
            return true;
        }
        catch (OperationCanceledException)
        {
            PersonalCloudStatusText = IsPersonalCloudConnected ? "Connected" : "Not connected";
            PersonalCloudSummaryText = "The Personal Cloud action was cancelled. No further change was made.";
            return false;
        }
        catch (PersonalCloudK0ContractException exception)
        {
            PersonalCloudStatusText = "Needs attention";
            PersonalCloudSummaryText = exception.Code switch
            {
                "pending_intents" => "Pending edits or deferred cloud changes must be synced or explicitly resolved before changing this connection. Your edits have been preserved.",
                "oauth_denied" => "Cloudflare authorization was cancelled. No cloud resource was changed.",
                "oauth_timeout" => "Cloudflare authorization expired. Open Manage Personal Cloud and try Download backup again.",
                "resource_not_found" => "The Personal Cloud resources were not found in the authorized Cloudflare account.",
                "account_selection_required" => "Authorize only the Cloudflare account containing this Personal Cloud.",
                "cloudflare_export_timeout" => "Cloudflare did not finish the backup in time. Your Personal Cloud was not changed.",
                _ => "The Personal Cloud action stopped safely. You can try again.",
            };
            return false;
        }
        catch
        {
            PersonalCloudStatusText = "Needs attention";
            PersonalCloudSummaryText = "The Personal Cloud action stopped safely. You can try again.";
            return false;
        }
        finally
        {
            IsConnectingPersonalCloud = false;
        }
    }

    private void SetDisconnected(string summary)
    {
        _initialSyncProgress = null;
        OnPropertyChanged(nameof(HasInitialSyncProgress));
        OnPropertyChanged(nameof(CanRetryInitialSync));
        OnPropertyChanged(nameof(InitialSyncSummary));
        PersonalCloudStatusText = "Not connected";
        PersonalCloudSummaryText = summary;
        PersonalCloudActionText = "Set up Personal Cloud";
        OnPropertyChanged(nameof(IsPersonalCloudConnected));
    }

    private static Task OpenUriAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
