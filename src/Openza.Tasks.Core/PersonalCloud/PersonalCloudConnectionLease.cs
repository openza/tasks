using Openza.Tasks.Core.Services;

namespace Openza.Tasks.Core.PersonalCloud;

// Authoring and sync share this channel-local lease. Connection replacement
// owns it exclusively, so its pending-history check remains true until saved.
public sealed class PersonalCloudConnectionLease : IDisposable
{
    private readonly FileStream _stream;
    private PersonalCloudConnectionLease(FileStream stream) => _stream = stream;

    public static PersonalCloudConnectionLease AcquireAuthoring(string databasePath) => Acquire(databasePath, false);
    public static PersonalCloudConnectionLease AcquireReplacement(string databasePath) => Acquire(databasePath, true);

    private static PersonalCloudConnectionLease Acquire(string databasePath, bool exclusive)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $".{Path.GetFileName(databasePath)}.personal-cloud-connection.lock");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate,
                exclusive ? FileAccess.ReadWrite : FileAccess.Read,
                exclusive ? FileShare.None : FileShare.Read);
            try
            {
                PrivateFilePermissions.EnsureFile(path);
                return new PersonalCloudConnectionLease(stream);
            }
            catch { stream.Dispose(); throw; }
        }
        catch (IOException exception)
        {
            throw new PersonalCloudK0ContractException("connection_busy",
                "Another task edit, sync, or connection change is in progress. Retry when it finishes; no edit or connection change was made.", exception);
        }
    }

    public void Dispose() => _stream.Dispose();
}
