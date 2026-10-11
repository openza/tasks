using Avalonia.Controls;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed class DesktopPersonalCloudSettingsTests(EditorTestSession fixture) : IClassFixture<EditorTestSession>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Abandoned_backup_can_be_cancelled_by_button_or_window_close_and_retried(bool closeWindow)
    {
        await fixture.Session.Dispatch(async () =>
        {
            var connection = new PersonalCloudConnectionProfile
            {
                ServiceOrigin = new Uri("https://openza-personal-test.workers.dev/"),
                InstanceId = "instance-test", DeviceId = "device-test", ArtifactVersion = "1.2.3", ConnectedAt = DateTimeOffset.UtcNow,
            };
            var service = new StubSetupService(new(connection.ServiceOrigin, "worker-test", "database-test", "1.2.3", connection.ServiceOrigin), connection);
            service.ExportAction = async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Unreachable");
            };
            var vm = CreateViewModel(service);
            var window = new Openza.Tasks.Desktop.Dialogs.PersonalCloudManageWindow(vm);
            window.Show();
            try
            {
                var export = window.FindControl<Avalonia.Controls.Button>("ExportButton")!;
                var cancel = window.FindControl<Avalonia.Controls.Button>("CancelBackupButton")!;
                var result = window.FindControl<TextBlock>("ResultText")!;
                export.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                Assert.True(vm.IsConnectingPersonalCloud);
                Assert.Equal("Waiting for Cloudflare authorization", vm.PersonalCloudStatusText);
                Assert.Equal(vm.PersonalCloudSummaryText, result.Text);
                Assert.Contains("Complete Cloudflare authorization", result.Text);
                Assert.True(cancel.IsVisible);
                Assert.False(export.IsEnabled);
                if (closeWindow) window.Close();
                else cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (vm.IsConnectingPersonalCloud || cancel.IsVisible) await Task.Delay(10, deadline.Token);
                Assert.True(export.IsEnabled);
                Assert.Equal(connection, service.CurrentConnection);
                Assert.Equal("Connected", vm.PersonalCloudStatusText);
                if (!closeWindow)
                {
                    Assert.Contains("cancelled", result.Text);
                    export.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                    Assert.Contains("Complete Cloudflare authorization", result.Text);
                    Assert.DoesNotContain("cancelled", result.Text);
                    cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                    while (vm.IsConnectingPersonalCloud || cancel.IsVisible) await Task.Delay(10, deadline.Token);
                }
                service.ExportAction = null;
                Assert.True(await vm.ExportPersonalCloudAsync((_, _) => Task.CompletedTask));
                Assert.Equal(closeWindow ? 2 : 3, service.ExportCalls);
            }
            finally { window.Close(); }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Authorization_timeout_clears_busy_and_explains_retry_without_saving()
    {
        var service = new StubSetupService(new(new Uri("https://openza-personal-test.workers.dev/"), "worker", "database", "1.2.3", new Uri("https://example.com/")));
        service.ExportAction = _ => Task.FromException<CloudflareD1Export>(new PersonalCloudK0ContractException("oauth_timeout", "private details"));
        var vm = CreateViewModel(service);
        Assert.False(await vm.ExportPersonalCloudAsync((_, _) => throw new InvalidOperationException("Must not save")));
        Assert.False(vm.IsConnectingPersonalCloud);
        Assert.Contains("authorization expired", vm.PersonalCloudSummaryText);
        Assert.Contains("Download backup again", vm.PersonalCloudSummaryText);
        Assert.DoesNotContain("private details", vm.PersonalCloudSummaryText);
    }
    [Fact]
    public void Connect_action_stays_disabled_when_cloudflare_is_not_configured()
    {
        var vm = CreateViewModel(null);

        Assert.False(vm.CanConnectPersonalCloud);
        Assert.Equal("Not connected", vm.PersonalCloudStatusText);
    }

    [Fact]
    public async Task Successful_permanent_setup_reports_the_paired_instance()
    {
        var service = new StubSetupService(new PersonalCloudProvisioningResult(
            new Uri("https://openza-personal-test.workers.dev/"),
            "openza-personal-test",
            "database-test",
            "1.2.3",
            new Uri("https://openza-personal-test.workers.dev/#setup=owner-bootstrap-grant")));
        var vm = CreateViewModel(service);

        await vm.ConnectPersonalCloudAsync();

        Assert.True(vm.CanConnectPersonalCloud);
        Assert.Equal("Connected", vm.PersonalCloudStatusText);
        Assert.Contains("openza-personal-test.workers.dev", vm.PersonalCloudSummaryText, StringComparison.Ordinal);
        Assert.Equal(1, service.ConnectCalls);
    }

    [Fact]
    public async Task Manual_and_automatic_sync_include_connected_personal_cloud()
    {
        var connection = new PersonalCloudConnectionProfile
        {
            ServiceOrigin = new Uri("https://openza-personal-test.workers.dev/"),
            InstanceId = "instance-test",
            DeviceId = "device-test",
            ArtifactVersion = "1.2.3",
            ConnectedAt = DateTimeOffset.UtcNow,
        };
        var service = new StubSetupService(
            new PersonalCloudProvisioningResult(
                connection.ServiceOrigin,
                "openza-personal-test",
                "database-test",
                connection.ArtifactVersion,
                new Uri("https://openza-personal-test.workers.dev/#setup=owner-bootstrap-grant")),
            connection,
            new PersonalCloudSyncResult(2, 3, 17));
        var path = Path.Combine(Path.GetTempPath(), $"openza-personal-cloud-sync-ui-{Guid.NewGuid():N}.db");
        var store = new SqliteTaskStore(path);
        await store.InitializeAsync();
        var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), personalCloudSetup: service);

        await vm.RunTodoistSyncAsync();
        await vm.RunAutomaticTodoistSyncAsync();

        Assert.Equal(2, service.SyncCalls);
        Assert.Contains("Personal Cloud synced", vm.LastSyncResult, StringComparison.Ordinal);
        Assert.Contains("2 uploaded, 3 downloaded", vm.LastSyncResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Existing_cloud_pairing_uses_the_one_time_grant_and_syncs()
    {
        var service = new StubSetupService(new PersonalCloudProvisioningResult(
            new Uri("https://openza-personal-test.workers.dev/"), "worker", "database", "1.2.3",
            new Uri("https://openza-personal-test.workers.dev/#setup=unused")));
        var vm = CreateViewModel(service);

        var connection = await vm.PairExistingPersonalCloudAsync(
            "https://openza-personal-test.workers.dev/", "pair-this-is-a-long-one-time-code", true);

        Assert.NotNull(connection);
        Assert.Equal("Connected", vm.PersonalCloudStatusText);
        Assert.Equal(1, service.PairCalls);
        Assert.Equal(1, service.SyncCalls);
        Assert.True(service.LastProviderBridge);
    }

    [Theory]
    [InlineData("contract")]
    [InlineData("transport")]
    [InlineData("cancel")]
    public async Task First_sync_failure_after_pairing_preserves_connection_and_does_not_request_a_new_code(string failure)
    {
        var origin = new Uri("https://openza-personal-test.workers.dev/");
        var service = new StubSetupService(new(origin, "worker", "database", "1.2.3", origin));
        using var cancellation = new CancellationTokenSource();
        service.SyncAction = token =>
        {
            if (failure == "cancel")
            {
                cancellation.Cancel();
                return Task.FromCanceled<PersonalCloudSyncResult>(token);
            }
            return Task.FromException<PersonalCloudSyncResult>(failure == "contract"
                ? new PersonalCloudK0ContractException("entity_exists", "private details")
                : new HttpRequestException("private details"));
        };
        var vm = CreateViewModel(service);
        var paired = await vm.PairExistingPersonalCloudAsync(origin.AbsoluteUri, "synthetic-pair-code-1234567890", false, cancellation.Token);
        Assert.NotNull(paired);
        Assert.Equal(service.CurrentConnection, paired);
        Assert.True(vm.IsPersonalCloudConnected);
        Assert.False(vm.IsConnectingPersonalCloud);
        Assert.Equal("Manage Personal Cloud", vm.PersonalCloudActionText);
        Assert.Contains("desktop is paired", vm.PersonalCloudSummaryText);
        Assert.Contains("Retry Sync", vm.PersonalCloudSummaryText);
        Assert.DoesNotContain("rejected", vm.PersonalCloudSummaryText);
        Assert.DoesNotContain("private details", vm.PersonalCloudSummaryText);
        Assert.Equal(1, service.PairCalls);
    }

    [Fact]
    public async Task Connected_cloud_management_exports_updates_disconnects_and_preserves_local_tasks()
    {
        var connection = new PersonalCloudConnectionProfile
        {
            ServiceOrigin = new Uri("https://openza-personal-test.workers.dev/"),
            InstanceId = "instance-test",
            DeviceId = "device-test",
            ArtifactVersion = "1.2.3",
            ConnectedAt = DateTimeOffset.UtcNow,
        };
        var service = new StubSetupService(
            new PersonalCloudProvisioningResult(
                connection.ServiceOrigin, "openza-personal-test", "database-test", "1.2.4",
                new Uri("https://openza-personal-test.workers.dev/#setup=unused")),
            connection);
        var vm = CreateViewModel(service);

        var saved = false;
        Assert.True(await vm.ExportPersonalCloudAsync((export, _) =>
        {
            Assert.Equal("openza-personal-cloud.sql", export.FileName);
            saved = true;
            return Task.CompletedTask;
        }));
        Assert.True(saved);
        Assert.Equal("Personal Cloud backup saved.", vm.PersonalCloudSummaryText);
        Assert.True(await vm.UpgradePersonalCloudAsync());
        Assert.True(await vm.DisconnectPersonalCloudAsync());

        Assert.Equal(1, service.ExportCalls);
        Assert.Equal(1, service.UpgradeCalls);
        Assert.Equal(1, service.DisconnectCalls);
        Assert.False(vm.IsPersonalCloudConnected);
        Assert.Equal("Not connected", vm.PersonalCloudStatusText);
        Assert.Equal("Set up Personal Cloud", vm.PersonalCloudActionText);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Backup_save_cancellation_or_failure_does_not_report_success_or_change_connection(bool cancelled)
    {
        var connection = new PersonalCloudConnectionProfile
        {
            ServiceOrigin = new Uri("https://openza-personal-test.workers.dev/"),
            InstanceId = "instance-test", DeviceId = "device-test", ArtifactVersion = "1.2.3", ConnectedAt = DateTimeOffset.UtcNow,
        };
        var service = new StubSetupService(new(connection.ServiceOrigin, "worker-test", "database-test", "1.2.3", connection.ServiceOrigin), connection);
        var vm = CreateViewModel(service);

        var success = await vm.ExportPersonalCloudAsync((_, _) => cancelled
            ? Task.FromException(new OperationCanceledException())
            : Task.FromException(new IOException("private download details must not be displayed")));

        Assert.False(success);
        Assert.Equal(1, service.ExportCalls);
        Assert.Equal(connection, service.CurrentConnection);
        Assert.False(vm.IsConnectingPersonalCloud);
        Assert.DoesNotContain("backup saved", vm.PersonalCloudSummaryText);
        Assert.DoesNotContain("private download details", vm.PersonalCloudSummaryText);
        Assert.Equal(cancelled ? "Connected" : "Needs attention", vm.PersonalCloudStatusText);
    }

    private static MainWindowViewModel CreateViewModel(IPersonalCloudSetupService? service)
    {
        var path = Path.Combine(Path.GetTempPath(), $"openza-personal-cloud-ui-{Guid.NewGuid():N}.db");
        return new MainWindowViewModel(new SqliteTaskStore(path), new InMemoryCredentialStore(), personalCloudSetup: service);
    }

    private sealed class StubSetupService(
        PersonalCloudProvisioningResult result,
        PersonalCloudConnectionProfile? connection = null,
        PersonalCloudSyncResult? syncResult = null) : IPersonalCloudSetupService
    {
        private PersonalCloudConnectionProfile? _connection = connection;
        public bool IsConfigured => true;
        public PersonalCloudConnectionProfile? CurrentConnection => _connection;
        public Task OpenAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public int SyncCalls { get; private set; }
        public Func<CancellationToken, Task<PersonalCloudSyncResult>>? SyncAction { get; set; }
        public Task<PersonalCloudSyncResult> SyncAsync(CancellationToken cancellationToken = default)
        {
            SyncCalls++;
            return SyncAction?.Invoke(cancellationToken) ?? Task.FromResult(syncResult ?? new PersonalCloudSyncResult(0, 0, 0));
        }
        public int ExportCalls { get; private set; }
        public Func<CancellationToken, Task<CloudflareD1Export>>? ExportAction { get; set; }
        public Task<CloudflareD1Export> ExportAsync(CancellationToken cancellationToken = default)
        { ExportCalls++; return ExportAction?.Invoke(cancellationToken) ?? Task.FromResult(new CloudflareD1Export("openza-personal-cloud.sql", new Uri("https://downloads.example/openza.sql"))); }
        public int UpgradeCalls { get; private set; }
        public Task<PersonalCloudUpgradeResult> UpgradeAsync(CancellationToken cancellationToken = default)
        { UpgradeCalls++; return Task.FromResult(new PersonalCloudUpgradeResult(result.ArtifactVersion)); }
        public int DisconnectCalls { get; private set; }
        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        { DisconnectCalls++; _connection = null; return Task.CompletedTask; }
        public int RemoveCalls { get; private set; }
        public Task RemoveAsync(CancellationToken cancellationToken = default)
        { RemoveCalls++; _connection = null; return Task.CompletedTask; }
        public int ConnectCalls { get; private set; }
        public Task<PersonalCloudProvisioningResult> ConnectAsync(
            CancellationToken cancellationToken = default,
            IProgress<PersonalCloudSetupProgress>? progress = null)
        { ConnectCalls++; return Task.FromResult(result); }
        public int PairCalls { get; private set; }
        public bool LastProviderBridge { get; private set; }
        public Task<PersonalCloudConnectionProfile> PairExistingAsync(
            Uri serviceOrigin,
            string grant,
            string deviceName,
            bool providerBridge,
            CancellationToken cancellationToken = default)
        {
            PairCalls++;
            LastProviderBridge = providerBridge;
            _connection = new PersonalCloudConnectionProfile
            {
                ServiceOrigin = serviceOrigin,
                InstanceId = "instance-test",
                DeviceId = "device-test",
                ArtifactVersion = "1.2.3",
                ConnectedAt = DateTimeOffset.UtcNow,
            };
            return Task.FromResult(_connection!);
        }
    }
}
