namespace Openza.Tasks.Core.PersonalCloud;

public enum PersonalCloudInitialSyncPhase { Preparing, Uploading, Downloading, Complete, Interrupted, Blocked }

public sealed record PersonalCloudInitialSyncProgress(
    string InstanceId, string DeviceId, PersonalCloudInitialSyncPhase Phase,
    long? Acknowledged, long? Total, string? ErrorCode = null)
{
    public bool IsComplete => Phase == PersonalCloudInitialSyncPhase.Complete;
    public string Summary => Phase switch
    {
        PersonalCloudInitialSyncPhase.Preparing => "Your cloud is ready. Your existing tasks are now syncing. Preparing changes…",
        PersonalCloudInitialSyncPhase.Uploading => $"Your existing tasks are syncing · {Counts}. Spaces, projects and labels may still be arriving.",
        PersonalCloudInitialSyncPhase.Downloading => $"{Counts}. Downloading cloud changes…",
        PersonalCloudInitialSyncPhase.Complete => $"Initial sync complete · {Counts}.",
        PersonalCloudInitialSyncPhase.Blocked when ErrorCode is "local_budget_exceeded" or "local_operation_budget_exceeded" or "daily_quota_exceeded" or "cloud_retry_backoff" or "fanout_bound_exceeded" or "catchup_status_unavailable"
            => $"Initial sync uploads are paused · {Counts}. Pending edits are preserved. Downloads may continue when the request and cloud limits allow. Check the dispatch status before retrying Sync.",
        PersonalCloudInitialSyncPhase.Blocked => $"Initial sync is blocked · {Counts}. Pending edits are preserved. Resolve the conflict or authorization issue, then retry Sync.",
        _ => $"Initial sync was interrupted · {Counts}. Retry Sync to continue; no new pairing code is needed.",
    };
    private string Counts => Total is { } total ? $"{Acknowledged ?? 0} of {total} changes uploaded" : "Upload count is being prepared";
}

// Optional interface keeps older hosts and setup-service implementations compatible.
public interface IPersonalCloudInitialSyncProgressService
{
    event Action<PersonalCloudInitialSyncProgress>? InitialSyncProgressChanged;
    Task<PersonalCloudInitialSyncProgress?> GetInitialSyncProgressAsync(CancellationToken cancellationToken = default);
}
