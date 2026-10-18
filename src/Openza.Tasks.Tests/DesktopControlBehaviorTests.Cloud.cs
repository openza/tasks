using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Services;
using Openza.Tasks.Desktop.Dialogs;
using Openza.Tasks.Desktop.Shell;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed partial class DesktopControlBehaviorTests
{
    [Fact]
    public Task Cloud_backup_settings_upload_refresh_select_restore_and_encryption_use_simulated_storage() => Run(async context =>
    {
        await context.Preferences.UpdateAsync(value => value with { OneDriveAccount = new("synthetic-account", "synthetic-user") });
        var provider = new ControlBackupProvider();
        var vm = new MainWindowViewModel(context.Store, new InMemoryCredentialStore(), preferencesStore: context.Preferences,
            cloudService: new CloudBackupService(provider));
        var window = new MainWindow(vm) { Width = 1440, Height = 900 };
        window.Show(context.Window);
        try
        {
            await Until(() => vm.StatusMessage.StartsWith("Local data") && !vm.IsBusy);
            window.FindControl<Button>("SettingsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var settings = window.FindControl<ScrollViewer>("SettingsWorkspace")!;
            await Until(() => settings.IsVisible); window.UpdateLayout();
            ToggleSwitch Toggle(string text) => settings.GetVisualDescendants().OfType<ToggleSwitch>().Single(toggle => Equals(toggle.Content, text));
            Toggle("Encrypt backups with a passphrase").IsChecked = true;
            await Until(() => window.OwnedWindows.Any(dialog => dialog.Title == "Backup passphrase"));
            var passphrase = window.OwnedWindows.Single(dialog => dialog.Title == "Backup passphrase");
            foreach (var box in passphrase.GetVisualDescendants().OfType<TextBox>()) box.Text = "synthetic-passphrase";
            Click(passphrase, "Continue"); await Until(() => vm.OneDriveEncrypted);
            Toggle("Automatic OneDrive backup").IsChecked = true;
            await Until(() => vm.OneDriveEnabled && vm.CloudStatus.StartsWith("Backup uploaded"));
            Click(settings, "Back up now");
            await Until(() => provider.Uploads >= 2 && vm.CloudStatus.StartsWith("Backup uploaded"));
            Click(settings, "Refresh backups");
            await Until(() => vm.CloudBackups.Count > 0 && !vm.CloudStatus.StartsWith("Loading"));
            Assert.NotNull(vm.SelectedCloudBackup);
            Assert.True(FindButton(settings, "Restore selected cloud backup").IsEnabled);
            await context.Store.UpsertTaskAsync((await context.Store.GetTaskAsync("task-0"))! with { Title = "After cloud snapshot" });
            Click(settings, "Restore selected cloud backup");
            await Until(() => window.OwnedWindows.OfType<ConfirmWindow>().Any());
            Click(window.OwnedWindows.OfType<ConfirmWindow>().Single(), "Cancel");
            Assert.Equal("After cloud snapshot", (await context.Store.GetTaskAsync("task-0"))!.Title);
            Click(settings, "Restore selected cloud backup");
            await Until(() => window.OwnedWindows.OfType<ConfirmWindow>().Any());
            Click(window.OwnedWindows.OfType<ConfirmWindow>().Single(), "Restore");
            await Until(() => window.OwnedWindows.Any(dialog => dialog.Title == "Backup passphrase"));
            passphrase = window.OwnedWindows.Single(dialog => dialog.Title == "Backup passphrase");
            passphrase.GetVisualDescendants().OfType<TextBox>().First().Text = "synthetic-passphrase";
            Click(passphrase, "Continue");
            await Until(() => vm.CloudStatus.Contains("safety restore point"));
            Assert.Equal("Control test 0", (await context.Store.GetTaskAsync("task-0"))!.Title);
            Toggle("Automatic OneDrive backup").IsChecked = false;
            await Until(() => !vm.OneDriveEnabled);
            Toggle("Encrypt backups with a passphrase").IsChecked = false;
            await Until(() => !vm.OneDriveEncrypted);
            Assert.False(FindButton(settings, "Back up now").IsEnabled);
        }
        finally { window.Close(); }
    });

    private sealed class ControlBackupProvider : ICloudBackupProvider
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        public int Uploads { get; private set; }
        public Task UploadFileAsync(string remotePath, string localPath, string contentType, CancellationToken cancellationToken = default)
        { _files[remotePath] = File.ReadAllBytes(localPath); Uploads++; return Task.CompletedTask; }
        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default)
        { Directory.CreateDirectory(Path.GetDirectoryName(localPath)!); File.WriteAllBytes(localPath, _files[remotePath]); return Task.CompletedTask; }
        public Task<IReadOnlyList<CloudBackupInfo>> ListBackupsAsync(string appFlavor, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CloudBackupInfo>>(_files.Where(pair => pair.Key.StartsWith(CloudBackupPaths.BackupDirectory(appFlavor), StringComparison.Ordinal) && pair.Key.EndsWith(".json", StringComparison.Ordinal))
                .Select(pair => JsonSerializer.Deserialize<CloudBackupInfo>(pair.Value)!).ToList());
        public Task DeleteBackupAsync(CloudBackupInfo backup, CancellationToken cancellationToken = default)
        { _files.Remove(backup.CloudPath); _files.Remove(backup.MetadataPath); return Task.CompletedTask; }
        public Task UploadManifestAsync(string appFlavor, CloudBackupManifest manifest, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
