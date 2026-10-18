using System.Security.Cryptography;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Desktop.Services.Continuity;

/// <summary>Completed transition only: preserve corrupt metadata and keep Settings usable.</summary>
public sealed class WindowsRecoveryConnectionStore(string path) : IPersonalCloudConnectionStore
{
    private readonly JsonPersonalCloudConnectionStore _inner = new(path);
    public PersonalCloudConnectionProfile? Load()
    {
        if (new FileInfo(path).LinkTarget is not null) return null;
        try { return _inner.Load(); }
        catch (PersonalCloudK0ContractException) { return null; }
    }
    public async Task SaveAsync(PersonalCloudConnectionProfile connection, CancellationToken cancellationToken = default)
    {
        await PreserveAsync(cancellationToken).ConfigureAwait(false);
        await _inner.SaveAsync(connection, cancellationToken).ConfigureAwait(false);
    }
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await PreserveAsync(cancellationToken).ConfigureAwait(false);
        await _inner.ClearAsync(cancellationToken).ConfigureAwait(false);
    }
    private async Task PreserveAsync(CancellationToken cancellationToken)
    {
        if (new FileInfo(path).LinkTarget is not null)
            throw new WindowsContinuityException("Remove the connection-file redirect before repairing this package's pairing.");
        if (!File.Exists(path)) return;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var retained = Path.Combine(Path.GetDirectoryName(path)!, $"connection-recovery-{Convert.ToHexString(SHA256.HashData(bytes))}.json");
        if (File.Exists(retained))
        {
            if (new FileInfo(retained).LinkTarget is not null)
                throw new WindowsContinuityException("A retained connection file conflicts with recovery. Neither copy was replaced.");
            var existing = await File.ReadAllBytesAsync(retained, cancellationToken).ConfigureAwait(false);
            if (!bytes.SequenceEqual(existing))
                throw new WindowsContinuityException("A retained connection file conflicts with recovery. Neither copy was replaced.");
            return;
        }
        using var stream = new FileStream(retained, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        stream.Flush(true);
    }
}
