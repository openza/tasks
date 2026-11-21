#if WINDOWS
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Windows.AppLifecycle;
using Package = Windows.ApplicationModel.Package;

namespace Openza.Tasks.Desktop.Services.Continuity;

public sealed class NativeWindowsActivationPlatform : IWindowsActivationPlatform
{
    private readonly AppInstance _instance;
    public NativeWindowsActivationPlatform()
    {
        _instance = AppInstance.FindOrRegisterForKey("Openza.Tasks.Main");
        WindowsActivationDiagnostics.Write($"activation owner={_instance.ProcessId} current={_instance.IsCurrent}");
    }
    private string ReadyName => $"Local\\Openza.Tasks.Desktop.Ready.{Package.Current.Id.FamilyName}.{_instance.ProcessId}";
    public bool IsCurrentOwner => _instance.IsCurrent;
    public bool IsOwnerReady
    {
        get
        {
            if (!EventWaitHandle.TryOpenExisting(ReadyName, out var ready))
            {
                WindowsActivationDiagnostics.Write($"readiness missing: {ReadyName}");
                return false;
            }
            using (ready)
            {
                var signaled = ready.WaitOne(0);
                WindowsActivationDiagnostics.Write($"readiness signaled={signaled}: {ReadyName}");
                return signaled;
            }
        }
    }

    public async Task RedirectAsync()
    {
        WindowsActivationDiagnostics.Write("redirect requested");
        AllowSetForegroundWindow(_instance.ProcessId);
        await _instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
        WindowsActivationDiagnostics.Write("redirect completed");
    }

    public IDisposable PublishReady(Action activated)
    {
        var current = AppInstance.GetCurrent();
        void OnActivated(object? sender, AppActivationArguments args)
        {
            WindowsActivationDiagnostics.Write("Activated received");
            activated();
        }
        current.Activated += OnActivated;
        EventWaitHandle? ready = null;
        try
        {
            ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyName);
            ready.Set();
            WindowsActivationDiagnostics.Write($"readiness published: {ReadyName}");
            return new Registration(current, OnActivated, ready);
        }
        catch
        {
            current.Activated -= OnActivated;
            ready?.Dispose();
            throw;
        }
    }

    private sealed class Registration(AppInstance instance, EventHandler<AppActivationArguments> handler, EventWaitHandle ready) : IDisposable
    {
        public void Dispose()
        {
            ready.Reset();
            instance.Activated -= handler;
            ready.Dispose();
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}

internal sealed class NativeWindowsActivationWindow(Window window) : IWindowsActivationWindow
{
    public void Dispatch(Action action) => Dispatcher.UIThread.Post(action);
    public bool IsMinimized => window.WindowState == WindowState.Minimized;
    public void Restore()
    {
        if (window.TryGetPlatformHandle() is { } handle) ShowWindow(handle.Handle, 9);
        else window.WindowState = WindowState.Normal;
    }
    public void Activate()
    {
        window.Activate();
        if (window.TryGetPlatformHandle() is { } handle)
            WindowsActivationDiagnostics.Write($"foreground result={SetForegroundWindow(handle.Handle)}");
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}

internal static class WindowsActivationDiagnostics
{
    public static void Write(string message)
    {
        try
        {
            var folder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            if (!File.Exists(Path.Combine(folder, "activation-diagnostics.enabled"))) return;
            File.AppendAllText(Path.Combine(folder, "activation-diagnostics.log"), $"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} thread={Environment.CurrentManagedThreadId} pool={Thread.CurrentThread.IsThreadPoolThread} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
#endif
