using Openza.Tasks.Core.Data;

namespace Openza.Tasks.Desktop.Services;

internal interface IDesktopReminderNotifications : IDisposable
{
    Task ShowAsync(TaskReminderSchedule reminder, CancellationToken cancellationToken);
}

internal static class DesktopReminderNotifications
{
    public static IDesktopReminderNotifications? Create(string appName, Action activate)
    {
        if (OperatingSystem.IsLinux()) return new LinuxReminderNotifications(appName, activate);
#if OPENZA_WINDOWS_NOTIFICATIONS
        if (OperatingSystem.IsWindows()) return new WindowsReminderNotifications(activate);
#endif
        return null;
    }
}
