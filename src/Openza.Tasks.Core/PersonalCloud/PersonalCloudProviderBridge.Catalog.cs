using System.Text.Json;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Core.PersonalCloud;

public sealed partial class PersonalCloudProviderBridge
{
    private async Task<PersonalCloudProviderBridgeResult> RunCatalogAsync(
        IReadOnlyDictionary<string, (PersonalCloudConfiguredProvider Configuration, IReadOnlyList<ProviderSourceItem> Tasks, DateTimeOffset ObservedAt)> snapshots,
        IReadOnlyDictionary<string, IReadOnlyList<ProjectItem>> projects, IReadOnlyDictionary<string, IReadOnlyList<string>> labels, CancellationToken cancellationToken)
    {
        var scopeIds = new Dictionary<string, string?>();
        await store.EnqueueUnavailablePersonalCloudScopesAsync(connection.InstanceId, connection.DeviceId, snapshots.Keys.ToArray(), cancellationToken).ConfigureAwait(false);
        foreach (var (integration, snapshot) in snapshots)
        {
            var choices = await store.MapPersonalCloudProjectChoicesAsync(connection.InstanceId, connection.DeviceId, integration, snapshot.Configuration.Provider.ProviderConnectionId, projects[integration], cancellationToken).ConfigureAwait(false);
            scopeIds[integration] = await store.EnqueuePersonalCloudProviderScopeAsync(connection.InstanceId, connection.DeviceId, integration, snapshot.Configuration.Provider.ProviderConnectionId, true, labels[integration], choices, cancellationToken).ConfigureAwait(false);
        }
        var intents = await delivery.GetOwnedIntentsAsync(cancellationToken).ConfigureAwait(false);
        var confirmed = 0; var rejected = 0; var uncertain = 0; var pending = 0;
        foreach (var intent in intents)
        {
            var id = PersonalCloudWorkspaceCodec.Text(intent, "id")!;
            var kind = PersonalCloudWorkspaceCodec.Text(intent, "action");
            if (kind is "route.put" or "route.delete")
            {
                if (await store.ProcessPersonalCloudRouteIntentAsync(connection.InstanceId, connection.DeviceId, intent, snapshots.Values.Select(v => v.Configuration).ToArray(), cancellationToken).ConfigureAwait(false)) confirmed++;
                else pending++;
                continue;
            }
            var catalogId = PersonalCloudWorkspaceCodec.Text(intent, "catalogId")!;
            var source = await store.GetPersonalCloudCatalogSourceAsync(connection.InstanceId, catalogId, cancellationToken).ConfigureAwait(false);
            if (source is null || !snapshots.TryGetValue(source.IntegrationId, out var snapshot) ||
                source.ProviderConnectionId != snapshot.Configuration.Provider.ProviderConnectionId) { pending++; continue; }
            var fresh = snapshot.Tasks.Where(t => SafeIdentity(t) == SafeIdentity(source)).ToArray();
            if (fresh.Length != 1) { pending++; continue; }
            var action = await store.GetPersonalCloudCatalogActionAsync(connection.InstanceId, id, cancellationToken).ConfigureAwait(false);
            if (PersonalCloudWorkspaceCodec.Text(intent, "state") == "pending_desktop")
            {
                var policy = ProviderSourceRoutingPolicy.FromRoutes(await store.GetSyncRoutesAsync(cancellationToken).ConfigureAwait(false), source.ProviderConnectionId, source.IntegrationId);
                await store.PreparePersonalCloudCatalogIntentAsync(connection.InstanceId, connection.DeviceId, intent,
                    snapshot.Configuration.AccountBinding, source.ProviderConnectionId, kind == "adopt" ? policy.Match(source).PostImportAction?.MoveToProjectId : null, cancellationToken).ConfigureAwait(false);
                pending++; continue; // No local/provider action before the durable cloud start.
            }
            if (action is null || action.AccountBinding != snapshot.Configuration.AccountBinding || action.SourceConnectionId != source.ProviderConnectionId)
            { pending++; continue; } // Changed/ambiguous account is never dispatched.
            if (action.Phase == "prepared")
            {
                await store.ApplyPersonalCloudSourceIntentAsync(connection.InstanceId, connection.DeviceId, intent, source.Id, cancellationToken).ConfigureAwait(false);
                action = await store.GetPersonalCloudCatalogActionAsync(connection.InstanceId, id, cancellationToken).ConfigureAwait(false);
            }
            if (action is null) { pending++; continue; }
            if (action.Phase == "local_conflict")
            {
                await store.ResolvePersonalCloudCatalogIntentAsync(connection.InstanceId, connection.DeviceId, intent, "rejected", null, "provider_rejected", cancellationToken).ConfigureAwait(false);
                rejected++; continue;
            }
            if (action.AdoptedTaskId is { } taskId)
            {
                var cloudTask = (await store.GetPersonalCloudWorkspaceRecordsAsync(connection.InstanceId, "tasks", cancellationToken).ConfigureAwait(false)).FirstOrDefault(t => PersonalCloudWorkspaceCodec.Text(t, "id") == taskId);
                if (cloudTask.ValueKind == JsonValueKind.Undefined || PersonalCloudWorkspaceCodec.Text(cloudTask, "sourceOwnerDeviceId") != connection.DeviceId)
                { pending++; continue; } // Rich task is delivered before the adoption result.
            }
            if (action.Phase == "result_queued") { pending++; continue; }
            if (action.MoveToProjectId is { } target && action.Phase != "effect_confirmed")
            {
                if (fresh[0].SourceProjectId == target || projects[source.IntegrationId].Any(p => p.Id == fresh[0].SourceProjectId && (p.ExternalId ?? p.Id) == target))
                    await store.SetPersonalCloudCatalogActionPhaseAsync(connection.InstanceId, id, "effect_confirmed", cancellationToken).ConfigureAwait(false);
                else if (action.Phase is "dispatching" or "uncertain")
                {
                    if (PersonalCloudWorkspaceCodec.Text(intent, "state") != "uncertain")
                        await store.ResolvePersonalCloudCatalogIntentAsync(connection.InstanceId, connection.DeviceId, intent, "uncertain", null, "delivery_uncertain", cancellationToken).ConfigureAwait(false);
                    uncertain++; continue; // A restarted provider side effect is reconciled, never repeated blindly.
                }
                else if (snapshot.Configuration.Provider is not ITaskProjectMoveProvider mover)
                {
                    await store.ResolvePersonalCloudCatalogIntentAsync(connection.InstanceId, connection.DeviceId, intent, "rejected", null, "unsupported", cancellationToken).ConfigureAwait(false);
                    rejected++; continue;
                }
                else
                {
                    await store.SetPersonalCloudCatalogActionPhaseAsync(connection.InstanceId, id, "dispatching", cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await mover.MoveTaskAsync(fresh[0].ProviderTaskId ?? fresh[0].ExternalId, target, cancellationToken).ConfigureAwait(false);
                        await store.SetPersonalCloudCatalogActionPhaseAsync(connection.InstanceId, id, "effect_confirmed", CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        await store.ResolvePersonalCloudCatalogIntentAsync(connection.InstanceId, connection.DeviceId, intent, "uncertain", null, "delivery_uncertain", CancellationToken.None).ConfigureAwait(false);
                        uncertain++; continue;
                    }
                }
            }
            await store.ResolvePersonalCloudCatalogIntentAsync(connection.InstanceId, connection.DeviceId, intent, "confirmed", action.AdoptedTaskId, null, cancellationToken).ConfigureAwait(false);
            confirmed++;
        }
        var localItems = await store.GetPersonalCloudCatalogSourceItemsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var local in localItems)
        {
            if (!snapshots.TryGetValue(local.IntegrationId, out var snapshot) || local.ProviderConnectionId != snapshot.Configuration.Provider.ProviderConnectionId) continue;
            // Workflow facts settle through their intent, never a competing catalog publication.
            var currentCatalog = (await store.GetPersonalCloudWorkspaceRecordsAsync(connection.InstanceId, "catalog", cancellationToken).ConfigureAwait(false)).FirstOrDefault(r =>
                PersonalCloudWorkspaceCodec.Text(r.GetProperty("source"), "externalIdHash") == SafeIdentity(local) && PersonalCloudWorkspaceCodec.Text(r.GetProperty("source"), "integrationKind") == local.IntegrationId);
            if (currentCatalog.ValueKind != JsonValueKind.Undefined && intents.Any(i => PersonalCloudWorkspaceCodec.Text(i, "catalogId") == PersonalCloudWorkspaceCodec.Text(currentCatalog, "id"))) continue;
            var matches = snapshot.Tasks.Where(t => SafeIdentity(t) == SafeIdentity(local)).ToArray();
            if (matches.Length > 1) continue;
            var fresh = matches.SingleOrDefault() ?? local;
            var safe = SafeCatalogSource(fresh, snapshot.ObservedAt);
            var choices = await store.MapPersonalCloudProjectChoicesAsync(connection.InstanceId, connection.DeviceId, local.IntegrationId, local.ProviderConnectionId,
                projects[local.IntegrationId], cancellationToken).ConfigureAwait(false);
            await store.EnqueuePersonalCloudCatalogItemAsync(connection.InstanceId, connection.DeviceId, local, safe, matches.Length == 1,
                CatalogLabels(fresh.SnapshotJson).Take(32).ToArray(), choices, cancellationToken, scopeIds[local.IntegrationId]).ConfigureAwait(false);
        }
        foreach (var (integration, snapshot) in snapshots)
            if (integration == IntegrationIds.Todoist && snapshot.Configuration.Provider.ProviderConnectionId == "todoist_default" && scopeIds[integration] is { } scopeId)
                await store.EnqueuePersonalCloudTodoistRoutesAsync(connection.InstanceId, connection.DeviceId, scopeId, cancellationToken).ConfigureAwait(false);
        return new(confirmed, rejected, uncertain, pending);
    }

    private static K0SafeSourceDescriptor SafeCatalogSource(ProviderSourceItem source, DateTimeOffset observedAt) => new()
    {
        IntegrationKind = source.IntegrationId, ExternalIdHash = SafeIdentity(source), SourceUrl = source.SourceUrl,
        Title = source.Title, Description = source.Description, ProjectName = source.SourceProjectName, Priority = source.Priority,
        PlannedOn = source.PlannedOn?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), PlannedAt = source.PlannedAt is null ? null : PersonalCloudK0Mapper.FormatTimestamp(source.PlannedAt.Value),
        DeadlineOn = source.DeadlineOn?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), DeadlineAt = source.DeadlineAt is null ? null : PersonalCloudK0Mapper.FormatTimestamp(source.DeadlineAt.Value),
        RecurrenceRule = source.RecurrenceRule, RefreshedAt = PersonalCloudK0Mapper.FormatTimestamp(observedAt),
    };
    private static IReadOnlyList<string> CatalogLabels(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("sourceTask", out var source) || !source.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array) return [];
            return labels.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        catch (JsonException) { return []; }
    }
}
