namespace Openza.Tasks.Core.Services;

public static class PrivateFilePermissions
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void EnsureDirectory(string path)
    {
        var existed = Directory.Exists(path);
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows() && !existed)
        {
            File.SetUnixFileMode(path, PrivateDirectoryMode);
        }
    }

    public static void EnsureOwnedDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, PrivateDirectoryMode);
        }
    }

    public static void EnsureFile(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            File.SetUnixFileMode(path, PrivateFileMode);
        }
    }

    public static void EnsureFiles(string directory, string searchPattern)
    {
        EnsureDirectory(directory);
        foreach (var path in Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly))
        {
            EnsureFile(path);
        }
    }
}
