using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Openza.Tasks.Core.Data;

namespace Openza.Tasks.Core.PersonalCloud;

public sealed record PersonalCloudSyncResult(int PushedMutations, int PulledChanges, long Checkpoint);

public sealed class PersonalCloudSyncClient(
    HttpClient http,
    PersonalCloudDeviceIdentityClient identity,
    SqliteTaskStore store,
    PersonalCloudConnectionProfile connection)
{
    private const int ChangePageLimit = 50;
    private const int PushBatchLimit = 100;
    private const int MaximumChangePages = 100;
    private const int MaximumResponseBytes = 2 * 1024 * 1024;

    public async Task<PersonalCloudSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        var access = await identity.IssueAccessTokenAsync(connection.ServiceOrigin, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(access.InstanceId, connection.InstanceId, StringComparison.Ordinal) ||
            !string.Equals(access.DeviceId, connection.DeviceId, StringComparison.Ordinal))
        {
            throw new PersonalCloudK0ContractException(
                "unauthorized",
                "The paired desktop identity does not match the saved Personal Cloud connection.");
        }

        var pushed = 0;
        var pending = await store.GetPendingPersonalCloudOutboxAsync(
            connection.InstanceId,
            connection.DeviceId,
            cancellationToken).ConfigureAwait(false);
        foreach (var mutation in pending.Take(PushBatchLimit))
        {
            using var request = CreateAuthorizedRequest(HttpMethod.Post, "/v1/tasks/mutations", access.AccessToken);
            request.Content = new StringContent(mutation.CanonicalJson, Encoding.UTF8, "application/json");
            var receiptJson = await SendForJsonAsync(request, cancellationToken).ConfigureAwait(false);
            await store.AcknowledgePersonalCloudMutationAsync(
                connection.InstanceId,
                connection.DeviceId,
                receiptJson,
                cancellationToken).ConfigureAwait(false);
            pushed++;
        }

        var checkpoint = await store.GetPersonalCloudCheckpointAsync(
            connection.InstanceId,
            connection.DeviceId,
            cancellationToken).ConfigureAwait(false);
        var pulled = 0;
        var caughtUp = false;
        for (var pageNumber = 0; pageNumber < MaximumChangePages; pageNumber++)
        {
            var path = $"/sync/changes?after={checkpoint.ToString(CultureInfo.InvariantCulture)}&limit={ChangePageLimit}";
            using var request = CreateAuthorizedRequest(HttpMethod.Get, path, access.AccessToken);
            var pageJson = await SendForJsonAsync(request, cancellationToken).ConfigureAwait(false);
            var page = PersonalCloudK3Codec.ParseChangePage(pageJson);
            checkpoint = await store.ApplyPersonalCloudChangePageAsync(
                connection.InstanceId,
                connection.DeviceId,
                pageJson,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            pulled = checked(pulled + page.Changes.Count);
            if (page.Changes.Count < ChangePageLimit)
            {
                caughtUp = true;
                break;
            }
        }
        if (!caughtUp)
        {
            throw new PersonalCloudK0ContractException(
                "sync_incomplete",
                "Personal Cloud returned too many change pages for one sync pass.");
        }

        var acknowledgement = PersonalCloudK4Codec.CreateCheckpointAcknowledgement(
            connection.InstanceId,
            connection.DeviceId,
            checkpoint);
        using (var request = CreateAuthorizedRequest(HttpMethod.Post, "/sync/checkpoints", access.AccessToken))
        {
            request.Content = new StringContent(
                PersonalCloudK0Codec.Serialize(acknowledgement),
                Encoding.UTF8,
                "application/json");
            var receiptJson = await SendForJsonAsync(request, cancellationToken).ConfigureAwait(false);
            var receipt = PersonalCloudK4Codec.ParseCheckpointReceipt(receiptJson, connection.DeviceId);
            if (receipt.AcknowledgedCheckpoint != checkpoint)
            {
                throw new PersonalCloudK0ContractException(
                    "sequence_gap",
                    "Personal Cloud acknowledged a different replica checkpoint.");
            }
        }

        return new PersonalCloudSyncResult(pushed, pulled, checkpoint);
    }

    private HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string path, string accessToken)
    {
        var request = new HttpRequestMessage(method, new Uri(connection.ServiceOrigin, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<string> SendForJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new PersonalCloudK0ContractException(
                response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    ? "unauthorized"
                    : "sync_failed",
                "Personal Cloud could not complete the sync request.");
        }

        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new PersonalCloudK0ContractException("sync_failed", "Personal Cloud returned an oversized response.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length + read > MaximumResponseBytes)
            {
                throw new PersonalCloudK0ContractException("sync_failed", "Personal Cloud returned an oversized response.");
            }
            destination.Write(buffer, 0, read);
        }
        if (destination.Length == 0)
        {
            throw new PersonalCloudK0ContractException("sync_failed", "Personal Cloud returned an empty response.");
        }
        return Encoding.UTF8.GetString(destination.GetBuffer(), 0, checked((int)destination.Length));
    }
}
