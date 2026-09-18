using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.PersonalCloud;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskDeleteMutation
{
    public required int ProtocolVersion { get; init; }
    public required string Product { get; init; }
    public required string InstanceId { get; init; }
    public required string DeviceId { get; init; }
    public required string MutationId { get; init; }
    public required long DeviceSequence { get; init; }
    public required string Operation { get; init; }
    public required string EntityId { get; init; }
    public required long BaseEntityVersion { get; init; }
    public required PersonalCloudTaskDeletePayload Payload { get; init; }
    public required string ClientCreatedAt { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskDeletePayload { public required string DeletedAt { get; init; } }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskDeletedChange : IPersonalCloudTaskChange
{
    public required int ProtocolVersion { get; init; } public required string Product { get; init; }
    public required long ChangeSeq { get; init; } public required string MutationId { get; init; }
    public required string Operation { get; init; } public required string EntityKind { get; init; }
    public required string EntityId { get; init; } public required long EntityVersion { get; init; }
    public required PersonalCloudTaskDeletedPayload Payload { get; init; } public required string ServerAcceptedAt { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskDeletedPayload { public required PersonalCloudTaskDeletedRepresentation Task { get; init; } }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskDeletedRepresentation { public required string Id { get; init; } public required string DeletedAt { get; init; } }

public static class PersonalCloudDeleteCodec
{
    public static PersonalCloudTaskDeleteMutation CreateMutation(string instanceId, string deviceId, string mutationId,
        long deviceSequence, string entityId, long baseEntityVersion, string deletedAt, string clientCreatedAt)
    {
        var value = new PersonalCloudTaskDeleteMutation { ProtocolVersion=1, Product="tasks", InstanceId=instanceId,
            DeviceId=deviceId, MutationId=mutationId, DeviceSequence=deviceSequence, Operation="task.delete",
            EntityId=entityId, BaseEntityVersion=baseEntityVersion, Payload=new(){DeletedAt=deletedAt}, ClientCreatedAt=clientCreatedAt };
        ValidateMutation(value, instanceId); return value;
    }
    public static PersonalCloudTaskDeleteMutation ParseMutation(string json, string expectedInstanceId)
    { _=PersonalCloudK0Codec.Canonicalize(json); var value=PersonalCloudK0Codec.Deserialize<PersonalCloudTaskDeleteMutation>(json); ValidateMutation(value,expectedInstanceId); return value; }
    public static void ValidateMutation(PersonalCloudTaskDeleteMutation value,string expected)
    {
        if(value.ProtocolVersion!=1||value.Product!="tasks"||value.Operation!="task.delete"||value.DeviceSequence<=0||value.BaseEntityVersion<=0)
            throw Invalid("invalid_request","The task deletion is invalid.");
        PersonalCloudK0Codec.ValidateIdentifier(value.InstanceId,nameof(value.InstanceId)); PersonalCloudK0Codec.ValidateIdentifier(value.DeviceId,nameof(value.DeviceId));
        PersonalCloudK0Codec.ValidateIdentifier(value.MutationId,nameof(value.MutationId)); PersonalCloudK0Codec.ValidateIdentifier(value.EntityId,nameof(value.EntityId));
        if(value.InstanceId!=expected) throw Invalid("instance_mismatch","The mutation targets another instance.");
        PersonalCloudK0Codec.ValidateTimestamp(value.Payload.DeletedAt,"payload.deletedAt"); PersonalCloudK0Codec.ValidateTimestamp(value.ClientCreatedAt,nameof(value.ClientCreatedAt));
    }
    public static void ValidateChange(PersonalCloudTaskDeletedChange value)
    {
        var task=value.Payload?.Task;
        if(value.ProtocolVersion!=1||value.Product!="tasks"||value.Operation!="task.deleted"||value.EntityKind!="task"||value.ChangeSeq<=0||value.EntityVersion<=1||task?.Id!=value.EntityId)
            throw Invalid("invalid_request","The task deletion change is invalid.");
        PersonalCloudK0Codec.ValidateTimestamp(task.DeletedAt,"payload.task.deletedAt"); PersonalCloudK0Codec.ValidateTimestamp(value.ServerAcceptedAt,nameof(value.ServerAcceptedAt));
    }
    private static PersonalCloudK0ContractException Invalid(string code,string message)=>new(code,message);
}
