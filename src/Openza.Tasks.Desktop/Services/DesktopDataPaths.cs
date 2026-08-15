using Openza.Tasks.Core.Services;

namespace Openza.Tasks.Desktop.Services;

public static class DesktopDataPaths
{
    private const string DataDirectoryOverride = "OPENZA_TASKS_DATA_DIR";

    public static string DataDirectory
    {
        get
        {
            var configuredPath = Environment.GetEnvironmentVariable(DataDirectoryOverride);
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                var overridePath = Path.GetFullPath(configuredPath);
                PrivateFilePermissions.EnsureOwnedDirectory(overridePath);
                return overridePath;
            }

            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Openza",
                "Tasks");
            PrivateFilePermissions.EnsureOwnedDirectory(path);
            return path;
        }
    }

    public static string DatabasePath
    {
        get
        {
            var path = Path.Combine(DataDirectory, "openza-tasks.db");
            PrivateFilePermissions.EnsureFile(path);
            return path;
        }
    }

    public static string RestorePointDirectory
    {
        get
        {
            var path = Path.Combine(DataDirectory, "restore-points");
            PrivateFilePermissions.EnsureOwnedDirectory(path);
            PrivateFilePermissions.EnsureFiles(path, "*.db");
            PrivateFilePermissions.EnsureFiles(path, "*.db.json");
            return path;
        }
    }
}
