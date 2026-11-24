using System.Collections.Concurrent;
using System.Diagnostics;
using Openza.Tasks.Core.Data;
using Tmds.DBus.Protocol;

namespace Openza.Tasks.Desktop.Services;

internal sealed class LinuxReminderNotifications(string appName, Action activate) : IDesktopReminderNotifications
{
    private const string Service = "org.freedesktop.Notifications";
    private const string Path = "/org/freedesktop/Notifications";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<uint, byte> _notifications = new();
    private IDisposable? _actionObserver;
    private IDisposable? _closedObserver;

    public async Task ShowAsync(TaskReminderSchedule reminder, CancellationToken cancellationToken)
    {
        var connection = DBusConnection.Session;
        if (_actionObserver is null)
        {
            try
            {
                _closedObserver ??= await ObserveAsync(connection, "NotificationClosed", id => _notifications.TryRemove(id, out _))
                    .AsTask().WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
                _actionObserver = await ObserveAsync(connection, "ActionInvoked", id =>
                {
                    if (_notifications.TryRemove(id, out _)) activate();
                }).AsTask().WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is DBusExceptionBase or TimeoutException)
            {
                Trace.TraceWarning("Desktop notification actions are unavailable.");
            }
        }

        var id = await connection.CallMethodAsync(CreateNotification(connection, reminder.Title),
                static (message, _) => message.GetBodyReader().ReadUInt32())
            .WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
        if (_actionObserver is not null) _notifications.TryAdd(id, 0);
    }

    private MessageBuffer CreateNotification(DBusConnection connection, string title)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, Path, Service, "Notify", "susssasa{sv}i");
        writer.WriteString(appName);
        writer.WriteUInt32(0);
        writer.WriteString(string.Empty);
        writer.WriteString("Task reminder");
        writer.WriteString(System.Security.SecurityElement.Escape(title) ?? string.Empty);
        writer.WriteArray(_actionObserver is null ? Array.Empty<string>() : new[] { "default", "Open Openza Tasks" });
        var hints = new Dictionary<string, VariantValue>();
        if (Environment.GetEnvironmentVariable("SNAP_NAME") is "openza-tasks")
            hints["desktop-entry"] = "openza-tasks_openza-tasks";
        writer.WriteDictionary(hints);
        writer.WriteInt32(-1);
        return writer.CreateMessage();
    }

    private static ValueTask<IDisposable> ObserveAsync(DBusConnection connection, string member, Action<uint> onNotification) =>
        connection.AddMatchAsync(new MatchRule
        {
            Type = MessageType.Signal, Sender = Service, Path = Path, Interface = Service, Member = member,
        }, static (message, _) => message.GetBodyReader().ReadUInt32(), notification =>
        {
            if (notification.HasValue) onNotification(notification.Value);
        }, emitOnCapturedContext: false);

    public void Dispose()
    {
        _actionObserver?.Dispose();
        _closedObserver?.Dispose();
    }
}
