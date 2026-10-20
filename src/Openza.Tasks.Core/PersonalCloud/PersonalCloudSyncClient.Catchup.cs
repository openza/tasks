using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Openza.Tasks.Core.PersonalCloud;

public sealed partial class PersonalCloudSyncClient
{
    private bool _supportsCatchupStatus;
    private bool _catchupReportRejected;
    private bool _catchupCanUpload;

    internal async Task<bool> ReportCatchupStatusAsync(CancellationToken cancellationToken)
    {
        if (!_supportsCatchupStatus || PersonalCloudDispatchHttp.PolicyFor(http) is not { } policy) return true;
        if (_catchupReportRejected) return _catchupCanUpload;
        _catchupCanUpload = false; // Unknown journal state is never proof a backlog is ordinary.
        try
        {
            var pending = await store.GetPendingPersonalCloudOutboxAsync(connection.InstanceId, connection.DeviceId, cancellationToken).ConfigureAwait(false);
            var initial = await store.GetPersonalCloudInitialSyncProgressAsync(connection.InstanceId, connection.DeviceId, cancellationToken).ConfigureAwait(false);
            var recovering = await store.GetDeferredPersonalCloudChangeCountAsync(connection.InstanceId, cancellationToken).ConfigureAwait(false) > 0;
            long credits = 0;
            foreach (var entry in pending)
            {
                using var json = JsonDocument.Parse(entry.CanonicalJson);
                var cost = PersonalCloudMutationCost.ForBudget(json.RootElement, PersonalCloudMutationCost.DailyWriteCredits,
                    _fanoutBounds.TryGetValue(entry.MutationId, out var bound) ? bound : null);
                credits = cost > long.MaxValue - credits ? long.MaxValue : credits + cost;
            }
            var report = await policy.ReserveCatchupReportAsync(connection, pending, initial is { IsComplete: false }, recovering,
                credits, cancellationToken).ConfigureAwait(false);
            if (report is null)
                return _catchupCanUpload = await policy.CanUploadWithCatchupStatusAsync(connection, cancellationToken).ConfigureAwait(false);
            _catchupCanUpload = report.State == "idle";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var access = await identity.IssueAccessTokenAsync(connection.ServiceOrigin, timeout.Token).ConfigureAwait(false);
            if (access.InstanceId != connection.InstanceId || access.DeviceId != connection.DeviceId) return false;
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(connection.ServiceOrigin, "/sync/catchup-status"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessToken);
            request.Content = new StringContent(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");
            using var response = await PersonalCloudDispatchHttp.SendAsync(http, request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) { policy.SetCatchupTelemetryUnknown(connection.ServiceOrigin, true); _catchupReportRejected = true; return _catchupCanUpload; }
            using var acknowledgement = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            var result = acknowledgement.RootElement;
            RequireKeys(result, "protocolVersion", "accepted", "reportSequence", "updatedAt");
            if (result.GetProperty("protocolVersion").GetInt32() != 1 ||
                result.GetProperty("accepted").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !result.GetProperty("reportSequence").TryGetInt64(out var serverSequence) || serverSequence is < 0 or > 9007199254740991 ||
                result.GetProperty("updatedAt").ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                throw new JsonException("Invalid catch-up acknowledgement.");
            var accepted = result.GetProperty("accepted").GetBoolean();
            if (accepted && serverSequence != report.ReportSequence) throw new JsonException("Invalid catch-up sequence.");
            if (result.GetProperty("updatedAt") is { ValueKind: JsonValueKind.String } updatedAt &&
                !DateTimeOffset.TryParseExact(updatedAt.GetString(), "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out _))
                throw new JsonException("Invalid catch-up timestamp.");
            await policy.ConfirmCatchupSequenceAsync(connection, serverSequence, timeout.Token, accepted).ConfigureAwait(false);
            _catchupReportRejected = !accepted;
            policy.SetCatchupTelemetryUnknown(connection.ServiceOrigin, !accepted);
            return _catchupCanUpload = accepted || report.State == "idle";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Pulls and local work remain available; a captured backlog needs a
            // current accepted catching_up report before its first upload.
            policy.SetCatchupTelemetryUnknown(connection.ServiceOrigin, true);
            _catchupReportRejected = true;
            return _catchupCanUpload;
        }
    }
}
