using Openza.Tasks.Core.Services;

namespace Openza.Tasks.Application.Runtime;

public sealed class ChannelRuntimeLease : IDisposable
{
    private readonly FileStream _stream;

    private ChannelRuntimeLease(FileStream stream) => _stream = stream;

    public static ChannelRuntimeLease AcquireShared(OpenzaRuntimeContext context)
    {
        var lockPath = context.RuntimeLockPath;
        EnsureLockDirectory(lockPath);
        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
            PrivateFilePermissions.EnsureFile(lockPath);
            return new ChannelRuntimeLease(stream);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException($"{context.DisplayName} data replacement is in progress. Try again when it finishes.", exception);
        }
    }

    public static ChannelRuntimeLease AcquireExclusive(OpenzaRuntimeContext context)
    {
        var lockPath = context.RuntimeLockPath;
        EnsureLockDirectory(lockPath);
        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            PrivateFilePermissions.EnsureFile(lockPath);
            return new ChannelRuntimeLease(stream);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException($"Close {context.DisplayName} before replacing its data.", exception);
        }
    }

    public static ChannelRuntimeLease AcquireDatabaseRead(OpenzaRuntimeContext context) =>
        Acquire(
            context.DatabaseReplacementLockPath,
            FileAccess.Read,
            FileShare.Read,
            $"{context.DisplayName} database restore is in progress. Try again when it finishes.");

    public static ChannelRuntimeLease AcquireDatabaseReplacement(OpenzaRuntimeContext context) =>
        Acquire(
            context.DatabaseReplacementLockPath,
            FileAccess.ReadWrite,
            FileShare.None,
            $"Close other {context.DisplayName} operations before restoring its database.");

    private static ChannelRuntimeLease Acquire(
        string lockPath,
        FileAccess access,
        FileShare sharing,
        string failureMessage)
    {
        EnsureLockDirectory(lockPath);
        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, access, sharing);
            PrivateFilePermissions.EnsureFile(lockPath);
            return new ChannelRuntimeLease(stream);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(failureMessage, exception);
        }
    }

    private static void EnsureLockDirectory(string lockPath) =>
        PrivateFilePermissions.EnsureOwnedDirectory(Path.GetDirectoryName(lockPath)
            ?? throw new InvalidOperationException("The runtime coordination directory could not be resolved."));

    public void Dispose() => _stream.Dispose();
}
