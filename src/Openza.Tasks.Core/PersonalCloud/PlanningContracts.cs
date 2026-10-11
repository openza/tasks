using System.Globalization;
using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.PersonalCloud;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskPlannedOnUpdateMutation : IPersonalCloudOpenzaFieldsMutation
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
    public required PersonalCloudTaskPlannedOnUpdatePayload Payload { get; init; }
    public required string ClientCreatedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskPlannedOnUpdatePayload { public required string? PlannedOn { get; init; } }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskPlannedAtUpdateMutation : IPersonalCloudOpenzaFieldsMutation
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
    public required PersonalCloudTaskPlannedAtUpdatePayload Payload { get; init; }
    public required string ClientCreatedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskPlannedAtUpdatePayload { public required string? PlannedAt { get; init; } }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskPlannedOnUpdatedChange : IPersonalCloudTaskChange
{
    public required int ProtocolVersion { get; init; }
    public required string Product { get; init; }
    public required long ChangeSeq { get; init; }
    public required string MutationId { get; init; }
    public required string Operation { get; init; }
    public required string EntityKind { get; init; }
    public required string EntityId { get; init; }
    public required long EntityVersion { get; init; }
    public required PersonalCloudTaskPlannedOnUpdatedPayload Payload { get; init; }
    public required string ServerAcceptedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskPlannedOnUpdatedPayload { public required PersonalCloudTaskPlannedOnRepresentation Task { get; init; } }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskPlannedOnRepresentation { public required string Id { get; init; } public required string? PlannedOn { get; init; } }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskPlannedAtUpdatedChange : IPersonalCloudTaskChange
{
    public required int ProtocolVersion { get; init; }
    public required string Product { get; init; }
    public required long ChangeSeq { get; init; }
    public required string MutationId { get; init; }
    public required string Operation { get; init; }
    public required string EntityKind { get; init; }
    public required string EntityId { get; init; }
    public required long EntityVersion { get; init; }
    public required PersonalCloudTaskPlannedAtUpdatedPayload Payload { get; init; }
    public required string ServerAcceptedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskPlannedAtUpdatedPayload { public required PersonalCloudTaskPlannedAtRepresentation Task { get; init; } }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskPlannedAtRepresentation { public required string Id { get; init; } public required string? PlannedAt { get; init; } }

public static class PersonalCloudPlanningCodec
{
    public static PersonalCloudTaskPlannedOnUpdateMutation CreatePlannedOnMutation(
        string instanceId, string deviceId, string mutationId, long deviceSequence,
        string entityId, long baseEntityVersion, DateOnly? plannedOn, string clientCreatedAt)
    {
        var mutation = new PersonalCloudTaskPlannedOnUpdateMutation
        {
            ProtocolVersion = 1, Product = "tasks", InstanceId = instanceId, DeviceId = deviceId,
            MutationId = mutationId, DeviceSequence = deviceSequence, Operation = "task.updateOpenzaFields",
            EntityId = entityId, BaseEntityVersion = baseEntityVersion,
            Payload = new() { PlannedOn = plannedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            ClientCreatedAt = clientCreatedAt,
        };
        Validate(mutation, instanceId);
        return mutation;
    }

    public static PersonalCloudTaskPlannedAtUpdateMutation CreatePlannedAtMutation(
        string instanceId, string deviceId, string mutationId, long deviceSequence,
        string entityId, long baseEntityVersion, DateTimeOffset? plannedAt, string clientCreatedAt)
    {
        var mutation = new PersonalCloudTaskPlannedAtUpdateMutation
        {
            ProtocolVersion = 1, Product = "tasks", InstanceId = instanceId, DeviceId = deviceId,
            MutationId = mutationId, DeviceSequence = deviceSequence, Operation = "task.updateOpenzaFields",
            EntityId = entityId, BaseEntityVersion = baseEntityVersion,
            Payload = new() { PlannedAt = plannedAt?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) },
            ClientCreatedAt = clientCreatedAt,
        };
        Validate(mutation, instanceId);
        return mutation;
    }

    public static PersonalCloudTaskPlannedOnUpdateMutation ParsePlannedOnMutation(string json, string expectedInstanceId)
    {
        _ = PersonalCloudK0Codec.Canonicalize(json);
        var value = PersonalCloudK0Codec.Deserialize<PersonalCloudTaskPlannedOnUpdateMutation>(json);
        Validate(value, expectedInstanceId);
        return value;
    }

    public static PersonalCloudTaskPlannedAtUpdateMutation ParsePlannedAtMutation(string json, string expectedInstanceId)
    {
        _ = PersonalCloudK0Codec.Canonicalize(json);
        var value = PersonalCloudK0Codec.Deserialize<PersonalCloudTaskPlannedAtUpdateMutation>(json);
        Validate(value, expectedInstanceId);
        return value;
    }

    public static void ValidateChange(PersonalCloudTaskPlannedOnUpdatedChange change)
    {
        var task = change.Payload?.Task;
        ValidateChangeEnvelope(change, task?.Id);
        if (task?.PlannedOn is { } value) ValidateDate(value);
    }

    public static void ValidateChange(PersonalCloudTaskPlannedAtUpdatedChange change)
    {
        var task = change.Payload?.Task;
        ValidateChangeEnvelope(change, task?.Id);
        if (task?.PlannedAt is { } value) PersonalCloudK0Codec.ValidateTimestamp(value, "payload.task.plannedAt");
    }

    private static void Validate(IPersonalCloudOpenzaFieldsMutation mutation, string expectedInstanceId)
    {
        if (mutation.ProtocolVersion != 1 || mutation.Product != "tasks" || mutation.Operation != "task.updateOpenzaFields" ||
            mutation.DeviceSequence <= 0 || mutation.BaseEntityVersion <= 0)
            throw Invalid("invalid_request", "The planning mutation envelope is invalid.");
        PersonalCloudK0Codec.ValidateIdentifier(mutation.InstanceId, nameof(mutation.InstanceId));
        PersonalCloudK0Codec.ValidateIdentifier(mutation.DeviceId, nameof(mutation.DeviceId));
        PersonalCloudK0Codec.ValidateIdentifier(mutation.MutationId, nameof(mutation.MutationId));
        PersonalCloudK0Codec.ValidateIdentifier(mutation.EntityId, nameof(mutation.EntityId));
        if (mutation.InstanceId != expectedInstanceId) throw Invalid("instance_mismatch", "The mutation targets another instance.");
        PersonalCloudK0Codec.ValidateTimestamp(mutation.ClientCreatedAt, nameof(mutation.ClientCreatedAt));
        if (mutation is PersonalCloudTaskPlannedOnUpdateMutation on && on.Payload.PlannedOn is { } date) ValidateDate(date);
        if (mutation is PersonalCloudTaskPlannedAtUpdateMutation at && at.Payload.PlannedAt is { } instant)
            PersonalCloudK0Codec.ValidateTimestamp(instant, "payload.plannedAt");
    }

    private static void ValidateChangeEnvelope(IPersonalCloudTaskChange change, string? payloadId)
    {
        if (change.ProtocolVersion != 1 || change.Product != "tasks" || change.Operation != "task.updatedOpenzaFields" ||
            change.ChangeSeq <= 0 || change.EntityVersion <= 1 || change.EntityKind != "task" || payloadId != change.EntityId)
            throw Invalid("invalid_request", "The planning change envelope is invalid.");
        PersonalCloudK0Codec.ValidateIdentifier(change.MutationId, nameof(change.MutationId));
        PersonalCloudK0Codec.ValidateIdentifier(change.EntityId, nameof(change.EntityId));
        PersonalCloudK0Codec.ValidateTimestamp(change.ServerAcceptedAt, nameof(change.ServerAcceptedAt));
    }

    private static void ValidateDate(string value)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw Invalid("invalid_request", "The planned date must be an ISO calendar date.");
    }

    private static PersonalCloudK0ContractException Invalid(string code, string message) => new(code, message);
}
