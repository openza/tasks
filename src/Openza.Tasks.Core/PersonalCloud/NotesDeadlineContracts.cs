using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.PersonalCloud;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskNotesUpdateMutation : IPersonalCloudOpenzaFieldsMutation
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
    public required PersonalCloudTaskNotesUpdatePayload Payload { get; init; }
    public required string ClientCreatedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskNotesUpdatePayload(string? Notes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskDeadlineOnUpdateMutation : IPersonalCloudOpenzaFieldsMutation
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
    public required PersonalCloudTaskDeadlineOnUpdatePayload Payload { get; init; }
    public required string ClientCreatedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskDeadlineOnUpdatePayload(string? DeadlineOn);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskNotesUpdatedChange : IPersonalCloudTaskChange
{
    public required int ProtocolVersion { get; init; }
    public required string Product { get; init; }
    public required long ChangeSeq { get; init; }
    public required string MutationId { get; init; }
    public required string Operation { get; init; }
    public required string EntityKind { get; init; }
    public required string EntityId { get; init; }
    public required long EntityVersion { get; init; }
    public required PersonalCloudTaskNotesUpdatedPayload Payload { get; init; }
    public required string ServerAcceptedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskNotesUpdatedPayload(PersonalCloudTaskNotesRepresentation Task);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskNotesRepresentation(string Id, string? Notes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskDeadlineOnUpdatedChange : IPersonalCloudTaskChange
{
    public required int ProtocolVersion { get; init; }
    public required string Product { get; init; }
    public required long ChangeSeq { get; init; }
    public required string MutationId { get; init; }
    public required string Operation { get; init; }
    public required string EntityKind { get; init; }
    public required string EntityId { get; init; }
    public required long EntityVersion { get; init; }
    public required PersonalCloudTaskDeadlineOnUpdatedPayload Payload { get; init; }
    public required string ServerAcceptedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskDeadlineOnUpdatedPayload(PersonalCloudTaskDeadlineOnRepresentation Task);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskDeadlineOnRepresentation(string Id, string? DeadlineOn);

public static class PersonalCloudNotesDeadlineCodec
{
    public static PersonalCloudTaskNotesUpdateMutation CreateNotesMutation(
        string instanceId, string deviceId, string mutationId, long deviceSequence,
        string entityId, long baseEntityVersion, string? notes, string clientCreatedAt) =>
        Validate(new PersonalCloudTaskNotesUpdateMutation
        {
            ProtocolVersion = 1, Product = "tasks", InstanceId = instanceId, DeviceId = deviceId,
            MutationId = mutationId, DeviceSequence = deviceSequence, Operation = "task.updateOpenzaFields",
            EntityId = entityId, BaseEntityVersion = baseEntityVersion,
            Payload = new PersonalCloudTaskNotesUpdatePayload(notes), ClientCreatedAt = clientCreatedAt,
        }, instanceId);

    public static PersonalCloudTaskDeadlineOnUpdateMutation CreateDeadlineOnMutation(
        string instanceId, string deviceId, string mutationId, long deviceSequence,
        string entityId, long baseEntityVersion, string? deadlineOn, string clientCreatedAt) =>
        Validate(new PersonalCloudTaskDeadlineOnUpdateMutation
        {
            ProtocolVersion = 1, Product = "tasks", InstanceId = instanceId, DeviceId = deviceId,
            MutationId = mutationId, DeviceSequence = deviceSequence, Operation = "task.updateOpenzaFields",
            EntityId = entityId, BaseEntityVersion = baseEntityVersion,
            Payload = new PersonalCloudTaskDeadlineOnUpdatePayload(deadlineOn), ClientCreatedAt = clientCreatedAt,
        }, instanceId);

    public static PersonalCloudTaskNotesUpdateMutation ParseNotesMutation(string json, string instanceId) =>
        Validate(PersonalCloudK0Codec.Deserialize<PersonalCloudTaskNotesUpdateMutation>(json), instanceId);

    public static PersonalCloudTaskDeadlineOnUpdateMutation ParseDeadlineOnMutation(string json, string instanceId) =>
        Validate(PersonalCloudK0Codec.Deserialize<PersonalCloudTaskDeadlineOnUpdateMutation>(json), instanceId);

    private static T Validate<T>(T mutation, string instanceId) where T : IPersonalCloudOpenzaFieldsMutation
    {
        if (mutation.ProtocolVersion != 1 || mutation.Product != "tasks" || mutation.Operation != "task.updateOpenzaFields" ||
            mutation.InstanceId != instanceId || mutation.DeviceSequence <= 0 || mutation.BaseEntityVersion <= 0)
            throw new PersonalCloudK0ContractException("invalid_request", "The Openza field mutation is invalid.");
        PersonalCloudK0Codec.ValidateIdentifier(mutation.InstanceId, nameof(mutation.InstanceId));
        PersonalCloudK0Codec.ValidateIdentifier(mutation.DeviceId, nameof(mutation.DeviceId));
        PersonalCloudK0Codec.ValidateIdentifier(mutation.MutationId, nameof(mutation.MutationId));
        PersonalCloudK0Codec.ValidateIdentifier(mutation.EntityId, nameof(mutation.EntityId));
        PersonalCloudK0Codec.ValidateTimestamp(mutation.ClientCreatedAt, nameof(mutation.ClientCreatedAt));
        if (mutation is PersonalCloudTaskDeadlineOnUpdateMutation deadline && deadline.Payload.DeadlineOn is { } value &&
            !DateOnly.TryParseExact(value, "yyyy-MM-dd", out _))
            throw new PersonalCloudK0ContractException("invalid_request", "The deadline date is invalid.");
        return mutation;
    }

    public static void ValidateChange(PersonalCloudTaskNotesUpdatedChange change) => ValidateChangeEnvelope(
        change, change.Payload.Task.Id, change.ServerAcceptedAt);

    public static void ValidateChange(PersonalCloudTaskDeadlineOnUpdatedChange change)
    {
        ValidateChangeEnvelope(change, change.Payload.Task.Id, change.ServerAcceptedAt);
        if (change.Payload.Task.DeadlineOn is { } value && !DateOnly.TryParseExact(value, "yyyy-MM-dd", out _))
            throw new PersonalCloudK0ContractException("invalid_request", "The deadline date is invalid.");
    }

    private static void ValidateChangeEnvelope(IPersonalCloudTaskChange change, string payloadId, string timestamp)
    {
        if (change.ProtocolVersion != 1 || change.Product != "tasks" || change.ChangeSeq <= 0 ||
            change.Operation != "task.updatedOpenzaFields" || change.EntityKind != "task" ||
            change.EntityVersion <= 1 || change.EntityId != payloadId)
            throw new PersonalCloudK0ContractException("invalid_request", "The Openza field change is invalid.");
        PersonalCloudK0Codec.ValidateIdentifier(change.MutationId, nameof(change.MutationId));
        PersonalCloudK0Codec.ValidateIdentifier(change.EntityId, nameof(change.EntityId));
        PersonalCloudK0Codec.ValidateTimestamp(timestamp, nameof(timestamp));
    }
}
