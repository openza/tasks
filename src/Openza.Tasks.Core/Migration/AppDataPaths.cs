namespace Openza.Tasks.Core.Migration;

public static class AppDataPaths
{
    public const string DatabaseFileName = "openza_tasks.db";
    public const string PreviousCleanCoreDatabaseFileName = "openza_tasks_v3.db";
    public const string LegacyDatabaseFileName = "openza.db";

    public static string GetDefaultAppDataDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Openza.Tasks");
    }

    public static string GetDefaultDatabasePath() => Path.Combine(GetDefaultAppDataDirectory(), DatabaseFileName);

    public static string GetLegacyFlutterDatabasePath()
    {
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(roamingAppData, "com.openza.tasks", DatabaseFileName);
    }

    public static IReadOnlyList<string> GetLegacyFlutterDatabaseCandidates()
    {
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        return
        [
            Path.Combine(roamingAppData, "com.openza.tasks", DatabaseFileName),
            Path.Combine(roamingAppData, "com.openza.tasks", PreviousCleanCoreDatabaseFileName),
            Path.Combine(roamingAppData, "openza_tasks", DatabaseFileName),
            Path.Combine(roamingAppData, "openza_tasks", PreviousCleanCoreDatabaseFileName),
            Path.Combine(roamingAppData, "Openza Tasks", DatabaseFileName),
            Path.Combine(roamingAppData, "Openza Tasks", PreviousCleanCoreDatabaseFileName),
            Path.Combine(roamingAppData, "openza", DatabaseFileName),
            Path.Combine(roamingAppData, "openza", PreviousCleanCoreDatabaseFileName),
            Path.Combine(roamingAppData, "openza", LegacyDatabaseFileName),
            Path.Combine(roamingAppData, "com.openza", DatabaseFileName),
            Path.Combine(roamingAppData, "com.openza", PreviousCleanCoreDatabaseFileName),
            Path.Combine(localAppData, "com.openza.tasks", DatabaseFileName),
            Path.Combine(localAppData, "com.openza.tasks", PreviousCleanCoreDatabaseFileName),
            Path.Combine(localAppData, "openza_tasks", DatabaseFileName),
            Path.Combine(localAppData, "openza_tasks", PreviousCleanCoreDatabaseFileName),
            Path.Combine(localAppData, "Openza Tasks", DatabaseFileName),
            Path.Combine(localAppData, "Openza Tasks", PreviousCleanCoreDatabaseFileName),
            Path.Combine(documents, DatabaseFileName),
            Path.Combine(documents, PreviousCleanCoreDatabaseFileName),
            Path.Combine(documents, LegacyDatabaseFileName),
        ];
    }
}
