using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudMigrationPreflightTests
{
    [Theory]
    [InlineData("newer")] [InlineData("unknown")] [InlineData("gap")]
    [InlineData("duplicate")] [InlineData("changed")] [InlineData("unordered")]
    public async Task Entire_history_must_be_a_known_ordered_prefix_before_update_setup_resume_or_migration(string defect)
    {
        var bundle = Bundle();
        var history = bundle.Migrations.Select(item => (item.Name, item.Sha256)).ToList();
        switch (defect)
        {
            case "newer": history.Add(("0004_future.sql", new string('a', 64))); break;
            case "unknown": history[1] = ("0002_other.sql", history[1].Sha256); break;
            case "gap": history.RemoveAt(1); break;
            case "duplicate": history[1] = history[0]; break;
            case "changed": history[0] = (history[0].Name, new string('b', 64)); break;
            case "unordered": history.Reverse(); break;
        }
        var http = new HistoryHttp(history);
        var deployment = new Deployment();
        var gateway = Gateway(bundle, http, deployment);
        foreach (var action in new Func<Task>[]
        {
            () => gateway.VerifyMigrationHistoryAsync("synthetic-access", "account", "database", bundle.ArtifactVersion),
            () => gateway.DeployApplicationAsync("synthetic-access", "account", "database", "pc_attempt_1234567890", bundle.ArtifactVersion),
            () => gateway.ApplyMigrationsAsync("synthetic-access", "account", "database", bundle.ArtifactVersion),
        })
            Assert.Equal("migration_mismatch", (await Assert.ThrowsAsync<PersonalCloudK0ContractException>(action)).Code);
        Assert.Equal(0, deployment.Calls);
        Assert.Empty(http.Writes);
        Assert.Contains(http.Reads, sql => sql.Contains("ORDER BY name COLLATE BINARY LIMIT 4", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("order")] [InlineData("gap")] [InlineData("duplicate")]
    [InlineData("checksum")] [InlineData("sql")]
    public async Task Invalid_bundle_is_rejected_before_any_resource_request(string defect)
    {
        var items = Bundle().Migrations.ToArray();
        switch (defect)
        {
            case "order": Array.Reverse(items); break;
            case "gap": items = [items[0], items[2]]; break;
            case "duplicate": items[1] = items[0]; break;
            case "checksum": items[0] = items[0] with { Sha256 = new string('a', 64) }; break;
            case "sql": items[0] = items[0] with { Sql = items[0].Sql + "-- changed" }; break;
        }
        var bundle = new TestBundle("same-version-label", items);
        var http = new HistoryHttp([]);
        var deployment = new Deployment();
        Assert.Equal("artifact_invalid", (await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
            Gateway(bundle, http, deployment).DeployApplicationAsync("synthetic-access", "account", "database", "pc_attempt_1234567890", bundle.ArtifactVersion))).Code);
        Assert.Empty(http.Reads);
        Assert.Empty(http.Writes);
        Assert.Equal(0, deployment.Calls);
    }

    [Theory]
    [InlineData("fresh-upgrade")] [InlineData("missing")]
    [InlineData("empty-existing")] [InlineData("wrangler")]
    public async Task Upgrade_never_invents_checksum_provenance_from_empty_or_names_only_history(string state)
    {
        var bundle = Bundle();
        var http = new HistoryHttp([]) { Tables = state switch
        {
            "fresh-upgrade" => [], "missing" => ["tasks"],
            "wrangler" => ["d1_migrations", "tasks"], _ => ["openza_schema_migrations", "tasks"],
        } };
        var deployment = new Deployment();
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
            Gateway(bundle, http, deployment).VerifyMigrationHistoryAsync("synthetic-access", "account", "database", bundle.ArtifactVersion));
        Assert.Equal("migration_history_unverifiable", error.Code);
        Assert.Equal(0, deployment.Calls);
        Assert.Empty(http.Writes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Fresh_setup_and_known_partial_resume_apply_only_the_missing_ordered_suffix(bool resumed)
    {
        var bundle = Bundle();
        var http = new HistoryHttp(resumed ? [(bundle.Migrations[0].Name, bundle.Migrations[0].Sha256)] : [])
        { Tables = resumed ? ["openza_schema_migrations", "tasks"] : [] };
        var deployment = new Deployment();
        var gateway = Gateway(bundle, http, deployment);
        await gateway.DeployApplicationAsync("synthetic-access", "account", "database", "pc_attempt_1234567890", bundle.ArtifactVersion);
        Assert.Equal(1, deployment.Calls);
        Assert.Empty(http.Writes);
        await gateway.ApplyMigrationsAsync("synthetic-access", "account", "database", bundle.ArtifactVersion);
        if (!resumed) Assert.StartsWith("CREATE TABLE IF NOT EXISTS openza_schema_migrations", http.Writes[0]);
        var expected = bundle.Migrations.Skip(resumed ? 1 : 0).ToArray();
        var offset = resumed ? 0 : 1;
        Assert.Equal(expected.Length + offset, http.Writes.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.StartsWith(expected[index].Sql, http.Writes[index + offset]);
            Assert.Contains(expected[index].Sha256, http.Writes[index + offset]);
        }
        if (resumed) Assert.DoesNotContain(http.Writes, sql => sql.StartsWith(bundle.Migrations[0].Sql, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"results\":null}]")]
    [InlineData("[{\"success\":false,\"results\":[]}]")]
    [InlineData("[{\"results\":[{\"name\":\"0001_step.sql\"}]}]")]
    public async Task Malformed_history_cannot_be_treated_as_a_fresh_database(string response)
    {
        var http = new HistoryHttp([]) { JournalResponse = response };
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => Gateway(Bundle(), http, new()).ApplyMigrationsAsync(
            "synthetic-access", "account", "database", "same-version-label"));
        Assert.Contains(error.Code, new[] { "invalid_response", "migration_mismatch" });
        Assert.Empty(http.Writes);
    }

    private static TestBundle Bundle() => new("same-version-label", Enumerable.Range(1, 3).Select(index =>
    {
        var sql = $"CREATE TABLE synthetic_{index}(id TEXT);";
        return new PersonalCloudMigration($"{index:D4}_step.sql", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql))), sql);
    }).ToArray());

    [Fact]
    public async Task Failed_statement_result_inside_successful_api_envelope_stops_the_migration_suffix()
    {
        var http = new HistoryHttp([]) { Tables = [], FailWriteIndex = 1 };
        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() =>
            Gateway(Bundle(), http, new()).ApplyMigrationsAsync("synthetic-access", "account", "database", "same-version-label"));
        Assert.Equal("invalid_response", error.Code);
        Assert.Equal(2, http.Writes.Count); // Journal creation and failed first migration, never the next migration.
        Assert.DoesNotContain(http.Writes, sql => sql.StartsWith(Bundle().Migrations[1].Sql, StringComparison.Ordinal));
    }

    private static CloudflareProvisioningGateway Gateway(TestBundle bundle, HistoryHttp handler, Deployment deployment) => new(
        new(new HttpClient(handler)), deployment, bundle, new(new HttpClient(handler)), "Synthetic desktop", false);

    private sealed record TestBundle(string ArtifactVersion, IReadOnlyList<PersonalCloudMigration> Migrations) : IPersonalCloudMigrationBundle;

    private sealed class Deployment : ICloudflareArtifactDeploymentClient
    {
        public int Calls { get; private set; }
        public Task<CloudflareProvisioningApplication> DeployAsync(string accessToken, string accountId, string databaseId, string resourceName,
            string attemptId, string artifactVersion, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new CloudflareProvisioningApplication(resourceName, new("https://openza-personal-test.example.workers.dev/"))); }
    }

    private sealed class HistoryHttp(IReadOnlyList<(string Name, string Sha256)> history) : HttpMessageHandler
    {
        public string[] Tables { get; init; } = ["openza_schema_migrations", "tasks"];
        public string? JournalResponse { get; init; }
        public int? FailWriteIndex { get; init; }
        public List<string> Reads { get; } = [];
        public List<string> Writes { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var sql = body.RootElement.GetProperty("sql").GetString()!;
            object result;
            if (sql.Contains("sqlite_master", StringComparison.Ordinal))
            { Reads.Add(sql); result = new[] { new { results = Tables.Select(name => new { name }).ToArray() } }; }
            else if (sql.StartsWith("SELECT name,sha256", StringComparison.Ordinal))
            {
                Reads.Add(sql);
                if (JournalResponse is not null)
                    return new(HttpStatusCode.OK) { Content = new StringContent("{\"success\":true,\"result\":" + JournalResponse + "}", Encoding.UTF8, "application/json") };
                result = new[] { new { results = history.Select(item => new { name = item.Name, sha256 = item.Sha256 }).ToArray() } };
            }
            else { Writes.Add(sql); result = FailWriteIndex == Writes.Count - 1 ? new object[] { new { success = false } } : Array.Empty<object>(); }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { success = true, result }), Encoding.UTF8, "application/json") };
        }
    }
}
