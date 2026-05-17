namespace Openza.Tasks.Core.Services;

public static class BackupPaths
{
    public const string ProductionPackageIdentity = "Openza.OpenzaTasks";
    public const string DevPackageIdentity = "Openza.OpenzaTasks.Dev";
    public const string ProductionFlavor = "production";
    public const string DevFlavor = "dev";

    public static string GetStableBackupDirectory(string packageIdentityOrAppFlavor)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folderName = IsDev(packageIdentityOrAppFlavor) ? "Tasks Dev" : "Tasks";
        return Path.Combine(localAppData, "Openza", folderName, "Backups");
    }

    public static string GetLegacyPackageBackupDirectory(string localFolderPath) =>
        Path.Combine(localFolderPath, "backups");

    public static string GetAppFlavor(string packageIdentityOrAppFlavor) =>
        IsDev(packageIdentityOrAppFlavor) ? DevFlavor : ProductionFlavor;

    private static bool IsDev(string packageIdentityOrAppFlavor) =>
        string.Equals(packageIdentityOrAppFlavor, DevFlavor, StringComparison.OrdinalIgnoreCase) ||
        packageIdentityOrAppFlavor.EndsWith(".Dev", StringComparison.OrdinalIgnoreCase);
}
