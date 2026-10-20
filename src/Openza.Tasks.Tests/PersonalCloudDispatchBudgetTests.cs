using System.Net;
using System.Text;
using System.Text.Json;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed partial class PersonalCloudDispatchBudgetTests : IDisposable
{
    private static readonly Uri Origin = new("https://budget.example.test/");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-write-budget", Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "PersonalCloudQuota", "dispatch-budget-v1.json");

    public static IEnumerable<object[]> CostCases()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(FixturePath));
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("operation").GetString()!, item.Clone(), item.GetProperty("credits").GetInt64()];
    }

    [Theory]
    [MemberData(nameof(CostCases))]
    public void Exact_shared_cost_fixture_covers_rich_source_compound_and_fanout_costs(string operation, JsonElement mutation, long credits)
    {
        Assert.Equal(operation, mutation.GetProperty("operation").GetString());
        Assert.Equal(credits, PersonalCloudMutationCost.Estimate(mutation));
        Assert.Equal(credits, PersonalCloudMutationCost.ForBudget(mutation, 45000));
    }

    [Fact]
    public void Exact_shared_operation_table_and_default_limits_match_the_companion()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(FixturePath));
        var expected = fixture.RootElement.GetProperty("operations").EnumerateObject()
            .ToDictionary(value => value.Name, value => value.Value.GetInt32(), StringComparer.Ordinal);
        Assert.Equal(expected.OrderBy(value => value.Key), PersonalCloudMutationCost.Weights.OrderBy(value => value.Key));
        var limits = new PersonalCloudDispatchLimits();
        var shared = fixture.RootElement.GetProperty("dailyLimits");
        Assert.Equal(shared.GetProperty("envelopes").GetInt32(), limits.EnvelopesPerDay);
        Assert.Equal(shared.GetProperty("requests").GetInt32(), limits.RequestsPerDay);
        Assert.Equal(shared.GetProperty("writeCredits").GetInt32(), limits.WriteUnitsPerDay);
    }

    [Theory]
    [InlineData("project.delete")]
    [InlineData("task.moveTree")]
    public async Task Legacy_fanout_reserves_a_whole_daily_budget_but_keeps_read_requests_available(string operation)
    {
        var policy = Policy();
        var handler = new Handler();
        using var client = PersonalCloudDispatchHttp.Create(policy, handler);
        using var response = await client.PostAsync(new Uri(Origin, "/v1/tasks/mutations"), Mutation(operation));
        var status = await policy.GetStatusAsync(Origin);
        Assert.Equal(45000, status.ReservedWriteUnits);
        Assert.Equal(0, status.RemainingWriteUnits);
        Assert.Equal("local_budget_exceeded", status.Code);
        Assert.False(status.BlocksReads);
        using (await client.GetAsync(new Uri(Origin, "/health"))) { }
        Assert.Equal("local_budget_exceeded", (await policy.GetStatusAsync(Origin)).Code);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("future.operation", 45000)]
    [InlineData("future.operation", 100000)]
    [InlineData("project.delete", 100)]
    public async Task Unknown_or_over_whole_day_operation_is_visible_and_never_retried_each_day(string operation, int dailyCredits)
    {
        var policy = Policy(dailyCredits);
        var handler = new Handler();
        using var client = PersonalCloudDispatchHttp.Create(policy, handler);
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.PostAsync(new Uri(Origin, "/v1/tasks/mutations"), Mutation(operation)));
        Assert.Equal("local_operation_budget_exceeded", error.Code);
        Assert.Null(error.DispatchStatus!.ResumeAt);
        Assert.Contains("allow-once", error.Message);
        Assert.True(error.DispatchStatus.BlockingWriteUnits > dailyCredits);
        Assert.Equal(0, error.DispatchStatus.AttemptedRequests);
        _clock.Utc = _clock.Utc.AddDays(3);
        using var restarted = PersonalCloudDispatchHttp.Create(Policy(dailyCredits), handler);
        using (await restarted.GetAsync(new Uri(Origin, "/health"))) { }
        Assert.Equal("local_operation_budget_exceeded", (await policy.GetStatusAsync(Origin)).Code);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Compound_request_reserves_exact_sum_and_next_request_cannot_overspend_remainder()
    {
        var policy = Policy(50);
        var handler = new Handler();
        using var client = PersonalCloudDispatchHttp.Create(policy, handler);
        using var body = new StringContent("""{"mutations":[{"operation":"task.patch","payload":{}},{"operation":"source.refreshSnapshot","payload":{}}]}""", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri(Origin, "/v1/tasks/mutations/batch"), body);
        var status = await policy.GetStatusAsync(Origin);
        Assert.Equal(41, status.ReservedWriteUnits);
        Assert.Equal(9, status.RemainingWriteUnits);
        Assert.Equal(2, status.AttemptedEnvelopes);
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.PostAsync(new Uri(Origin, "/v1/tasks/mutations"), Mutation("task.patch")));
        Assert.Equal("local_budget_exceeded", error.Code);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(41, (await Policy(50).GetStatusAsync(Origin)).ReservedWriteUnits);
    }

    [Fact]
    public async Task Rich_creation_with_labels_and_source_cannot_be_undercharged_as_flat_creation()
    {
        var handler = new Handler();
        var policy = Policy(80);
        using var client = PersonalCloudDispatchHttp.Create(policy, handler);
        using var body = new StringContent("""{"operation":"task.createRich","payload":{"task":{"labels":[{},{}]},"source":{}}}""", Encoding.UTF8, "application/json");
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => client.PostAsync(new Uri(Origin, "/v1/tasks/mutations"), body));
        Assert.Equal("local_operation_budget_exceeded", error.Code);
        Assert.Equal(86, error.DispatchStatus!.BlockingWriteUnits);
        Assert.Equal(0, handler.Calls);
    }

    private PersonalCloudDispatchPolicy Policy(int credits = 45000) => new(_directory, new(3000, 10000, credits), _clock, () => TimeSpan.Zero);
    private static StringContent Mutation(string operation) => new(JsonSerializer.Serialize(new { operation, payload = new { } }), Encoding.UTF8, "application/json");
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Utc = DateTimeOffset.Parse("2026-10-19T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Utc;
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        }
    }
    public void Dispose() => TestDirectory.Delete(_directory);
}
