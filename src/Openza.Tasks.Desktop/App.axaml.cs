using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Openza.Tasks.Application.Runtime;
using Openza.Tasks.Application.Tasks;
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
    private IDesktopReminderNotifications? _reminderNotifications;
    private CancellationTokenSource? _reminderCancellation;
    private Task? _reminderTask;
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
                var window = new MainWindow(new MainWindowViewModel(
                    store,
                    DesktopCredentialStore.Create(DesktopDataPaths.Runtime),
                    preferencesStore: new DesktopPreferencesStore(),
                    personalCloudSetup: DesktopPersonalCloudSetup.TryCreate(store)))
                {
                    Title = DesktopDataPaths.Runtime.DisplayName,
                };
                desktop.MainWindow = window;
                window.Opened += (_, _) => WindowsContinuityHost.AttachReadyWindow(window);
                window.StartupCompleted += (_, _) => StartReminders(window, store);
                desktop.Exit += (_, _) => StopReminders();
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

    private void StartReminders(Window window, SqliteTaskStore store)
    {
        try
        {
            _reminderNotifications = DesktopReminderNotifications.Create(window.Title!, () => Dispatcher.UIThread.Post(() =>
            {
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            }));
            if (_reminderNotifications is null) return;
            var scheduler = new TaskReminderScheduler(store, DesktopDataPaths.DataDirectory, _reminderNotifications.ShowAsync);
            _reminderCancellation = new CancellationTokenSource();
            var token = _reminderCancellation.Token;
            _reminderTask = Task.Run(() => scheduler.RunAsync(token), token);
        }
        catch (Exception)
        {
            Trace.TraceWarning("Native task reminder notifications could not be initialized.");
        }
    }

    private void StopReminders()
    {
        try
        {
            _reminderCancellation?.Cancel();
            _reminderTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { }
        finally
        {
            _reminderCancellation?.Dispose();
            try { _reminderNotifications?.Dispose(); }
            catch (Exception) { Trace.TraceWarning("Could not close native task reminders."); }
        }
    }
}
