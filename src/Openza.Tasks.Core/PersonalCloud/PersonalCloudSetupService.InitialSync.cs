using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Openza.Tasks.Core.PersonalCloud;

public sealed partial class PersonalCloudSetupService
{
    private readonly SemaphoreSlim _syncProgressGate = new(1, 1);
    private readonly SemaphoreSlim _reportGate = new(1, 1);
    private DateTimeOffset _lastReportAt;
    private PersonalCloudInitialSyncPhase? _lastReportedPhase;
    public event Action<PersonalCloudInitialSyncProgress>? InitialSyncProgressChanged;

    public async Task<PersonalCloudInitialSyncProgress?> GetInitialSyncProgressAsync(CancellationToken cancellationToken = default)
    {
        if (taskStore is null || CurrentConnection is not { } current) return null;
        var progress = await taskStore.GetPersonalCloudInitialSyncProgressAsync(current.InstanceId, current.DeviceId, cancellationToken).ConfigureAwait(false);
        // An active phase on disk after a restart describes unfinished work, not a running upload.
        return progress is { Phase: PersonalCloudInitialSyncPhase.Preparing or PersonalCloudInitialSyncPhase.Uploading or PersonalCloudInitialSyncPhase.Downloading }
            && _syncProgressGate.CurrentCount != 0
            ? progress with { Phase = PersonalCloudInitialSyncPhase.Interrupted, ErrorCode = "interrupted" } : progress;
    }

