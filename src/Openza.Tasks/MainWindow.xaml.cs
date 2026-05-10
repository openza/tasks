using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Migration;
using Openza.Tasks.Core.Services;
using Openza.Tasks.Core.Sync;
using Openza.Tasks.Services;
using Openza.Tasks.Shell;

namespace Openza.Tasks;

public sealed partial class MainWindow : Window
{
    private readonly AppSettingsService _settings;
    private readonly AppShell _shell;

    public MainWindow(
        ITaskStore store,
        TaskSyncEngine syncEngine,
        ICredentialStore credentials,
        BackupService backupService,
        AppSettingsService settings,
        MicrosoftToDoAuthService microsoftAuth,
        MigrationResult migration)
    {
        _settings = settings;

        InitializeComponent();
        Title = "Openza Tasks";
        TryEnableMica();
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)_settings.Settings.WindowWidth, (int)_settings.Settings.WindowHeight));
        Closed += OnClosed;

        _shell = new AppShell(this, store, syncEngine, credentials, backupService, settings, microsoftAuth, migration);
        Root.Children.Add(_shell);
    }

    public Task InitializeAsync() => _shell.InitializeAsync();

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        _settings.Settings.LastView = _shell.CurrentView;
        _settings.Settings.WindowWidth = AppWindow.Size.Width;
        _settings.Settings.WindowHeight = AppWindow.Size.Height;
        await _settings.SaveAsync().ConfigureAwait(false);
    }

    private void TryEnableMica()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            SystemBackdrop = null;
        }
    }
}
