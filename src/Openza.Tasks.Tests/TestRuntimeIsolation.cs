using System.Runtime.CompilerServices;

namespace Openza.Tasks.Tests;

internal static class TestRuntimeIsolation
{
    // Headless App startup reads preferences before an individual fixture is built.
    // Isolate that startup even when a test command forgets the explicit override.
    [ModuleInitializer]
    internal static void Configure()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fallback = Path.Combine(Path.GetTempPath(), "openza-test-runtime", Guid.NewGuid().ToString("N"));
        var configured = Environment.GetEnvironmentVariable("OPENZA_TASKS_DEV_DATA_DIR");
        var directory = ResolveDirectory(configured, fallback,
            Path.Combine(local, "Openza", "Tasks"), Path.Combine(local, "Openza", "Tasks Preview"),
            Path.Combine(local, "Openza", "Tasks Dev"), Path.Combine(homeDirectory, "snap", "openza-tasks", "common", "tasks"));
        Environment.SetEnvironmentVariable("OPENZA_TASKS_DEV_DATA_DIR", directory);
    }

    internal static string ResolveDirectory(string? configured, string fallback, params string[] protectedProfiles)
    {
        if (string.IsNullOrWhiteSpace(configured)) return fallback;
        var candidate = Path.GetFullPath(configured).TrimEnd(Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return protectedProfiles.Any(profile =>
        {
            var path = Path.GetFullPath(profile).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(candidate, path, comparison) || candidate.StartsWith(path + Path.DirectorySeparatorChar, comparison);
        }) ? fallback : candidate;
    }
}

public sealed class TestRuntimeIsolationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Missing_override_uses_a_disposable_profile_before_headless_startup(string? configured)
    {
        var fallback = Path.Combine(Path.GetTempPath(), "synthetic-isolation");
        Assert.Equal(fallback, TestRuntimeIsolation.ResolveDirectory(configured, fallback));
    }

    [Fact]
    public void Explicit_disposable_override_is_retained_and_known_live_profile_is_replaced()
    {
        var fallback = Path.Combine(Path.GetTempPath(), "synthetic-isolation");
        var protectedProfile = Path.Combine(Path.GetTempPath(), "synthetic-protected-profile");
        Assert.Equal(Path.GetFullPath(fallback), TestRuntimeIsolation.ResolveDirectory(fallback, "unused", protectedProfile));
        Assert.Equal(fallback, TestRuntimeIsolation.ResolveDirectory(protectedProfile, fallback, protectedProfile));
        Assert.Equal(fallback, TestRuntimeIsolation.ResolveDirectory(Path.Combine(protectedProfile, "child"), fallback, protectedProfile));
    }
}
