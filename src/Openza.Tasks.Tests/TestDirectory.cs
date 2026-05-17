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

        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                SqliteConnection.ClearAllPools();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 29)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 29)
            {
                Thread.Sleep(100);
            }
        }
    }
}
