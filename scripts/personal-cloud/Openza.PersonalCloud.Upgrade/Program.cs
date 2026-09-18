using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Openza.Tasks.Core.PersonalCloud;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: Openza.PersonalCloud.Upgrade <artifact-directory> <service-origin> <instance-id>");
    return 2;
}

var artifact = PersonalCloudArtifactDirectory.Load(args[0]);
var serviceOrigin = new Uri(args[1], UriKind.Absolute);
var instanceId = args[2];
var resourceName = ResourceName(instanceId);
if (!string.Equals(serviceOrigin.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
    !string.Equals(serviceOrigin.Host.Split('.')[0], resourceName, StringComparison.Ordinal) ||
    !string.IsNullOrEmpty(serviceOrigin.Query) || !string.IsNullOrEmpty(serviceOrigin.Fragment))
    throw new InvalidOperationException("The service origin does not match the Personal Cloud instance identity.");

const string clientId = "ab8248060e7dc03bb9ccf7f71c179e45";
var redirect = new Uri("http://127.0.0.1:48192/cloudflare/callback");
string[] scopes = ["account-settings.read", "d1.write", "workers-scripts.write"];
var oauth = new CloudflareOAuthClient(new HttpClient(), new CloudflareOAuthConfiguration(clientId, redirect, scopes));
var broker = new LoopbackCloudflareAuthorizationBroker(OpenBrowserAsync);
var management = new CloudflareManagementClient(new HttpClient());
var deployment = new CloudflareArtifactDeploymentClient(new HttpClient(), artifact.Deployment);
var gateway = new CloudflareProvisioningGateway(
    management,
    deployment,
    artifact.Migrations,
    new PersonalCloudHealthClient(new HttpClient()),
    Environment.MachineName,
    providerBridge: true);

Console.WriteLine($"Target: {serviceOrigin}");
Console.WriteLine($"Artifact: {artifact.Deployment.Version}; migrations: {artifact.Migrations.Migrations.Count}");
var currentHealth = await ReadHealthAsync(serviceOrigin);
Console.WriteLine($"Current health: artifact {currentHealth.ArtifactVersion}; schema {currentHealth.SchemaVersion}");
if (currentHealth.ArtifactVersion == artifact.Deployment.Version && currentHealth.SchemaVersion == 17)
{
    Console.WriteLine("The Personal Cloud is already current; no Cloudflare authorization was requested.");
    return 0;
}
if (currentHealth.ArtifactVersion != "0.1.0-preview.1" || currentHealth.SchemaVersion != 16)
    throw new InvalidOperationException("The live Personal Cloud is not at the reviewed upgrade starting point.");

Console.WriteLine("Opening Cloudflare authorization in the default browser...");
var access = await oauth.AuthorizeAsync(broker);
Exception? operationFailure = null;
try
{
    var accounts = await management.ListAccountsAsync(access.AccessToken);
    if (accounts.Count != 1)
        throw new InvalidOperationException("Exactly one Cloudflare account must be authorized for this upgrade.");
    var accountId = accounts[0].Id;
    var databases = (await management.ListDatabasesAsync(access.AccessToken, accountId))
        .Where(database => string.Equals(database.Name, resourceName, StringComparison.Ordinal))
        .ToArray();
    if (databases.Length != 1)
        throw new InvalidOperationException("The existing Personal Cloud D1 database could not be resolved unambiguously.");
    var databaseId = databases[0].Id;

    var before = await ReadCountsAsync(management, access.AccessToken, accountId, databaseId);
    Console.WriteLine($"Before: {JsonSerializer.Serialize(before)}");
    if (before.Migrations != 16)
        throw new InvalidOperationException("The live D1 migration history is not at the reviewed upgrade starting point.");

    // Schema 17 only adds nullable columns, so the existing preview.1 Worker
    // remains compatible if deployment fails after this forward-only step.
    await gateway.ApplyMigrationsAsync(access.AccessToken, accountId, databaseId, artifact.Deployment.Version);
    var migrated = await ReadCountsAsync(management, access.AccessToken, accountId, databaseId);
    EnsureDomainCountsMatch(before, migrated);
    if (migrated.Migrations != artifact.Migrations.Migrations.Count)
        throw new InvalidOperationException("D1 did not record the complete reviewed migration bundle.");
    Console.WriteLine($"Migrated: schema history {migrated.Migrations}; domain counts unchanged");

    var application = await deployment.DeployAsync(
        access.AccessToken,
        accountId,
        databaseId,
        resourceName,
        instanceId,
        artifact.Deployment.Version);
    if (application.ServiceOrigin != serviceOrigin)
        throw new InvalidOperationException("Cloudflare returned an unexpected Personal Cloud origin.");

    var health = await WaitForHealthAsync(serviceOrigin, artifact.Deployment.Version);
    var after = await ReadCountsAsync(management, access.AccessToken, accountId, databaseId);
    EnsureDomainCountsMatch(before, after);
    if (after.Migrations != artifact.Migrations.Migrations.Count)
        throw new InvalidOperationException("Migration history changed unexpectedly after deployment.");
    Console.WriteLine($"After: {JsonSerializer.Serialize(after)}");
    Console.WriteLine($"Upgrade complete: artifact {health.ArtifactVersion}; schema {health.SchemaVersion}");
}
catch (Exception exception)
{
    operationFailure = exception;
    throw;
}
finally
{
    try
    {
        await oauth.RevokeAsync(access.AccessToken);
        Console.WriteLine("Temporary Cloudflare authorization revoked.");
    }
    catch when (operationFailure is not null)
    {
        Console.Error.WriteLine("WARNING: the upgrade failed and Cloudflare token revocation also failed.");
    }
}

return 0;

static Task OpenBrowserAsync(Uri uri, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    return Task.CompletedTask;
}

static string ResourceName(string instanceId)
{
    var suffix = new string(instanceId.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
    if (suffix.StartsWith("pc", StringComparison.Ordinal)) suffix = suffix[2..];
    if (suffix.Length < 12) throw new InvalidOperationException("The Personal Cloud instance identity is invalid.");
    return $"openza-personal-{suffix[..12]}";
}

static async Task<Health> ReadHealthAsync(Uri origin)
{
    using var response = await new HttpClient().GetAsync(new Uri(origin, "/health"));
    response.EnsureSuccessStatusCode();
    var health = await response.Content.ReadFromJsonAsync<Health>();
    return health is { Status: "ok", ProtocolVersion: 1, Product: "tasks" }
        ? health
        : throw new InvalidOperationException("The live Personal Cloud health response is invalid.");
}

static async Task<Health> WaitForHealthAsync(Uri origin, string artifactVersion)
{
    Exception? last = null;
    for (var attempt = 0; attempt < 12; attempt++)
    {
        try
        {
            var health = await ReadHealthAsync(origin);
            if (health.ArtifactVersion == artifactVersion && health.SchemaVersion == 17) return health;
        }
        catch (Exception exception)
        {
            last = exception;
        }
        await Task.Delay(TimeSpan.FromSeconds(2));
    }
    throw new InvalidOperationException("The upgraded Personal Cloud did not become healthy in time.", last);
}

static async Task<Counts> ReadCountsAsync(
    CloudflareManagementClient management,
    string accessToken,
    string accountId,
    string databaseId)
{
    const string sql = """
        SELECT
          (SELECT COUNT(*) FROM spaces) AS spaces,
          (SELECT COUNT(*) FROM projects) AS projects,
          (SELECT COUNT(*) FROM labels) AS labels,
          (SELECT COUNT(*) FROM tasks) AS tasks,
          (SELECT COUNT(*) FROM changes) AS changes,
          (SELECT COUNT(*) FROM mutation_receipts) AS mutations,
          (SELECT COALESCE(MAX(change_seq),0) FROM changes) AS checkpoint,
          (SELECT COUNT(*) FROM openza_schema_migrations) AS migrations
        """;
    var result = await management.QueryD1Async(accessToken, accountId, databaseId, sql);
    if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() != 1 ||
        !result[0].TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != 1)
        throw new InvalidOperationException("Cloudflare returned an invalid D1 continuity result.");
    var row = rows[0];
    return new Counts(
        ReadLong(row, "spaces"), ReadLong(row, "projects"), ReadLong(row, "labels"),
        ReadLong(row, "tasks"), ReadLong(row, "changes"), ReadLong(row, "mutations"),
        ReadLong(row, "checkpoint"), ReadLong(row, "migrations"));
}

static long ReadLong(JsonElement row, string name) =>
    row.TryGetProperty(name, out var value) && value.TryGetInt64(out var number)
        ? number
        : throw new InvalidOperationException("Cloudflare returned an invalid D1 count.");

static void EnsureDomainCountsMatch(Counts before, Counts after)
{
    if (before with { Migrations = after.Migrations } != after)
        throw new InvalidOperationException("Personal Cloud domain counts changed during the infrastructure upgrade.");
}

internal sealed record Health(string Status, int ProtocolVersion, string Product, string ArtifactVersion, int SchemaVersion);
internal sealed record Counts(long Spaces, long Projects, long Labels, long Tasks, long Changes, long Mutations, long Checkpoint, long Migrations);
