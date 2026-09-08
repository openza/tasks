using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Openza.Tasks.Application.Runtime;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.Shell;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Desktop;

public sealed partial class App : Avalonia.Application
{
    private ChannelRuntimeLease? _runtimeLease;
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var preferences = new DesktopPreferencesStore().Load();
        RequestedThemeVariant = preferences.Theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _runtimeLease = ChannelRuntimeLease.AcquireShared(DesktopDataPaths.Runtime);
            var store = new SqliteTaskStore(DesktopDataPaths.DatabasePath);
            desktop.MainWindow = new MainWindow(new MainWindowViewModel(store))
            {
                Title = DesktopDataPaths.Runtime.DisplayName,
            };
            desktop.Exit += (_, _) => _runtimeLease?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
