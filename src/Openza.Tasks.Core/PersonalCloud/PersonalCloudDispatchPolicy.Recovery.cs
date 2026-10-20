using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.PersonalCloud;

public sealed partial class PersonalCloudDispatchPolicy
{
    public static string ExactEnvelopeDigest(string canonicalJson) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));

    public bool HasSingleAttemptPermission(Uri origin, string canonicalJson) =>
        ReadAttemptPermit(StatePath(origin), origin, canonicalJson) is { Consumed: false };

    internal bool NeedsFanoutFallback(Uri origin, string canonicalJson) =>
        File.Exists(StatePath(origin) + ".fanout-stale." + ExactEnvelopeDigest(canonicalJson));

    internal async Task RememberStaleFanoutAsync(Uri origin, string canonicalJson, CancellationToken cancellationToken)
    {
        var path = StatePath(origin);
        try
        {
            using var fileLock = await AcquireLockAsync(path, cancellationToken).ConfigureAwait(false);
            Write(path + ".fanout-stale." + ExactEnvelopeDigest(canonicalJson), new { Format = 1 });
        }
        catch (Exception exception) when (StorageFailure(exception)) { throw StorageBrake(path).ToException(); }
    }

    internal async Task<PersonalCloudDispatchStatus> ReconsiderOperationHoldAsync(Uri origin, long newCost, CancellationToken cancellationToken)
    {
        var path = StatePath(origin);
        try
        {
            using var fileLock = await AcquireLockAsync(path, cancellationToken).ConfigureAwait(false);
            var state = ReadForDispatch(path, Origin(origin));
            AdvanceDay(state, _clock.GetUtcNow());
            if (state.Code == "local_operation_budget_exceeded" && newCost <= _limits.WriteUnitsPerDay)
            {
                state.BlockingWriteUnits = null;
                SetPause(state, "local_budget_exceeded", null, _clock.GetUtcNow(), NextMidnight(_clock.GetUtcNow()));
                if (newCost <= _limits.WriteUnitsPerDay - state.WriteUnits && state.Envelopes < _limits.EnvelopesPerDay && state.Requests < _limits.RequestsPerDay)
                { state.Code = null; state.Resource = null; state.ObservedAt = null; state.ResumeAt = null; }
                Write(path, state);
            }
            return Status(state, _clock.GetUtcNow());
        }
        catch (Exception exception) when (StorageFailure(exception)) { return StorageBrake(path); }
    }

    public async Task AuthorizeSingleAttemptAsync(PersonalCloudConnectionProfile connection,
        PersonalCloudK1OutboxEntry entry, string expectedDigest, CancellationToken cancellationToken = default)
    {
        if (entry.InstanceId != connection.InstanceId || entry.DeviceId != connection.DeviceId ||
            expectedDigest != ExactEnvelopeDigest(entry.CanonicalJson))
            throw new ArgumentException("The pending entry identity or exact SHA256 does not match.");
        using var json = JsonDocument.Parse(entry.CanonicalJson);
        if (json.RootElement.GetProperty("mutationId").GetString() != entry.MutationId ||
            json.RootElement.GetProperty("instanceId").GetString() != entry.InstanceId ||
            json.RootElement.GetProperty("deviceId").GetString() != entry.DeviceId)
            throw new ArgumentException("The pending envelope identity does not match.");
        var path = StatePath(connection.ServiceOrigin);
        if (ActiveStorageBrake(path) is { } brake) throw brake.ToException();
        try
        {
            using var fileLock = await AcquireLockAsync(path, cancellationToken).ConfigureAwait(false);
            var state = ReadForDispatch(path, Origin(connection.ServiceOrigin));
            var status = Status(state, _clock.GetUtcNow());
            if (status.BlocksReads) throw status.ToException();
            var now = _clock.GetUtcNow();
            Write(path + ".allow-once", new AttemptPermit
            {
                Origin = Origin(connection.ServiceOrigin), InstanceId = entry.InstanceId, DeviceId = entry.DeviceId,
                MutationId = entry.MutationId, Digest = expectedDigest, CreatedAt = now, ExpiresAt = now.AddMinutes(15),
            });
        }
        catch (Exception exception) when (StorageFailure(exception)) { throw StorageBrake(path).ToException(); }
    }

    private AttemptPermit? ReadAttemptPermit(string path, Uri origin, string canonicalJson)
    {
        try
        {
            var permitPath = path + ".allow-once";
            if (!File.Exists(permitPath) || new FileInfo(permitPath).Length > 4096) return null;
            var permit = JsonSerializer.Deserialize<AttemptPermit>(File.ReadAllText(permitPath), Json);
            var now = _clock.GetUtcNow();
            if (permit is null || permit.Format != 1 || permit.Origin != Origin(origin) ||
                permit.CreatedAt > now || permit.ExpiresAt <= now || permit.ExpiresAt - permit.CreatedAt > TimeSpan.FromMinutes(15) ||
                permit.Digest != ExactEnvelopeDigest(canonicalJson)) return null;
            using var document = JsonDocument.Parse(canonicalJson);
            var mutation = document.RootElement;
            return mutation.GetProperty("mutationId").GetString() == permit.MutationId &&
                mutation.GetProperty("instanceId").GetString() == permit.InstanceId &&
                mutation.GetProperty("deviceId").GetString() == permit.DeviceId ? permit : null;
        }
        catch (Exception exception) when (StorageFailure(exception) || exception is KeyNotFoundException or InvalidOperationException)
        { return null; } // Invalid permission grants no exemption; never reset the quota ledger.
    }

    private void ConsumeAttemptPermit(string path, Uri origin, string canonicalJson)
    {
        var permit = ReadAttemptPermit(path, origin, canonicalJson);
        if (permit is null || permit.Consumed) throw new InvalidOperationException("The single-attempt permission is no longer available.");
        permit.Consumed = true;
        Write(path + ".allow-once", permit);
    }

    private sealed class AttemptPermit
    {
        [JsonRequired] public int Format { get; set; } = 1;
        [JsonRequired] public string Origin { get; set; } = "";
        [JsonRequired] public string InstanceId { get; set; } = "";
        [JsonRequired] public string DeviceId { get; set; } = "";
        [JsonRequired] public string MutationId { get; set; } = "";
        [JsonRequired] public string Digest { get; set; } = "";
        [JsonRequired] public DateTimeOffset CreatedAt { get; set; }
        [JsonRequired] public DateTimeOffset ExpiresAt { get; set; }
        [JsonRequired] public bool Consumed { get; set; }
    }
}
