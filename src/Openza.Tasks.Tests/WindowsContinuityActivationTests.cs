using Openza.Tasks.Desktop.Services.Continuity;

namespace Openza.Tasks.Tests;

public sealed class WindowsContinuityActivationTests
{
    [Fact]
    public async Task Primary_launch_continues_without_publishing_readiness_or_redirecting()
    {
        var platform = new FakePlatform { IsCurrentOwner = true };
        Assert.False(await new WindowsActivationCoordinator(platform).RedirectIfSecondaryAsync());
        Assert.Equal(0, platform.ReadinessChecks);
        Assert.Equal(0, platform.Redirects);
        Assert.False(platform.ReadyPublished);
    }

    [Fact]
    public async Task Secondary_launch_waits_for_redirect_before_exiting_silently()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new FakePlatform { Ready = true, Redirect = () => completion.Task };
        var redirect = new WindowsActivationCoordinator(platform).RedirectIfSecondaryAsync();
        Assert.False(redirect.IsCompleted);
        Assert.Equal(1, platform.Redirects);
        Assert.False(platform.ReadyPublished);
        completion.SetResult();
        Assert.True(await redirect);
    }

    [Fact]
    public async Task Unready_owner_including_WinUI_or_incomplete_transition_keeps_startup_stopped()
    {
        var platform = new FakePlatform();
        var exception = await Assert.ThrowsAsync<WindowsContinuityException>(() =>
            new WindowsActivationCoordinator(platform).RedirectIfSecondaryAsync());
        Assert.Contains("Close the other Openza Tasks Dev window", exception.Message);
        Assert.Equal(0, platform.Redirects);
        Assert.False(platform.ReadyPublished);
    }

    [Fact]
    public async Task Failed_redirect_is_not_reported_as_success_and_does_not_publish_another_owner()
    {
        var failure = new InvalidOperationException("Synthetic redirect failure");
        var platform = new FakePlatform { Ready = true, Redirect = () => Task.FromException(failure) };
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WindowsActivationCoordinator(platform).RedirectIfSecondaryAsync()));
        Assert.False(platform.ReadyPublished);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ready_owner_dispatches_activation_and_restores_only_minimized_window(bool minimized)
    {
        var platform = new FakePlatform { IsCurrentOwner = true };
        var window = new FakeWindow { IsMinimized = minimized };
        var coordinator = new WindowsActivationCoordinator(platform);
        using (coordinator.AttachReadyWindow(window))
        {
            Assert.True(platform.ReadyPublished);
            platform.RaiseActivated();
            Assert.Empty(window.Operations);
            Assert.NotNull(window.Pending);
            window.Pending();
            Assert.Equal(minimized ? new[] { "restore", "activate" } : new[] { "activate" }, window.Operations);
        }
        Assert.False(platform.ReadyPublished);
        window.Pending = null;
        platform.RaiseActivated();
        Assert.Null(window.Pending);
    }

    [Fact]
    public void Secondary_process_cannot_publish_ready_window_or_subscribe_to_activation()
    {
        var platform = new FakePlatform { Ready = true };
        Assert.Throws<WindowsContinuityException>(() =>
            new WindowsActivationCoordinator(platform).AttachReadyWindow(new FakeWindow()));
        Assert.False(platform.ReadyPublished);
    }

    private sealed class FakePlatform : IWindowsActivationPlatform
    {
        public bool IsCurrentOwner { get; init; }
        public bool Ready { get; init; }
        public bool IsOwnerReady { get { ReadinessChecks++; return Ready; } }
        public int ReadinessChecks { get; private set; }
        public int Redirects { get; private set; }
        public bool ReadyPublished { get; private set; }
        public Func<Task> Redirect { get; init; } = () => Task.CompletedTask;
        private Action? _activated;
        public Task RedirectAsync() { Redirects++; return Redirect(); }
        public IDisposable PublishReady(Action activated)
        {
            ReadyPublished = true;
            _activated = activated;
            return new Registration(() => { ReadyPublished = false; _activated = null; });
        }
        public void RaiseActivated() => _activated?.Invoke();
    }

    private sealed class FakeWindow : IWindowsActivationWindow
    {
        public bool IsMinimized { get; init; }
        public List<string> Operations { get; } = [];
        public Action? Pending { get; set; }
        public void Dispatch(Action action) => Pending = action;
        public void Restore() => Operations.Add("restore");
        public void Activate() => Operations.Add("activate");
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
