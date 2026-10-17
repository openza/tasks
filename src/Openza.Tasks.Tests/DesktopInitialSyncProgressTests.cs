using Avalonia.Controls;
using Avalonia.Interactivity;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.Shell;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

[Collection(nameof(EditorTestCollection))]
public sealed class DesktopInitialSyncProgressTests(EditorTestSession fixture)
{
    [Fact]
    public async Task Existing_cloud_pairing_modal_shows_paired_state_and_live_initial_counts()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "openza-pair-progress", Guid.NewGuid().ToString("N"));
            var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
            await store.InitializeAsync();
            var preferences = new DesktopPreferencesStore(Path.Combine(directory, "settings.json"));
            var service = new ProgressService { AllowPair = true };
            var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences, personalCloudSetup: service);
            var window = new Openza.Tasks.Desktop.Dialogs.PersonalCloudSetupWindow(vm);
            try
            {
                window.Show();
                window.FindControl<StackPanel>("ReviewPanel")!.IsVisible = false;
                window.FindControl<StackPanel>("PairPanel")!.IsVisible = true;
                window.FindControl<TextBox>("ExistingCloudAddress")!.Text = "https://synthetic.example.test/";
                window.FindControl<TextBox>("ExistingPairingGrant")!.Text = "synthetic-new-pair-code-123456789";
                var button = window.FindControl<Button>("PairExistingButton")!;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (service.SyncCalls == 0) await Task.Delay(10, deadline.Token);
                Assert.Equal("This desktop is paired", window.FindControl<TextBlock>("PairingTitle")!.Text);
                Assert.Equal("Syncing…", button.Content);
                var text = window.FindControl<TextBlock>("PairingProgressText")!;
                Assert.True(text.IsVisible);
                Assert.Contains("paired", text.Text);
                service.Publish(new("instance-ui", "paired-ui", PersonalCloudInitialSyncPhase.Uploading, 150, 500));
                Assert.Contains("150 of 500 changes uploaded", text.Text);
                Assert.Contains("Spaces, projects and labels", text.Text);
                Assert.False(button.IsEnabled);
                service.Finish();
                while (vm.IsConnectingPersonalCloud || window.IsVisible) await Task.Delay(10, deadline.Token);
                Assert.Equal(1, service.PairCalls);
                Assert.True(vm.InitialSyncProgress!.IsComplete);
            }
            finally { window.Close(); TestDirectory.Delete(directory); }
            return true;
        }, CancellationToken.None);
    }
    [Fact]
    public async Task Restart_shows_persistent_counts_and_retry_uses_normal_sync_without_pairing()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "openza-initial-ui", Guid.NewGuid().ToString("N"));
            var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
            await store.InitializeAsync();
            var preferences = new DesktopPreferencesStore(Path.Combine(directory, "settings.json"));
            await preferences.SaveAsync(new() { AutomaticSyncEnabled = false, AutomaticRestorePointsEnabled = false });
            var service = new ProgressService();
            var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences, personalCloudSetup: service);
            var window = new MainWindow(vm) { Width = 1400, Height = 900 };
            try
            {
                window.Show();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (vm.IsBusy || !vm.HasInitialSyncProgress) await Task.Delay(10, deadline.Token);
                var banner = window.FindControl<Border>("InitialSyncBanner")!;
                Assert.True(banner.IsVisible);
                Assert.Contains("150 of 500 changes uploaded", vm.InitialSyncSummary);
                Assert.Contains("interrupted", vm.PersonalCloudSummaryText);
                Assert.Equal("Sync interrupted", vm.PersonalCloudStatusText);
                var retry = (Button)((Grid)banner.Child!).Children[1];
                Assert.True(retry.IsVisible);
                retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                while (service.SyncCalls == 0) await Task.Delay(10, deadline.Token);
                Assert.True(vm.IsSyncing);
                Assert.False(vm.CanRetryInitialSync);
                service.Publish(new("instance-ui", "device-ui", PersonalCloudInitialSyncPhase.Uploading, 300, 500));
                Assert.Contains("300 of 500 changes uploaded", vm.InitialSyncSummary);
                Assert.Contains("Spaces, projects and labels", vm.PersonalCloudSummaryText);
                service.Finish();
                while (vm.IsSyncing) await Task.Delay(10, deadline.Token);
                Assert.True(vm.InitialSyncProgress!.IsComplete);
                Assert.False(retry.IsVisible);
                Assert.Equal(0, service.PairCalls);
                Assert.Equal(0, service.ConnectCalls);
                Assert.Equal(1, service.SyncCalls);
                Assert.True(vm.IsPersonalCloudConnected);
                Assert.Contains("Initial sync complete", vm.PersonalCloudSummaryText);
            }
            finally { window.Close(); await Task.Delay(100); TestDirectory.Delete(directory); }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Cancelled_setup_after_profile_save_keeps_paired_status_and_retry_guidance()
    {
        var directory = Path.Combine(Path.GetTempPath(), "openza-initial-cancel", Guid.NewGuid().ToString("N"));
        var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
        await store.InitializeAsync();
        var preferences = new DesktopPreferencesStore(Path.Combine(directory, "settings.json"));
        var service = new ProgressService { FailConnectAfterSave = true };
        var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences, personalCloudSetup: service);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            Assert.Null(await vm.ConnectPersonalCloudAsync(cancellation.Token));
            Assert.True(vm.IsPersonalCloudConnected);
            Assert.DoesNotContain("Not connected", vm.PersonalCloudStatusText);
            Assert.Contains("Retry Sync", vm.PersonalCloudSummaryText);
            Assert.Equal("Manage Personal Cloud", vm.PersonalCloudActionText);
        }
        finally { TestDirectory.Delete(directory); }
    }

    [Fact]
    public async Task Background_initial_sync_does_not_replace_backup_prompt_success_or_failure()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "openza-backup-progress", Guid.NewGuid().ToString("N"));
            var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
            await store.InitializeAsync();
            var preferences = new DesktopPreferencesStore(Path.Combine(directory, "settings.json"));
            await preferences.SaveAsync(new() { AutomaticSyncEnabled = false, AutomaticRestorePointsEnabled = false });
            var service = new ProgressService();
            var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences, personalCloudSetup: service);
            try
            {
                var complete = new PersonalCloudInitialSyncProgress("instance-ui", "device-ui", PersonalCloudInitialSyncPhase.Complete, 500, 500);
                service.ExportAction = _ =>
                {
                    service.Publish(complete);
                    Assert.Equal("Waiting for Cloudflare authorization", vm.PersonalCloudStatusText);
                    Assert.Contains("Complete Cloudflare authorization", vm.PersonalCloudSummaryText);
                    return Task.FromResult(new CloudflareD1Export("backup.sql", new Uri("https://downloads.example.test/backup.sql")));
                };
                Assert.True(await vm.ExportPersonalCloudAsync((_, _) => Task.CompletedTask));
                service.Publish(complete);
                Assert.Equal("Personal Cloud backup saved.", vm.PersonalCloudSummaryText);
                Assert.True(vm.InitialSyncProgress!.IsComplete);

                Assert.False(await vm.ExportPersonalCloudAsync((_, _) => throw new IOException("synthetic failure")));
                var error = vm.PersonalCloudSummaryText;
                service.Publish(complete);
                Assert.Equal(error, vm.PersonalCloudSummaryText);
                Assert.Equal("Needs attention", vm.PersonalCloudStatusText);
            }
            finally { TestDirectory.Delete(directory); }
            return true;
        }, CancellationToken.None);
    }

    private sealed class ProgressService : IPersonalCloudSetupService, IPersonalCloudInitialSyncProgressService
    {
        private PersonalCloudInitialSyncProgress _progress = new("instance-ui", "device-ui", PersonalCloudInitialSyncPhase.Interrupted, 150, 500, "interrupted");
        private readonly TaskCompletionSource<PersonalCloudSyncResult> _sync = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsConfigured => true;
        public PersonalCloudConnectionProfile? CurrentConnection { get; private set; } = new()
        {
            ServiceOrigin = new Uri("https://synthetic.example.test/"), InstanceId = "instance-ui", DeviceId = "device-ui",
            ArtifactVersion = "test", ConnectedAt = DateTimeOffset.UtcNow,
        };
        public bool FailConnectAfterSave { get; init; }
        public bool AllowPair { get; init; }
        public int ConnectCalls { get; private set; }
        public int PairCalls { get; private set; }
        public int SyncCalls { get; private set; }
        public event Action<PersonalCloudInitialSyncProgress>? InitialSyncProgressChanged;
        public Task<PersonalCloudInitialSyncProgress?> GetInitialSyncProgressAsync(CancellationToken cancellationToken = default) => Task.FromResult<PersonalCloudInitialSyncProgress?>(_progress);
        public void Publish(PersonalCloudInitialSyncProgress progress) { _progress = progress; InitialSyncProgressChanged?.Invoke(progress); }
        public void Finish()
        {
            Publish(_progress with { Phase = PersonalCloudInitialSyncPhase.Complete, Acknowledged = 500 });
            _sync.SetResult(new(350, 0, 0) { InitialSyncProgress = _progress });
        }
        public Task<PersonalCloudSyncResult> SyncAsync(CancellationToken cancellationToken = default) { SyncCalls++; return _sync.Task; }
        public Task<PersonalCloudProvisioningResult> ConnectAsync(CancellationToken cancellationToken = default, IProgress<PersonalCloudSetupProgress>? progress = null)
        {
            ConnectCalls++;
            if (FailConnectAfterSave)
            {
                CurrentConnection = CurrentConnection! with { DeviceId = "replacement-ui" };
                _progress = _progress with { DeviceId = "replacement-ui" };
                return Task.FromCanceled<PersonalCloudProvisioningResult>(cancellationToken);
            }
            throw new InvalidOperationException("Setup must not run for a sync retry");
        }
        public Task<PersonalCloudConnectionProfile> PairExistingAsync(Uri origin, string grant, string name, bool bridge, CancellationToken cancellationToken = default)
        {
            PairCalls++;
            if (!AllowPair) throw new InvalidOperationException("Pairing must not run for a sync retry");
            CurrentConnection = CurrentConnection! with { DeviceId = "paired-ui" };
            _progress = _progress with { DeviceId = "paired-ui" };
            return Task.FromResult(CurrentConnection);
        }
        public Task OpenAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Func<CancellationToken, Task<CloudflareD1Export>>? ExportAction { get; set; }
        public Task<CloudflareD1Export> ExportAsync(CancellationToken cancellationToken = default) => ExportAction?.Invoke(cancellationToken) ?? throw new NotSupportedException();
        public Task<PersonalCloudUpgradeResult> UpgradeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
