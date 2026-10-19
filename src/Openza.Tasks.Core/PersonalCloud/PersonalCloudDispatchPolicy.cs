using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Openza.Tasks.Core.Services;

namespace Openza.Tasks.Core.PersonalCloud;

public sealed record PersonalCloudDispatchLimits(int EnvelopesPerDay = 3000, int RequestsPerDay = 10000, int WriteUnitsPerDay = 45000);

public sealed record PersonalCloudDispatchStatus(string? Code, string? Resource,
    DateTimeOffset? ObservedAt, DateTimeOffset? ResumeAt, bool IsDurable,
    int AttemptedEnvelopes, int AttemptedRequests, int EnvelopeLimit, int RequestLimit)
{
    public long? PendingEnvelopes { get; init; }
    public DateTimeOffset? CloudResetAt { get; init; }
    public long ReservedWriteUnits { get; init; }
    public long WriteUnitLimit { get; init; }
    public long? BlockingWriteUnits { get; init; }
    public long RemainingWriteUnits => Math.Max(0, WriteUnitLimit - ReservedWriteUnits);
    public bool IsPaused => Code is not null;
    public string Summary => !IsDurable
        ? "Personal Cloud is paused because its local dispatch state could not be saved or read. This pause is not restart-safe. Local edits and pending changes are preserved."
        : Code == "local_operation_budget_exceeded"
        ? $"A pending change requires {BlockingWriteUnits} reserved write credits, beyond the whole daily budget of {WriteUnitLimit}. Cloud retries are stopped; the change and local edits are preserved. Its cost policy or budget needs review before cloud sync can continue."
        : $"Personal Cloud is paused until {CloudResetAt ?? ResumeAt:yyyy-MM-dd HH:mm:ss} UTC ({(Code == "daily_quota_exceeded" ? "daily cloud quota reached" : Code == "local_budget_exceeded" ? "daily upload budget reached" : "retry backoff")}). " +
          (CloudResetAt is { } reset && ResumeAt is { } local && (reset - local).Duration() > TimeSpan.FromMinutes(2)
              ? $"A clock difference was detected; the local retry deadline is {local:yyyy-MM-dd HH:mm:ss} UTC. " : "") +
          $"Today: {AttemptedEnvelopes} of {EnvelopeLimit} upload attempts, {AttemptedRequests} of {RequestLimit} requests. " +
          (WriteUnitLimit > 0 ? $"{RemainingWriteUnits} of {WriteUnitLimit} estimated write credits remain; credits are not billing rows. " : "") +
          (PendingEnvelopes is { } pending ? $"{pending} changes remain pending. " : "") +
          "Local edits and pending changes are preserved.";
    public PersonalCloudK0ContractException ToException() => new(Code ?? "dispatch_paused", Summary) { DispatchStatus = this };
}

public interface IPersonalCloudDispatchStatusService
{
    Task<PersonalCloudDispatchStatus?> GetDispatchStatusAsync(CancellationToken cancellationToken = default);
}

// One private ledger per profile/origin. No task data, credentials or cloud schema changes.
// The cross-process file lease covers reservation AND dispatch, serializing the first resume.
public sealed class PersonalCloudDispatchPolicy
{
    private static readonly ConcurrentDictionary<string, PersonalCloudDispatchStatus> MemoryBrakes = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string _directory;
    private readonly PersonalCloudDispatchLimits _limits;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan> _resumeJitter;
    private readonly ConcurrentDictionary<string, string> _instances = new(StringComparer.Ordinal);

    public PersonalCloudDispatchPolicy(string directory, PersonalCloudDispatchLimits? limits = null,
        TimeProvider? clock = null, Func<TimeSpan>? resumeJitter = null)
    {
        _directory = Path.GetFullPath(directory);
        _limits = limits ?? new();
        if (_limits.EnvelopesPerDay < 1 || _limits.RequestsPerDay < 1 || _limits.WriteUnitsPerDay < 1) throw new ArgumentOutOfRangeException(nameof(limits));
        _clock = clock ?? TimeProvider.System;
        _resumeJitter = resumeJitter ?? (() => TimeSpan.FromMilliseconds(Random.Shared.Next(100, 1001)));
    }

