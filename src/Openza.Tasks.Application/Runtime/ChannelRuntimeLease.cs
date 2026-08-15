using Openza.Tasks.Core.Services;

namespace Openza.Tasks.Application.Runtime;

public sealed class ChannelRuntimeLease : IDisposable
{
    private readonly FileStream _stream;

    private ChannelRuntimeLease(FileStream stream) => _stream = stream;

    public static ChannelRuntimeLease AcquireShared(OpenzaRuntimeContext context)
    {
        PrivateFilePermissions.EnsureOwnedDirectory(context.DataDirectory);
        var stream = new FileStream(context.RuntimeLockPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
        PrivateFilePermissions.EnsureFile(context.RuntimeLockPath);
        return new ChannelRuntimeLease(stream);
    }

    public static ChannelRuntimeLease AcquireExclusive(OpenzaRuntimeContext context)
    {
        PrivateFilePermissions.EnsureOwnedDirectory(context.DataDirectory);
        try
        {
            var stream = new FileStream(context.RuntimeLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            PrivateFilePermissions.EnsureFile(context.RuntimeLockPath);
            return new ChannelRuntimeLease(stream);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException($"Close {context.DisplayName} before replacing its data.", exception);
        }
    }

    public void Dispose() => _stream.Dispose();
}
