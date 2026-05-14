using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Migration;
using Openza.Tasks.Core.Services;
using Openza.Tasks.Core.Sync;
using Openza.Tasks.Services;
using Windows.Storage;
using WinRT.Interop;
using CoreAppDataPaths = Openza.Tasks.Core.Migration.AppDataPaths;

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

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        AppLog.Write("OnLaunched");
        var current = AppInstance.FindOrRegisterForKey("Openza.Tasks.Main");
        if (!current.IsCurrent)
        {
            _ = current.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
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
            var migration = MigratePreviousCleanCoreDatabaseName(appData, databasePath);
            AppLog.Write(migration.Message);

            var store = new SqliteTaskStore(databasePath);
            await store.InitializeAsync().ConfigureAwait(true);

            var settings = new AppSettingsService();
            await settings.LoadAsync().ConfigureAwait(true);

            var credentials = new WindowsCredentialStore();
            var syncEngine = new TaskSyncEngine(store);
            var backupService = new BackupService(databasePath, Path.Combine(appData, "backups"));
            var microsoftAuth = new MicrosoftToDoAuthService(
                credentials,
                Path.Combine(appData, "msal-cache.bin"),
                () => _window is null ? IntPtr.Zero : WindowNative.GetWindowHandle(_window));

            _window = new MainWindow(store, syncEngine, credentials, backupService, settings, microsoftAuth, migration);
            _window.Activate();
            await _window.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            AppLog.Write(exception);
            throw;
        }
    }

    private static MigrationResult MigratePreviousCleanCoreDatabaseName(string appData, string databasePath)
    {
        var previousPath = Path.Combine(appData, CoreAppDataPaths.PreviousCleanCoreDatabaseFileName);
        if (!File.Exists(previousPath))
        {
            return MigrationResult.Skipped("Store V1 uses the local Openza Tasks database.");
        }

        if (File.Exists(databasePath) &&
            File.GetLastWriteTimeUtc(databasePath) >= File.GetLastWriteTimeUtc(previousPath))
        {
            return MigrationResult.Skipped("Openza Tasks database filename is already current.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? ".");
        string? backupPath = null;
        if (File.Exists(databasePath))
        {
            backupPath = $"{databasePath}.pre-db-rename-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
            File.Copy(databasePath, backupPath, overwrite: false);
        }

        File.Copy(previousPath, databasePath, overwrite: true);
        return new MigrationResult(
            true,
            $"Migrated Openza Tasks database filename from {CoreAppDataPaths.PreviousCleanCoreDatabaseFileName} to {CoreAppDataPaths.DatabaseFileName}.",
            backupPath,
            previousPath);
    }
}
