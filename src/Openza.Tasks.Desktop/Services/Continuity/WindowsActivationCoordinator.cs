namespace Openza.Tasks.Desktop.Services.Continuity;

public interface IWindowsActivationPlatform
{
    bool IsCurrentOwner { get; }
    bool IsOwnerReady { get; }
    Task RedirectAsync();
    IDisposable PublishReady(Action activated);
}

public interface IWindowsActivationWindow
{
    void Dispatch(Action action);
    bool IsMinimized { get; }
    void Restore();
    void Activate();
}

public sealed class WindowsActivationCoordinator(IWindowsActivationPlatform platform)
{
    public async Task<bool> RedirectIfSecondaryAsync()
    {
        if (platform.IsCurrentOwner) return false;
        if (!platform.IsOwnerReady)
            throw new WindowsContinuityException("Close the other Openza Tasks Dev window before retrying the transition. No data was changed.");
        await platform.RedirectAsync().ConfigureAwait(false);
        return true;
    }

    public IDisposable AttachReadyWindow(IWindowsActivationWindow window)
    {
        if (!platform.IsCurrentOwner)
            throw new WindowsContinuityException("Only the owning Windows instance can accept activation.");
        return platform.PublishReady(() => window.Dispatch(() =>
        {
            if (window.IsMinimized) window.Restore();
            window.Activate();
        }));
    }
}
