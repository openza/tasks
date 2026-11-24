#if OPENZA_WINDOWS_NOTIFICATIONS
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Openza.Tasks.Core.Data;

namespace Openza.Tasks.Desktop.Services;

internal sealed class WindowsReminderNotifications : IDesktopReminderNotifications
{
    private readonly Action _activate;
    private readonly AppNotificationManager _manager = AppNotificationManager.Default;

    public WindowsReminderNotifications(Action activate)
    {
        _activate = activate;
        _manager.NotificationInvoked += OnNotificationInvoked;
        try { _manager.Register(); }
        catch
        {
            _manager.NotificationInvoked -= OnNotificationInvoked;
            throw;
        }
    }

    public Task ShowAsync(TaskReminderSchedule reminder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var notification = new AppNotificationBuilder()
            .AddArgument("action", "reminder")
            .AddText("Task reminder")
            .AddText(reminder.Title)
            .BuildNotification();
        _manager.Show(notification);
        if (notification.Id == 0) throw new InvalidOperationException("Windows did not accept the task reminder.");
        return Task.CompletedTask;
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => _activate();

    public void Dispose()
    {
        _manager.NotificationInvoked -= OnNotificationInvoked;
        _manager.Unregister();
    }
}
#endif
