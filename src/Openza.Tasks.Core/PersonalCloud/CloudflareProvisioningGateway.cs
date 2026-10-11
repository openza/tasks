using System.Security.Cryptography;
using System.Text;

namespace Openza.Tasks.Core.PersonalCloud;

public interface ICloudflareArtifactDeploymentClient
{
    Task<CloudflareProvisioningApplication> DeployAsync(
        string accessToken,
        string accountId,
        string databaseId,
        string resourceName,
        string attemptId,
        string artifactVersion,
        CancellationToken cancellationToken = default);
}

public interface IPersonalCloudMigrationBundle
{
    string ArtifactVersion { get; }
    IReadOnlyList<PersonalCloudMigration> Migrations { get; }
}

public sealed record PersonalCloudMigration(string Name, string Sha256, string Sql);

public sealed class CloudflareProvisioningGateway(
    CloudflareManagementClient management,
    ICloudflareArtifactDeploymentClient deployment,
    IPersonalCloudMigrationBundle migrations,
    PersonalCloudHealthClient health,
    string deviceName,
    bool providerBridge) : ICloudflareProvisioningGateway
{
    public async Task<CloudflareProvisioningAccount> ResolveAccountAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var accounts = await management.ListAccountsAsync(accessToken, cancellationToken).ConfigureAwait(false);
        return accounts.Count switch
        {
            1 => new CloudflareProvisioningAccount(accounts[0].Id),
            0 => throw new PersonalCloudK0ContractException("account_unavailable", "Cloudflare did not authorize an account for Personal Cloud."),
            _ => throw new PersonalCloudK0ContractException("account_selection_required", "Choose one Cloudflare account for Personal Cloud."),
        };
    }

    public async Task<CloudflareProvisioningDatabase> EnsureDatabaseAsync(string accessToken, string accountId, string attemptId, CancellationToken cancellationToken = default)
    {
        var database = await management.EnsureDatabaseAsync(accessToken, accountId, ResourceName(attemptId), cancellationToken).ConfigureAwait(false);
        return new CloudflareProvisioningDatabase(database.Id);
    }

    public Task<CloudflareProvisioningApplication> DeployApplicationAsync(string accessToken, string accountId, string databaseId, string attemptId, string artifactVersion, CancellationToken cancellationToken = default) =>
        deployment.DeployAsync(accessToken, accountId, databaseId, ResourceName(attemptId), attemptId, artifactVersion, cancellationToken);

    public async Task ApplyMigrationsAsync(string accessToken, string accountId, string databaseId, string artifactVersion, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(migrations.ArtifactVersion, artifactVersion, StringComparison.Ordinal) || migrations.Migrations.Count == 0)
            throw new PersonalCloudK0ContractException("artifact_mismatch", "The Personal Cloud migration bundle does not match the deployment artifact.");
        await management.ExecuteD1Async(accessToken, accountId, databaseId,
            "CREATE TABLE IF NOT EXISTS openza_schema_migrations(name TEXT PRIMARY KEY,sha256 TEXT NOT NULL,applied_at TEXT NOT NULL)",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var migration in migrations.Migrations)
        {
            ValidateMigration(migration);
            var result = await management.QueryD1Async(accessToken, accountId, databaseId,
                "SELECT sha256 FROM openza_schema_migrations WHERE name=?1", [migration.Name], cancellationToken).ConfigureAwait(false);
            var existing = ReadChecksum(result);
            if (existing is not null)
            {
                if (!string.Equals(existing, migration.Sha256, StringComparison.Ordinal))
                    throw new PersonalCloudK0ContractException("migration_mismatch", "The deployed Personal Cloud migration history does not match the artifact.");
                continue;
            }
            var appliedAt = DateTimeOffset.UtcNow.ToString("O");
            await management.ExecuteD1Async(accessToken, accountId, databaseId,
                $"{migration.Sql.TrimEnd()}\nINSERT INTO openza_schema_migrations(name,sha256,applied_at) VALUES('{Literal(migration.Name)}','{Literal(migration.Sha256)}','{Literal(appliedAt)}');",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public Task VerifyHealthAsync(Uri serviceOrigin, string artifactVersion, CancellationToken cancellationToken = default) =>
        health.VerifyAsync(serviceOrigin, artifactVersion, cancellationToken);

    public async Task<PersonalCloudInitialPairing> CreateInitialPairingAsync(
        string accessToken,
        string accountId,
        string databaseId,
        Uri serviceOrigin,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || deviceName.Length > 80)
            throw new PersonalCloudK0ContractException("invalid_request", "The desktop name is invalid.");
        var grant = $"pair-{Base64Url(RandomNumberGenerator.GetBytes(24))}";
        var grantHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(grant)));
        var createdAt = DateTimeOffset.UtcNow;
        await management.ExecuteD1Async(accessToken, accountId, databaseId,
            "INSERT INTO pairing_grants(grant_hash,expires_at,created_at) VALUES(?1,?2,?3)",
            [grantHash, createdAt.AddMinutes(10).ToString("O"), createdAt.ToString("O")], cancellationToken).ConfigureAwait(false);
        return new PersonalCloudInitialPairing(grant, deviceName.Trim(), providerBridge);
    }

    public async Task<Uri> CreateOwnerBootstrapAsync(
        string accessToken,
        string accountId,
        string databaseId,
        Uri serviceOrigin,
        CancellationToken cancellationToken = default)
    {
        var grant = $"setup-{Base64Url(RandomNumberGenerator.GetBytes(24))}";
        var grantHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(grant)));
        await management.ExecuteD1Async(accessToken, accountId, databaseId,
            """INSERT INTO owner_bootstrap(singleton,grant_hash,expires_at,consumed_at) VALUES(1,?1,?2,NULL) ON CONFLICT(singleton) DO UPDATE SET grant_hash=excluded.grant_hash,expires_at=excluded.expires_at WHERE owner_bootstrap.consumed_at IS NULL""",
            [grantHash, DateTimeOffset.UtcNow.AddMinutes(30).ToString("O")], cancellationToken).ConfigureAwait(false);
        var builder = new UriBuilder(serviceOrigin) { Fragment = $"setup={Uri.EscapeDataString(grant)}" };
        return builder.Uri;
    }

    internal static string ResourceName(string attemptId)
    {
        var suffix = new string(attemptId.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        if (suffix.StartsWith("pc", StringComparison.Ordinal)) suffix = suffix[2..];
        if (suffix.Length < 12) throw new PersonalCloudK0ContractException("invalid_state", "The Personal Cloud setup identity is invalid.");
        return $"openza-personal-{suffix[..12]}";
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void ValidateMigration(PersonalCloudMigration migration)
    {
        if (string.IsNullOrWhiteSpace(migration.Name) || migration.Name.Length > 100 ||
            migration.Name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.')) ||
            migration.Sha256.Length != 64 || migration.Sha256.Any(character => !char.IsAsciiHexDigit(character)) ||
            string.IsNullOrWhiteSpace(migration.Sql) || migration.Sql.Length > 100_000)
            throw new PersonalCloudK0ContractException("artifact_invalid", "The Personal Cloud migration artifact is invalid.");
    }

    private static string? ReadChecksum(System.Text.Json.JsonElement result)
    {
        if (result.ValueKind != System.Text.Json.JsonValueKind.Array || result.GetArrayLength() == 0) return null;
        var query = result[0];
        if (!query.TryGetProperty("results", out var rows) || rows.ValueKind != System.Text.Json.JsonValueKind.Array || rows.GetArrayLength() == 0) return null;
        return rows[0].TryGetProperty("sha256", out var checksum) && checksum.ValueKind == System.Text.Json.JsonValueKind.String
            ? checksum.GetString()
            : throw new PersonalCloudK0ContractException("invalid_response", "Cloudflare returned an invalid migration response.");
    }

    private static string Literal(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
