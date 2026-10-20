using System.Data;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Data;

/// <summary>Shared SQLite hydration and schema-read conventions for reads and transactional authoring.</summary>
internal static class SqliteReadValues
{
    public static async Task<HashSet<string>> GetColumnNamesAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    public static string? GetNullableString(IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static int ReadNullableInt(IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    public static object ToDbValue(string? value) => string.IsNullOrEmpty(value) ? DBNull.Value : value;

    public static DateTimeOffset? ReadDate(IDataRecord reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        if (reader.GetValue(ordinal) is double precise)
            return DateTimeOffset.FromUnixTimeMilliseconds(checked((long)Math.Round(precise * 1000, MidpointRounding.AwayFromZero)));
        var value = reader.GetInt64(ordinal);
        return value > 100_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : DateTimeOffset.FromUnixTimeSeconds(value);
    }

    public static DateOnly? ReadDateOnly(IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : TaskDateValues.FromStorageValue(reader.GetString(ordinal));

}
