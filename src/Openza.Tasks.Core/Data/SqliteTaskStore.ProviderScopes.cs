using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Core.Data;

public sealed partial class SqliteTaskStore
{
    public async Task EnqueueUnavailablePersonalCloudScopesAsync(string instanceId, string deviceId, IReadOnlyCollection<string> observedIntegrations, CancellationToken cancellationToken = default)
    {
        var records = await GetPersonalCloudWorkspaceRecordsAsync(instanceId, "scopes", cancellationToken).ConfigureAwait(false);
        foreach (var scope in records.Where(s => WText(s, "bridgeDeviceId") == deviceId && !observedIntegrations.Contains(WText(s, "integrationKind")!)))
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var read = connection.CreateCommand(); read.CommandText = "SELECT source_connection_id FROM personal_cloud_provider_scope_mappings WHERE instance_id=@i AND scope_id=@s";
            read.Parameters.AddWithValue("@i", instanceId); read.Parameters.AddWithValue("@s", WText(scope, "id")!);
            if ((string?)await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not { } sourceConnection) continue;
            await EnqueuePersonalCloudProviderScopeAsync(instanceId, deviceId, WText(scope, "integrationKind")!, sourceConnection, false,
                scope.GetProperty("labelNames").EnumerateArray().Select(l => l.GetString()!).ToArray(),
                scope.GetProperty("projectChoices").EnumerateArray().Select(p => new PersonalCloudCatalogProjectChoice(WText(p, "id")!, WText(p, "name")!)).ToArray(), cancellationToken).ConfigureAwait(false);
        }
    }
    public async Task<string?> EnqueuePersonalCloudProviderScopeAsync(string instanceId, string deviceId, string integrationId,
        string sourceConnectionId, bool available, IReadOnlyList<string> labelNames,
        IReadOnlyList<PersonalCloudCatalogProjectChoice> projectChoices, CancellationToken cancellationToken = default)
    {
        if (!await SupportsPersonalCloudProviderScopesAsync(instanceId, cancellationToken).ConfigureAwait(false)) return null;
        var id = PersonalCloudOpaqueId("scope_", $"{deviceId}:{integrationId}:{sourceConnectionId}");
        var scope = new { name = integrationId == IntegrationIds.Todoist ? "Todoist" : integrationId == IntegrationIds.MicrosoftToDo ? "Microsoft To Do" : integrationId,
            integrationKind = integrationId, available, labelNames, projectChoices };
        // Validate the same bounded public projection before persisting any sequence.
        var record = JsonSerializer.SerializeToElement(new { id, bridgeDeviceId = deviceId, scope.name, scope.integrationKind, available, labelNames, projectChoices,
            entityVersion = 1, createdAt = "2026-01-01T00:00:00.000Z", updatedAt = "2026-01-01T00:00:00.000Z" }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        PersonalCloudWorkspaceCodec.ValidateRecord("scopes", record);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await WorkspaceExecuteAsync(connection, "INSERT OR IGNORE INTO personal_cloud_provider_scope_mappings(instance_id,scope_id,integration_id,source_connection_id) VALUES(@i,@s,@k,@c)", cancellationToken,
            ("@i", instanceId), ("@s", id), ("@k", integrationId), ("@c", sourceConnectionId)).ConfigureAwait(false);
        var previous = await ReadWorkspaceRecordAsync(connection, instanceId, "scopes", id, cancellationToken).ConfigureAwait(false);
        if (previous is { } p && WText(p, "bridgeDeviceId") != deviceId) throw PersonalCloudWorkspaceCodec.Invalid();
        if (!await HasPendingWorkspaceOverlayAsync(connection, instanceId, id, cancellationToken).ConfigureAwait(false) &&
            (previous is null || PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize(scope)) != PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize(new {
                name = WText(previous.Value, "name"), integrationKind = WText(previous.Value, "integrationKind"), available = previous.Value.GetProperty("available").GetBoolean(),
                labelNames = previous.Value.GetProperty("labelNames"), projectChoices = previous.Value.GetProperty("projectChoices") }))))
            await AppendWorkspaceMutationAsync(connection, instanceId, deviceId, "provider.scope.put", id,
                await WorkspaceVersionAsync(connection, instanceId, "scopes", id, cancellationToken).ConfigureAwait(false), new { scope }, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    private static async Task<string> PersonalCloudLocalRouteIdAsync(SqliteConnection connection, string instanceId, string scopeId, string routeId, CancellationToken cancellationToken)
    {
        var read = connection.CreateCommand(); read.CommandText = "SELECT local_rule_id FROM personal_cloud_route_mappings WHERE instance_id=@i AND route_id=@r AND scope_id=@s";
        read.Parameters.AddWithValue("@i", instanceId); read.Parameters.AddWithValue("@r", routeId); read.Parameters.AddWithValue("@s", scopeId);
        var local = (string?)await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? PersonalCloudOpaqueId("todoist_rule_", routeId);
        await WorkspaceExecuteAsync(connection, "INSERT OR IGNORE INTO personal_cloud_route_mappings(instance_id,route_id,scope_id,local_rule_id) VALUES(@i,@r,@s,@l)", cancellationToken,
            ("@i", instanceId), ("@r", routeId), ("@s", scopeId), ("@l", local)).ConfigureAwait(false);
        return local;
    }

    public async Task EnqueuePersonalCloudTodoistRoutesAsync(string instanceId, string deviceId, string scopeId, CancellationToken cancellationToken = default)
    {
        if (!await SupportsPersonalCloudProviderScopesAsync(instanceId, cancellationToken).ConfigureAwait(false)) return;
        var scopes = await GetPersonalCloudWorkspaceRecordsAsync(instanceId, "scopes", cancellationToken).ConfigureAwait(false);
        var scope = scopes.SingleOrDefault(s => WText(s, "id") == scopeId);
        if (scope.ValueKind == JsonValueKind.Undefined || WText(scope, "bridgeDeviceId") != deviceId || WText(scope, "integrationKind") != IntegrationIds.Todoist || !scope.GetProperty("available").GetBoolean()) return;
        var routes = await GetPersonalCloudWorkspaceRecordsAsync(instanceId, "routes", cancellationToken).ConfigureAwait(false);
        var intents = await GetPersonalCloudWorkspaceRecordsAsync(instanceId, "intents", cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var parent = (await ReadSyncRoutesCoreAsync(connection, cancellationToken).ConfigureAwait(false)).FirstOrDefault(r => r.Id == TodoistRoutingRuleCodec.RouteId && r.SourceConnectionId == "todoist_default");
        var settings = TodoistRoutingRuleCodec.Read(parent?.SettingsJson);
        var rules = parent?.IsEnabled == true ? settings.LabelRoutes.Concat(settings.UnlabeledRoute is { } unlabeled ? [unlabeled] : []).ToArray() : [];
        var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
        var read = connection.CreateCommand(); read.CommandText = "SELECT route_id,local_rule_id FROM personal_cloud_route_mappings WHERE instance_id=@i AND scope_id=@s";
        read.Parameters.AddWithValue("@i", instanceId); read.Parameters.AddWithValue("@s", scopeId);
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) mapped[reader.GetString(1)] = reader.GetString(0);
        foreach (var rule in rules) mapped.TryAdd(rule.Id, PersonalCloudOpaqueId("route_", $"{deviceId}:{scopeId}:{rule.Id}"));
        foreach (var (localId, routeId) in mapped)
        {
            var rule = rules.SingleOrDefault(r => r.Id == localId);
            var remote = routes.SingleOrDefault(r => WText(r, "id") == routeId);
            if (rule is null && (remote.ValueKind == JsonValueKind.Undefined || WText(remote, "deletedAt") is not null)) continue;
            if (intents.Any(i => WText(i, "state") is "pending_desktop" or "started" or "uncertain" && i.GetProperty("route").ValueKind == JsonValueKind.Object && WText(i.GetProperty("route"), "id") == routeId)) continue;
            string? choice = null;
            if (rule?.PostImport is { } post)
            {
                read = connection.CreateCommand(); read.CommandText = "SELECT choice_id FROM personal_cloud_project_choice_mappings WHERE instance_id=@i AND integration_id=@k AND source_connection_id='todoist_default' AND provider_project_id=@p";
                read.Parameters.AddWithValue("@i", instanceId); read.Parameters.AddWithValue("@k", IntegrationIds.Todoist); read.Parameters.AddWithValue("@p", post.MoveToProjectId);
                choice = (string?)await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (choice is null || !scope.GetProperty("projectChoices").EnumerateArray().Any(p => WText(p, "id") == choice)) continue;
            }
            var route = new { id = routeId, scopeId, label = rule is null ? WText(remote, "label") : string.IsNullOrWhiteSpace(rule.Label) ? null : rule.Label,
                spaceId = rule?.SpaceId ?? WText(remote, "spaceId"), moveToProjectId = rule is null ? WText(remote, "moveToProjectId") : choice };
            if (rule is not null && remote.ValueKind != JsonValueKind.Undefined && WText(remote, "deletedAt") is null &&
                WText(remote, "scopeId") == scopeId && WText(remote, "label") == route.label && WText(remote, "spaceId") == route.spaceId && WText(remote, "moveToProjectId") == choice) continue;
            var intent = new { bridgeDeviceId = deviceId, action = rule is null ? "route.delete" : "route.put", catalogId = (string?)null, spaceId = (string?)null, route };
            var fingerprint = PersonalCloudK0Codec.Canonicalize(PersonalCloudK0Codec.Serialize(intent));
            await PersonalCloudLocalRouteIdAsync(connection, instanceId, scopeId, routeId, cancellationToken).ConfigureAwait(false);
            // The mapping for a Desktop-authored rule preserves the existing editor identity.
            await WorkspaceExecuteAsync(connection, "UPDATE personal_cloud_route_mappings SET local_rule_id=@l WHERE instance_id=@i AND route_id=@r", cancellationToken, ("@i", instanceId), ("@r", routeId), ("@l", localId)).ConfigureAwait(false);
            read = connection.CreateCommand(); read.CommandText = "SELECT last_requested_json FROM personal_cloud_route_mappings WHERE instance_id=@i AND route_id=@r";
            read.Parameters.AddWithValue("@i", instanceId); read.Parameters.AddWithValue("@r", routeId);
            if (await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string requested && requested == fingerprint) continue;
            await AppendWorkspaceMutationAsync(connection, instanceId, deviceId, "provider.intent.request", $"intent_{Guid.NewGuid():N}", null, new { intent }, cancellationToken).ConfigureAwait(false);
            await WorkspaceExecuteAsync(connection, "UPDATE personal_cloud_route_mappings SET last_requested_json=@j WHERE instance_id=@i AND route_id=@r", cancellationToken,
                ("@i", instanceId), ("@r", routeId), ("@j", fingerprint)).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