    public void BindInstance(Uri origin, string instanceId) => _instances[Origin(origin)] = instanceId;
    internal DateTimeOffset UtcNow => _clock.GetUtcNow();

    public Task<PersonalCloudDispatchStatus> GetStatusAsync(Uri origin, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = StatePath(origin);
        if (MemoryBrakes.TryGetValue(path, out var brake)) return Task.FromResult(brake);
        try { return Task.FromResult(Status(Read(path, Origin(origin)), _clock.GetUtcNow())); }
        catch (Exception e) when (StorageFailure(e)) { return Task.FromResult(StorageBrake(path)); }
    }

    internal async Task<DispatchLease> BeginAsync(Uri origin, int envelopes, long writeUnits, CancellationToken cancellationToken)
    {
        var path = StatePath(origin);
        if (MemoryBrakes.TryGetValue(path, out var brake)) throw brake.ToException();
        FileStream? fileLock = null;
        try
        {
            fileLock = await AcquireLockAsync(path, cancellationToken).ConfigureAwait(false);
            if (MemoryBrakes.TryGetValue(path, out brake)) throw brake.ToException();
            var state = Read(path, Origin(origin));
            var now = _clock.GetUtcNow();
            var status = Status(state, now);
            if (status.IsPaused) throw status.ToException();
            var resuming = state.Code is not null;
            AdvanceDay(state, now);
            state.Code = null; state.Resource = null; state.ResumeAt = null; state.ObservedAt = null; state.CloudResetAt = null;
            state.BlockingWriteUnits = null;
            if (_instances.TryGetValue(state.Origin, out var instance)) state.InstanceId = instance;
            if (writeUnits > _limits.WriteUnitsPerDay)
            {
                state.BlockingWriteUnits = writeUnits;
                SetPause(state, "local_operation_budget_exceeded", null, now, null);
                Write(path, state);
                throw Status(state, now).ToException();
            }
            if (envelopes > _limits.EnvelopesPerDay - state.Envelopes || state.Requests >= _limits.RequestsPerDay || writeUnits > _limits.WriteUnitsPerDay - state.WriteUnits)
            {
                SetPause(state, "local_budget_exceeded", null, now, NextMidnight(now));
                Write(path, state);
                throw Status(state, now).ToException();
            }
            state.Envelopes = checked(state.Envelopes + envelopes);
            state.Requests = checked(state.Requests + 1);
            state.WriteUnits = checked(state.WriteUnits + writeUnits);
            if (state.Envelopes >= _limits.EnvelopesPerDay || state.Requests >= _limits.RequestsPerDay || state.WriteUnits >= _limits.WriteUnitsPerDay)
                SetPause(state, "local_budget_exceeded", null, now, NextMidnight(now));
            Write(path, state); // A crash or uncertain request consumes its reservation.
            if (resuming)
            {
                var jitter = _resumeJitter();
                if (jitter < TimeSpan.Zero || jitter > TimeSpan.FromSeconds(2)) throw new InvalidOperationException("Invalid resume jitter.");
                await Task.Delay(jitter, _clock, cancellationToken).ConfigureAwait(false);
            }
            var lease = new DispatchLease(this, path, state, fileLock);
            fileLock = null;
            return lease;
        }
        catch (Exception e) when (StorageFailure(e)) { throw StorageBrake(path).ToException(); }
        finally { fileLock?.Dispose(); }
    }

    internal async Task<PersonalCloudDispatchStatus> PauseBudgetAsync(Uri origin, long? oversizedWriteUnits, CancellationToken cancellationToken)
    {
        var path = StatePath(origin);
        if (MemoryBrakes.TryGetValue(path, out var brake)) return brake;
        try
        {
            using var fileLock = await AcquireLockAsync(path, cancellationToken).ConfigureAwait(false);
            var state = Read(path, Origin(origin));
            var now = _clock.GetUtcNow();
            var status = Status(state, now);
            if (status.IsPaused && (state.Code is "daily_quota_exceeded" or "local_operation_budget_exceeded" ||
                oversizedWriteUnits is null && state.Code == "local_budget_exceeded")) return status;
            AdvanceDay(state, now);
            if (oversizedWriteUnits is { } units)
            {
                state.BlockingWriteUnits = units;
                SetPause(state, "local_operation_budget_exceeded", null, now, null);
            }
            else SetPause(state, "local_budget_exceeded", null, now, NextMidnight(now));
            Write(path, state);
            return Status(state, now);
        }
        catch (Exception e) when (StorageFailure(e)) { return StorageBrake(path); }
    }

