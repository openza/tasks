using Openza.Tasks.Application.Tasks;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed partial class PersonalCloudLifecycleIntegrationTests
{
    [Theory]
    [InlineData("origin")] [InlineData("database")] [InlineData("instance")] [InlineData("duplicate")]
    public async Task Update_resource_preflight_rejects_before_upload_or_database_write(string defect)
    {
        using var f = await Fixture.CreateAsync();
        switch (defect)
        {
            case "origin": f.Http.Subdomain = "other"; break;
            case "database": f.Http.BoundDatabase = "database_other"; break;
            case "instance": f.Http.BoundInstance = "instance_other"; break;
            case "duplicate": f.Http.DuplicateDatabaseBinding = true; break;
        }
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => f.Service.UpgradeAsync());
        Assert.Equal("resource_mismatch", error.Code);
        Assert.DoesNotContain("deploy", f.Events);
        Assert.DoesNotContain("journal", f.Events);
        Assert.DoesNotContain("migrate", f.Events);
        Assert.Equal("oauth-revoke", f.Events[^1]);
        await f.AssertPreservedAsync();
    }

    [Fact]
    public async Task Known_partial_upgrade_can_repair_unready_health_without_replacing_identity_or_pending_intents()
    {
        using var f = await Fixture.CreateAsync();
        var before = f.Service.CurrentConnection!;
        await f.Tasks.StageUnpublishedTasksForPersonalCloudAsync(before.InstanceId, before.DeviceId);
        var pending = await f.Tasks.GetPendingPersonalCloudOutboxAsync(before.InstanceId, before.DeviceId);
        var checkpoint = await f.Tasks.GetPersonalCloudCheckpointAsync(before.InstanceId, before.DeviceId);
        f.Http.HealthUnreadyUntilMigrated = true;
        await f.Service.UpgradeAsync();
        Assert.Equal(before with { ArtifactVersion = "2.0.0" }, f.Service.CurrentConnection);
        Assert.Equal(pending, await f.Tasks.GetPendingPersonalCloudOutboxAsync(before.InstanceId, before.DeviceId));
        Assert.Equal(checkpoint, await f.Tasks.GetPersonalCloudCheckpointAsync(before.InstanceId, before.DeviceId));
        Assert.True(f.Events.IndexOf("migrate") < f.Events.IndexOf("health"));
        Assert.DoesNotContain("pair", f.Events);
    }

    [Fact]
    public async Task Upgrade_shares_authoring_lease_but_excludes_disconnect_or_repair_until_publication()
    {
        using var f = await Fixture.CreateAsync();
        var before = f.Service.CurrentConnection!;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Deployment.BeforeComplete = async () => { started.SetResult(); await release.Task; };
        var upgrade = f.Service.UpgradeAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("connection_busy", (await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => f.Service.DisconnectAsync())).Code);
            Assert.Equal("connection_busy", (await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
                f.Service.PairExistingAsync(before.ServiceOrigin, "synthetic-pair-code-1234567890", "Other desktop", false))).Code);
            await new TaskApplicationService(f.Tasks, () => f.Service.CurrentConnection).CreateTaskAsync(new() { Title = "Synthetic local edit while updating" });
        }
        finally { release.TrySetResult(); await upgrade; }
        Assert.Equal(before with { ArtifactVersion = "2.0.0" }, f.Connections.Load());
        Assert.NotEmpty(await f.Tasks.GetPendingPersonalCloudOutboxAsync(before.InstanceId, before.DeviceId));
        Assert.DoesNotContain("device-revoke", f.Events);
        Assert.DoesNotContain("pair", f.Events);
    }

    [Fact]
    public async Task Upgrade_refuses_replacement_in_progress_before_authorization()
    {
        using var f = await Fixture.CreateAsync();
        using var lease = PersonalCloudConnectionLease.AcquireReplacement(f.Tasks.DatabasePath);
        Assert.Equal("connection_busy", (await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => f.Service.UpgradeAsync())).Code);
        Assert.Empty(f.Events);
        await f.AssertPreservedAsync();
    }

    [Fact]
    public async Task Failed_profile_save_keeps_memory_and_disk_consistent_and_known_upgrade_can_retry()
    {
        using var f = await Fixture.CreateAsync();
        var original = await File.ReadAllBytesAsync(f.ConnectionPath);
        f.FailConnectionSave = true;
        await Assert.ThrowsAsync<IOException>(() => f.Service.UpgradeAsync());
        Assert.Equal(original, await File.ReadAllBytesAsync(f.ConnectionPath));
        await f.AssertPreservedAsync();
        f.FailConnectionSave = false;
        await f.Service.UpgradeAsync();
        Assert.Equal(1, f.Events.Count(item => item == "migrate"));
        Assert.Equal("2.0.0", f.Connections.Load()!.ArtifactVersion);
        Assert.Equal(f.Connections.Load(), f.Service.CurrentConnection);
    }

    private sealed class SaveFailureStore(IPersonalCloudConnectionStore inner, Func<bool> fail) : IPersonalCloudConnectionStore
    {
        public PersonalCloudConnectionProfile? Load() => inner.Load();
        public Task SaveAsync(PersonalCloudConnectionProfile connection, CancellationToken cancellationToken = default)
            => fail() ? Task.FromException(new IOException("Synthetic profile persistence failure")) : inner.SaveAsync(connection, cancellationToken);
        public Task ClearAsync(CancellationToken cancellationToken = default) => inner.ClearAsync(cancellationToken);
    }
}
