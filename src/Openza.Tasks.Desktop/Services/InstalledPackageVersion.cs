using System.Runtime.InteropServices;
using System.Text;

namespace Openza.Tasks.Desktop.Services;

internal static class InstalledPackageVersion
{
    public static string? GetMsixVersion()
    {
        if (!OperatingSystem.IsWindows()) return null;
        uint length = 0;
        if (GetCurrentPackageFullName(ref length, null) != 122) return null;
        var name = new StringBuilder((int)length);
        if (GetCurrentPackageFullName(ref length, name) != 0) return null;
        var parts = name.ToString().Split('_');
        return parts.Length == 5 && Version.TryParse(parts[1], out var version) ? version.ToString() : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, StringBuilder? packageFullName);
}
