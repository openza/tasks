using System.Net;
using System.Text.Json.Nodes;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed partial class PersonalCloudDispatchPolicyTests
{
    [Fact]
    public async Task Unreadable_ledger_is_preserved_and_storage_brake_recovers_after_the_file_is_released()
    {
        var policy = Policy();
        using var client = PersonalCloudDispatchHttp.Create(policy, new Handler(_ => new(HttpStatusCode.OK)));
        using (await client.GetAsync(new Uri(Origin, "/health"))) { }
        var path = Assert.Single(Directory.GetFiles(_directory, "*.json"));
        var original = File.ReadAllText(path);
        using (var unreadable = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var blocked = await policy.GetStatusAsync(Origin);
            Assert.True(blocked.IsPaused);
        }
        _clock.Utc = _clock.Utc.AddSeconds(5);
        var recovered = await policy.GetStatusAsync(Origin);
        Assert.True(recovered.IsDurable);
        Assert.Equal("dispatch_state_recovered", recovered.Resource);
        Assert.Equal(10000, recovered.AttemptedRequests);
        Assert.Contains(Directory.GetFiles(_directory, "*.quarantine"), file => File.ReadAllText(file) == original);
    }

    [Fact]
    public async Task Transient_storage_failure_retries_after_bound_and_clears_shared_memory_brake_when_durable()
    {
        Directory.CreateDirectory(_directory);
        var blocked = Path.Combine(_directory, "temporary-block");
        File.WriteAllText(blocked, "synthetic");
        var policy = new PersonalCloudDispatchPolicy(blocked, clock: _clock, resumeJitter: () => TimeSpan.Zero);
        var handler = new Handler(_ => new(HttpStatusCode.OK));
        using var client = PersonalCloudDispatchHttp.Create(policy, handler);
        var unavailable = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.GetAsync(new Uri(Origin, "/health")));
        Assert.Equal("dispatch_state_unavailable", unavailable.Code);
        Assert.False(unavailable.DispatchStatus!.IsDurable);
        File.Delete(blocked);
        _clock.Utc = _clock.Utc.AddSeconds(4);
        Assert.False((await policy.GetStatusAsync(Origin)).IsDurable);
        Assert.False(Directory.Exists(blocked)); // No retry on every status read.
        _clock.Utc = _clock.Utc.AddSeconds(1);
        var recovered = await policy.GetStatusAsync(Origin);
        Assert.True(recovered.IsDurable);
        Assert.Equal("dispatch_state_recovered", recovered.Resource);
        Assert.Equal(10000, recovered.AttemptedRequests);
        Assert.Equal(0, handler.Calls);
        var restarted = new PersonalCloudDispatchPolicy(blocked, clock: _clock, resumeJitter: () => TimeSpan.Zero);
        Assert.True((await restarted.GetStatusAsync(Origin)).IsDurable);
        _clock.Utc = recovered.ResumeAt!.Value;
        using (await client.GetAsync(new Uri(Origin, "/health"))) { }
        Assert.Equal(1, handler.Calls);
        Assert.False((await policy.GetStatusAsync(Origin)).IsPaused);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("newer")]
    [InlineData("oversized")]
    public async Task Invalid_ledger_preserves_evidence_and_recovers_after_at_most_one_local_day(string defect)
    {
        var policy = Policy();
        using var client = PersonalCloudDispatchHttp.Create(policy, new Handler(_ => new(HttpStatusCode.OK)));
        using (await client.GetAsync(new Uri(Origin, "/health"))) { }
        var path = Assert.Single(Directory.GetFiles(_directory, "*.json"));
        var raw = defect == "corrupt" ? "{" : defect == "oversized" ? new string('x', 17000)
            : File.ReadAllText(path).Replace("\"Format\":1", "\"Format\":2", StringComparison.Ordinal);
        File.WriteAllText(path, raw);
        var status = await Policy().GetStatusAsync(Origin);
        Assert.True(status.IsDurable);
        Assert.Equal("dispatch_state_recovered", status.Resource);
        Assert.Equal(raw, File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.quarantine"))));
        Assert.False(File.Exists(path + ".recovering"));
        _clock.Utc = status.ResumeAt!.Value;
        using (await client.GetAsync(new Uri(Origin, "/health"))) { }
        Assert.Equal(1, (await policy.GetStatusAsync(Origin)).AttemptedRequests);
    }

    [Fact]
    public async Task Recovery_marker_survives_quarantine_replacement_crash_window()
    {
        var policy = Policy();
        using var client = PersonalCloudDispatchHttp.Create(policy, new Handler(_ => new(HttpStatusCode.OK)));
        using (await client.GetAsync(new Uri(Origin, "/health"))) { }
        var path = Assert.Single(Directory.GetFiles(_directory, "*.json"));
        File.Move(path, path + ".recovering");
        var recovered = await Policy().GetStatusAsync(Origin);
        Assert.True(recovered.IsDurable);
        Assert.Equal("dispatch_state_recovered", recovered.Resource);
        Assert.Equal(10000, recovered.AttemptedRequests);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".recovering"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(3650)]
    public async Task Future_ledger_day_is_conservatively_reanchored_without_multiday_outage(int days)
    {
        var policy = Policy();
        using var client = PersonalCloudDispatchHttp.Create(policy, new Handler(_ => new(HttpStatusCode.OK)));
        using (await client.GetAsync(new Uri(Origin, "/health"))) { }
        var path = Assert.Single(Directory.GetFiles(_directory, "*.json"));
        var state = JsonNode.Parse(File.ReadAllText(path))!;
        state["Day"] = _clock.Utc.AddDays(days).ToString("yyyy-MM-dd");
        File.WriteAllText(path, state.ToJsonString());
        var recovered = await policy.GetStatusAsync(Origin);
        Assert.Equal("dispatch_state_recovered", recovered.Resource);
        Assert.Equal(10000, recovered.AttemptedRequests);
        Assert.Equal(PersonalCloudDispatchPolicy.NextMidnight(_clock.Utc), recovered.ResumeAt);
        _clock.Utc = recovered.ResumeAt!.Value;
        using (await client.GetAsync(new Uri(Origin, "/health"))) { }
        Assert.False((await policy.GetStatusAsync(Origin)).IsPaused);
    }

    [Fact]
    public async Task Caller_cancellation_keeps_reservation_without_persisting_failure_backoff()
    {
        using var caller = new CancellationTokenSource();
        var policy = Policy();
        using var client = PersonalCloudDispatchHttp.Create(policy, new AsyncHandler(async (_, linked) =>
        {
            caller.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, linked);
            return new(HttpStatusCode.OK);
        }));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Origin, "/health"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PersonalCloudDispatchHttp.SendAsync(client, request, caller.Token));
        var status = await Policy().GetStatusAsync(Origin);
        Assert.False(status.IsPaused);
        Assert.Equal(1, status.AttemptedRequests);
    }

    [Fact]
    public async Task Real_HttpClient_timeout_persists_backoff_even_though_handler_linked_token_is_cancelled()
    {
        var policy = Policy();
        var linkedCancelled = false;
        using var client = PersonalCloudDispatchHttp.Create(policy, new AsyncHandler(async (_, linked) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, linked); }
            catch (OperationCanceledException) { linkedCancelled = linked.IsCancellationRequested; throw; }
            return new(HttpStatusCode.OK);
        }));
        client.Timeout = TimeSpan.FromMilliseconds(100);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Origin, "/health"));
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
            PersonalCloudDispatchHttp.SendAsync(client, request, CancellationToken.None));
        Assert.True(linkedCancelled);
        Assert.Equal("cloud_retry_backoff", error.Code);
        Assert.Equal("cloud_retry_backoff", (await Policy().GetStatusAsync(Origin)).Code);
        Assert.Equal(1, error.DispatchStatus!.AttemptedRequests);
    }
}