    public async Task<PersonalCloudSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        await _syncProgressGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? heartbeat = null;
        PersonalCloudSyncClient? client = null;
        try
        {
            using var connectionLease = taskStore is null ? null : PersonalCloudConnectionLease.AcquireAuthoring(taskStore.DatabasePath);
            var current = CurrentConnection ?? throw new PersonalCloudK0ContractException("not_connected", "Personal Cloud is not connected.");
            if (deviceIdentity is null || syncHttp is null || taskStore is null)
                throw new PersonalCloudK0ContractException("sync_unavailable", "Personal Cloud sync is not available in this build.");

            var initial = await taskStore.GetPersonalCloudInitialSyncProgressAsync(current.InstanceId, current.DeviceId, cancellationToken).ConfigureAwait(false);
            // Legacy journals do not identify their initial seed. Pending ordinary edits are not evidence of first sync.
            var tracking = initial is { IsComplete: false };
            client = new PersonalCloudSyncClient(syncHttp, deviceIdentity, taskStore, current,
                tracking ? (phase, token) => PublishInitialProgressAsync(current, phase, cancellationToken: token) : null);
            await client.DiscoverCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            if (initial is { IsComplete: true })
                await ReportInitialProgressAsync(current, initial, true, cancellationToken).ConfigureAwait(false);
            if (tracking)
            {
                _lastReportedPhase = null;
                await PublishInitialProgressAsync(current, initial!.Total is null ? PersonalCloudInitialSyncPhase.Preparing : PersonalCloudInitialSyncPhase.Uploading,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                heartbeat = HeartbeatAsync(current, client, heartbeatCancellation.Token);
            }
            var firstPass = await client.SyncAsync(cancellationToken).ConfigureAwait(false);
            if (firstPass.BlockedCode is not null)
                return await FinishPassAsync(current, firstPass, tracking, cancellationToken).ConfigureAwait(false);

            var staged = await taskStore.StageUnpublishedTasksForPersonalCloudAsync(current.InstanceId, current.DeviceId, cancellationToken).ConfigureAwait(false);
            var pushed = firstPass.PushedMutations;
            var pulled = firstPass.PulledChanges;
            var checkpoint = firstPass.Checkpoint;
            // A restarted initial seed is already staged; one retry must drain its remaining batches too.
            if (staged > 0 || tracking)
            {
                while ((await taskStore.GetPendingPersonalCloudOutboxAsync(current.InstanceId, current.DeviceId, cancellationToken).ConfigureAwait(false)).Count > 0)
                {
                    var pass = await client.SyncAsync(cancellationToken).ConfigureAwait(false);
                    pushed = checked(pushed + pass.PushedMutations);
                    pulled = checked(pulled + pass.PulledChanges);
                    checkpoint = pass.Checkpoint;
                    if (pass.BlockedCode is not null)
                        return await FinishPassAsync(current, new(pushed, pulled, checkpoint) { BlockedCode = pass.BlockedCode }, tracking, cancellationToken).ConfigureAwait(false);
                }
            }
            return await FinishPassAsync(current, new(pushed, pulled, checkpoint), tracking, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await heartbeatCancellation.CancelAsync().ConfigureAwait(false);
            if (CurrentConnection is { } current && taskStore is not null)
            {
                var code = exception is PersonalCloudK0ContractException contract ? contract.Code : null;
                var phase = code is not null ? PersonalCloudInitialSyncPhase.Blocked : PersonalCloudInitialSyncPhase.Interrupted;
                var error = code == "unauthorized" ? "unauthorized" : code is "version_conflict" or "entity_exists" or "pending_changes" ? "conflict"
                    : exception is OperationCanceledException ? "interrupted" : exception is HttpRequestException ? "offline" : "cloud_unavailable";
                await PublishInitialProgressAsync(current, phase, error, CancellationToken.None,
                    reportToCloud: client?.HasCurrentCapabilities == true).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            await heartbeatCancellation.CancelAsync().ConfigureAwait(false);
            if (heartbeat is not null)
            {
                try { await heartbeat.ConfigureAwait(false); }
                catch (Exception) { /* The optional heartbeat cannot fail actual sync or retain the sync gate. */ }
            }
            _syncProgressGate.Release();
        }
    }

    private async Task<PersonalCloudSyncResult> FinishPassAsync(PersonalCloudConnectionProfile current,
        PersonalCloudSyncResult result, bool tracking, CancellationToken cancellationToken)
    {
        if (tracking && taskStore is not null)
        {
            var progress = await taskStore.GetPersonalCloudInitialSyncProgressAsync(current.InstanceId, current.DeviceId, cancellationToken).ConfigureAwait(false);
            if (result.BlockedCode is not null)
                await PublishInitialProgressAsync(current, PersonalCloudInitialSyncPhase.Blocked, "conflict", cancellationToken).ConfigureAwait(false);
            else if (progress is { Total: not null } && progress.Acknowledged == progress.Total
                && await taskStore.GetDeferredPersonalCloudChangeCountAsync(current.InstanceId, cancellationToken).ConfigureAwait(false) == 0)
                await PublishInitialProgressAsync(current, PersonalCloudInitialSyncPhase.Complete, cancellationToken: cancellationToken).ConfigureAwait(false);
            else
                await PublishInitialProgressAsync(current, PersonalCloudInitialSyncPhase.Interrupted, "interrupted", cancellationToken).ConfigureAwait(false);
        }
        return result with { InitialSyncProgress = await GetInitialSyncProgressAsync(cancellationToken).ConfigureAwait(false) };
    }

    private async Task PublishInitialProgressAsync(PersonalCloudConnectionProfile current, PersonalCloudInitialSyncPhase phase,
        string? errorCode = null, CancellationToken cancellationToken = default, bool reportToCloud = true)
    {
        if (taskStore is null) return;
        var before = await taskStore.GetPersonalCloudInitialSyncProgressAsync(current.InstanceId, current.DeviceId, cancellationToken).ConfigureAwait(false);
        if (before is null || before.IsComplete) return;
        // Counts become available only after the staging transaction has frozen the complete seed range.
        if (before.Total is null && phase is PersonalCloudInitialSyncPhase.Uploading or PersonalCloudInitialSyncPhase.Downloading)
            phase = PersonalCloudInitialSyncPhase.Preparing;
        if (before.Phase != phase || before.ErrorCode != errorCode)
            await taskStore.SetPersonalCloudInitialSyncPhaseAsync(current.InstanceId, current.DeviceId, phase, errorCode, cancellationToken).ConfigureAwait(false);
        var progress = before with { Phase = phase, ErrorCode = errorCode };
        InitialSyncProgressChanged?.Invoke(progress);
        if (reportToCloud) await ReportInitialProgressAsync(current, progress, false, cancellationToken).ConfigureAwait(false);
    }

    private async Task HeartbeatAsync(PersonalCloudConnectionProfile current, PersonalCloudSyncClient client, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (taskStore is null) return;
            if (!client.HasCurrentCapabilities) continue;
            var progress = await taskStore.GetPersonalCloudInitialSyncProgressAsync(current.InstanceId, current.DeviceId, cancellationToken).ConfigureAwait(false);
            if (progress is null || progress.IsComplete) return;
            await ReportInitialProgressAsync(current, progress, true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReportInitialProgressAsync(PersonalCloudConnectionProfile current, PersonalCloudInitialSyncProgress progress,
        bool heartbeat, CancellationToken cancellationToken)
    {
        if (taskStore is null || syncHttp is null || deviceIdentity is null) return;
        await _reportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!heartbeat && _lastReportedPhase == progress.Phase && DateTimeOffset.UtcNow - _lastReportAt < TimeSpan.FromSeconds(30)) return;
            _lastReportAt = DateTimeOffset.UtcNow;
            _lastReportedPhase = progress.Phase;
            // Reserve durably before sending: a failed/late request must never reuse a sequence after restart.
            var sequence = await taskStore.AdvancePersonalCloudInitialReportSequenceAsync(current.InstanceId, current.DeviceId, cancellationToken).ConfigureAwait(false);
            if (sequence <= 0) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var access = await deviceIdentity.IssueAccessTokenAsync(current.ServiceOrigin, timeout.Token).ConfigureAwait(false);
            var blocked = progress.Phase is PersonalCloudInitialSyncPhase.Blocked or PersonalCloudInitialSyncPhase.Interrupted;
            var payload = new Dictionary<string, object?>
            {
                ["protocolVersion"] = 1, ["reportSequence"] = sequence,
                ["state"] = progress.IsComplete ? "complete" : blocked ? "blocked" : "uploading",
                ["acknowledged"] = progress.Acknowledged, ["total"] = progress.Total,
            };
            if (blocked) payload["errorCode"] = progress.ErrorCode ?? "unknown";
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(current.ServiceOrigin, "/sync/initial-status"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessToken);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await syncHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            // Optional diagnostics: unsupported endpoints, revoked auth, timeouts and all HTTP failures preserve real sync.
        }
        catch (Exception) { /* Progress reporting must never discard or fail task delivery. */ }
        finally { _reportGate.Release(); }
    }
}
