using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.PersonalCloud;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskReopenMutation
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
    public required PersonalCloudTaskReopenPayload Payload { get; init; }
    public required string ClientCreatedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskReopenPayload
{
    public required string ReopenedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskReopenedChange : IPersonalCloudTaskChange
{
    public required int ProtocolVersion { get; init; }
    public required string Product { get; init; }
    public required long ChangeSeq { get; init; }
    public required string MutationId { get; init; }
    public required string Operation { get; init; }
    public required string EntityKind { get; init; }
    public required string EntityId { get; init; }
    public required long EntityVersion { get; init; }
    public required PersonalCloudTaskReopenedPayload Payload { get; init; }
    public required string ServerAcceptedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskReopenedPayload
{
    public required PersonalCloudTaskReopenedRepresentation Task { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskReopenedRepresentation
{
    public required string Id { get; init; }
    public required string CompletionState { get; init; }
    public required string? CompletedAt { get; init; }
}

public static class PersonalCloudReopenCodec
{
    public static PersonalCloudTaskReopenMutation CreateMutation(
        string instanceId,
        string deviceId,
        string mutationId,
        long deviceSequence,
        string entityId,
        long baseEntityVersion,
        string reopenedAt,
        string clientCreatedAt)
    {
        var mutation = new PersonalCloudTaskReopenMutation
        {
            ProtocolVersion = PersonalCloudK0Protocol.Version,
            Product = PersonalCloudK0Protocol.TasksProduct,
            InstanceId = instanceId,
            DeviceId = deviceId,
            MutationId = mutationId,
            DeviceSequence = deviceSequence,
            Operation = PersonalCloudK0Protocol.ReopenTaskOperation,
            EntityId = entityId,
            BaseEntityVersion = baseEntityVersion,
            Payload = new PersonalCloudTaskReopenPayload { ReopenedAt = reopenedAt },
            ClientCreatedAt = clientCreatedAt,
        };
        ValidateMutation(mutation, instanceId);
        return mutation;
    }

    public static PersonalCloudTaskReopenMutation ParseMutation(string json, string expectedInstanceId)
    {
        _ = PersonalCloudK0Codec.Canonicalize(json);
        var mutation = PersonalCloudK0Codec.Deserialize<PersonalCloudTaskReopenMutation>(json);
        ValidateMutation(mutation, expectedInstanceId);
        return mutation;
    }

    public static void ValidateMutation(PersonalCloudTaskReopenMutation mutation, string expectedInstanceId)
    {
        if (mutation.ProtocolVersion != PersonalCloudK0Protocol.Version)
            throw Invalid("unsupported_protocol", "Only Personal Cloud protocol version 1 is supported.");
        if (!string.Equals(mutation.Product, PersonalCloudK0Protocol.TasksProduct, StringComparison.Ordinal))
            throw Invalid("unsupported_product", "This mutation belongs to another Openza product.");
        PersonalCloudK0Codec.ValidateIdentifier(mutation.InstanceId, nameof(mutation.InstanceId));
        PersonalCloudK0Codec.ValidateIdentifier(mutation.DeviceId, nameof(mutation.DeviceId));
        PersonalCloudK0Codec.ValidateIdentifier(mutation.MutationId, nameof(mutation.MutationId));
        PersonalCloudK0Codec.ValidateIdentifier(mutation.EntityId, nameof(mutation.EntityId));
        if (!string.Equals(mutation.InstanceId, expectedInstanceId, StringComparison.Ordinal))
            throw Invalid("instance_mismatch", "The mutation targets another Personal Cloud instance.");
        if (mutation.DeviceSequence <= 0 || mutation.BaseEntityVersion <= 0 ||
            !string.Equals(mutation.Operation, PersonalCloudK0Protocol.ReopenTaskOperation, StringComparison.Ordinal) ||
            mutation.Payload is null)
            throw Invalid("invalid_request", "The mutation is not a valid task reopening.");
        PersonalCloudK0Codec.ValidateTimestamp(mutation.Payload.ReopenedAt, "payload.reopenedAt");
        PersonalCloudK0Codec.ValidateTimestamp(mutation.ClientCreatedAt, nameof(mutation.ClientCreatedAt));
    }

    public static void ValidateChange(PersonalCloudTaskReopenedChange change)
    {
        if (change.ProtocolVersion != PersonalCloudK0Protocol.Version ||
            !string.Equals(change.Product, PersonalCloudK0Protocol.TasksProduct, StringComparison.Ordinal) ||
            change.ChangeSeq <= 0 || change.EntityVersion <= 1 ||
            !string.Equals(change.Operation, PersonalCloudK0Protocol.TaskReopenedOperation, StringComparison.Ordinal) ||
            !string.Equals(change.EntityKind, "task", StringComparison.Ordinal) ||
            change.Payload?.Task is null ||
            !string.Equals(change.Payload.Task.CompletionState, "open", StringComparison.Ordinal) ||
            change.Payload.Task.CompletedAt is not null)
            throw Invalid("invalid_request", "The change is not a task reopening.");
        PersonalCloudK0Codec.ValidateIdentifier(change.MutationId, nameof(change.MutationId));
        PersonalCloudK0Codec.ValidateIdentifier(change.EntityId, nameof(change.EntityId));
        PersonalCloudK0Codec.ValidateIdentifier(change.Payload.Task.Id, "payload.task.id");
        if (!string.Equals(change.EntityId, change.Payload.Task.Id, StringComparison.Ordinal))
            throw Invalid("invalid_request", "The change entity ID must match the task ID.");
        PersonalCloudK0Codec.ValidateTimestamp(change.ServerAcceptedAt, nameof(change.ServerAcceptedAt));
    }

    private static PersonalCloudK0ContractException Invalid(string code, string message) => new(code, message);
}
