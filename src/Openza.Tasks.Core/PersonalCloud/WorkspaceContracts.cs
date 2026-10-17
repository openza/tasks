using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.PersonalCloud;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudWorkspaceChange : IPersonalCloudTaskChange
{
    public required int ProtocolVersion { get; init; }
    public required string Product { get; init; }
    public required long ChangeSeq { get; init; }
    public required string MutationId { get; init; }
    public required string Operation { get; init; }
    public required string EntityKind { get; init; }
    public required string EntityId { get; init; }
    public required long EntityVersion { get; init; }
    public required JsonElement Payload { get; init; }
    public required string ServerAcceptedAt { get; init; }
}

/// <summary>The negotiated schema 23 projection. Raw provider settings never belong here.</summary>
public static class PersonalCloudWorkspaceCodec
{
    internal static readonly string[] SnapshotSections = ["spaces", "projects", "tasks", "labels", "taskLabels", "tombstones", "sourceDescriptors", "externalCommands", "scopes", "catalog", "intents", "routes"];
    public static PersonalCloudWorkspaceChange ParseChange(JsonElement element)
    {
        var change = PersonalCloudK0Codec.Deserialize<PersonalCloudWorkspaceChange>(element.GetRawText());
        if (change.ProtocolVersion != 1 || change.Product != "tasks" || change.Operation != "workspace.changed" ||
            change.EntityKind != "workspace" || change.ChangeSeq <= 0 || change.EntityVersion <= 0) throw Invalid();
        PersonalCloudK0Codec.ValidateIdentifier(change.EntityId, "entityId");
        PersonalCloudK0Codec.ValidateIdentifier(change.MutationId, "mutationId");
        PersonalCloudK0Codec.ValidateTimestamp(change.ServerAcceptedAt, "serverAcceptedAt");
        ValidatePayload(change.Payload);
        return change;
    }

    public static void ValidatePayload(JsonElement payload)
    {
        Keys(payload, payload.TryGetProperty("scopes", out _) ? "spaces projects tasks scopes catalog intents routes" : "spaces projects tasks catalog intents routes");
        ValidateArrays(payload);
    }

