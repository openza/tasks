using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.PersonalCloud;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudK4CheckpointAcknowledgement
{
    public required int ProtocolVersion { get; init; }
    public required string Product { get; init; }
    public required string InstanceId { get; init; }
    public required string DeviceId { get; init; }
    public required long Checkpoint { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudK4CheckpointReceipt
{
    public required int ProtocolVersion { get; init; }
    public required string Product { get; init; }
    public required string DeviceId { get; init; }
    public required long AcknowledgedCheckpoint { get; init; }
}

public static class PersonalCloudK4Codec
{
    public static PersonalCloudK4CheckpointAcknowledgement CreateCheckpointAcknowledgement(
        string instanceId,
        string deviceId,
        long checkpoint)
    {
        PersonalCloudK0Codec.ValidateIdentifier(instanceId, nameof(instanceId));
        PersonalCloudK0Codec.ValidateIdentifier(deviceId, nameof(deviceId));
        if (checkpoint < 0)
        {
            throw new PersonalCloudK0ContractException("invalid_request", "Checkpoint must be non-negative.");
        }
        return new PersonalCloudK4CheckpointAcknowledgement
        {
            ProtocolVersion = PersonalCloudK0Protocol.Version,
            Product = PersonalCloudK0Protocol.TasksProduct,
            InstanceId = instanceId,
            DeviceId = deviceId,
            Checkpoint = checkpoint,
        };
    }

    public static PersonalCloudK4CheckpointReceipt ParseCheckpointReceipt(string json, string expectedDeviceId)
    {
        _ = PersonalCloudK0Codec.Canonicalize(json);
        var receipt = PersonalCloudK0Codec.Deserialize<PersonalCloudK4CheckpointReceipt>(json);
        if (receipt.ProtocolVersion != PersonalCloudK0Protocol.Version)
        {
            throw new PersonalCloudK0ContractException("unsupported_protocol", "Only Personal Cloud protocol version 1 is supported by K4.");
        }
        if (!string.Equals(receipt.Product, PersonalCloudK0Protocol.TasksProduct, StringComparison.Ordinal))
        {
            throw new PersonalCloudK0ContractException("unsupported_product", "This checkpoint receipt belongs to another Openza product.");
        }
        PersonalCloudK0Codec.ValidateIdentifier(receipt.DeviceId, nameof(receipt.DeviceId));
        if (!string.Equals(receipt.DeviceId, expectedDeviceId, StringComparison.Ordinal) || receipt.AcknowledgedCheckpoint < 0)
        {
            throw new PersonalCloudK0ContractException("invalid_request", "The checkpoint receipt does not match this device.");
        }
        return receipt;
    }
}
