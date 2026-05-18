namespace Openza.Tasks.Core.Services;

public static class BackupPaths
{
    public const string ProductionPackageIdentity = "Openza.OpenzaTasks";
    public const string DevPackageIdentity = "Openza.OpenzaTasks.Dev";
    public const string PreviewPackageIdentity = "Openza.OpenzaTasks.Preview";
    public const string ProductionFlavor = "production";
    public const string DevFlavor = "dev";
    public const string PreviewFlavor = "preview";

    public static string GetRestorePointDirectory(string localFolderPath) =>
        Path.Combine(localFolderPath, "restore-points");

    public static string GetLegacyPackageBackupDirectory(string localFolderPath) =>
        Path.Combine(localFolderPath, "backups");

    public static IReadOnlyList<string> GetRedirectedStableBackupDirectories(
        string localFolderPath,
        string packageIdentityOrAppFlavor)
    {
        var packageRoot = Directory.GetParent(localFolderPath)?.FullName;
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            return [];
        }

        var folderName = GetLegacyStableBackupFolderName(packageIdentityOrAppFlavor);
        return
        [
            Path.Combine(packageRoot, "LocalCache", "Local", "Openza", folderName, "Backups"),
        ];
    }

    public static string GetAppFlavor(string packageIdentityOrAppFlavor)
    {
        if (IsDev(packageIdentityOrAppFlavor))
        {
            return DevFlavor;
        }

        if (IsPreview(packageIdentityOrAppFlavor))
        {
            return PreviewFlavor;
        }

        return ProductionFlavor;
    }

    private static bool IsDev(string packageIdentityOrAppFlavor) =>
        string.Equals(packageIdentityOrAppFlavor, DevFlavor, StringComparison.OrdinalIgnoreCase) ||
        packageIdentityOrAppFlavor.EndsWith(".Dev", StringComparison.OrdinalIgnoreCase);

    private static bool IsPreview(string packageIdentityOrAppFlavor) =>
        string.Equals(packageIdentityOrAppFlavor, PreviewFlavor, StringComparison.OrdinalIgnoreCase) ||
        packageIdentityOrAppFlavor.EndsWith(".Preview", StringComparison.OrdinalIgnoreCase);

    private static string GetLegacyStableBackupFolderName(string packageIdentityOrAppFlavor) =>
        GetAppFlavor(packageIdentityOrAppFlavor) switch
        {
            DevFlavor => "Tasks Dev",
            PreviewFlavor => "Tasks Preview",
            _ => "Tasks",
        };
}
