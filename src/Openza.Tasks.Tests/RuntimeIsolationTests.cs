using Microsoft.Data.Sqlite;
using Openza.Tasks.Application.Runtime;
using Openza.Tasks.Application.Tasks;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;

namespace Openza.Tasks.Tests;

public sealed class RuntimeIsolationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"openza_runtime_tests_{Guid.NewGuid():N}");

    public RuntimeIsolationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Channels_use_distinct_data_and_credential_namespaces()
    {
        var production = OpenzaRuntimeContext.Create(OpenzaChannel.Production);
        var preview = OpenzaRuntimeContext.Create(OpenzaChannel.Preview);
        var dev = OpenzaRuntimeContext.Create(OpenzaChannel.Dev, Path.Combine(_directory, "dev"));

        Assert.Equal("tasks", production.CredentialNamespace);
        Assert.Equal("tasks-preview", preview.CredentialNamespace);
        Assert.Equal("tasks-dev", dev.CredentialNamespace);
        Assert.NotEqual(production.DataDirectory, preview.DataDirectory);
        Assert.NotEqual(production.DataDirectory, dev.DataDirectory);
        Assert.NotEqual(preview.DataDirectory, dev.DataDirectory);
        Assert.Equal("Openza Tasks", production.DisplayName);
        Assert.Equal("Openza Tasks Preview", preview.DisplayName);
        Assert.Equal("Openza Tasks Dev", dev.DisplayName);
        Assert.Equal(OpenzaChannel.Dev, OpenzaRuntimeContext.ReadChannel(typeof(RuntimeIsolationTests).Assembly));
        Assert.Equal(production.DataDirectory, OpenzaRuntimeContext.Create(OpenzaChannel.Production, Path.Combine(_directory, "ignored")).DataDirectory);
    }

    [Fact]
    public void Production_snap_uses_the_refresh_stable_common_data_directory()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var snapRoot = Path.Combine(_directory, "snap", "openza-tasks", "current");
        var snapUserCommon = Path.Combine(_directory, "home", "snap", "openza-tasks", "common");

        Assert.Equal(
            Path.Combine(snapUserCommon, "tasks"),
            OpenzaRuntimeContext.ResolveSnapDataDirectory(
                OpenzaChannel.Production,
                snapRoot,
                "openza-tasks",
                snapUserCommon));
    }

    [Theory]
    [InlineData(OpenzaChannel.Dev, "/snap/openza-tasks/current", "openza-tasks", "/home/user/snap/openza-tasks/common")]
    [InlineData(OpenzaChannel.Preview, "/snap/openza-tasks/current", "openza-tasks", "/home/user/snap/openza-tasks/common")]
    [InlineData(OpenzaChannel.Production, null, "openza-tasks", "/home/user/snap/openza-tasks/common")]
    [InlineData(OpenzaChannel.Production, "relative", "openza-tasks", "/home/user/snap/openza-tasks/common")]
    [InlineData(OpenzaChannel.Production, "/snap/openza-tasks/current", "another-snap", "/home/user/snap/openza-tasks/common")]
    [InlineData(OpenzaChannel.Production, "/snap/openza-tasks/current", "openza-tasks", null)]
    [InlineData(OpenzaChannel.Production, "/snap/openza-tasks/current", "openza-tasks", "relative")]
    public void Untrusted_or_nonproduction_snap_environment_cannot_select_snap_data(
        OpenzaChannel channel,
        string? snapRoot,
        string? snapName,
        string? snapUserCommon)
    {
        Assert.Null(OpenzaRuntimeContext.ResolveSnapDataDirectory(channel, snapRoot, snapName, snapUserCommon));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("invalid", null, null)]
    [InlineData("Production", null, "Production")]
    [InlineData("Production", "Production", null)]
    [InlineData("Production", "Preview", "Production")]
    [InlineData("Production", "Production", "Preview")]
    [InlineData("Preview", null, "Preview")]
    [InlineData("Preview", "Production", "Preview")]
    [InlineData("Preview", "Preview", "Production")]
    public void Missing_invalid_or_untrusted_channel_metadata_resolves_to_dev(
        string? channel,
        string? packagingProfile,
        string? publishedChannelMarker)
    {
        Assert.Equal(OpenzaChannel.Dev, OpenzaRuntimeContext.ResolvePackagedChannel(channel, packagingProfile, publishedChannelMarker));
    }

    [Theory]
    [InlineData("Production", "Production", "Production", OpenzaChannel.Production)]
    [InlineData("Preview", "Preview", "Preview", OpenzaChannel.Preview)]
    [InlineData("Dev", null, null, OpenzaChannel.Dev)]
    public void Valid_packaging_metadata_resolves_expected_channel(
        string channel,
        string? packagingProfile,
        string? publishedChannelMarker,
        OpenzaChannel expected)
    {
        Assert.Equal(expected, OpenzaRuntimeContext.ResolvePackagedChannel(channel, packagingProfile, publishedChannelMarker));
    }

    [Theory]
    [InlineData("Production", OpenzaChannel.Production)]
    [InlineData("Preview", OpenzaChannel.Preview)]
    public void Published_marker_and_matching_assembly_metadata_resolve_expected_channel(
        string channel,
        OpenzaChannel expected)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"OpenzaRuntimeTest_{channel}_{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);
        var metadataConstructor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
        assembly.SetCustomAttribute(new CustomAttributeBuilder(metadataConstructor, ["OpenzaChannel", channel]));
        assembly.SetCustomAttribute(new CustomAttributeBuilder(metadataConstructor, ["OpenzaPackagingProfile", channel]));
        var markerDirectory = Path.Combine(_directory, $"published-{channel}");
        Directory.CreateDirectory(markerDirectory);
        File.WriteAllText(Path.Combine(markerDirectory, ".openza-channel"), channel);

        Assert.Equal(expected, OpenzaRuntimeContext.ReadChannel(assembly, markerDirectory));

        File.WriteAllText(Path.Combine(markerDirectory, ".openza-channel"), channel == "Production" ? "Preview" : "Production");
        Assert.Equal(OpenzaChannel.Dev, OpenzaRuntimeContext.ReadChannel(assembly, markerDirectory));
    }

    [Fact]
    public void Maintenance_seed_lock_is_exclusive()
    {
        var context = new OpenzaRuntimeContext { Channel = OpenzaChannel.Dev, DataDirectory = Path.Combine(_directory, "locked") };
        using var shared = ChannelRuntimeLease.AcquireShared(context);
        Assert.False(context.RuntimeLockPath.StartsWith(context.DataDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(context.DataDirectory, ".runtime.lock")));
        Assert.Throws<InvalidOperationException>(() => ChannelRuntimeLease.AcquireExclusive(context));
    }

    [Fact]
    public void Provider_sync_lease_serializes_same_provider_only()
    {
        var context = new OpenzaRuntimeContext
        {
            Channel = OpenzaChannel.Dev,
            DataDirectory = Path.Combine(_directory, "provider-sync-lock"),
        };

        using var todoist = ChannelRuntimeLease.AcquireProviderSync(context, IntegrationIds.Todoist);
        Assert.Throws<InvalidOperationException>(() =>
            ChannelRuntimeLease.AcquireProviderSync(context, IntegrationIds.Todoist));
        using var microsoft = ChannelRuntimeLease.AcquireProviderSync(context, IntegrationIds.MicrosoftToDo);
    }

    [Fact]
    public void Coordination_lock_avoids_an_unwritable_xdg_runtime_directory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runtimeDirectory = Path.Combine(_directory, "read-only-runtime");
        Directory.CreateDirectory(runtimeDirectory);
        File.SetUnixFileMode(runtimeDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var previousRuntimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtimeDirectory);
            var context = new OpenzaRuntimeContext
            {
                Channel = OpenzaChannel.Dev,
                DataDirectory = Path.Combine(_directory, "fallback-lock"),
            };

            using var lease = ChannelRuntimeLease.AcquireShared(context);
            Assert.False(context.RuntimeLockPath.StartsWith(runtimeDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousRuntimeDirectory);
            File.SetUnixFileMode(runtimeDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Coordination_lock_rejects_a_symlinked_xdg_runtime_directory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var targetDirectory = Path.Combine(_directory, "runtime-target");
        var linkedDirectory = Path.Combine(_directory, "runtime-link");
        Directory.CreateDirectory(targetDirectory);
        Directory.CreateSymbolicLink(linkedDirectory, targetDirectory);
        var previousRuntimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", linkedDirectory);
            var context = new OpenzaRuntimeContext
            {
                Channel = OpenzaChannel.Dev,
                DataDirectory = Path.Combine(_directory, "symlink-fallback-lock"),
            };

            using var lease = ChannelRuntimeLease.AcquireShared(context);
            Assert.False(context.RuntimeLockPath.StartsWith(linkedDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            Assert.Empty(Directory.EnumerateFileSystemEntries(targetDirectory));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousRuntimeDirectory);
        }
    }

    [Fact]
    public void Linux_coordination_path_is_stable_across_xdg_runtime_visibility()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var context = new OpenzaRuntimeContext
        {
            Channel = OpenzaChannel.Dev,
            DataDirectory = Path.Combine(_directory, "stable-runtime-lock"),
        };
        var previousRuntimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var previousTemporaryDirectory = Environment.GetEnvironmentVariable("TMPDIR");
        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", Path.Combine(_directory, "visible-runtime"));
            Environment.SetEnvironmentVariable("TMPDIR", Path.Combine(_directory, "visible-tmp"));
            var visiblePath = context.RuntimeLockPath;
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", "/run/user/1000");
            Environment.SetEnvironmentVariable("TMPDIR", "/different-sandbox-tmp");
            var sandboxedPath = context.RuntimeLockPath;

            Assert.Equal(visiblePath, sandboxedPath);
            Assert.StartsWith("/tmp/openza-runtime-", visiblePath, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousRuntimeDirectory);
            Environment.SetEnvironmentVariable("TMPDIR", previousTemporaryDirectory);
        }
    }

    [Fact]
    public async Task Concurrent_task_update_rejects_stale_revision()
    {
        var store = new SqliteTaskStore(Path.Combine(_directory, "concurrency.db"));
        var service = new TaskApplicationService(store);
        await service.InitializeAsync();
        var created = await service.CreateTaskAsync(new CreateTaskRequest { Title = "Original" });

        var first = await service.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = created.Id,
            ExpectedRevision = created.Revision,
            Title = OptionalValue<string?>.Set("First"),
        });
        Assert.Equal(created.Revision + 1, first.Revision);

        await Assert.ThrowsAsync<TaskConflictException>(() => service.UpdateTaskAsync(new UpdateTaskRequest
        {
            TaskId = created.Id,
            ExpectedRevision = created.Revision,
            Title = OptionalValue<string?>.Set("Stale"),
        }));
        Assert.Equal("First", (await service.GetTaskAsync(created.Id))!.Title);
    }

    [Fact]
    public async Task Seed_copies_tasks_but_disables_remote_writes_and_does_not_change_source()
    {
        var source = new OpenzaRuntimeContext { Channel = OpenzaChannel.Production, DataDirectory = Path.Combine(_directory, "production") };
        var target = new OpenzaRuntimeContext { Channel = OpenzaChannel.Dev, DataDirectory = Path.Combine(_directory, "dev") };
        var sourceStore = new SqliteTaskStore(source.DatabasePath);
        var service = new TaskApplicationService(sourceStore);
        await service.InitializeAsync();
        var task = await service.CreateTaskAsync(new CreateTaskRequest { Title = "Seeded" });
        await sourceStore.SetIntegrationConfiguredAsync(IntegrationIds.Todoist, true);
        await sourceStore.SetIntegrationActiveAsync(IntegrationIds.Todoist, true);
        var sourceBytes = await File.ReadAllBytesAsync(source.DatabasePath);
        Directory.CreateDirectory(target.DataDirectory);
        await File.WriteAllTextAsync(target.SettingsPath, """
            {
              "Theme": "Dark",
              "AutomaticSyncEnabled": true
            }
            """);

        await DevelopmentDataSeeder.SeedAsync(source, target, replace: false);

        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source.DatabasePath));
        var targetStore = new SqliteTaskStore(target.DatabasePath);
        await targetStore.InitializeAsync();
        Assert.Equal("Seeded", (await targetStore.GetTaskAsync(task.Id))!.Title);
        var todoist = (await targetStore.GetIntegrationsAsync()).Single(item => item.Id == IntegrationIds.Todoist);
        Assert.False(todoist.IsConfigured);
        Assert.False(todoist.IsActive);
        await using var connection = new SqliteConnection($"Data Source={target.DatabasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pending_completions";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(target.SettingsPath));
        Assert.False(settings.RootElement.GetProperty("AutomaticSyncEnabled").GetBoolean());
        Assert.Equal("Dark", settings.RootElement.GetProperty("Theme").GetString());
    }

    public void Dispose() => TestDirectory.Delete(_directory);
}
