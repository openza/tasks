using Microsoft.Data.Sqlite;

namespace Openza.Tasks.Tests;

internal static class TestDirectory
{
    public static void Delete(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
                SqliteConnection.ClearAllPools();
            }
        }
    }
}
