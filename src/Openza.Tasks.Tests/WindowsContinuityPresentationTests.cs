using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Headless;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.Services.Continuity;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

[Collection(nameof(EditorTestCollection))]
public sealed class WindowsContinuityPresentationTests(EditorTestSession fixture)
{
    [Fact]
    public async Task Initial_failure_window_shows_safe_reason_without_raw_exception_or_alternate_profile()
    {
        await fixture.Session.Dispatch(() =>
        {
            var unexpected = WindowsContinuityHost.CreateFailureWindow(new IOException("synthetic-private-exception-detail"));
            unexpected.Show();
            var text = Assert.IsType<TextBlock>(unexpected.Content).Text!;
            Assert.Contains("Original files and credentials were retained", text);
            Assert.DoesNotContain("synthetic-private-exception-detail", text);
            unexpected.Close();
            var known = WindowsContinuityHost.CreateFailureWindow(new TypeInitializationException("synthetic", new WindowsContinuityException("Synthetic source still owns Dev registration.")));
            known.Show();
            Assert.Equal("Synthetic source still owns Dev registration.", Assert.IsType<TextBlock>(known.Content).Text);
            known.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Completed_cloud_recovery_displays_notice_keeps_settings_access_and_blocks_sync()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"openza-continuity-ui-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var preferences = new DesktopPreferencesStore(Path.Combine(root, "settings.json"));
            const string notice = "Synthetic recovery: local tasks and pending work are retained. Open Settings.";
            await preferences.SaveAsync(new()
            {
                WindowsCloudRecoveryRequired = true, WindowsContinuityNotices = [notice],
                AutomaticSyncEnabled = false, AutomaticRestorePointsEnabled = false,
            });
            var vm = new MainWindowViewModel(new SqliteTaskStore(Path.Combine(root, "tasks.db")), new InMemoryCredentialStore(), preferencesStore: preferences);
            var window = new Openza.Tasks.Desktop.Shell.MainWindow(vm);
            try
            {
                window.Show();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (vm.SelectedNavigation is null || vm.IsBusy || !vm.StatusMessage.Contains("Synthetic recovery", StringComparison.Ordinal))
                    await Task.Delay(10, deadline.Token);
                var settings = window.FindControl<Button>("SettingsNavigationButton")!;
                Assert.True(settings.IsEnabled);
                settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                while (!window.FindControl<ScrollViewer>("SettingsWorkspace")!.IsVisible) await Task.Delay(10, deadline.Token);
                await vm.RunTodoistSyncAsync();
                Assert.Contains("sync is paused", vm.StatusMessage);
                Assert.True(preferences.Load().WindowsCloudRecoveryRequired);
            }
            finally { window.Close(); await Task.Delay(100); TestDirectory.Delete(root); }
            return true;
        }, CancellationToken.None);
    }
}
