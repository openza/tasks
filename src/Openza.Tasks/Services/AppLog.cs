using Windows.Storage;

namespace Openza.Tasks.Services;

public static class AppLog
{
    private static readonly object Sync = new();

    public static void Write(string message)
    {
        try
        {
            var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "startup.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            lock (Sync)
            {
                File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    public static void Write(Exception exception) => Write(exception.ToString());
}
