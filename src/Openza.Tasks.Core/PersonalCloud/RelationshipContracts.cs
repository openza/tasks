using System.Text.Json;
using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.PersonalCloud;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskRelationshipChange : IPersonalCloudTaskChange
{
    public required int ProtocolVersion { get; init; }
    public required string Product { get; init; }
    public required long ChangeSeq { get; init; }
    public required string MutationId { get; init; }
    public required string Operation { get; init; }
    public required string EntityKind { get; init; }
    public required string EntityId { get; init; }
    public required long EntityVersion { get; init; }
    public required PersonalCloudTaskRelationshipPayload Payload { get; init; }
    public required string ServerAcceptedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskRelationshipPayload
{
    public required JsonElement Task { get; init; }
}

public static class PersonalCloudRelationshipCodec
{
    public static readonly IReadOnlySet<string> ChangeOperations = new HashSet<string>(StringComparer.Ordinal)
    {
        "task.projectSet", "task.moved", "task.labelAdded", "task.labelRemoved", "task.parentSet",
    };

    public static void ValidateChange(PersonalCloudTaskRelationshipChange change)
    {
        if (change.ProtocolVersion != 1 || change.Product != "tasks" || change.EntityKind != "task" ||
            change.ChangeSeq <= 0 || change.EntityVersion <= 1 || !ChangeOperations.Contains(change.Operation) ||
            change.Payload is null || change.Payload.Task.ValueKind != JsonValueKind.Object ||
            !change.Payload.Task.TryGetProperty("id", out var id) || id.GetString() != change.EntityId)
            throw new PersonalCloudK0ContractException("invalid_request", "The relationship change is invalid.");

        var names = change.Payload.Task.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        var expected = change.Operation switch
        {
            "task.projectSet" => new[] { "id", "project", "projectId" },
            "task.moved" => new[] { "id", "parentId", "projectId", "space", "spaceId" },
            "task.labelAdded" => new[] { "id", "label" },
            "task.labelRemoved" => new[] { "id", "labelId" },
            _ => new[] { "id", "parentId" },
        };
        if (!names.SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new PersonalCloudK0ContractException("invalid_request", "The relationship payload has unexpected fields.");
        _ = DateTimeOffset.ParseExact(change.ServerAcceptedAt, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
    }
}
