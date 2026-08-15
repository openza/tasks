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
        Assert.Throws<InvalidOperationException>(() => ChannelRuntimeLease.AcquireExclusive(context));
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
