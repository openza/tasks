using System.Net;
using System.Text;
using System.Text.Json;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed partial class PersonalCloudDispatchBudgetTests
{
    [Theory]
    [InlineData("{\"operation\":42,\"payload\":{}}")]
    [InlineData("{\"operation\":null,\"payload\":{}}")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Malformed_operation_shape_keeps_conservative_unknown_hold_instead_of_crashing_cost_selection(string value)
    {
        using var mutation = JsonDocument.Parse(value);
        Assert.True(PersonalCloudMutationCost.ForBudget(mutation.RootElement, 45000, 0) > 45000);
    }

    private static PersonalCloudK1OutboxEntry Entry(string operation = "future.operation")
    {
        var json = JsonSerializer.Serialize(new { protocolVersion = 1, product = "tasks", instanceId = "instance_budget_synthetic",
            deviceId = "device_budget_synthetic", mutationId = "mutation_budget_synthetic", deviceSequence = 1,
            operation, entityId = "task_budget_synthetic", baseEntityVersion = 1, payload = new { }, clientCreatedAt = "2026-10-19T12:00:00.000Z" });
        return new() { LocalSequence = 1, MutationId = "mutation_budget_synthetic", Product = "tasks", InstanceId = "instance_budget_synthetic",
            DeviceId = "device_budget_synthetic", DeviceSequence = 1, Operation = operation, EntityId = "task_budget_synthetic",
            CanonicalJson = json, CanonicalHash = PersonalCloudK0Codec.ComputeCanonicalHash(json), CreatedAt = DateTimeOffset.Parse("2026-10-19T12:00:00Z") };
    }
    private PersonalCloudConnectionProfile Connection() => new() { ServiceOrigin = Origin, InstanceId = "instance_budget_synthetic",
        DeviceId = "device_budget_synthetic", ArtifactVersion = "synthetic", ConnectedAt = _clock.Utc };

    [Fact]
    public async Task Exact_pending_permission_is_durable_consumed_once_and_preserves_canonical_body_on_uncertain_outcome()
    {
        var entry = Entry();
        var digest = PersonalCloudDispatchPolicy.ExactEnvelopeDigest(entry.CanonicalJson);
        var policy = Policy();
        await Assert.ThrowsAsync<ArgumentException>(() => policy.AuthorizeSingleAttemptAsync(Connection(), entry, new string('0', 64)));
        await policy.AuthorizeSingleAttemptAsync(Connection(), entry, digest);
        Assert.True(Policy().HasSingleAttemptPermission(Origin, entry.CanonicalJson));
        string? transmitted = null;
        using var client = PersonalCloudDispatchHttp.Create(Policy(), new AttemptHandler(async request =>
        {
            transmitted = await request.Content!.ReadAsStringAsync();
            Assert.False(Policy().HasSingleAttemptPermission(Origin, entry.CanonicalJson)); // Consumed before inner send.
            throw new HttpRequestException("Synthetic uncertain result");
        }));
        using var content = new StringContent(entry.CanonicalJson, Encoding.UTF8, "application/json");
        Assert.Equal("cloud_retry_backoff", (await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
            client.PostAsync(new Uri(Origin, "/v1/tasks/mutations"), content))).Code);
        Assert.Equal(entry.CanonicalJson, transmitted);
        Assert.False(Policy().HasSingleAttemptPermission(Origin, entry.CanonicalJson));
        Assert.Equal(45000, (await policy.GetStatusAsync(Origin)).ReservedWriteUnits);
        _clock.Utc = _clock.Utc.AddMinutes(1);
        using var retry = new StringContent(entry.CanonicalJson, Encoding.UTF8, "application/json");
        Assert.Equal("local_operation_budget_exceeded", (await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
            client.PostAsync(new Uri(Origin, "/v1/tasks/mutations"), retry))).Code);
        Assert.Equal(1, (await policy.GetStatusAsync(Origin)).AttemptedRequests);
    }

    [Fact]
    public async Task Permission_cannot_authorize_modified_body_another_origin_or_an_expired_attempt()
    {
        var entry = Entry();
        var policy = Policy();
        await policy.AuthorizeSingleAttemptAsync(Connection(), entry, PersonalCloudDispatchPolicy.ExactEnvelopeDigest(entry.CanonicalJson));
        Assert.False(policy.HasSingleAttemptPermission(new("https://other.example.test/"), entry.CanonicalJson));
        Assert.False(policy.HasSingleAttemptPermission(Origin, entry.CanonicalJson.Replace("future.operation", "different.operation", StringComparison.Ordinal)));
        _clock.Utc = _clock.Utc.AddMinutes(15);
        Assert.False(policy.HasSingleAttemptPermission(Origin, entry.CanonicalJson));
    }

    [Fact]
    public async Task Permission_never_bypasses_HTTP_limit_or_confirmed_cloud_quota()
    {
        var entry = Entry();
        var policy = new PersonalCloudDispatchPolicy(_directory, new(3000, 1), _clock, () => TimeSpan.Zero);
        await policy.AuthorizeSingleAttemptAsync(Connection(), entry, PersonalCloudDispatchPolicy.ExactEnvelopeDigest(entry.CanonicalJson));
        using var client = PersonalCloudDispatchHttp.Create(policy, new Handler());
        using (await client.GetAsync(new Uri(Origin, "/health"))) { }
        using var content = new StringContent(entry.CanonicalJson);
        var blocked = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.PostAsync(new Uri(Origin, "/v1/tasks/mutations"), content));
        Assert.True(blocked.DispatchStatus!.BlocksReads);
        Assert.True(policy.HasSingleAttemptPermission(Origin, entry.CanonicalJson));
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
            policy.AuthorizeSingleAttemptAsync(Connection(), entry, PersonalCloudDispatchPolicy.ExactEnvelopeDigest(entry.CanonicalJson)));

        _clock.Utc = _clock.Utc.AddDays(1);
        using (var lease = await policy.BeginAsync(Origin, 0, 0, CancellationToken.None))
            lease.Pause("daily_quota_exceeded", "d1_rows_written", _clock.Utc, PersonalCloudDispatchPolicy.NextMidnight(_clock.Utc), PersonalCloudDispatchPolicy.NextMidnight(_clock.Utc));
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
            policy.AuthorizeSingleAttemptAsync(Connection(), entry, PersonalCloudDispatchPolicy.ExactEnvelopeDigest(entry.CanonicalJson)));
    }

    [Theory]
    [InlineData("project.delete", 0, 100)]
    [InlineData("project.delete", 900, 10900)]
    [InlineData("task.moveTree", 900, 10900)]
    [InlineData("task.moveTree", 3500, 42100)]
    [InlineData("task.moveTree", 4000, 48100)]
    public void Guarded_fanout_formula_scales_without_capping_large_operations(string operation, long bound, long expected)
    {
        using var mutation = JsonDocument.Parse(JsonSerializer.Serialize(new { operation, payload = new { } }));
        Assert.Equal(expected, PersonalCloudMutationCost.ForBudget(mutation.RootElement, 45000, bound));
        Assert.Equal(45000, PersonalCloudMutationCost.ForBudget(mutation.RootElement, 45000));
    }

    private sealed class AttemptHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
