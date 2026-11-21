using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Openza.Tasks.Application.Runtime;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.Services.Continuity;
using Openza.Tasks.Desktop.Shell;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Desktop;

public sealed partial class App : Avalonia.Application
{
    private ChannelRuntimeLease? _runtimeLease;
    private DesktopThemeController? _themeController;
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        _themeController = new DesktopThemeController(this);
    }

    public void SetThemePreference(string? theme) => _themeController?.SetPreference(theme);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            lifetime.Exit += (_, _) => _themeController?.Dispose();
        try
        {
            WindowsContinuityHost.Prepare();
            var preferences = new DesktopPreferencesStore().Load();
            SetThemePreference(preferences.Theme);

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                _runtimeLease = ChannelRuntimeLease.AcquireShared(DesktopDataPaths.Runtime);
                var store = new SqliteTaskStore(DesktopDataPaths.DatabasePath);
                desktop.MainWindow = new MainWindow(new MainWindowViewModel(
                    store,
                    DesktopCredentialStore.Create(DesktopDataPaths.Runtime),
                    preferencesStore: new DesktopPreferencesStore(),
                    personalCloudSetup: DesktopPersonalCloudSetup.TryCreate(store)))
                {
                    Title = DesktopDataPaths.Runtime.DisplayName,
                };
                desktop.MainWindow.Opened += (_, _) => WindowsContinuityHost.AttachReadyWindow(desktop.MainWindow);
                desktop.Exit += (_, _) => { _runtimeLease?.Dispose(); WindowsContinuityHost.Close(); };
            }
        }
        catch (Exception exception) when (WindowsContinuityHost.IsEnabled && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime)
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime;
            desktop.MainWindow = WindowsContinuityHost.CreateFailureWindow(exception);
            desktop.Exit += (_, _) => { _runtimeLease?.Dispose(); WindowsContinuityHost.Close(); };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
