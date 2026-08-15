using Openza.Tasks.Application.Runtime;
using Openza.Tasks.Core.Services;

namespace Openza.Tasks.Desktop.Services;

public static class DesktopDataPaths
{
    private const string DevDataDirectoryOverride = "OPENZA_TASKS_DEV_DATA_DIR";

    public static OpenzaRuntimeContext Runtime { get; } = CreateRuntime();

    public static string DataDirectory
    {
        get
        {
            var path = Runtime.DataDirectory;
            PrivateFilePermissions.EnsureOwnedDirectory(path);
            return path;
        }
    }

    public static string DatabasePath
    {
        get
        {
            var path = Runtime.DatabasePath;
            PrivateFilePermissions.EnsureFile(path);
            return path;
        }
    }

    public static string RestorePointDirectory
    {
        get
        {
            var path = Runtime.RestorePointDirectory;
            PrivateFilePermissions.EnsureOwnedDirectory(path);
            PrivateFilePermissions.EnsureFiles(path, "*.db");
            PrivateFilePermissions.EnsureFiles(path, "*.db.json");
            return path;
        }
    }

    private static OpenzaRuntimeContext CreateRuntime()
    {
        var channel = OpenzaRuntimeContext.ReadChannel(typeof(DesktopDataPaths).Assembly);
        var devOverride = channel == OpenzaChannel.Dev
            ? Environment.GetEnvironmentVariable(DevDataDirectoryOverride)
            : null;
        return OpenzaRuntimeContext.Create(channel, devOverride);
    }
}
