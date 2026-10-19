using Openza.Tasks.Application.Runtime;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Openza.Tasks.Desktop.Services.Continuity;

public static class WindowsContinuityHost
{
#if OPENZA_WINDOWS_CONTINUITY_DEV
    public const bool IsEnabled = true;
#else
    public const bool IsEnabled = false;
#endif
    private static IWindowsContinuityPlatform? _platform = null;
    public static WindowsContinuitySession? Session { get; private set; }

    public static WindowsContinuitySession? Resolve(OpenzaChannel channel, string? devOverride)
    {
#if OPENZA_WINDOWS_CONTINUITY_DEV
        if (!OperatingSystem.IsWindows() || channel != OpenzaChannel.Dev || !string.IsNullOrWhiteSpace(devOverride))
            throw new WindowsContinuityException("The continuity build requires Windows, the Dev channel and its matching package LocalFolder; data-directory overrides are disabled for this build.");
        if (Session is not null) return Session;
        _platform = new NativeWindowsContinuityPlatform();
        Session = new WindowsContinuitySession(_platform);
#endif
        return Session;
    }

    public static void Prepare()
    {
        // Resolve DesktopDataPaths before checking Session. No native work in normal
        // cross-platform builds; Windows failures never fall back to another profile.
        _ = DesktopDataPaths.Runtime;
        if (Session is { } session && _platform is { } platform)
            Task.Run(() => new WindowsContinuityCoordinator().PrepareAsync(session, platform,
                DesktopMicrosoftAuthService.ClientId, DesktopMicrosoftAuthService.TenantId)).GetAwaiter().GetResult();
    }

    public static void Close()
    {
        Session?.Dispose();
        Session = null;
    }

    public static Window CreateFailureWindow(Exception exception)
    {
        var failure = exception;
        while (failure is TypeInitializationException { InnerException: { } inner }) failure = inner;
        var message = failure switch
        {
            System.Runtime.InteropServices.COMException or UnauthorizedAccessException =>
                "Windows preparation could not access credentials or package files. Restore access and retry startup. Original files and credentials were retained; no alternate profile was opened.",
            WindowsContinuityException => failure.Message,
            _ => "Windows preparation could not be verified. Original files and credentials were retained. Resolve the transition archive or restore its safety copy before retrying; no alternate profile was opened.",
        };
        return new Window
        {
            Title = "Openza Tasks — startup stopped", Width = 720, Height = 280,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24) },
        };
    }
}
