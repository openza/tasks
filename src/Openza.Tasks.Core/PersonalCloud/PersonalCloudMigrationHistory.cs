using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Openza.Tasks.Core.PersonalCloud;

internal static class PersonalCloudMigrationHistory
{
    public static PersonalCloudMigration[] ValidateBundle(string artifactVersion, IPersonalCloudMigrationBundle bundle)
    {
        if (bundle.ArtifactVersion != artifactVersion || bundle.Migrations is null || bundle.Migrations.Count is < 1 or > 9999)
            throw new PersonalCloudK0ContractException("artifact_mismatch", "The migration bundle does not match the Personal Cloud artifact.");
        var items = bundle.Migrations.ToArray();
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            var number = (index + 1).ToString("D4", CultureInfo.InvariantCulture);
            if (item is null || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 100 ||
                !(item.Name.StartsWith(number + "_", StringComparison.Ordinal) || item.Name == number + ".sql") ||
                !item.Name.EndsWith(".sql", StringComparison.Ordinal) ||
                item.Name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.')) ||
                item.Sha256 is null || item.Sha256.Length != 64 || item.Sha256.Any(character => !char.IsAsciiHexDigit(character)) ||
                string.IsNullOrWhiteSpace(item.Sql) || item.Sql.Length > 100_000 ||
                !string.Equals(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(item.Sql))), item.Sha256, StringComparison.Ordinal))
                throw new PersonalCloudK0ContractException("artifact_invalid", "Personal Cloud migrations must be an ordered, checksummed sequence starting at 0001.");
        }
        return items;
    }

    public static JsonElement ReadRows(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() != 1 ||
            result[0].ValueKind != JsonValueKind.Object ||
            result[0].TryGetProperty("success", out var success) && success.ValueKind != JsonValueKind.True ||
            !result[0].TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new PersonalCloudK0ContractException("invalid_response", "Cloudflare returned an invalid migration history response.");
        return rows;
    }

    public static int ValidatePrefix(JsonElement rows, IReadOnlyList<PersonalCloudMigration> bundle)
    {
        if (rows.GetArrayLength() > bundle.Count) throw Mismatch();
        var index = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object ||
                !row.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                !row.TryGetProperty("sha256", out var checksum) || checksum.ValueKind != JsonValueKind.String ||
                name.GetString() != bundle[index].Name || checksum.GetString() != bundle[index].Sha256)
                throw Mismatch();
            index++;
        }
        return index;
    }

    private static PersonalCloudK0ContractException Mismatch() => new("migration_mismatch",
        "Personal Cloud migration history is not a verified prefix of this artifact. Retain the database and use a compatible artifact or reviewed recovery.");
}
