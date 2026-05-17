using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Services;
using Openza.Tasks.Core.Sync;
using Openza.Tasks.Services;
using Windows.Storage;
using WinRT.Interop;
using CoreAppDataPaths = Openza.Tasks.Core.Migration.AppDataPaths;
using WindowsPackage = Windows.ApplicationModel.Package;
using WindowsPackageVersion = Windows.ApplicationModel.PackageVersion;

namespace Openza.Tasks;

public partial class App : Application
{
    private readonly DispatcherQueue _dispatcherQueue;
    private MainWindow? _window;

    public App()
    {
        AppLog.Write("App constructor");
        InitializeComponent();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        UnhandledException += (_, args) => AppLog.Write(args.Exception);
        AppInstance.GetCurrent().Activated += OnActivated;
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        AppLog.Write("OnLaunched");
        var current = AppInstance.FindOrRegisterForKey("Openza.Tasks.Main");
        if (!current.IsCurrent)
        {
            await current.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Environment.Exit(0);
            return;
        }

        _ = OpenMainWindowAsync();
    }

    private void OnActivated(object? sender, AppActivationArguments args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            _window?.Activate();
        });
    }

    private async Task OpenMainWindowAsync()
    {
        try
        {
            var appData = ApplicationData.Current.LocalFolder.Path;
            var databasePath = Path.Combine(appData, CoreAppDataPaths.DatabaseFileName);
            var packageIdentity = WindowsPackage.Current.Id.Name;
            var packageVersion = FormatPackageVersion(WindowsPackage.Current.Id.Version);
            AppLog.Write("Store V1 uses a fresh local Openza Tasks database.");

            var backupService = new BackupService(
                databasePath,
                BackupPaths.GetStableBackupDirectory(packageIdentity),
                BackupRetentionPolicy.Default,
                new BackupContext(
                    packageIdentity,
                    BackupPaths.GetAppFlavor(packageIdentity),
                    packageVersion));
            await backupService.MigrateLegacyBackupsAsync(BackupPaths.GetLegacyPackageBackupDirectory(appData)).ConfigureAwait(true);
            if (await backupService.ShouldCreatePreMigrationBackupAsync(SqliteTaskStore.CurrentSchemaVersion).ConfigureAwait(true))
            {
                await backupService.CreateBackupAsync(BackupReasons.PreMigration).ConfigureAwait(true);
            }

            var store = new SqliteTaskStore(databasePath);
            await store.InitializeAsync().ConfigureAwait(true);
            var startupRecoveryCandidate = await backupService.IsCurrentDatabaseFreshAsync().ConfigureAwait(true)
                ? await backupService.FindLatestRestorableBackupAsync().ConfigureAwait(true)
                : null;

            var settings = new AppSettingsService();
            await settings.LoadAsync().ConfigureAwait(true);

            var credentials = new WindowsCredentialStore();
            var syncEngine = new TaskSyncEngine(store);
            var microsoftAuth = new MicrosoftGraphAuthService(
                Path.Combine(appData, "msal-cache.bin"),
                () => _window is null ? IntPtr.Zero : WindowNative.GetWindowHandle(_window));

            _window = new MainWindow(store, syncEngine, credentials, backupService, settings, microsoftAuth, startupRecoveryCandidate);
            _window.Activate();
            await _window.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AppLog.Write(exception);
            throw;
        }
    }

    private static string FormatPackageVersion(WindowsPackageVersion version) =>
        $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
}