    internal async Task<PersonalCloudDispatchStatus> RecordFailureAsync(Uri origin, CancellationToken cancellationToken)
    {
        var path = StatePath(origin);
        if (MemoryBrakes.TryGetValue(path, out var brake)) return brake;
        try
        {
            using var fileLock = await AcquireLockAsync(path, cancellationToken).ConfigureAwait(false);
            var state = Read(path, Origin(origin));
            var status = Status(state, _clock.GetUtcNow());
            if (status.IsPaused) return status;
            AdvanceDay(state, _clock.GetUtcNow());
            using var lease = new DispatchLease(this, path, state, fileLock);
            return lease.Backoff(); // Parsing failures spend no additional HTTP reservation.
        }
        catch (Exception e) when (StorageFailure(e)) { return StorageBrake(path); }
    }

    private async Task<FileStream> AcquireLockAsync(string path, CancellationToken cancellationToken)
    {
        PrivateFilePermissions.EnsureOwnedDirectory(_directory);
        var waited = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream fileLock;
            try { fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (waited < 5000)
            { await Task.Delay(25, cancellationToken).ConfigureAwait(false); waited += 25; continue; }
            catch (IOException)
            { throw new PersonalCloudK0ContractException("cloud_dispatch_busy", "Another Personal Cloud request is still running. Retry later; pending changes are preserved."); }
            try { PrivateFilePermissions.EnsureFile(path + ".lock"); return fileLock; }
            catch { fileLock.Dispose(); throw; }
        }
    }