    public static (long Checkpoint, JsonElement Snapshot) ParseBootstrap(string json)
    {
        _ = PersonalCloudK0Codec.Canonicalize(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Keys(root, "protocolVersion product checkpoint snapshot");
        if (root.GetProperty("protocolVersion").GetInt32() != 1 || Text(root, "product") != "tasks" ||
            root.GetProperty("checkpoint").GetInt64() < 0) throw Invalid();
        var snapshot = root.GetProperty("snapshot");
        Keys(snapshot, "spaces projects tasks labels taskLabels tombstones sourceDescriptors externalCommands catalog intents routes" + (snapshot.TryGetProperty("scopes", out _) ? " scopes" : ""));
        ValidateArrays(snapshot);
        foreach (var name in new[] { "labels", "taskLabels", "tombstones", "sourceDescriptors", "externalCommands" })
            if (snapshot.GetProperty(name).ValueKind != JsonValueKind.Array) throw Invalid();
        return (root.GetProperty("checkpoint").GetInt64(), snapshot.Clone());
    }

    internal static (long Checkpoint, JsonElement Snapshot, bool HasMore, string? NextCursor) ParseBootstrapPage(string json)
    {
        _ = PersonalCloudK0Codec.Canonicalize(json);
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        Keys(root, "protocolVersion product checkpoint snapshot hasMore nextCursor");
        if (root.GetProperty("hasMore").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
        var more = root.GetProperty("hasMore").GetBoolean(); var cursor = Text(root, "nextCursor");
        if (more != !string.IsNullOrEmpty(cursor) || cursor?.Length > 8192) throw Invalid();
        // Reuse the complete snapshot validation without accepting paged keys on old endpoints.
        var baseline = "{\"protocolVersion\":" + root.GetProperty("protocolVersion").GetRawText() + ",\"product\":" + root.GetProperty("product").GetRawText() +
            ",\"checkpoint\":" + root.GetProperty("checkpoint").GetRawText() + ",\"snapshot\":" + root.GetProperty("snapshot").GetRawText() + "}";
        var parsed = ParseBootstrap(baseline);
        if (SnapshotSections.Any(k => !parsed.Snapshot.TryGetProperty(k, out _)) ||
            SnapshotSections.Sum(k => parsed.Snapshot.GetProperty(k).GetArrayLength()) > 100 ||
            SnapshotSections.Count(k => parsed.Snapshot.GetProperty(k).GetArrayLength() > 0) > 1) throw Invalid();
        return (parsed.Checkpoint, parsed.Snapshot, more, cursor);
    }

    private static void ValidateArrays(JsonElement payload)
    {
        foreach (var kind in new[] { "spaces", "projects", "tasks", "scopes", "catalog", "intents", "routes" })
        {
            if (kind == "scopes" && !payload.TryGetProperty(kind, out _)) continue;
            var array = payload.GetProperty(kind);
            if (array.ValueKind != JsonValueKind.Array) throw Invalid();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in array.EnumerateArray())
            {
                ValidateRecord(kind, item);
                if (!seen.Add(Text(item, "id")!)) throw Invalid();
            }
        }
    }

    internal static void ValidateRecord(string kind, JsonElement item)
    {
        var keys = kind switch
        {
            "spaces" => "id name color icon sortOrder isArchived createdAt updatedAt deletedAt entityVersion",
            "projects" => "id spaceId name description color icon parentId sortOrder status isFavorite createdAt updatedAt deletedAt entityVersion",
            "tasks" => "id spaceId projectId parentId title completionState workflowStatus priority plannedOn plannedAt deadlineOn deadlineAt scheduledStart scheduledEnd durationMinutes recurrenceRule notes completedAt createdAt entityVersion deletedAt labels source sourceOwnerDeviceId",
            "catalog" => "id bridgeDeviceId source isSkipped available adoptedTaskId labelNames projectChoices entityVersion createdAt updatedAt",
            "intents" => "id bridgeDeviceId action catalogId spaceId route state adoptedTaskId errorCode entityVersion createdAt updatedAt",
            "routes" => "id bridgeDeviceId label spaceId moveToProjectId entityVersion createdAt updatedAt deletedAt",
            "scopes" => "id bridgeDeviceId name integrationKind available labelNames projectChoices entityVersion createdAt updatedAt",
            _ => throw Invalid(),
        };
        if (kind is "catalog" or "routes" && item.TryGetProperty("scopeId", out _)) keys += " scopeId";
        Keys(item, keys);
        foreach (var name in new[] { "id", "spaceId", "projectId", "parentId", "bridgeDeviceId", "catalogId", "adoptedTaskId", "sourceOwnerDeviceId", "moveToProjectId", "scopeId" })
            if (item.TryGetProperty(name, out _ ) && Text(item, name) is { } id) PersonalCloudK0Codec.ValidateIdentifier(id, name);
        if (item.GetProperty("entityVersion").ValueKind != JsonValueKind.Number || !item.GetProperty("entityVersion").TryGetInt64(out var version) || version <= 0) throw Invalid();
        foreach (var name in new[] { "createdAt", "updatedAt", "deletedAt", "completedAt", "plannedAt", "deadlineAt", "scheduledStart", "scheduledEnd" })
            if (item.TryGetProperty(name, out _) && Text(item, name) is { } stamp) PersonalCloudK0Codec.ValidateTimestamp(stamp, name);
        foreach (var name in new[] { "plannedOn", "deadlineOn" })
            if (item.TryGetProperty(name, out _) && Text(item, name) is { } day && !DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw Invalid();
        foreach (var name in new[] { "isArchived", "isFavorite", "isSkipped", "available" })
            if (item.TryGetProperty(name, out var flag) && flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
        if (kind is "spaces" or "projects")
        {
            Bounded(item, "name", 512, true); Bounded(item, "color", 64); Bounded(item, "icon", 128);
            if (!item.GetProperty("sortOrder").TryGetInt32(out var order) || order < 0) throw Invalid();
        }
        if (kind == "projects")
        {
            Bounded(item, "description", 4096);
            if (Text(item, "status") is not ("active" or "completed" or "archived")) throw Invalid();
        }
        if (kind == "tasks")
        {
            Bounded(item, "title", 512, true); Bounded(item, "notes", 4096); Bounded(item, "recurrenceRule", 512);
            if (Text(item, "completionState") is not ("open" or "completed") ||
                Text(item, "workflowStatus") is not ("inbox" or "next" or "waiting" or "someday" or "none") ||
                !item.GetProperty("priority").TryGetInt32(out var priority) || priority is < 1 or > 4) throw Invalid();
            if (Text(item, "plannedOn") is not null && Text(item, "plannedAt") is not null ||
                Text(item, "deadlineOn") is not null && Text(item, "deadlineAt") is not null) throw Invalid();
            var duration = item.GetProperty("durationMinutes");
            if (duration.ValueKind != JsonValueKind.Null && (!duration.TryGetInt32(out var minutes) || minutes is < 0 or > 525600)) throw Invalid();
            Labels(item.GetProperty("labels"), false);
        }
        if (item.TryGetProperty("source", out var source) && source.ValueKind != JsonValueKind.Null)
        {
            Keys(source, "integrationKind externalIdHash sourceUrl title description projectName priority plannedOn plannedAt deadlineOn deadlineAt recurrenceRule refreshedAt");
            PersonalCloudK0Codec.ValidateSafeSource(PersonalCloudK0Codec.Deserialize<K0SafeSourceDescriptor>(source.GetRawText()));
        }
        if (kind == "catalog" && item.GetProperty("source").ValueKind == JsonValueKind.Null) throw Invalid();
        if (kind is "catalog" or "scopes") { Labels(item.GetProperty("labelNames"), true); Labels(item.GetProperty("projectChoices"), false); }
        if (kind == "scopes") { Bounded(item, "name", 512, true); Bounded(item, "integrationKind", 64, true); }
        if (kind == "routes") Bounded(item, "label", 512);
        if (kind == "intents")
        {
            if (Text(item, "action") is not ("adopt" or "skip" or "restore" or "route.put" or "route.delete") ||
                Text(item, "state") is not ("pending_desktop" or "started" or "uncertain" or "confirmed" or "rejected")) throw Invalid();
            if (item.GetProperty("route").ValueKind != JsonValueKind.Null)
            {
                var route = item.GetProperty("route"); Keys(route, "id label spaceId moveToProjectId" + (route.TryGetProperty("scopeId", out _) ? " scopeId" : ""));
                if (route.TryGetProperty("scopeId", out _) && Text(route, "scopeId") is { } scopeId) PersonalCloudK0Codec.ValidateIdentifier(scopeId, "route.scopeId");
                PersonalCloudK0Codec.ValidateIdentifier(Text(route, "id")!, "route.id");
                PersonalCloudK0Codec.ValidateIdentifier(Text(route, "spaceId")!, "route.spaceId");
                Bounded(route, "label", 512);
                if (Text(route, "moveToProjectId") is { } target) PersonalCloudK0Codec.ValidateIdentifier(target, "route.moveToProjectId");
            }
        }
    }

    private static void Labels(JsonElement array, bool strings)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 32) throw Invalid();
        foreach (var value in array.EnumerateArray())
        {
            if (strings) { if (value.ValueKind != JsonValueKind.String || Encoding.UTF8.GetByteCount(value.GetString()!) > 512) throw Invalid(); }
            else { Keys(value, "id name"); PersonalCloudK0Codec.ValidateIdentifier(Text(value, "id")!, "id"); Bounded(value, "name", 512, true); }
        }
    }
    internal static void Keys(JsonElement value, string keys)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(keys.Split(' ').Order(StringComparer.Ordinal))) throw Invalid();
    }
    internal static string? Text(JsonElement value, string key)
    {
        var item = value.GetProperty(key);
        return item.ValueKind switch { JsonValueKind.Null => null, JsonValueKind.String => item.GetString(), _ => throw Invalid() };
    }
    private static void Bounded(JsonElement item, string name, int bytes, bool required = false)
    {
        var value = Text(item, name);
        if (required && string.IsNullOrWhiteSpace(value) || value is not null && Encoding.UTF8.GetByteCount(value) > bytes) throw Invalid();
    }
    internal static PersonalCloudK0ContractException Invalid() => new("invalid_request", "The workspace projection is invalid. Its checkpoint has been preserved.");
}
