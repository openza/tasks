namespace Openza.Tasks.Core.PersonalCloud;

public sealed partial class PersonalCloudSetupService
{
    public async Task<PersonalCloudDispatchStatus?> GetDispatchStatusAsync(CancellationToken cancellationToken = default)
    {
        if (syncHttp is null || CurrentConnection is not { } current || PersonalCloudDispatchHttp.PolicyFor(syncHttp) is not { } policy) return null;
        policy.BindInstance(current.ServiceOrigin, current.InstanceId);
        var status = await policy.GetStatusAsync(current.ServiceOrigin, cancellationToken).ConfigureAwait(false);
        if (!status.IsPaused || taskStore is null) return status;
        try
        {
            return status with { PendingEnvelopes = await taskStore.GetPendingPersonalCloudOutboxCountAsync(current.InstanceId, current.DeviceId, cancellationToken).ConfigureAwait(false) };
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        { return status; } // An unavailable optional count must not conceal the durable brake/reset.
    }

    private async Task EnsureDispatchAvailableAsync(CancellationToken cancellationToken)
    {
        if (await GetDispatchStatusAsync(cancellationToken).ConfigureAwait(false) is { BlocksReads: true } status)
            throw status.ToException();
    }
}