    private string StatePath(Uri uri) => Path.Combine(_directory,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Origin(uri)))) + ".json");

    private static string Origin(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            throw new ArgumentException("Invalid Personal Cloud origin.", nameof(uri));
        return uri.GetLeftPart(UriPartial.Authority) + "/";
    }

    internal static DateTimeOffset NextMidnight(DateTimeOffset now) =>
        new(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
    private static string Day(DateTimeOffset now) => now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private DispatchState Read(string path, string origin)
    {
        if (!File.Exists(path)) return new() { Origin = origin, Day = Day(_clock.GetUtcNow()) };
        if (new FileInfo(path).Length > 16384) throw new JsonException("Invalid dispatch ledger.");
        var raw = File.ReadAllText(path);
        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Count() != 14 ||
            document.RootElement.EnumerateObject().Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != 14)
            throw new JsonException("Invalid dispatch ledger.");
        var state = JsonSerializer.Deserialize<DispatchState>(raw, Json);
        if (state is null || state.Format != 1 || state.Origin != origin || state.Envelopes < 0 || state.Requests < 0 || state.WriteUnits < 0 ||
            state.Failures is < 0 or > 20 ||
            !DateOnly.TryParseExact(state.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            state.Code is not (null or "daily_quota_exceeded" or "local_budget_exceeded" or "local_operation_budget_exceeded" or "cloud_retry_backoff") ||
            state.Code == "daily_quota_exceeded" && state.CloudResetAt is null ||
            state.Code == "local_operation_budget_exceeded" && (state.BlockingWriteUnits is null or <= 0 || state.ObservedAt is null || state.ResumeAt is not null) ||
            state.Code is not (null or "local_operation_budget_exceeded") && (state.ObservedAt is null || state.ResumeAt is null ||
                state.ResumeAt <= state.ObservedAt || state.ResumeAt - state.ObservedAt > TimeSpan.FromDays(1)))
            throw new JsonException("Invalid dispatch ledger.");
        return state;
    }

    private static void AdvanceDay(DispatchState state, DateTimeOffset now)
    {
        // Clock rollback cannot grant a second daily allowance.
        if (string.CompareOrdinal(Day(now), state.Day) > 0)
        { state.Day = Day(now); state.Envelopes = 0; state.Requests = 0; state.WriteUnits = 0; state.Failures = 0; }
    }

    private PersonalCloudDispatchStatus Status(DispatchState state, DateTimeOffset now)
    {
        var paused = state.Code == "local_operation_budget_exceeded" ? state.BlockingWriteUnits > _limits.WriteUnitsPerDay : state.Code is not null && state.ResumeAt > now;
        var nextDay = string.CompareOrdinal(Day(now), state.Day) > 0;
        return new(paused ? state.Code : null, paused ? state.Resource : null,
            state.ObservedAt, state.ResumeAt, true, nextDay ? 0 : state.Envelopes,
            nextDay ? 0 : state.Requests, _limits.EnvelopesPerDay, _limits.RequestsPerDay)
        { CloudResetAt = state.CloudResetAt, ReservedWriteUnits = nextDay ? 0 : state.WriteUnits,
            WriteUnitLimit = _limits.WriteUnitsPerDay, BlockingWriteUnits = state.BlockingWriteUnits };
    }

    private void Write(string path, DispatchState state)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                PrivateFilePermissions.EnsureFile(temporary);
                JsonSerializer.Serialize(stream, state, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private PersonalCloudDispatchStatus StorageBrake(string path) => MemoryBrakes.GetOrAdd(path, _ =>
        new("dispatch_state_unavailable", null, _clock.GetUtcNow(), null, false, 0, 0,
            _limits.EnvelopesPerDay, _limits.RequestsPerDay));

    private static bool StorageFailure(Exception e) => e is IOException or UnauthorizedAccessException or JsonException;
    private static void SetPause(DispatchState state, string code, string? resource, DateTimeOffset observedAt, DateTimeOffset? resetAt, DateTimeOffset? cloudResetAt = null)
    { state.Code = code; state.Resource = resource; state.ObservedAt = observedAt; state.ResumeAt = resetAt; state.CloudResetAt = cloudResetAt; }

    internal sealed class DispatchState
    {
        public DispatchState() { }
        [JsonRequired] public int Format { get; set; } = 1;
        [JsonRequired] public string Origin { get; set; } = "";
        [JsonRequired] public string? InstanceId { get; set; }
        [JsonRequired] public string Day { get; set; } = "";
        [JsonRequired] public int Envelopes { get; set; }
        [JsonRequired] public int Requests { get; set; }
        [JsonRequired] public long WriteUnits { get; set; }
        [JsonRequired] public long? BlockingWriteUnits { get; set; }
        [JsonRequired] public int Failures { get; set; }
        [JsonRequired] public string? Code { get; set; }
        [JsonRequired] public string? Resource { get; set; }
        [JsonRequired] public DateTimeOffset? ObservedAt { get; set; }
        [JsonRequired] public DateTimeOffset? ResumeAt { get; set; }
        [JsonRequired] public DateTimeOffset? CloudResetAt { get; set; }
    }

    internal sealed class DispatchLease(PersonalCloudDispatchPolicy owner, string path, DispatchState state, FileStream fileLock) : IDisposable
    {
        public PersonalCloudDispatchStatus Pause(string code, string? resource, DateTimeOffset observedAt, DateTimeOffset resetAt, DateTimeOffset? cloudResetAt = null)
        {
            SetPause(state, code, resource, observedAt, resetAt, cloudResetAt);
            return Save();
        }
        public PersonalCloudDispatchStatus Backoff()
        {
            if ((state.Code is "daily_quota_exceeded" or "local_budget_exceeded") && state.ResumeAt > owner._clock.GetUtcNow())
                return owner.Status(state, owner._clock.GetUtcNow());
            state.Failures = Math.Min(20, state.Failures + 1);
            var now = owner._clock.GetUtcNow();
            return Pause("cloud_retry_backoff", null, now,
                now.AddSeconds(Math.Min(300, Math.Pow(2, state.Failures))));
        }
        private PersonalCloudDispatchStatus Save()
        {
            try { owner.Write(path, state); return owner.Status(state, owner._clock.GetUtcNow()); }
            catch (Exception e) when (StorageFailure(e)) { return owner.StorageBrake(path); }
        }
        public void Dispose() => fileLock.Dispose();
    }
}
