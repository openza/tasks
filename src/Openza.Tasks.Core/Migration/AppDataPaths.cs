namespace Openza.Tasks.Core.Migration;

public static class AppDataPaths
{
    public const string DatabaseFileName = "openza_tasks.db";

    public static string GetDefaultAppDataDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Openza.Tasks");
    }

    public static string GetDefaultDatabasePath() => Path.Combine(GetDefaultAppDataDirectory(), DatabaseFileName);
}
