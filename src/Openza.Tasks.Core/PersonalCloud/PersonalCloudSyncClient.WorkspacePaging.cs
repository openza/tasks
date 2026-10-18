namespace Openza.Tasks.Core.PersonalCloud;

public sealed partial class PersonalCloudSyncClient
{
    private async Task<long> ReplaceWorkspaceBaselineAsync(string accessToken, Func<bool> reserveAttempt, CancellationToken cancellationToken)
    {
        if (!_supportsWorkspacePaging)
        {
            using var request = CreateAuthorizedRequest(HttpMethod.Get, "/sync/bootstrap?workspace=1", accessToken);
            var json = await SendForJsonAsync(request, cancellationToken).ConfigureAwait(false);
            return await store.ReplacePersonalCloudWorkspaceFromBootstrapAsync(connection.InstanceId, connection.DeviceId, json, cancellationToken).ConfigureAwait(false);
        }
        while (reserveAttempt())
        {
            var transfer = await store.BeginPersonalCloudWorkspaceBootstrapAsync(connection.InstanceId, connection.DeviceId, cancellationToken).ConfigureAwait(false);
            try
            {
                string? cursor = null;
                do
                {
                    var path = "/sync/bootstrap?workspace=1&paged=1" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
                    using var request = CreateAuthorizedRequest(HttpMethod.Get, path, accessToken);
                    var json = await SendForJsonAsync(request, cancellationToken).ConfigureAwait(false);
                    cursor = await store.StagePersonalCloudWorkspaceBootstrapPageAsync(transfer, cursor, json, cancellationToken).ConfigureAwait(false);
                } while (cursor is not null);
                return await store.CommitPersonalCloudWorkspaceBootstrapAsync(transfer, connection.InstanceId, connection.DeviceId, cancellationToken).ConfigureAwait(false);
            }
            catch (PersonalCloudK0ContractException error) when (error.Code == "bootstrap_changed")
            {
                // A new attempt always starts at the first section. No staged prefix
                // has changed domain rows, the committed cursor, or acknowledgements.
            }
            finally
            {
                await store.DiscardPersonalCloudWorkspaceBootstrapAsync(transfer, CancellationToken.None).ConfigureAwait(false);
            }
        }
        throw new PersonalCloudK0ContractException("bootstrap_changed", "Personal Cloud kept changing during its baseline download. Retry Sync.");
    }
}
