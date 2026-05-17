using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
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
        MicrosoftGraphAuthService microsoftAuth,
        BackupInfo? startupRecoveryCandidate = null)
    {
        _settings = settings;

        InitializeComponent();
#if DEBUG
        Title = "Openza Tasks Dev";
#else
        Title = "Openza Tasks";
#endif
        TryEnableMica();
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)_settings.Settings.WindowWidth, (int)_settings.Settings.WindowHeight));
        RestoreWindowState();
        Closed += OnClosed;

        _shell = new AppShell(this, store, syncEngine, credentials, backupService, settings, microsoftAuth, startupRecoveryCandidate);
        Root.Children.Add(_shell);
    }

    public Task InitializeAsync() => _shell.InitializeAsync();

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        _settings.Settings.LastView = _shell.CurrentView;
        var isMaximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        _settings.Settings.WindowIsMaximized = isMaximized;
        if (!isMaximized)
        {
            _settings.Settings.WindowWidth = AppWindow.Size.Width;
            _settings.Settings.WindowHeight = AppWindow.Size.Height;
        }

        await _settings.SaveAsync().ConfigureAwait(false);
    }

    private void RestoreWindowState()
    {
        if (!_settings.Settings.WindowIsMaximized ||
            AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        presenter.Maximize();
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
