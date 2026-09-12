using System.Collections.ObjectModel;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Services;
using Openza.Tasks.Desktop.Services;

namespace Openza.Tasks.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string CloudPassphraseKey = "onedrive-backup-passphrase";
    private readonly SemaphoreSlim _cloudGate = new(1, 1);
    private string _cloudStatus = "Not connected.";
    private CloudBackupInfo? _selectedCloudBackup;
    public ObservableCollection<CloudBackupInfo> CloudBackups { get; } = [];
    public CloudBackupInfo? SelectedCloudBackup { get => _selectedCloudBackup; set => SetProperty(ref _selectedCloudBackup, value); }
    public string CloudStatus { get => _cloudStatus; private set => SetProperty(ref _cloudStatus, value); }
    public string MicrosoftConnectionText => _preferencesStore.Load().MicrosoftToDoAccount?.Username ?? "Not connected.";
    public string OneDriveConnectionText => _preferencesStore.Load().OneDriveAccount?.Username ?? "Not connected.";
    public bool OneDriveEnabled => _preferencesStore.Load().OneDriveEnabled;
    public bool OneDriveEncrypted => _preferencesStore.Load().OneDriveEncrypted;

    public async Task ConnectMicrosoftAsync(string feature, Func<SignInCode, Task> showCode, CancellationToken cancellationToken)
    {
        if (IsSyncing) throw new InvalidOperationException("Wait for sync to finish before changing accounts.");
        if (!await _cloudGate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("Wait for cloud backup to finish before changing accounts.");
        try
        {
            var access = await _microsoftAuth.ConnectAsync(feature, showCode, cancellationToken);
            await _preferencesStore.UpdateAsync(preferences => feature == "todo"
                ? preferences with { MicrosoftToDoAccount = access.Account }
                : preferences with { OneDriveAccount = access.Account });
            if (feature == "todo") await _store.SetIntegrationConfiguredAsync(IntegrationIds.MicrosoftToDo, true);
            OnPropertyChanged(nameof(MicrosoftConnectionText));
            OnPropertyChanged(nameof(OneDriveConnectionText));
            StatusMessage = "Microsoft account connected";
        }
        finally { _cloudGate.Release(); }
    }

    public async Task DisconnectMicrosoftAsync()
    {
        if (IsSyncing) { ReportError("Wait for sync to finish before disconnecting."); return; }
        await RunBusyAsync(async () =>
        {
            await _microsoftAuth.DisconnectAsync("todo");
            await _preferencesStore.UpdateAsync(preferences => preferences with { MicrosoftToDoAccount = null });
            await _store.SetIntegrationConfiguredAsync(IntegrationIds.MicrosoftToDo, false);
            await _store.SetIntegrationActiveAsync(IntegrationIds.MicrosoftToDo, false);
            OnPropertyChanged(nameof(MicrosoftConnectionText));
            StatusMessage = "Microsoft To Do disconnected; existing tasks remain";
        });
    }

    private string BackupFlavor => DesktopDataPaths.Runtime.Channel.ToString().ToLowerInvariant();
    private CloudBackupService CreateCloudService() => _cloudService ?? new(new OneDriveBackupProvider(_httpClient,
        cancellationToken => _microsoftAuth.GetTokenAsync("backup", _preferencesStore.Load().OneDriveAccount, cancellationToken)));

    public async Task SetCloudEnabledAsync(bool enabled)
    {
        if (enabled && _preferencesStore.Load().OneDriveAccount is null)
            throw new InvalidOperationException("Connect a OneDrive account first.");
        await _preferencesStore.UpdateAsync(preferences => preferences with { OneDriveEnabled = enabled });
        OnPropertyChanged(nameof(OneDriveEnabled));
        if (enabled) await UploadCloudBackupsAsync(createNew: false);
    }

    public async Task SetCloudEncryptionAsync(bool enabled, string? passphrase)
    {
        if (!await _cloudGate.WaitAsync(0)) throw new InvalidOperationException("Wait for cloud backup to finish before changing encryption.");
        try
        {
            if (enabled)
            {
                if (string.IsNullOrWhiteSpace(passphrase)) throw new InvalidOperationException("Enter a passphrase.");
                await _credentials.SaveAsync(CloudPassphraseKey, passphrase);
            }
            else await _credentials.RemoveAsync(CloudPassphraseKey);
            await _preferencesStore.UpdateAsync(preferences => preferences with { OneDriveEncrypted = enabled });
            OnPropertyChanged(nameof(OneDriveEncrypted));
        }
        finally { _cloudGate.Release(); }
    }

    public async Task UploadCloudBackupsAsync(bool createNew)
    {
        if (!OneDriveEnabled || _backupService.Value is not { } backup || !await _cloudGate.WaitAsync(0)) return;
        try
        {
            CloudStatus = "Uploading backups…";
            var preferences = _preferencesStore.Load();
            await Task.Run(async () =>
            {
                if (createNew) await backup.CreateBackupAsync();
                var passphrase = preferences.OneDriveEncrypted ? await _credentials.GetAsync(CloudPassphraseKey) : null;
                await CreateCloudService().UploadPendingBackupsAsync(backup.ListBackupInfo(),
                    new CloudBackupOptions(true, preferences.OneDriveEncrypted, passphrase, BackupFlavor));
            });
            CloudStatus = $"Backup uploaded · {DateTimeOffset.Now:g}";
            LoadRestorePointsCore();
        }
        catch (Exception exception) { CloudStatus = $"Cloud backup failed: {exception.Message}"; ReportError(CloudStatus); }
        finally { _cloudGate.Release(); }
    }

    public async Task RefreshCloudBackupsAsync()
    {
        if (!await _cloudGate.WaitAsync(0)) return;
        try
        {
            CloudStatus = "Loading backups…";
            var backups = await Task.Run(() => CreateCloudService().ListBackupsAsync(BackupFlavor));
            CloudBackups.Clear();
            foreach (var backup in backups) CloudBackups.Add(backup);
            SelectedCloudBackup = CloudBackups.FirstOrDefault();
            CloudStatus = $"{CloudBackups.Count} cloud backups";
        }
        catch (Exception exception) { CloudStatus = $"Cloud backup failed: {exception.Message}"; ReportError(CloudStatus); }
        finally { _cloudGate.Release(); }
    }

    public async Task RestoreCloudBackupAsync(CloudBackupInfo backup, string? passphrase)
    {
        if (IsSyncing) { ReportError("Wait for sync to finish before restoring."); return; }
        if (!await _cloudGate.WaitAsync(0)) return;
        var directory = Path.Combine(Path.GetTempPath(), "openza-cloud-restore", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "restore.db");
        try
        {
            Directory.CreateDirectory(directory);
            CloudStatus = "Downloading backup…";
            await Task.Run(() => CreateCloudService().DownloadBackupAsync(backup, path, passphrase));
            await RestoreDatabaseAsync(path);
            CloudStatus = StatusMessage;
        }
        catch (Exception exception) { CloudStatus = $"Restore failed: {exception.Message}"; ReportError(CloudStatus); }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { _cloudGate.Release(); }
        }
    }
}
