using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
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
                OnPropertyChanged(nameof(CanConnectPersonalCloud));
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
        try
        {
            var result = await _personalCloudSetup.ConnectAsync(cancellationToken, progress);
            var sync = await _personalCloudSetup.SyncAsync(cancellationToken);
            PersonalCloudStatusText = "Connected";
            PersonalCloudSummaryText = $"This desktop is paired with {result.ServiceOrigin.Host}. Cloud connection verified at checkpoint {sync.Checkpoint}.";
            PersonalCloudActionText = "Manage Personal Cloud";
            OnPropertyChanged(nameof(IsPersonalCloudConnected));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PersonalCloudStatusText = "Not connected";
            PersonalCloudSummaryText = "Personal Cloud setup was cancelled. You can start again whenever you are ready.";
            return null;
        }
        catch (PersonalCloudK0ContractException exception)
        {
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
        try
        {
            var connection = await _personalCloudSetup.PairExistingAsync(
                serviceOrigin,
                pairingGrant.Trim(),
                Environment.MachineName,
                providerBridge,
                cancellationToken);
            var sync = await _personalCloudSetup.SyncAsync(cancellationToken);
            PersonalCloudStatusText = "Connected";
            PersonalCloudSummaryText = $"This desktop is paired with {connection.ServiceOrigin.Host}. Cloud connection verified at checkpoint {sync.Checkpoint}.";
            PersonalCloudActionText = "Manage Personal Cloud";
            OnPropertyChanged(nameof(IsPersonalCloudConnected));
            return connection;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PersonalCloudStatusText = "Not connected";
            PersonalCloudSummaryText = "Pairing was cancelled. The one-time code can be retried until it expires.";
            return null;
        }
        catch (PersonalCloudK0ContractException exception)
        {
            PersonalCloudStatusText = "Needs attention";
            PersonalCloudSummaryText = exception.Code == "health_mismatch"
                ? "This Personal Cloud must be updated before this desktop can pair."
                : "The Personal Cloud address or one-time pairing code was rejected.";
            return null;
        }
        catch
        {
            PersonalCloudStatusText = "Needs attention";
            PersonalCloudSummaryText = "Pairing stopped safely. Create a new code in Personal Cloud Settings and try again.";
            return null;
        }
        finally
        {
            IsConnectingPersonalCloud = false;
        }
    }

    public Task OpenPersonalCloudSettingsAsync(CancellationToken cancellationToken = default)
    {
        var current = _personalCloudSetup?.CurrentConnection
            ?? throw new PersonalCloudK0ContractException("not_connected", "Personal Cloud is not connected.");
        return OpenUriAsync(new Uri(current.ServiceOrigin, "/settings"), cancellationToken);
    }

    public async Task<bool> ExportPersonalCloudAsync(CancellationToken cancellationToken = default)
    {
        if (_personalCloudSetup is null) return false;
        return await RunManagementAsync(
            "Preparing backup",
            "Authorize Cloudflare to prepare a complete SQL backup.",
            async () =>
            {
                var result = await _personalCloudSetup.ExportAsync(cancellationToken);
                PersonalCloudSummaryText = $"Cloudflare prepared {result.FileName}. The secure download opened in your browser.";
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
                "oauth_denied" => "Cloudflare authorization was cancelled. No cloud resource was changed.",
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
