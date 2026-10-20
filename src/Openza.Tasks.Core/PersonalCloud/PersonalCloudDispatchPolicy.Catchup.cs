using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;

namespace Openza.Tasks.Core.PersonalCloud;

internal sealed record PersonalCloudCatchupReport(int ProtocolVersion, long ReportSequence, string State, int Pending);

public sealed partial class PersonalCloudDispatchPolicy
{
    private readonly ConcurrentDictionary<string, bool> _catchupUnknown = new(StringComparer.Ordinal);
    internal void SetCatchupTelemetryUnknown(Uri origin, bool unknown)
    {
        if (unknown) _catchupUnknown[Origin(origin)] = true;
        else _catchupUnknown.TryRemove(Origin(origin), out _);
    }

    private string CatchupPath(PersonalCloudConnectionProfile connection) => StatePath(connection.ServiceOrigin) + "." +
        ExactEnvelopeDigest(connection.InstanceId + "\n" + connection.DeviceId) + ".catchup";
    internal async Task<PersonalCloudCatchupReport?> ReserveCatchupReportAsync(PersonalCloudConnectionProfile connection,
        IReadOnlyList<PersonalCloudK1OutboxEntry> pending, bool seedIncomplete, bool recoveryIncomplete,
        long pendingCredits, CancellationToken cancellationToken)
    {
        var path = StatePath(connection.ServiceOrigin);
        if (ActiveStorageBrake(path) is not null) return null;
        using var fileLock = await AcquireLockAsync(path, cancellationToken).ConfigureAwait(false);
        var budget = Status(ReadForDispatch(path, Origin(connection.ServiceOrigin)), _clock.GetUtcNow());
        if (budget.BlocksReads) return null;
        var journalPath = CatchupPath(connection);
        CatchupJournal state;
        try
        {
            if (File.Exists(journalPath + ".recovering")) throw new JsonException("Interrupted catch-up recovery.");
            state = File.Exists(journalPath) ? ReadCatchupJournal(journalPath, connection)
                : new CatchupJournal { Origin = Origin(connection.ServiceOrigin), InstanceId = connection.InstanceId, DeviceId = connection.DeviceId };
        }
        catch (Exception exception) when (StorageFailure(exception))
        {
            state = new CatchupJournal { Origin = Origin(connection.ServiceOrigin), InstanceId = connection.InstanceId,
                DeviceId = connection.DeviceId, CatchingUp = true, NeedsServerSequence = true };
            Write(journalPath + ".recovering", state);
            if (File.Exists(journalPath))
            {
                var quarantine = journalPath + "." + Guid.NewGuid().ToString("N") + ".quarantine";
                File.Move(journalPath, quarantine);
                Openza.Tasks.Core.Services.PrivateFilePermissions.EnsureFile(quarantine);
            }
            Write(journalPath, state);
            File.Delete(journalPath + ".recovering");
        }
        var now = _clock.GetUtcNow();
        var trigger = state.NeedsServerSequence || seedIncomplete || recoveryIncomplete || pending.Count > 0 &&
            (budget.IsPaused || pendingCredits > _limits.WriteUnitsPerDay ||
             pending.Any(entry => entry.CreatedAt.UtcDateTime.Date < now.UtcDateTime.Date));
        // Once captured, new ordinary arrivals cannot hide an unfinished backlog.
        var catchingUp = trigger || state.CatchingUp && pending.Count > 0;
        if (trigger && !state.CatchingUp) state.Watermark = pending.Count == 0 ? 0 : pending.Max(entry => entry.DeviceSequence);
        if (!catchingUp) state.Watermark = 0;
        if (state.ReportSequence > 0 && state.CatchingUp == catchingUp && state.Pending == pending.Count &&
            now >= state.LastReservedAt && now - state.LastReservedAt < TimeSpan.FromMinutes(5)) return null;
        state.CatchingUp = catchingUp;
        state.Pending = pending.Count;
        state.LastReservedAt = now;
        state.ReportSequence = checked(state.ReportSequence + 1);
        if (state.ReportSequence > 9007199254740991) throw new JsonException("Catch-up sequence exhausted.");
        Write(journalPath, state); // Reserve before HTTP; failure never reuses a sequence.
        return new(1, state.ReportSequence, catchingUp ? "catching_up" : "idle", pending.Count);
    }

    internal async Task ConfirmCatchupSequenceAsync(PersonalCloudConnectionProfile connection, long serverSequence,
        CancellationToken cancellationToken, bool accepted = false)
    {
        using var fileLock = await AcquireLockAsync(StatePath(connection.ServiceOrigin), cancellationToken).ConfigureAwait(false);
        var path = CatchupPath(connection);
        var state = ReadCatchupJournal(path, connection);
        state.ReportSequence = Math.Max(state.ReportSequence, serverSequence);
        if (accepted) state.LastAcceptedSequence = Math.Max(state.LastAcceptedSequence, serverSequence);
        state.NeedsServerSequence = false;
        Write(path, state);
    }

    internal async Task<bool> CanUploadWithCatchupStatusAsync(PersonalCloudConnectionProfile connection, CancellationToken cancellationToken)
    {
        using var fileLock = await AcquireLockAsync(StatePath(connection.ServiceOrigin), cancellationToken).ConfigureAwait(false);
        var state = ReadCatchupJournal(CatchupPath(connection), connection);
        return !state.CatchingUp || state.LastAcceptedSequence == state.ReportSequence &&
            _clock.GetUtcNow() >= state.LastReservedAt && _clock.GetUtcNow() - state.LastReservedAt < TimeSpan.FromMinutes(5);
    }

    private static CatchupJournal ReadCatchupJournal(string path, PersonalCloudConnectionProfile connection)
    {
        if (new FileInfo(path).Length > 4096) throw new JsonException("Invalid catch-up journal.");
        var state = JsonSerializer.Deserialize<CatchupJournal>(File.ReadAllText(path), Json);
        if (state is null || state.Format != 1 || state.Origin != Origin(connection.ServiceOrigin) ||
            state.InstanceId != connection.InstanceId || state.DeviceId != connection.DeviceId || state.ReportSequence is < 0 or > 9007199254740991 ||
            state.Pending < 0 || state.Watermark < 0 || state.LastAcceptedSequence < 0 || state.LastAcceptedSequence > state.ReportSequence)
            throw new JsonException("Invalid catch-up journal.");
        return state;
    }

    private sealed class CatchupJournal
    {
        [JsonRequired] public int Format { get; set; } = 1;
        [JsonRequired] public string Origin { get; set; } = "";
        [JsonRequired] public string InstanceId { get; set; } = "";
        [JsonRequired] public string DeviceId { get; set; } = "";
        [JsonRequired] public long ReportSequence { get; set; }
        [JsonRequired] public bool CatchingUp { get; set; }
        [JsonRequired] public long Watermark { get; set; }
        [JsonRequired] public int Pending { get; set; }
        [JsonRequired] public DateTimeOffset LastReservedAt { get; set; }
        [JsonRequired] public bool NeedsServerSequence { get; set; }
        [JsonRequired] public long LastAcceptedSequence { get; set; }
    }
}
