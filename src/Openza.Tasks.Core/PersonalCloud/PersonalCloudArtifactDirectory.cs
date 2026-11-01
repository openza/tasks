using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.PersonalCloud;

public sealed record LoadedPersonalCloudArtifact(
    PersonalCloudDeploymentArtifact Deployment,
    IPersonalCloudMigrationBundle Migrations);

public static class PersonalCloudArtifactDirectory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    // When expectedManifestSha256 is given (a signed release), the manifest is read
    // once, checked against that digest, and parsed from those exact bytes.
    public static LoadedPersonalCloudArtifact Load(string directory, string? expectedManifestSha256 = null)
    {
        var root = Path.GetFullPath(directory);
        var manifestPath = Path.Combine(root, "artifact-manifest.json");
        try
        {
            var manifestBytes = File.ReadAllBytes(manifestPath);
            if (expectedManifestSha256 is not null &&
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(manifestBytes), Convert.FromHexString(expectedManifestSha256)))
                throw new PersonalCloudK0ContractException("release_invalid", "The Personal Cloud release manifest does not match its signed checksum.");
            var manifest = JsonSerializer.Deserialize<ArtifactManifest>(manifestBytes, Json)
                ?? throw InvalidArtifact();
            if (manifest.FormatVersion != 1 || string.IsNullOrWhiteSpace(manifest.ArtifactVersion) ||
                manifest.ArtifactVersion.Length > 128 || manifest.Worker is null || manifest.Assets is null || manifest.Migrations is null ||
                manifest.Assets.Count == 0 || manifest.Migrations.Count == 0)
                throw InvalidArtifact();
            var worker = ReadVerified(root, manifest.Worker);
            var assets = manifest.Assets.Select(item => new PersonalCloudStaticAsset(
                ValidateAssetPath(item.Path),
                item.ContentType,
                ReadVerified(root, item))).ToArray();
            var migrations = manifest.Migrations.Select(item => new PersonalCloudMigration(
                Path.GetFileName(ValidatedFile(root, item.File)),
                item.Sha256,
                new System.Text.UTF8Encoding(false, true).GetString(ReadVerified(root, item)))).ToArray();
            var bundle = new MigrationBundle(manifest.ArtifactVersion, migrations);
            PersonalCloudMigrationHistory.ValidateBundle(manifest.ArtifactVersion, bundle);
            return new LoadedPersonalCloudArtifact(
                new PersonalCloudDeploymentArtifact(manifest.ArtifactVersion, manifest.CompatibilityDate, worker, assets),
                bundle);
        }
        catch (PersonalCloudK0ContractException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException or System.Text.DecoderFallbackException)
        {
            throw new PersonalCloudK0ContractException("artifact_invalid", "The Personal Cloud deployment artifact could not be verified.", exception);
        }
    }

    private static byte[] ReadVerified(string root, ArtifactFile item)
    {
        if (item is null || item.Size < 0 || item.Size > 25 * 1024 * 1024 || item.Sha256 is null || item.Sha256.Length != 64 ||
            item.Sha256.Any(character => !char.IsAsciiHexDigit(character))) throw InvalidArtifact();
        var content = File.ReadAllBytes(ValidatedFile(root, item.File));
        if (content.LongLength != item.Size) throw InvalidArtifact();
        var expected = Convert.FromHexString(item.Sha256);
        var actual = SHA256.HashData(content);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw InvalidArtifact();
        return content;
    }

    private static string ValidatedFile(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) throw InvalidArtifact();
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) throw InvalidArtifact();
        return path;
    }

    private static string ValidateAssetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal) ||
            path.IndexOfAny(['?', '#', '\\']) >= 0) throw InvalidArtifact();
        return path;
    }

    private static PersonalCloudK0ContractException InvalidArtifact() =>
        new("artifact_invalid", "The Personal Cloud deployment artifact could not be verified.");

    private sealed record ArtifactManifest(
        int FormatVersion,
        string ArtifactVersion,
        DateOnly CompatibilityDate,
        ArtifactFile Worker,
        IReadOnlyList<ArtifactAsset> Assets,
        IReadOnlyList<ArtifactFile> Migrations);

    private record ArtifactFile(string File, long Size, string Sha256);
    private sealed record ArtifactAsset(string File, long Size, string Sha256, string Path, string ContentType)
        : ArtifactFile(File, Size, Sha256);
    private sealed record MigrationBundle(string ArtifactVersion, IReadOnlyList<PersonalCloudMigration> Migrations)
        : IPersonalCloudMigrationBundle;
}
