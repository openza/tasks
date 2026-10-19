using System.Net;
using System.Text;
using System.Text.Json;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudDispatchPolicyTests : IDisposable
{
    private static readonly Uri Origin = new("https://quota.example.test/");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-quota", Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private PersonalCloudDispatchPolicy Policy(int envelopes = 3000, int requests = 10000) =>
        new(_directory, new(envelopes, requests), _clock, () => TimeSpan.Zero);
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "PersonalCloudQuota", "quota-pause-v1.json");

    public static IEnumerable<object[]> EdgeCases()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(FixturePath));
        foreach (var item in fixture.RootElement.GetProperty("edgeCases").EnumerateArray())
            yield return [item.GetProperty("name").GetString()!, item.Clone()];
    }

    [Fact]
    public void Shared_typed_quota_and_batch_receipt_fixture_match_the_desktop_protocol()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(FixturePath));
        Assert.True(PersonalCloudQuotaResponse.TryReadError(fixture.RootElement.GetProperty("quotaError"), _clock.Utc, out var quota));
        Assert.Equal("d1_rows_read", quota.Resource);
        var batch = fixture.RootElement.GetProperty("batch");
        Assert.Equal(1, batch.GetProperty("processedCount").GetInt32());
        Assert.Equal(1, batch.GetProperty("stoppedAt").GetInt32());
        var receipt = PersonalCloudK0Codec.ParseMutationReceipt(Assert.Single(batch.GetProperty("receipts").EnumerateArray()).GetRawText());
        Assert.Equal("mutation_synthetic_1", receipt.MutationId);
        Assert.True(PersonalCloudQuotaResponse.TryReadError(batch.GetProperty("error"), _clock.Utc, out quota));
        Assert.Equal("d1_rows_written", quota.Resource);
    }

    [Theory]
    [MemberData(nameof(EdgeCases))]
    public void Shared_edge_fixture_recognition_is_bounded_and_never_scans_successful_task_data(string name, JsonElement item)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)item.GetProperty("status").GetInt32());
        var body = item.GetProperty("body").GetString()!;
        if (item.TryGetProperty("paddingBytes", out var padding)) body += new string(' ', padding.GetInt32());
        response.Content = new StringContent(body);
        foreach (var header in item.GetProperty("headers").EnumerateObject())
        {
            if (header.Name == "content-type")
                response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(header.Value.GetString()!);
            else response.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
        }
        var recognized = PersonalCloudQuotaResponse.TryRead(response, body, _clock.Utc, out var quota);
        Assert.Equal(item.GetProperty("daily").GetBoolean(), recognized);
        if (recognized) { Assert.Equal("workers_requests", quota.Resource); Assert.Equal(_clock.Utc.Date.AddDays(1), quota.ResetAt.UtcDateTime); }
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/auth/device/token")]
    [InlineData("/auth/pair")]
    [InlineData("/auth/device/revoke")]
    [InlineData("/sync/initial-status")]
    [InlineData("/v1/provider/intents")]
    [InlineData("/v1/provider/delivery/claim")]
    [InlineData("/sync/bootstrap")]
    [InlineData("/sync/changes")]
    [InlineData("/sync/checkpoints")]
    public async Task Quota_on_any_worker_path_persists_before_restart_and_blocks_all_clients(string path)
    {
        var handler = new Handler(_ => QuotaResponse());
        using var client = PersonalCloudDispatchHttp.Create(Policy(), handler);
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.GetAsync(new Uri(Origin, path)));
        Assert.Equal("daily_quota_exceeded", error.Code);
        Assert.Equal("d1_rows_read", error.DispatchStatus!.Resource);
        var originalReset = error.DispatchStatus.ResumeAt;
        var restarted = Policy();
        var blockedHandler = new Handler(_ => new(HttpStatusCode.OK));
        using var other = PersonalCloudDispatchHttp.Create(restarted, blockedHandler);
        foreach (var endpoint in new[] { "/health", "/auth/device/token", "/sync/initial-status", "/v1/provider/intents", "/sync/checkpoints" })
            await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => other.GetAsync(new Uri(Origin, endpoint)));
        Assert.Equal(0, blockedHandler.Calls);
        Assert.Equal(originalReset, (await restarted.GetStatusAsync(Origin)).ResumeAt);
        Assert.Equal(1, (await restarted.GetStatusAsync(Origin)).AttemptedRequests);
    }

    [Fact]
    public async Task Optional_status_http_failure_is_optional_but_its_quota_is_not()
    {
        var handler = new Handler(_ => Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var policy = Policy();
        using var client = PersonalCloudDispatchHttp.Create(policy, handler);
        using var response = await client.GetAsync(new Uri(Origin, "/sync/initial-status"));
        Assert.False((await policy.GetStatusAsync(Origin)).IsPaused);
        handler.Reply = _ => QuotaResponse();
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.GetAsync(new Uri(Origin, "/sync/initial-status")));
        Assert.Equal("daily_quota_exceeded", (await policy.GetStatusAsync(Origin)).Code);
    }

    [Theory]
    [InlineData("2026-10-19T00:00:00.000Z")]
    [InlineData("2050-10-20T00:00:00.000Z")]
    [InlineData("2026-10-20T00:00:00+00:00")]
    [InlineData("2026-10-20T12:00:00.000Z")]
    [InlineData("invalid")]
    public async Task Recognized_typed_quota_with_invalid_or_stale_hint_uses_next_UTC_reset(string reset)
    {
        var policy = Policy();
        using var client = PersonalCloudDispatchHttp.Create(policy, new Handler(_ => QuotaResponse(reset)));
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.GetAsync(new Uri(Origin, "/health")));
        Assert.Equal("daily_quota_exceeded", error.Code);
        Assert.Equal(PersonalCloudDispatchPolicy.NextMidnight(_clock.Utc), error.DispatchStatus!.CloudResetAt);
        Assert.Equal("daily_quota_exceeded", (await policy.GetStatusAsync(Origin)).Code);
    }

    [Fact]
    public async Task Trusted_response_Date_handles_small_clock_skew_across_midnight()
    {
        _clock.Utc = DateTimeOffset.Parse("2026-10-19T23:59:55Z");
        var policy = Policy();
        using var client = PersonalCloudDispatchHttp.Create(policy, new Handler(_ =>
        {
            var response = QuotaResponse("2026-10-21T00:00:00.000Z");
            response.Headers.Date = DateTimeOffset.Parse("2026-10-20T00:00:05Z");
            return response;
        }));
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.GetAsync(new Uri(Origin, "/health")));
        Assert.Equal("daily_quota_exceeded", error.Code);
        Assert.Equal(DateTimeOffset.Parse("2026-10-21T00:00:00Z"), error.DispatchStatus!.CloudResetAt);
        Assert.Equal(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(5), error.DispatchStatus.ResumeAt - _clock.Utc);
    }

    [Theory]
    [InlineData(-24)]
    [InlineData(24)]
    public async Task Trusted_response_Date_translates_daily_pause_into_local_duration_across_restart(int clockOffsetHours)
    {
        var reference = _clock.Utc.AddHours(-clockOffsetHours);
        var reset = PersonalCloudDispatchPolicy.NextMidnight(reference);
        var policy = Policy();
        var handler = new Handler(_ =>
        {
            var response = QuotaResponse(reset.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
            response.Headers.Date = reference;
            return response;
        });
        using var client = PersonalCloudDispatchHttp.Create(policy, handler);
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.GetAsync(new Uri(Origin, "/health")));
        Assert.Equal("daily_quota_exceeded", error.Code);
        Assert.Equal(reset - reference, error.DispatchStatus!.ResumeAt - _clock.Utc);
        Assert.Equal(reset, error.DispatchStatus.CloudResetAt);
        Assert.True((await Policy().GetStatusAsync(Origin)).IsPaused);
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.GetAsync(new Uri(Origin, "/health")));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Parsing_failure_backoff_reserves_no_extra_HTTP_attempt_and_survives_restart()
    {
        var policy = Policy();
        using var client = PersonalCloudDispatchHttp.Create(policy, new Handler(_ => new(HttpStatusCode.OK)));
        using (await client.GetAsync(new Uri(Origin, "/health"))) { }
        var first = await policy.RecordFailureAsync(Origin, CancellationToken.None);
        Assert.Equal("cloud_retry_backoff", first.Code);
        Assert.Equal(1, first.AttemptedRequests);
        Assert.Equal(first.ResumeAt, (await Policy().GetStatusAsync(Origin)).ResumeAt);
        _clock.Utc = first.ResumeAt!.Value;
        using (await client.GetAsync(new Uri(Origin, "/health"))) { }
        var next = await policy.RecordFailureAsync(Origin, CancellationToken.None);
        Assert.Equal(2, next.AttemptedRequests);
        Assert.True(next.ResumeAt - _clock.Utc > first.ResumeAt - first.ObservedAt);
    }

    [Fact]
    public async Task Clock_rollback_does_not_extend_gate_and_UTC_boundary_reopens_dispatch()
    {
        _clock.Utc = DateTimeOffset.Parse("2026-10-19T23:59:59Z");
        var policy = Policy();
        var handler = new Handler(_ => QuotaResponse());
        using var client = PersonalCloudDispatchHttp.Create(policy, handler);
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.GetAsync(new Uri(Origin, "/health")));
        var reset = (await policy.GetStatusAsync(Origin)).ResumeAt;
        _clock.Utc = _clock.Utc.AddHours(-1);
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.GetAsync(new Uri(Origin, "/health")));
        Assert.Equal(reset, (await policy.GetStatusAsync(Origin)).ResumeAt);
        _clock.Utc = reset!.Value;
        handler.Reply = _ => new(HttpStatusCode.OK);
        using var result = await client.GetAsync(new Uri(Origin, "/health"));
        Assert.Equal(2, handler.Calls);
        Assert.False((await policy.GetStatusAsync(Origin)).IsPaused);
        Assert.Equal(1, (await policy.GetStatusAsync(Origin)).AttemptedRequests);
    }

    [Fact]
    public async Task Retry_and_unknown_outcome_reservations_survive_restart_and_rollback_cannot_refill_budget()
    {
        var policy = Policy(2);
        var handler = new Handler(_ => throw new HttpRequestException("Synthetic uncertain outcome"));
        using var client = PersonalCloudDispatchHttp.Create(policy, handler);
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.PostAsync(new Uri(Origin, "/v1/tasks/mutations"), new StringContent("""{"operation":"task.create","payload":{}}""")));
        _clock.Utc = _clock.Utc.AddMinutes(1);
        var restarted = Policy(2);
        var retryHandler = new Handler(_ => new(HttpStatusCode.OK));
        using var retry = PersonalCloudDispatchHttp.Create(restarted, retryHandler);
        using var response = await retry.PostAsync(new Uri(Origin, "/v1/tasks/mutations"), new StringContent("""{"operation":"task.create","payload":{}}"""));
        Assert.Equal(2, (await restarted.GetStatusAsync(Origin)).AttemptedEnvelopes);
        Assert.Equal("local_budget_exceeded", (await restarted.GetStatusAsync(Origin)).Code);
        _clock.Utc = _clock.Utc.AddDays(-1);
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => retry.GetAsync(new Uri(Origin, "/health")));
        Assert.Equal(1, retryHandler.Calls);
    }

    [Fact]
    public async Task Http_attempt_budget_includes_health_and_never_requires_seed_metadata()
    {
        var policy = Policy(requests: 2);
        var handler = new Handler(_ => new(HttpStatusCode.OK));
        using var client = PersonalCloudDispatchHttp.Create(policy, handler);
        using var first = await client.GetAsync(new Uri(Origin, "/health"));
        using var second = await client.GetAsync(new Uri(Origin, "/auth/device/token"));
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.GetAsync(new Uri(Origin, "/sync/changes")));
        Assert.Equal("local_budget_exceeded", error.Code);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(0, error.DispatchStatus!.AttemptedEnvelopes);
    }

    [Fact]
    public async Task Corrupt_or_unwritable_ledger_fails_closed_and_reports_restart_safety()
    {
        var policy = Policy();
        using (var client = PersonalCloudDispatchHttp.Create(policy, new Handler(_ => new(HttpStatusCode.OK))))
        using (await client.GetAsync(new Uri(Origin, "/health"))) { }
        File.WriteAllText(Assert.Single(Directory.GetFiles(_directory, "*.json")), "{");
        var handler = new Handler(_ => new(HttpStatusCode.OK));
        using var corrupt = PersonalCloudDispatchHttp.Create(Policy(), handler);
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => corrupt.GetAsync(new Uri(Origin, "/health")));
        Assert.Equal("dispatch_state_unavailable", error.Code);
        Assert.False(error.DispatchStatus!.IsDurable);
        Assert.Contains("not restart-safe", error.Message);
        Assert.Equal(0, handler.Calls);

        var file = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(file, "synthetic");
        using var unwritable = PersonalCloudDispatchHttp.Create(new(file), handler);
        Assert.False((await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => unwritable.GetAsync(new Uri(Origin, "/health")))).DispatchStatus!.IsDurable);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Concurrent_request_cancellation_and_status_reads_do_not_turn_a_slow_send_into_storage_failure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new AsyncHandler(async (_, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return new(HttpStatusCode.OK); });
        var policy = Policy();
        using var first = PersonalCloudDispatchHttp.Create(policy, slow);
        using var second = PersonalCloudDispatchHttp.Create(Policy(), new Handler(_ => new(HttpStatusCode.OK)));
        var pending = first.GetAsync(new Uri(Origin, "/health"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(75));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.GetAsync(new Uri(Origin, "/sync/initial-status"), cancel.Token));
        Assert.False((await policy.GetStatusAsync(Origin)).IsPaused);
        release.SetResult();
        using (await pending) { }
        using (await second.GetAsync(new Uri(Origin, "/auth/device/token"))) { }
        Assert.True((await policy.GetStatusAsync(Origin)).IsDurable);
    }

    [Fact]
    public async Task Slow_request_lock_timeout_is_busy_and_does_not_install_a_permanent_memory_brake()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = PersonalCloudDispatchHttp.Create(Policy(), new AsyncHandler(async (_, token) =>
        { entered.SetResult(); await release.Task.WaitAsync(token); return new(HttpStatusCode.OK); }));
        var policy = Policy();
        var handler = new Handler(_ => new(HttpStatusCode.OK));
        using var second = PersonalCloudDispatchHttp.Create(policy, handler);
        var slow = first.GetAsync(new Uri(Origin, "/health"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => second.GetAsync(new Uri(Origin, "/sync/initial-status")).WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.Equal("cloud_dispatch_busy", error.Code);
            Assert.Equal(0, handler.Calls);
            Assert.True((await policy.GetStatusAsync(Origin)).IsDurable);
            Assert.False((await policy.GetStatusAsync(Origin)).IsPaused);
        }
        finally { release.TrySetResult(); using (await slow) { } }
        using (await second.GetAsync(new Uri(Origin, "/health"))) { }
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Separate_identity_and_report_clients_share_the_gate_without_nested_token_deadlock()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = 0;
        var policy = Policy();
        using var identityHttp = PersonalCloudDispatchHttp.Create(policy, new AsyncHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/auth/pair")
                return Json(HttpStatusCode.Created, """{"protocolVersion":1,"instanceId":"instance_synthetic","deviceId":"device_synthetic","refreshCredential":"synthetic-refresh-123456789012345","capabilities":["desktop_sync"],"refreshExpiresAt":"2099-01-01T00:00:00Z"}""");
            tokens++;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Json(HttpStatusCode.OK, """{"protocolVersion":1,"accessToken":"synthetic-access-123456789012345","expiresAt":"2099-01-01T00:00:00Z","capabilities":["desktop_sync"]}""");
        }));
        var identity = new PersonalCloudDeviceIdentityClient(identityHttp, new InMemoryCredentialStore());
        await identity.PairAsync(Origin, "synthetic-one-time-grant-123456789", "Synthetic desktop", false);
        using var report = PersonalCloudDispatchHttp.Create(policy, new Handler(_ => new(HttpStatusCode.OK)));
        var access = identity.IssueAccessTokenAsync(Origin);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var anotherTokenConsumer = identity.IssueAccessTokenAsync(Origin);
        var heartbeat = report.GetAsync(new Uri(Origin, "/sync/initial-status"));
        release.TrySetResult();
        await Task.WhenAll(access, anotherTokenConsumer, heartbeat).WaitAsync(TimeSpan.FromSeconds(3));
        using var heartbeatResponse = await heartbeat;
        Assert.Equal(1, tokens);
        Assert.Equal((await access).AccessToken, (await anotherTokenConsumer).AccessToken);
        Assert.Equal(3, (await policy.GetStatusAsync(Origin)).AttemptedRequests);
    }

    private HttpResponseMessage QuotaResponse(string? reset = null)
    {
        var body = JsonSerializer.Serialize(new { protocolVersion = 1, product = "tasks", error = new { code = "daily_quota_exceeded", message = "Synthetic quota", retryable = true,
            requestId = "request_synthetic", quota = new { protocolVersion = 1, resource = "d1_rows_read",
                resetAt = reset ?? PersonalCloudDispatchPolicy.NextMidnight(_clock.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") } } });
        var response = Json(HttpStatusCode.ServiceUnavailable, body);
        response.Headers.Date = _clock.Utc;
        return response;
    }
    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Reply { get; set; } = reply;
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(Reply(request)); }
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Utc = DateTimeOffset.Parse("2026-10-19T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Utc;
    }
    public void Dispose() => TestDirectory.Delete(_directory);
}
