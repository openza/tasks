using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Core.Data;

public sealed record PersonalCloudCatalogProjectChoice(string Id, string Name);
public sealed record PersonalCloudCatalogAction(string Phase, string? AdoptedTaskId, string AccountBinding, string? SourceConnectionId, string? MoveToProjectId);

public sealed partial class SqliteTaskStore
{
    public static string PersonalCloudOpaqueId(string prefix, string identity) => prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();

    public async Task<IReadOnlyList<ProviderSourceItem>> GetPersonalCloudCatalogSourceItemsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand(); command.CommandText = "SELECT id FROM provider_source_items ORDER BY id";
        var ids = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(reader.GetString(0));
        var items = new List<ProviderSourceItem>();
        foreach (var id in ids)
            if (await ReadProviderSourceItemByIdAsync(connection, id, cancellationToken).ConfigureAwait(false) is { } item) items.Add(item);
        return items;
    }

    public async Task<bool> ProcessPersonalCloudRouteIntentAsync(string instanceId, string deviceId, JsonElement intent,
        IReadOnlyList<PersonalCloudConfiguredProvider> providers, CancellationToken cancellationToken = default)
    {
        PersonalCloudWorkspaceCodec.ValidateRecord("intents", intent);
        if (WText(intent, "bridgeDeviceId") != deviceId) throw PersonalCloudWorkspaceCodec.Invalid();
        var route = intent.GetProperty("route"); var intentId = WText(intent, "id")!;
        var scopeId = route.TryGetProperty("scopeId", out _) ? WText(route, "scopeId") : null;
        var deleting = WText(intent, "action") == "route.delete";
        var scope = scopeId is null ? null : (await GetPersonalCloudWorkspaceRecordsAsync(instanceId, "scopes", cancellationToken).ConfigureAwait(false)).Cast<JsonElement?>().SingleOrDefault(s => WText(s!.Value, "id") == scopeId);
        var supported = scope is { } owned && WText(owned, "bridgeDeviceId") == deviceId && WText(owned, "integrationKind") == IntegrationIds.Todoist;
        var choice = WText(route, "moveToProjectId"); string? providerProject = null; string? providerConnection = null; var binding = string.Empty;
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (supported)
        {
            var read = connection.CreateCommand(); read.CommandText = "SELECT source_connection_id FROM personal_cloud_provider_scope_mappings WHERE instance_id=@i AND scope_id=@s AND integration_id=@k";
            read.Parameters.AddWithValue("@i", instanceId); read.Parameters.AddWithValue("@s", scopeId!); read.Parameters.AddWithValue("@k", IntegrationIds.Todoist);
            providerConnection = (string?)await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var matching = providers.Where(p => p.Provider.IntegrationId == IntegrationIds.Todoist && p.Provider.ProviderConnectionId == providerConnection).ToArray();
            // An unavailable observation is not evidence that the local connection is unsupported.
            // Keep the intent untouched until both the snapshot and its published scope recover.
            if (!deleting && providerConnection == "todoist_default" &&
                (matching.Length == 0 || !scope!.Value.GetProperty("available").GetBoolean())) return false;
            supported = providerConnection == "todoist_default" && (deleting || matching.Length == 1);
            binding = matching.Length == 1 ? matching[0].AccountBinding : string.Empty;
            if (!deleting && choice is not null)
            {
                supported &= scope!.Value.GetProperty("projectChoices").EnumerateArray().Any(p => WText(p, "id") == choice);
                read = connection.CreateCommand(); read.CommandText = "SELECT provider_project_id FROM personal_cloud_project_choice_mappings WHERE instance_id=@i AND choice_id=@c AND integration_id=@k AND source_connection_id=@s";
                read.Parameters.AddWithValue("@i", instanceId); read.Parameters.AddWithValue("@c", choice); read.Parameters.AddWithValue("@k", IntegrationIds.Todoist); read.Parameters.AddWithValue("@s", providerConnection ?? "");
                providerProject = (string?)await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                supported &= providerProject is not null;
            }
            if (!deleting)
            {
                var spaces = await GetSpacesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                supported &= spaces.Any(s => s.Id == WText(route, "spaceId") && !s.IsArchived);
            }
        }
        if (WText(intent, "state") == "pending_desktop")
        {
            await PreparePersonalCloudCatalogIntentAsync(instanceId, deviceId, intent, binding, providerConnection, providerProject, cancellationToken).ConfigureAwait(false);
            return false;
        }
        var action = await GetPersonalCloudCatalogActionAsync(instanceId, intentId, cancellationToken).ConfigureAwait(false);
        if (action is null || !deleting && action.AccountBinding != binding || action.Phase == "result_queued") return false;
        if (!supported)
        {
            await ResolvePersonalCloudCatalogIntentAsync(instanceId, deviceId, intent, "rejected", null, "unsupported", cancellationToken).ConfigureAwait(false);
            return false;
        }
        if (action.Phase == "prepared")
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var routeId = WText(route, "id")!;
            var existing = (await ReadSyncRoutesCoreAsync(connection, cancellationToken).ConfigureAwait(false)).FirstOrDefault(r => r.Id == TodoistRoutingRuleCodec.RouteId);
            var settings = TodoistRoutingRuleCodec.Read(existing?.SettingsJson);
            var localId = await PersonalCloudLocalRouteIdAsync(connection, instanceId, scopeId!, routeId, cancellationToken).ConfigureAwait(false);
            var labels = settings.LabelRoutes.Where(r => r.Id != localId).ToList();
            var unlabeled = settings.UnlabeledRoute?.Id == localId ? null : settings.UnlabeledRoute;
            if (!deleting)
            {
                var label = WText(route, "label"); var spaceId = WText(route, "spaceId")!;
                var rule = new TodoistRoutingRule(localId, label ?? "", spaceId, providerProject is null ? null : new(providerProject));
                if (label is null) unlabeled = rule;
                else { labels.RemoveAll(r => r.Label.Equals(label, StringComparison.OrdinalIgnoreCase)); labels.Add(rule); }
            }
            var next = new TodoistRoutingRuleSettings(labels, unlabeled);
            await UpsertSyncRouteCoreAsync(connection, new SyncRouteInfo { Id = TodoistRoutingRuleCodec.RouteId, Name = "Todoist label rules", SourceConnectionId = providerConnection,
                IsEnabled = labels.Count > 0 || unlabeled is not null, SettingsJson = TodoistRoutingRuleCodec.Write(next), CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
            await WorkspaceExecuteAsync(connection, "UPDATE personal_cloud_provider_intent_actions SET phase='local_applied' WHERE instance_id=@i AND intent_id=@id", cancellationToken,
                ("@i", instanceId), ("@id", intentId)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        await ResolvePersonalCloudCatalogIntentAsync(instanceId, deviceId, intent, "confirmed", null, null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<PersonalCloudCatalogProjectChoice>> MapPersonalCloudProjectChoicesAsync(string instanceId, string deviceId, string integrationId,
        string sourceConnectionId, IReadOnlyList<ProjectItem> projects, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var choices = new List<PersonalCloudCatalogProjectChoice>();
        foreach (var project in projects.Where(p => p.IntegrationId == integrationId && !p.IsArchived).OrderBy(p => p.SortOrder).ThenBy(p => p.Id, StringComparer.Ordinal).Take(32))
        {
            var providerId = project.ExternalId ?? project.Id;
            var id = PersonalCloudOpaqueId("choice_", $"{deviceId}:{sourceConnectionId}:{providerId}");
            await WorkspaceExecuteAsync(connection, "INSERT OR IGNORE INTO personal_cloud_project_choice_mappings(instance_id,choice_id,integration_id,source_connection_id,provider_project_id) VALUES(@i,@id,@k,@c,@p)", cancellationToken,
                ("@i", instanceId), ("@id", id), ("@k", integrationId), ("@c", sourceConnectionId), ("@p", providerId)).ConfigureAwait(false);
            choices.Add(new(id, project.Name));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return choices;
    }

    public async Task<bool> EnqueuePersonalCloudCatalogItemAsync(string instanceId, string deviceId, ProviderSourceItem local, K0SafeSourceDescriptor source,
        bool available, IReadOnlyList<string> labelNames, IReadOnlyList<PersonalCloudCatalogProjectChoice> projectChoices, CancellationToken cancellationToken = default, string? scopeId = null)
    {
        PersonalCloudK0Codec.ValidateSafeSource(source);
        if (labelNames.Count > 32 || projectChoices.Count > 32) throw new PersonalCloudK0ContractException("unsupported", "The catalog supports up to 32 labels and project choices per source.");
        var records = await GetPersonalCloudWorkspaceRecordsAsync(instanceId, "catalog", cancellationToken).ConfigureAwait(false);
        var same = records.Where(r => WText(r.GetProperty("source"), "integrationKind") == source.IntegrationKind && WText(r.GetProperty("source"), "externalIdHash") == source.ExternalIdHash).ToArray();
        if (same.Any(r => WText(r, "bridgeDeviceId") != deviceId)) return false; // Another paired bridge owns this hashed identity.
        var id = same.Length == 1 ? WText(same[0], "id")! : PersonalCloudOpaqueId("catalog_", $"{deviceId}:{source.IntegrationKind}:{source.ExternalIdHash}");
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var scoped = await SupportsPersonalCloudProviderScopesAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (scoped && scopeId is null) throw new PersonalCloudK0ContractException("unsupported", "A catalog source requires its owning connection scope.");
        if (scoped && scopeId != PersonalCloudOpaqueId("scope_", $"{deviceId}:{local.IntegrationId}:{local.ProviderConnectionId}")) throw PersonalCloudWorkspaceCodec.Invalid();
        if (scoped && same.Length == 1 && same[0].TryGetProperty("scopeId", out _) && WText(same[0], "scopeId") is { } pinnedScope && pinnedScope != scopeId) return false;
        await WorkspaceExecuteAsync(connection, "INSERT OR IGNORE INTO personal_cloud_catalog_mappings(instance_id,catalog_id,source_item_id) VALUES(@i,@c,@s)", cancellationToken, ("@i", instanceId), ("@c", id), ("@s", local.Id)).ConfigureAwait(false);
        if (await HasPendingWorkspaceOverlayAsync(connection, instanceId, id, cancellationToken).ConfigureAwait(false))
        { await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return false; }
        var adoptedTaskId = local.AdoptedTaskId;
        if (adoptedTaskId is not null)
        {
            var taskRecords = await ReadWorkspaceRecordAsync(connection, instanceId, "tasks", adoptedTaskId, cancellationToken).ConfigureAwait(false);
            if (taskRecords is null || WText(taskRecords.Value, "sourceOwnerDeviceId") != deviceId || taskRecords.Value.GetProperty("source").ValueKind == JsonValueKind.Null)
            {
                // A locally adopted task with unverified bridge ownership must never appear as
                // an unadopted source and invite a duplicate adoption. Its task remains visible.
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
            var ownedSource = taskRecords.Value.GetProperty("source");
            if (WText(ownedSource, "integrationKind") != source.IntegrationKind || WText(ownedSource, "externalIdHash") != source.ExternalIdHash) return false;
            if (WText(taskRecords.Value, "deletedAt") is not null) adoptedTaskId = null;
        }
        var item = new Dictionary<string, object?> { ["source"] = source, ["isSkipped"] = local.IsSkipped, ["available"] = available, ["adoptedTaskId"] = adoptedTaskId, ["labelNames"] = labelNames, ["projectChoices"] = projectChoices };
        if (scoped) item["scopeId"] = scopeId;
        if (same.Length == 1)
        {
            var previous = same[0];
            var comparable = new Dictionary<string, object?> { ["source"] = PersonalCloudK0Codec.Deserialize<K0SafeSourceDescriptor>(previous.GetProperty("source").GetRawText()) with { RefreshedAt = source.RefreshedAt },
                ["isSkipped"] = previous.GetProperty("isSkipped").GetBoolean(), ["available"] = previous.GetProperty("available").GetBoolean(), ["adoptedTaskId"] = WText(previous, "adoptedTaskId"),
                ["labelNames"] = previous.GetProperty("labelNames"), ["projectChoices"] = previous.GetProperty("projectChoices") };
            if (scoped) comparable["scopeId"] = previous.TryGetProperty("scopeId", out _) ? WText(previous, "scopeId") : null;
            // Catalog time is the last published facts observation, not connection
            // liveness. Equal facts need no periodic full-record publication.
            // Adopted task source snapshots retain their separate six-hour heartbeat.
            if (PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize(item)) == PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize(comparable)))
            { await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return false; }
        }
        await AppendWorkspaceMutationAsync(connection, instanceId, deviceId, "provider.catalog.put", id,
            await WorkspaceVersionAsync(connection, instanceId, "catalog", id, cancellationToken).ConfigureAwait(false), new { item }, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<JsonElement?> ReadWorkspaceRecordAsync(SqliteConnection connection, string instanceId, string kind, string id, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand(); command.CommandText = "SELECT canonical_json FROM personal_cloud_workspace_records WHERE instance_id=@i AND kind=@k AND entity_id=@id";
        command.Parameters.AddWithValue("@i", instanceId); command.Parameters.AddWithValue("@k", kind); command.Parameters.AddWithValue("@id", id);
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (json is null) return null;
        using var document = JsonDocument.Parse(json); return document.RootElement.Clone();
    }

    public async Task<ProviderSourceItem?> GetPersonalCloudCatalogSourceAsync(string instanceId, string catalogId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand(); command.CommandText = "SELECT source_item_id FROM personal_cloud_catalog_mappings WHERE instance_id=@i AND catalog_id=@c";
        command.Parameters.AddWithValue("@i", instanceId); command.Parameters.AddWithValue("@c", catalogId);
        var id = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return id is null ? null : await ReadProviderSourceItemByIdAsync(connection, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PersonalCloudCatalogAction?> GetPersonalCloudCatalogActionAsync(string instanceId, string intentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand(); command.CommandText = "SELECT phase,adopted_task_id,account_binding,source_connection_id,move_to_project_id FROM personal_cloud_provider_intent_actions WHERE instance_id=@i AND intent_id=@id";
        command.Parameters.AddWithValue("@i", instanceId); command.Parameters.AddWithValue("@id", intentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new(reader.GetString(0), GetNullableString(reader, 1), reader.GetString(2), GetNullableString(reader, 3), GetNullableString(reader, 4)) : null;
    }

    public async Task PreparePersonalCloudCatalogIntentAsync(string instanceId, string deviceId, JsonElement intent, string accountBinding, string? sourceConnectionId, string? moveToProjectId, CancellationToken cancellationToken = default)
    {
        PersonalCloudWorkspaceCodec.ValidateRecord("intents", intent);
        if (WText(intent, "bridgeDeviceId") != deviceId || WText(intent, "state") != "pending_desktop") throw PersonalCloudWorkspaceCodec.Invalid();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var id = WText(intent, "id")!;
        var insert = connection.CreateCommand(); insert.CommandText = """
            INSERT OR IGNORE INTO personal_cloud_provider_intent_actions(instance_id,intent_id,canonical_json,phase,account_binding,source_connection_id,move_to_project_id)
            VALUES(@i,@id,@j,'prepared',@a,@c,@p)
            """;
        foreach (var (key, value) in new (string, object?)[] { ("@i", instanceId), ("@id", id), ("@j", PersonalCloudK0Codec.Canonicalize(intent.GetRawText())), ("@a", accountBinding), ("@c", sourceConnectionId), ("@p", moveToProjectId) }) insert.Parameters.AddWithValue(key, value ?? DBNull.Value);
        if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            await AppendWorkspaceMutationAsync(connection, instanceId, deviceId, "provider.intent.start", id, intent.GetProperty("entityVersion").GetInt64(), new { }, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyPersonalCloudSourceIntentAsync(string instanceId, string deviceId, JsonElement intent, string sourceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var source = await ReadProviderSourceItemByIdAsync(connection, sourceId, cancellationToken).ConfigureAwait(false) ?? throw PersonalCloudWorkspaceCodec.Invalid();
        PersonalCloudWorkspaceCodec.ValidateRecord("intents", intent);
        if (WText(intent, "bridgeDeviceId") != deviceId || WText(intent, "state") is not ("started" or "uncertain")) throw PersonalCloudWorkspaceCodec.Invalid();
        var catalog = await ReadWorkspaceRecordAsync(connection, instanceId, "catalog", WText(intent, "catalogId")!, cancellationToken).ConfigureAwait(false)
            ?? throw PersonalCloudWorkspaceCodec.Invalid();
        var descriptor = PersonalCloudK0Codec.Deserialize<K0SafeSourceDescriptor>(catalog.GetProperty("source").GetRawText());
        if (WText(catalog, "bridgeDeviceId") != deviceId || descriptor.IntegrationKind != source.IntegrationId ||
            descriptor.ExternalIdHash != PersonalCloudOpaqueId("", $"{source.IntegrationId}:{source.ExternalId}")) throw PersonalCloudWorkspaceCodec.Invalid();
        var id = WText(intent, "id")!; var action = WText(intent, "action");
        var read = connection.CreateCommand(); read.CommandText = "SELECT phase FROM personal_cloud_provider_intent_actions WHERE instance_id=@i AND intent_id=@id";
        read.Parameters.AddWithValue("@i", instanceId); read.Parameters.AddWithValue("@id", id);
        if ((string?)await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) != "prepared") return;
        string? adoptedId = null;
        if (source.AdoptedTaskId is not null)
        {
            await WorkspaceExecuteAsync(connection, "UPDATE personal_cloud_provider_intent_actions SET phase='local_conflict' WHERE instance_id=@i AND intent_id=@id", cancellationToken, ("@i", instanceId), ("@id", id)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (action == "adopt")
        {
            var spaceId = WText(intent, "spaceId")!;
            var task = CreateTaskFromSource(source, spaceId, DateTimeOffset.UtcNow, null) with
            {
                Title = descriptor.Title, SourceTitle = descriptor.Title, SourceMetadataJson = PersonalCloudK0Codec.Serialize(descriptor),
            };
            adoptedId = task.Id;
            await UpsertTaskCoreAsync(connection, task, cancellationToken).ConfigureAwait(false);
            await SetTaskLabelsCoreAsync(connection, task.Id, task.Labels, cancellationToken).ConfigureAwait(false);
            await UpsertProviderSourceItemCoreAsync(connection, source with { AdoptionState = ProviderSourceAdoptionStates.Adopted, AdoptedTaskId = task.Id }, cancellationToken).ConfigureAwait(false);
            await AppendRichTaskAsync(connection, task, instanceId, deviceId, descriptor, cancellationToken).ConfigureAwait(false);
        }
        else if (action is "skip" or "restore")
            await UpsertProviderSourceItemCoreAsync(connection, source with { AdoptionState = action == "skip" ? ProviderSourceAdoptionStates.Ignored : ProviderSourceAdoptionStates.NotAdopted }, cancellationToken).ConfigureAwait(false);
        else throw PersonalCloudWorkspaceCodec.Invalid();
        await WorkspaceExecuteAsync(connection, "UPDATE personal_cloud_provider_intent_actions SET phase='local_applied',adopted_task_id=@t WHERE instance_id=@i AND intent_id=@id", cancellationToken, ("@i", instanceId), ("@id", id), ("@t", adoptedId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPersonalCloudCatalogActionPhaseAsync(string instanceId, string id, string phase, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await WorkspaceExecuteAsync(connection, "UPDATE personal_cloud_provider_intent_actions SET phase=@p WHERE instance_id=@i AND intent_id=@id", cancellationToken, ("@p", phase), ("@i", instanceId), ("@id", id)).ConfigureAwait(false);
    }

    public async Task ResolvePersonalCloudCatalogIntentAsync(string instanceId, string deviceId, JsonElement intent, string state, string? adoptedTaskId, string? errorCode, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var id = WText(intent, "id")!;
        if (await HasPendingWorkspaceOverlayAsync(connection, instanceId, id, cancellationToken).ConfigureAwait(false)) return;
        await AppendWorkspaceMutationAsync(connection, instanceId, deviceId, "provider.intent.resolve", id, intent.GetProperty("entityVersion").GetInt64(), new { state, adoptedTaskId, errorCode }, cancellationToken).ConfigureAwait(false);
        await WorkspaceExecuteAsync(connection, "UPDATE personal_cloud_provider_intent_actions SET phase=@p WHERE instance_id=@i AND intent_id=@id", cancellationToken, ("@p", state == "uncertain" ? "uncertain" : "result_queued"), ("@i", instanceId), ("@id", id)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
