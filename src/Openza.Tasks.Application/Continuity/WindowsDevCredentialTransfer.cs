using Openza.Tasks.Application.Runtime;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Services;

namespace Openza.Tasks.Application.Continuity;

public sealed record WindowsCredentialTransferResult(int Copied, int AlreadyPresent, int Missing);

/// <summary>
/// Explicit Dev-fixture transfer between quiesced, caller-supplied stores. No vault discovery,
/// fallback reads, source deletion, Microsoft-cache conversion or pairing migration.
/// The owner must keep both stores quiesced for the operation; ICredentialStore has no CAS.
/// </summary>
public sealed class WindowsDevCredentialTransfer
{
    private static readonly (string Source, string Target)[] Keys =
    [
        ("todoist.accessToken", "todoist-token"),
        ("onedrive.backup.passphrase", "onedrive-backup-passphrase"),
        (GitHubIssueService.TokenKey, GitHubIssueService.TokenKey),
    ];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ICredentialStore _source;
    private readonly ICredentialStore _target;

    public WindowsDevCredentialTransfer(WindowsPackageDataContext context, ICredentialStore source, ICredentialStore target)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Runtime.Channel != OpenzaChannel.Dev)
            throw new InvalidOperationException("Credential transfer is limited to explicit Dev fixtures.");
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(source, target))
            throw new ArgumentException("Source and target credential stores must be distinct.", nameof(target));
        _source = source;
        _target = target;
    }

    public async Task<WindowsCredentialTransferResult> CopyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = new List<(string Key, string Value)>();
            var present = 0;
            var missing = 0;
            // Preflight every key before the first write. Existing destination values win;
            // a mismatch needs an explicit decision and must never be silently overwritten.
            foreach (var (sourceKey, targetKey) in Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = await _source.GetAsync(sourceKey, cancellationToken).ConfigureAwait(false);
                var target = await _target.GetAsync(targetKey, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(source)) { missing++; continue; }
                if (target is not null)
                {
                    if (!string.Equals(source, target, StringComparison.Ordinal))
                        throw new InvalidOperationException("The destination contains a different credential; no transfer was started.");
                    present++;
                }
                else pending.Add((targetKey, source));
            }

            foreach (var (key, value) in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await _target.GetAsync(key, cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("The destination changed during credential transfer. Retry only with quiesced stores.");
                await _target.SaveAsync(key, value, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(value, await _target.GetAsync(key, cancellationToken).ConfigureAwait(false), StringComparison.Ordinal))
                    throw new InvalidOperationException("Credential verification failed. Source credentials have been retained.");
            }
            return new WindowsCredentialTransferResult(pending.Count, present, missing);
        }
        finally { _gate.Release(); }
    }
}
