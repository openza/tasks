using Avalonia;
using Openza.Tasks.Desktop.Services.Continuity;
namespace Openza.Tasks.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            if (WindowsContinuityHost.TryRedirectActivation()) return;
        }
        catch (Exception exception)
        {
            WindowsContinuityHost.StartupFailure = exception;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
