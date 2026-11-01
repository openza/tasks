using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Sync;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

[Collection(nameof(EditorTestCollection))]
public sealed class DesktopPersonalCloudSettingsTests(EditorTestSession fixture)
{
    [Theory]
    [InlineData("local_budget_exceeded")]
    [InlineData("local_operation_budget_exceeded")]
    public async Task Upload_only_hold_keeps_manual_retry_available_with_visible_pending_recovery(string code)
    {
        await fixture.Session.Dispatch(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "openza-upload-hold-ui", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var connection = new PersonalCloudConnectionProfile { ServiceOrigin = new("https://ui-budget.example.test/"),
                    InstanceId = "instance_ui_budget", DeviceId = "device_ui_budget", ArtifactVersion = "synthetic", ConnectedAt = DateTimeOffset.UtcNow };
                var progress = new PersonalCloudInitialSyncProgress(connection.InstanceId, connection.DeviceId, PersonalCloudInitialSyncPhase.Blocked, 10, 100, code);
                var service = new ProgressStubSetupService(connection, progress) {
                    DispatchStatus = new(code, null, DateTimeOffset.UtcNow, code == "local_budget_exceeded" ? DateTimeOffset.UtcNow.AddHours(1) : null,
                        true, 10, 20, 3000, 10000) { PendingEnvelopes = 90, WriteUnitLimit = 45000, BlockingWriteUnits = 45001 },
                };
                var preferences = new Openza.Tasks.Desktop.Services.DesktopPreferencesStore(Path.Combine(directory, "settings.json"));
                await preferences.SaveAsync(new() { AutomaticSyncEnabled = false, AutomaticRestorePointsEnabled = false });
                var vm = new MainWindowViewModel(new SqliteTaskStore(Path.Combine(directory, "tasks.db")), new InMemoryCredentialStore(),
                    preferencesStore: preferences, personalCloudSetup: service);
                await vm.InitializeAsync();
                Assert.Equal("Uploads paused", vm.PersonalCloudStatusText);
                Assert.True(vm.CanRetryInitialSync);
                Assert.Equal(10, vm.InitialSyncProgress!.Acknowledged);
                Assert.False(vm.InitialSyncProgress.IsComplete);
                if (code == "local_operation_budget_exceeded") Assert.Contains("allow-once", vm.PersonalCloudSummaryText);
                await vm.RunTodoistSyncAsync();
                Assert.Equal(1, service.SyncCalls);
                Assert.True(vm.CanRetryInitialSync);
                Assert.False(vm.AutomaticSyncEnabled);
            }
            finally { TestDirectory.Delete(directory); }
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Quota_pause_shows_reset_and_backlog_then_resumes_respecting_automatic_sync(bool automaticSync)
    {
        await fixture.Session.Dispatch(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "openza-quota-ui", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var origin = new Uri("https://quota-ui.example.test/");
                var connection = new PersonalCloudConnectionProfile { ServiceOrigin = origin, InstanceId = "instance_quota_ui", DeviceId = "device_quota_ui",
                    ArtifactVersion = "synthetic", ConnectedAt = DateTimeOffset.UtcNow };
                var pause = new PersonalCloudDispatchStatus("daily_quota_exceeded", "d1_rows_read", DateTimeOffset.Parse("2026-10-19T12:00:00Z"),
                    DateTimeOffset.Parse("2026-10-20T00:00:00Z"), true, 10, 25, 3000, 10000) { PendingEnvelopes = 9000 };
                var service = new StubSetupService(new(origin, "worker", "database", "synthetic", origin), connection) { DispatchStatus = pause };
                service.SyncAction = _ => Task.FromException<PersonalCloudSyncResult>(pause.ToException());
                var preferences = new Openza.Tasks.Desktop.Services.DesktopPreferencesStore(Path.Combine(directory, "settings.json"));
                await preferences.SaveAsync(new() { AutomaticSyncEnabled = automaticSync, AutomaticRestorePointsEnabled = false });
                var vm = new MainWindowViewModel(new SqliteTaskStore(Path.Combine(directory, "tasks.db")), new InMemoryCredentialStore(),
                    preferencesStore: preferences, personalCloudSetup: service);
                await vm.InitializeAsync();
                Assert.Equal("Paused", vm.PersonalCloudStatusText);
                Assert.True(vm.IsPersonalCloudPaused);
                Assert.Contains("2026-10-20 00:00:00 UTC", vm.PersonalCloudSummaryText);
                Assert.Contains("9000 changes remain pending", vm.PersonalCloudSummaryText);
                Assert.False(vm.CanRetryInitialSync);
                await vm.RunAutomaticTodoistSyncAsync();
                Assert.Equal(automaticSync ? 1 : 0, service.SyncCalls);
                Assert.Equal(automaticSync, preferences.Load().AutomaticSyncEnabled);
                service.DispatchStatus = null;
                service.SyncAction = null;
                if (automaticSync) await vm.RunAutomaticTodoistSyncAsync();
                else await vm.RunTodoistSyncAsync();
                Assert.False(vm.IsPersonalCloudPaused);
                Assert.Equal("Connected", vm.PersonalCloudStatusText);
                Assert.DoesNotContain("daily cloud quota", vm.PersonalCloudSummaryText);
                Assert.Equal(automaticSync, vm.AutomaticSyncEnabled);
                Assert.Equal(automaticSync, preferences.Load().AutomaticSyncEnabled);
                Assert.Equal(automaticSync ? 2 : 1, service.SyncCalls);
            }
            finally { TestDirectory.Delete(directory); }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Native_sync_provider_retry_dispatches_the_selected_request_and_disables_while_busy()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "openza-bridge-ui", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var service = new BridgeStubSetupService();
            var preferences = new Openza.Tasks.Desktop.Services.DesktopPreferencesStore(Path.Combine(directory, "settings.json"));
            await preferences.SaveAsync(new() { AutomaticSyncEnabled = false, AutomaticRestorePointsEnabled = false });
            var vm = new MainWindowViewModel(new SqliteTaskStore(Path.Combine(directory, "tasks.db")), new InMemoryCredentialStore(),
                preferencesStore: preferences, personalCloudSetup: service);
            var window = new Openza.Tasks.Desktop.Shell.MainWindow(vm) { Width = 1440, Height = 900 };
            try
            {
                window.Show();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (vm.SelectedNavigation is null || vm.IsBusy) await Task.Delay(10, deadline.Token);
                var syncNavigation = window.GetVisualDescendants().OfType<Button>().Single(button =>
                    button.Classes.Contains("nav-command") && button.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Sync"));
                syncNavigation.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                while (!window.FindControl<ScrollViewer>("SyncWorkspace")!.IsVisible || window.FindControl<Control>("TaskWorkspace")!.IsVisible)
                    await Task.Delay(10, deadline.Token);
                Assert.False(window.FindControl<Control>("SettingsWorkspace")!.IsVisible);
                vm.ProviderRequests.Add(new("local-rejected-ui", "Synthetic task", "Rejected (provider_rejected).", true));
                vm.ProviderRequests.Add(new("local-pending-ui", "Synthetic pending task", "complete · pending_desktop", false));
                vm.ProviderRequests.Add(new("local-uncertain-ui", "Synthetic uncertain task", "Outcome uncertain. Stop and revoke the old desktop, check provider state, then reconcile in Personal Cloud. Automatic retry is blocked.", false));
                window.UpdateLayout();
                Button? retry = null;
                while ((retry = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => Equals(button.Content, "Retry provider request"))) is null)
                    await Task.Delay(10, deadline.Token);
                Assert.True(retry.IsVisible); Assert.True(retry.IsEnabled);
                if (Environment.GetEnvironmentVariable("OPENZA_BRIDGE_UI_SCREENSHOT_PATH") is { Length: > 0 } screenshot)
                {
                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshot))!);
                    frame.Save(screenshot, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                }
                retry.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("local-rejected-ui", service.RetriedTaskId); Assert.True(vm.IsSyncing); Assert.False(retry.IsEnabled);
                service.CompleteRetry();
                while (vm.IsSyncing) await Task.Delay(10, deadline.Token);
                Assert.Equal(1, service.SyncCalls); Assert.Contains("retry requested", vm.StatusMessage);
            }
            finally { service.CompleteRetry(); window.Close(); await Task.Delay(100); TestDirectory.Delete(directory); }
            return true;
        }, CancellationToken.None);
    }
    [Fact]
    public async Task Normal_provider_sync_then_cloud_upload_preserves_adopted_completion_and_reopening()
    {
        var directory = Path.Combine(Path.GetTempPath(), "openza-cloud-composition", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db")); await store.InitializeAsync();
            var raw = new TaskItem { Id = "todoist_synthetic-recurring", ExternalId = "synthetic-recurring",
                IntegrationId = "todoist", ProviderConnectionId = "todoist_default", Title = "Synthetic recurring source", RecurrenceRule = "every day" };
            var source = TaskSyncEngine.ToProviderSourceItem(raw, "todoist_default", new Dictionary<string, string>());
            await store.UpsertProviderSourceItemAsync(source);
            var adopted = (await store.AdoptProviderSourceItemAsync((await store.GetProviderSourceItemsAsync("todoist")).Single().Id))!;
            const string instance = "instance_composition"; const string device = "device_composition";
            var create = await store.EnqueueExistingTaskForPersonalCloudAsync(adopted.Id, instance, device);
            await store.AcknowledgePersonalCloudMutationAsync(instance, device, Accepted(create, 1));
            var completedAt = DateTimeOffset.Parse("2026-10-11T06:00:00Z");
            await store.CompleteTaskWithPersonalCloudIntentAsync(adopted.Id, completedAt, 1, instance, device);
            var credentials = new InMemoryCredentialStore(); await credentials.SaveAsync("todoist-token", "publicfake-token");
            var provider = new SnapshotProvider(raw);
            var connection = new PersonalCloudConnectionProfile { ServiceOrigin = new Uri("https://composition.example.test/"),
                InstanceId = instance, DeviceId = device, ArtifactVersion = "0.1.0-preview.9", ConnectedAt = DateTimeOffset.UtcNow };
            var service = new StubSetupService(new(connection.ServiceOrigin, "worker", "database", connection.ArtifactVersion, connection.ServiceOrigin), connection);
            var uploadCalls = 0;
            service.SyncAction = async _ =>
            {
                uploadCalls++;
                Assert.Equal(uploadCalls, provider.FetchCalls); // Normal Desktop imports before cloud upload.
                var task = (await store.GetTaskAsync(adopted.Id))!;
                Assert.Equal(uploadCalls == 1, task.IsCompleted);
                Assert.Equal(uploadCalls == 1 ? completedAt : (DateTimeOffset?)null, task.CompletedAt);
                var intent = Assert.Single(await store.GetPendingPersonalCloudOutboxAsync(instance, device));
                Assert.Equal(uploadCalls == 1 ? "task.complete" : "task.reopen", intent.Operation);
                await store.AcknowledgePersonalCloudMutationAsync(instance, device, Accepted(intent, uploadCalls + 1));
                return new(1, 0, uploadCalls);
            };
            var preferences = new Openza.Tasks.Desktop.Services.DesktopPreferencesStore(Path.Combine(directory, "settings.json"));
            await preferences.SaveAsync(new() { AutomaticSyncEnabled = false, AutomaticRestorePointsEnabled = false });
            var vm = new MainWindowViewModel(store, credentials, preferencesStore: preferences,
                personalCloudSetup: service, todoistSyncProviderFactory: _ => provider);
            await vm.RunTodoistSyncAsync();
            Assert.Equal(1, service.SyncCalls); Assert.True((await store.GetTaskAsync(adopted.Id))!.IsCompleted);
            await store.ReopenTaskWithPersonalCloudIntentAsync(adopted.Id, completedAt.AddMinutes(1), 2, instance, device);
            provider.Task = raw with { CompletionState = TaskCompletionState.Completed };
            await vm.RunTodoistSyncAsync();
            Assert.Equal(2, service.SyncCalls); Assert.False((await store.GetTaskAsync(adopted.Id))!.IsCompleted);
            Assert.Equal(0, provider.Actions); Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(instance, device));
        }
        finally { TestDirectory.Delete(directory); }
        static string Accepted(PersonalCloudK1OutboxEntry intent, long version) => PersonalCloudK0Codec.Serialize(new
        { protocolVersion = 1, product = "tasks", mutationId = intent.MutationId, outcome = "accepted", entityId = intent.EntityId,
            entityVersion = version, changeSeq = version, serverAcceptedAt = "2026-10-11T06:00:01.000Z" });
    }

    private sealed class SnapshotProvider(TaskItem task) : ISyncProvider
    {
        public TaskItem Task { get; set; } = task;
        public string IntegrationId => "todoist";
        public int FetchCalls { get; private set; }
        public int Actions { get; private set; }
        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default)
        { FetchCalls++; return System.Threading.Tasks.Task.FromResult(new ProviderSnapshot([Task], [], [])); }
        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default)
        { Actions++; return System.Threading.Tasks.Task.CompletedTask; }
    }

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

    [Fact]
    public async Task Management_desktop_update_requirement_shows_the_update_message_without_claiming_no_cloud_change()
    {
        var origin = new Uri("https://openza-personal-test.workers.dev/");
        const string message = "This Personal Cloud needs Openza Desktop 9.0.0 or newer. Update Openza Desktop first.";
        var service = new StubSetupService(new(origin, "worker", "database", "1.2.3", origin))
        {
            UpgradeAction = _ => Task.FromException<PersonalCloudUpgradeResult>(
                new PersonalCloudK0ContractException("desktop_update_required", message)),
        };
        var vm = CreateViewModel(service);

        Assert.False(await vm.UpgradePersonalCloudAsync());

        Assert.Equal(message, vm.PersonalCloudSummaryText);
        Assert.Equal("Needs attention", vm.PersonalCloudStatusText);
        Assert.False(vm.IsConnectingPersonalCloud);
        Assert.Equal(1, service.UpgradeCalls);
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

    private class StubSetupService(
        PersonalCloudProvisioningResult result,
        PersonalCloudConnectionProfile? connection = null,
        PersonalCloudSyncResult? syncResult = null) : IPersonalCloudSetupService, IPersonalCloudDispatchStatusService
    {
        private PersonalCloudConnectionProfile? _connection = connection;
        public bool IsConfigured => true;
        public PersonalCloudDispatchStatus? DispatchStatus { get; set; }
        public Task<PersonalCloudDispatchStatus?> GetDispatchStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(DispatchStatus);
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
        public Func<CancellationToken, Task<PersonalCloudUpgradeResult>>? UpgradeAction { get; set; }
        public Task<PersonalCloudUpgradeResult> UpgradeAsync(CancellationToken cancellationToken = default)
        { UpgradeCalls++; return UpgradeAction?.Invoke(cancellationToken) ?? Task.FromResult(new PersonalCloudUpgradeResult(result.ArtifactVersion)); }
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

    private sealed class ProgressStubSetupService(PersonalCloudConnectionProfile connection, PersonalCloudInitialSyncProgress progress)
        : StubSetupService(new(connection.ServiceOrigin, "worker", "database", "synthetic", connection.ServiceOrigin), connection), IPersonalCloudInitialSyncProgressService
    {
        public event Action<PersonalCloudInitialSyncProgress>? InitialSyncProgressChanged { add { } remove { } }
        public Task<PersonalCloudInitialSyncProgress?> GetInitialSyncProgressAsync(CancellationToken cancellationToken = default) => Task.FromResult<PersonalCloudInitialSyncProgress?>(progress);
    }

    private sealed class BridgeStubSetupService() : StubSetupService(new(new Uri("https://bridge.example.test/"), "worker", "database", "0.1.0-preview.9", new Uri("https://bridge.example.test/")),
        new() { ServiceOrigin = new Uri("https://bridge.example.test/"), InstanceId = "instance-ui", DeviceId = "device-ui", ArtifactVersion = "0.1.0-preview.9", ConnectedAt = DateTimeOffset.UtcNow }), IPersonalCloudProviderBridgeService
    {
        private readonly TaskCompletionSource _retry = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? RetriedTaskId { get; private set; }
        public Task RetryProviderCommandAsync(string taskId, CancellationToken cancellationToken = default)
        { RetriedTaskId = taskId; return _retry.Task.WaitAsync(cancellationToken); }
        public void CompleteRetry() => _retry.TrySetResult();
        public Task<PersonalCloudProviderBridgeResult> RunProviderBridgeAsync(IReadOnlyList<PersonalCloudConfiguredProvider> providers,
            CancellationToken cancellationToken = default) => Task.FromResult(new PersonalCloudProviderBridgeResult(0, 0, 0, 0));
    }
}
