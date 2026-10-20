using System.Text.Json;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed partial class CliContractTests
{
    [Fact]
    public async Task Cloud_budget_command_targets_only_this_channels_exact_FIFO_head_without_sending_or_changing_outbox()
    {
        var store = await CreateStoreAsync();
        const string instance = "instance_cli_budget", device = "device_cli_budget";
        var connection = new PersonalCloudConnectionProfile { ServiceOrigin = new("https://cli-budget.example.test/"),
            InstanceId = instance, DeviceId = device, ArtifactVersion = "synthetic", ConnectedAt = DateTimeOffset.UtcNow };
        await new JsonPersonalCloudConnectionStore(Path.Combine(_directory, "personal-cloud-connection.json")).SaveAsync(connection);
        await store.CreateTaskWithPersonalCloudIntentAsync(new() { Id = "local_cli_budget_1", Title = "Synthetic one" }, instance, device);
        await store.CreateTaskWithPersonalCloudIntentAsync(new() { Id = "local_cli_budget_2", Title = "Synthetic two" }, instance, device);
        var before = await store.GetPendingPersonalCloudOutboxAsync(instance, device);
        var inspection = await RunAsync("cloud-budget", "status", "--format", "json");
        AssertSuccess(inspection);
        using var json = JsonDocument.Parse(inspection.Stdout);
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("Dev", data.GetProperty("channel").GetString());
        Assert.Equal(before[0].MutationId, data.GetProperty("mutationId").GetString());
        var digest = data.GetProperty("sha256").GetString()!;
        Assert.Equal(PersonalCloudDispatchPolicy.ExactEnvelopeDigest(before[0].CanonicalJson), digest);
        Assert.DoesNotContain("Synthetic one", inspection.Stdout);
        var noConfirmation = await RunAsync("cloud-budget", "allow-once", "--mutation-id", before[0].MutationId, "--sha256", digest);
        Assert.Equal(5, noConfirmation.ExitCode);
        var later = await RunAsync("cloud-budget", "allow-once", "--mutation-id", before[1].MutationId,
            "--sha256", PersonalCloudDispatchPolicy.ExactEnvelopeDigest(before[1].CanonicalJson), "--yes");
        Assert.Equal(4, later.ExitCode);
        AssertSuccess(await RunAsync("cloud-budget", "allow-once", "--mutation-id", before[0].MutationId, "--sha256", digest, "--yes"));
        var policy = new PersonalCloudDispatchPolicy(Path.Combine(_directory, "personal-cloud-dispatch"));
        Assert.True(policy.HasSingleAttemptPermission(connection.ServiceOrigin, before[0].CanonicalJson));
        Assert.False(policy.HasSingleAttemptPermission(connection.ServiceOrigin, before[1].CanonicalJson));
        Assert.False(policy.HasSingleAttemptPermission(new("https://other.example.test/"), before[0].CanonicalJson));
        Assert.Equal(before, await store.GetPendingPersonalCloudOutboxAsync(instance, device));
        Assert.Equal(0, (await policy.GetStatusAsync(connection.ServiceOrigin)).AttemptedRequests);
    }
}
