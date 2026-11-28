using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.PersonalCloud;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskProviderObservedReopenMutation
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
    public required PersonalCloudTaskProviderObservedReopenPayload Payload { get; init; }
    public required string ClientCreatedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskProviderObservedReopenPayload
{
    public required string ObservedAt { get; init; }
}

public static class PersonalCloudProviderObservedReopenCodec
{
    public static PersonalCloudTaskProviderObservedReopenMutation CreateMutation(
        string instanceId, string deviceId, string mutationId, long deviceSequence, string entityId,
        long baseEntityVersion, string observedAt, string clientCreatedAt)
    {
        var mutation = new PersonalCloudTaskProviderObservedReopenMutation
        {
            ProtocolVersion = PersonalCloudK0Protocol.Version,
            Product = PersonalCloudK0Protocol.TasksProduct,
            InstanceId = instanceId,
            DeviceId = deviceId,
            MutationId = mutationId,
            DeviceSequence = deviceSequence,
            Operation = PersonalCloudK0Protocol.ProviderObservedReopenOperation,
            EntityId = entityId,
            BaseEntityVersion = baseEntityVersion,
            Payload = new PersonalCloudTaskProviderObservedReopenPayload { ObservedAt = observedAt },
            ClientCreatedAt = clientCreatedAt,
        };
        ValidateMutation(mutation, instanceId);
        return mutation;
    }

    public static PersonalCloudTaskProviderObservedReopenMutation ParseMutation(string json, string instanceId)
    {
        _ = PersonalCloudK0Codec.Canonicalize(json);
        var mutation = PersonalCloudK0Codec.Deserialize<PersonalCloudTaskProviderObservedReopenMutation>(json);
        ValidateMutation(mutation, instanceId);
        return mutation;
    }

    public static void ValidateMutation(PersonalCloudTaskProviderObservedReopenMutation mutation, string expectedInstanceId)
    {
        if (mutation.ProtocolVersion != PersonalCloudK0Protocol.Version ||
            !string.Equals(mutation.Product, PersonalCloudK0Protocol.TasksProduct, StringComparison.Ordinal))
            throw new PersonalCloudK0ContractException("unsupported_protocol", "Only Personal Cloud protocol version 1 is supported.");
        PersonalCloudK0Codec.ValidateIdentifier(mutation.InstanceId, nameof(mutation.InstanceId));
        PersonalCloudK0Codec.ValidateIdentifier(mutation.DeviceId, nameof(mutation.DeviceId));
        PersonalCloudK0Codec.ValidateIdentifier(mutation.MutationId, nameof(mutation.MutationId));
        PersonalCloudK0Codec.ValidateIdentifier(mutation.EntityId, nameof(mutation.EntityId));
        if (!string.Equals(mutation.InstanceId, expectedInstanceId, StringComparison.Ordinal) ||
            mutation.DeviceSequence <= 0 || mutation.BaseEntityVersion <= 0 || mutation.Payload is null ||
            mutation.Operation != PersonalCloudK0Protocol.ProviderObservedReopenOperation)
            throw new PersonalCloudK0ContractException("invalid_request", "The provider-observed reopen mutation is invalid.");
        PersonalCloudK0Codec.ValidateTimestamp(mutation.Payload.ObservedAt, "payload.observedAt");
        PersonalCloudK0Codec.ValidateTimestamp(mutation.ClientCreatedAt, nameof(mutation.ClientCreatedAt));
    }

}
