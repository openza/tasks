using System.Net;
using System.Security.Cryptography;
using System.Text;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Core.PersonalCloud;

// AccountBinding is local-only: an exact MSAL account identifier or credential fingerprint.
public sealed record PersonalCloudConfiguredProvider(ISyncProvider Provider, string AccountBinding);
public sealed record PersonalCloudProviderBridgeResult(int Confirmed, int Rejected, int Uncertain, int Pending)
{
    public string Summary => $"Provider requests: {Confirmed} confirmed, {Rejected} rejected, {Uncertain} uncertain, {Pending} pending.";
}

public interface IPersonalCloudProviderBridgeService
{
    Task<PersonalCloudProviderBridgeResult> RunProviderBridgeAsync(IReadOnlyList<PersonalCloudConfiguredProvider> providers,
        CancellationToken cancellationToken = default);
    Task RetryProviderCommandAsync(string taskId, CancellationToken cancellationToken = default);
}

public sealed class PersonalCloudProviderBridge(SqliteTaskStore store, PersonalCloudProviderDeliveryClient delivery,
    PersonalCloudConnectionProfile connection)
{
    public async Task<PersonalCloudProviderBridgeResult> RunAsync(IReadOnlyList<PersonalCloudConfiguredProvider> providers,
        CancellationToken cancellationToken = default)
    {
        using var connectionLease = PersonalCloudConnectionLease.AcquireAuthoring(store.DatabasePath);
        // A second process may sync/pull while this one runs, but cannot dispatch this replica's attempts.
        using var runLease = new FileStream(store.DatabasePath + ".provider-bridge.lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        Openza.Tasks.Core.Services.PrivateFilePermissions.EnsureFile(store.DatabasePath + ".provider-bridge.lock");
        if (providers.Count > 0 && !await delivery.IsEnabledAsync(cancellationToken).ConfigureAwait(false))
            return new(0, 0, 0, (await store.GetPendingPersonalCloudProviderCommandsAsync(connection.InstanceId, cancellationToken: cancellationToken).ConfigureAwait(false)).Count);
        var confirmed = 0; var rejected = 0; var uncertain = 0; var pending = 0;
        var snapshots = new Dictionary<string, (PersonalCloudConfiguredProvider Configuration, IReadOnlyList<ProviderSourceItem> Tasks, DateTimeOffset ObservedAt)>();
        foreach (var configuration in providers)
        {
            if (string.IsNullOrWhiteSpace(configuration.AccountBinding) || snapshots.ContainsKey(configuration.Provider.IntegrationId)) continue;
            try
            {
                var snapshot = await configuration.Provider.FetchSnapshotAsync(cancellationToken).ConfigureAwait(false);
                var observedAt = DateTimeOffset.UtcNow;
                var projects = snapshot.Projects.ToDictionary(project => project.Id, project => project.Name, StringComparer.Ordinal);
                var sources = snapshot.Tasks.Concat(snapshot.CompletedTasks)
                    .Where(task => task.IntegrationId == configuration.Provider.IntegrationId && !string.IsNullOrWhiteSpace(task.ExternalId) &&
                        (task.ProviderConnectionId is null || task.ProviderConnectionId == configuration.Provider.ProviderConnectionId))
                    .Select(task => TaskSyncEngine.ToProviderSourceItem(task, configuration.Provider.ProviderConnectionId, projects)).ToArray();
                snapshots.Add(configuration.Provider.IntegrationId, (configuration, sources, observedAt));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* No fresh account/source evidence: leave every request unclaimed. */ }
        }
        var knownSources = await store.GetPersonalCloudProviderSourcesAsync(connection.InstanceId, cancellationToken).ConfigureAwait(false);
        long afterRequestedSequence = 0;
        while (true)
        {
        var commands = await store.GetPendingPersonalCloudProviderCommandsAsync(connection.InstanceId, afterRequestedSequence, cancellationToken).ConfigureAwait(false);
        if (commands.Count == 0) break;
        afterRequestedSequence = commands[^1].RequestedChangeSeq;
        foreach (var request in commands)
        {
            if (knownSources.Count(item => item.Source.IntegrationKind == request.Source.IntegrationKind &&
                item.Source.ExternalIdHash == request.Source.ExternalIdHash) != 1) { pending++; continue; }
            if (!snapshots.TryGetValue(request.Source.IntegrationKind, out var snapshot)) { pending++; continue; }
            var matches = snapshot.Tasks.Where(task => task.IntegrationId == request.Source.IntegrationKind &&
                SafeIdentity(task) == request.Source.ExternalIdHash).ToArray();
            if (matches.Length != 1 || string.IsNullOrWhiteSpace(matches[0].ProviderTaskId)) { pending++; continue; }
            var source = matches[0];
            PersonalCloudProviderAttempt attempt;
            try
            {
                attempt = await store.PreparePersonalCloudProviderAttemptAsync(connection.InstanceId, connection.DeviceId,
                    request, snapshot.Configuration.AccountBinding, cancellationToken).ConfigureAwait(false);
            }
            catch (PersonalCloudK0ContractException exception) when (exception.Code is "source_identity_conflict" or "command_superseded")
            { pending++; continue; }
            if (attempt.State is "starting" or "dispatching" or "uncertain")
            {
                if (attempt.State != "uncertain")
                    await store.TransitionPersonalCloudProviderAttemptAsync(attempt.ClaimId, attempt.State, "uncertain", CancellationToken.None).ConfigureAwait(false);
                uncertain++; continue;
            }
            if (attempt.State is "outcome" or "settled" or "cancelled") { pending++; continue; }
            PersonalCloudProviderClaim claim;
            try
            {
                claim = await delivery.ClaimAsync(attempt.ClaimId, attempt.TaskId, attempt.RequestedChangeSeq,
                    attempt.Source, cancellationToken).ConfigureAwait(false);
                if (claim.Command != attempt.Command) throw new PersonalCloudK0ContractException("invalid_request", "Claim command changed.");
                if (claim.State == "cancelled")
                {
                    await store.TransitionPersonalCloudProviderAttemptAsync(attempt.ClaimId, attempt.State, "cancelled", cancellationToken).ConfigureAwait(false);
                    pending++; continue;
                }
                if (claim.State != "reserved")
                {
                    await store.TransitionPersonalCloudProviderAttemptAsync(attempt.ClaimId, attempt.State, "uncertain", cancellationToken).ConfigureAwait(false);
                    uncertain++; continue;
                }
                if (attempt.State == "claiming" && !await store.TransitionPersonalCloudProviderAttemptAsync(attempt.ClaimId,
                    "claiming", "reserved", cancellationToken).ConfigureAwait(false)) { pending++; continue; }
                if (!await store.TransitionPersonalCloudProviderAttemptAsync(attempt.ClaimId, "reserved", "starting", cancellationToken).ConfigureAwait(false))
                { pending++; continue; }
            }
            catch (PersonalCloudK0ContractException exception) when (exception.Code is "delivery_busy" or "command_superseded" or "source_identity_conflict")
            {
                if (exception.Code == "command_superseded")
                    await store.TransitionPersonalCloudProviderAttemptAsync(attempt.ClaimId, attempt.State, "cancelled", CancellationToken.None).ConfigureAwait(false);
                pending++; continue;
            }
            // From the first start request onward, any unknown outcome blocks automatic replay.
            try
            {
                claim = await delivery.StartAsync(claim, cancellationToken).ConfigureAwait(false);
                if (claim.State == "cancelled")
                {
                    await store.TransitionPersonalCloudProviderAttemptAsync(attempt.ClaimId, "starting", "cancelled", cancellationToken).ConfigureAwait(false);
                    pending++; continue;
                }
                if (claim.State != "started") throw new PersonalCloudK0ContractException("invalid_transition", "Delivery cannot start.");
                if (!await store.TransitionPersonalCloudProviderAttemptAsync(attempt.ClaimId, "starting", "dispatching", cancellationToken).ConfigureAwait(false))
                { uncertain++; continue; }
                string? errorCode = null;
                try
                {
                    await snapshot.Configuration.Provider.CompleteTaskAsync(new PendingCompletion
                    {
                        Id = attempt.ClaimId, TaskId = attempt.TaskId, Provider = source.IntegrationId,
                        ProviderTaskId = source.ProviderTaskId, Completed = attempt.Command == "complete",
                        CompletedAt = attempt.Command == "complete" ? DateTimeOffset.UtcNow : null,
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException exception) when (KnownNoEffectCode(exception.StatusCode) is { } code)
                { errorCode = code; }
                await store.CompletePersonalCloudProviderAttemptAsync(connection.InstanceId, attempt, errorCode, CancellationToken.None).ConfigureAwait(false);
                if (errorCode is null) confirmed++; else rejected++;
            }
            catch (PersonalCloudK0ContractException exception) when (exception.Code == "command_superseded")
            {
                await store.TransitionPersonalCloudProviderAttemptAsync(attempt.ClaimId, "starting", "cancelled", CancellationToken.None).ConfigureAwait(false);
                pending++;
            }
            catch (Exception)
            {
                await store.TransitionPersonalCloudProviderAttemptAsync(attempt.ClaimId, "starting", "uncertain", CancellationToken.None).ConfigureAwait(false);
                await store.TransitionPersonalCloudProviderAttemptAsync(attempt.ClaimId, "dispatching", "uncertain", CancellationToken.None).ConfigureAwait(false);
                uncertain++;
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        }
        foreach (var item in await store.GetPersonalCloudProviderSourcesAsync(connection.InstanceId, cancellationToken).ConfigureAwait(false))
        {
            if (!snapshots.TryGetValue(item.Source.IntegrationKind, out var snapshot)) continue;
            var matches = snapshot.Tasks.Where(task => SafeIdentity(task) == item.Source.ExternalIdHash &&
                task.IntegrationId == item.Source.IntegrationKind).ToArray();
            if (matches.Length != 1) continue;
            var fresh = matches[0];
            var safe = new K0SafeSourceDescriptor
            {
                IntegrationKind = fresh.IntegrationId, ExternalIdHash = SafeIdentity(fresh), SourceUrl = fresh.SourceUrl,
                Title = fresh.Title, Description = fresh.Description, ProjectName = fresh.SourceProjectName, Priority = fresh.Priority,
                PlannedOn = fresh.PlannedOn?.ToString("yyyy-MM-dd"), PlannedAt = fresh.PlannedAt is { } planned ? PersonalCloudK0Mapper.FormatTimestamp(planned) : null,
                DeadlineOn = fresh.DeadlineOn?.ToString("yyyy-MM-dd"), DeadlineAt = fresh.DeadlineAt is { } deadline ? PersonalCloudK0Mapper.FormatTimestamp(deadline) : null,
                RecurrenceRule = fresh.RecurrenceRule, RefreshedAt = PersonalCloudK0Mapper.FormatTimestamp(snapshot.ObservedAt),
            };
            await store.EnqueuePersonalCloudSourceSnapshotAsync(connection.InstanceId, connection.DeviceId, item.TaskId, safe, cancellationToken).ConfigureAwait(false);
        }
        return new(confirmed, rejected, uncertain, pending);
    }

    private static string SafeIdentity(ProviderSourceItem task) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{task.IntegrationId}:{task.ExternalId}"))).ToLowerInvariant();

    private static string? KnownNoEffectCode(HttpStatusCode? status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "authentication_required",
        HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity => "provider_rejected",
        HttpStatusCode.TooManyRequests => "retry_exhausted",
        _ => null,
    };

    public async Task RetryAsync(string taskId, CancellationToken cancellationToken = default)
    {
        using var connectionLease = PersonalCloudConnectionLease.AcquireAuthoring(store.DatabasePath);
        var command = await store.GetPersonalCloudProviderCommandAsync(connection.InstanceId, taskId, cancellationToken).ConfigureAwait(false);
        if (command is not { State: "rejected" })
            throw new PersonalCloudK0ContractException("invalid_transition", "Only a rejected provider request can be retried.");
        var retryId = await store.PreparePersonalCloudProviderRetryAsync(connection.InstanceId, taskId,
            command.RequestedChangeSeq, cancellationToken).ConfigureAwait(false);
        await delivery.RetryAsync(retryId, taskId, command.RequestedChangeSeq, command.Source, cancellationToken).ConfigureAwait(false);
        // The new request is downloaded through the normal ordered feed, never fabricated locally.
    }
}
