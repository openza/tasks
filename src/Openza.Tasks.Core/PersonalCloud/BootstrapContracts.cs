using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.PersonalCloud;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudBootstrap
{
    public required int ProtocolVersion { get; init; }
    public required string Product { get; init; }
    public required long Checkpoint { get; init; }
    public required PersonalCloudBootstrapSnapshot Snapshot { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudBootstrapSnapshot
{
    public required IReadOnlyList<PersonalCloudBootstrapSpace> Spaces { get; init; }
    public required IReadOnlyList<PersonalCloudBootstrapProject> Projects { get; init; }
    public required IReadOnlyList<PersonalCloudBootstrapTask> Tasks { get; init; }
    public required IReadOnlyList<PersonalCloudBootstrapLabel> Labels { get; init; }
    public required IReadOnlyList<PersonalCloudBootstrapTaskLabel> TaskLabels { get; init; }
    public required IReadOnlyList<PersonalCloudBootstrapTombstone> Tombstones { get; init; }
    public IReadOnlyList<PersonalCloudBootstrapSourceDescriptor> SourceDescriptors { get; init; } = [];
    public IReadOnlyList<PersonalCloudBootstrapExternalCommand> ExternalCommands { get; init; } = [];
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] public sealed record PersonalCloudBootstrapSpace(string Id,string Name,string? DeletedAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] public sealed record PersonalCloudBootstrapProject(string Id,string SpaceId,string Name,string? DeletedAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] public sealed record PersonalCloudBootstrapTask(string Id,string SpaceId,string? ProjectId,string? ParentId,string Title,string CompletionState,string WorkflowStatus,int Priority,string? PlannedOn,string? PlannedAt,string? Notes,string? DeadlineOn,string? CompletedAt,string CreatedAt,long EntityVersion,string? DeletedAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] public sealed record PersonalCloudBootstrapLabel(string Id,string Name,string? DeletedAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] public sealed record PersonalCloudBootstrapTaskLabel(string TaskId,string LabelId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] public sealed record PersonalCloudBootstrapTombstone(string EntityKind,string EntityId,long EntityVersion,string DeletedAt,string? PurgeAfter);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] public sealed record PersonalCloudBootstrapSourceDescriptor(string TaskId,string IntegrationKind,string ExternalIdHash,string? SourceUrl,string Title,string? Description,string? ProjectName,int? Priority,string? PlannedOn,string? PlannedAt,string? DeadlineOn,string? DeadlineAt,string? RecurrenceRule,string RefreshedAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] public sealed record PersonalCloudBootstrapExternalCommand(string TaskId,string Command,string State,long RequestedChangeSeq,string UpdatedAt,string? ErrorCode);

public static class PersonalCloudBootstrapCodec
{
    public static PersonalCloudBootstrap Parse(string json)
    {
        _=PersonalCloudK0Codec.Canonicalize(json);var value=PersonalCloudK0Codec.Deserialize<PersonalCloudBootstrap>(json);
        if(value.ProtocolVersion!=1||value.Product!="tasks"||value.Checkpoint<0||value.Snapshot is null)
            throw new PersonalCloudK0ContractException("invalid_request","The bootstrap snapshot is invalid.");
        if(value.Snapshot.Tasks.Any(task=>task.EntityVersion<=0||string.IsNullOrWhiteSpace(task.Id)||string.IsNullOrWhiteSpace(task.SpaceId))||
           value.Snapshot.Tombstones.Any(t=>t.EntityKind!="task"||t.EntityVersion<=0))
            throw new PersonalCloudK0ContractException("invalid_request","The bootstrap contains invalid entities.");
        return value;
    }
}
