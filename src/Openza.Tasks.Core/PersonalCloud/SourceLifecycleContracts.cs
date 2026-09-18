using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.PersonalCloud;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudSourceRefreshedTaskPayload
{
    public required string Id { get; init; }
    public required K0SafeSourceDescriptor Source { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudSourceRefreshedPayload
{
    public required PersonalCloudSourceRefreshedTaskPayload Task { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudSourceRefreshedChange : IPersonalCloudTaskChange
{
    public required int ProtocolVersion { get; init; }
    public required string Product { get; init; }
    public required long ChangeSeq { get; init; }
    public required string MutationId { get; init; }
    public required string Operation { get; init; }
    public required string EntityKind { get; init; }
    public required string EntityId { get; init; }
    public required long EntityVersion { get; init; }
    public required PersonalCloudSourceRefreshedPayload Payload { get; init; }
    public required string ServerAcceptedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudExternalCommandState(string Command, string State, string? ErrorCode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudExternalCommandTaskPayload(string Id, PersonalCloudExternalCommandState ExternalCommand);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudExternalCommandPayload(PersonalCloudExternalCommandTaskPayload Task);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudExternalCommandChangedChange : IPersonalCloudTaskChange
{
    public required int ProtocolVersion { get; init; }
    public required string Product { get; init; }
    public required long ChangeSeq { get; init; }
    public required string MutationId { get; init; }
    public required string Operation { get; init; }
    public required string EntityKind { get; init; }
    public required string EntityId { get; init; }
    public required long EntityVersion { get; init; }
    public required PersonalCloudExternalCommandPayload Payload { get; init; }
    public required string ServerAcceptedAt { get; init; }
}

public static class PersonalCloudSourceLifecycleCodec
{
    public static void ValidateSourceChange(PersonalCloudSourceRefreshedChange change)
    {
        ValidateEnvelope(change, change.Payload?.Task?.Id);
        if (change.Payload?.Task?.Source is null)
            throw new PersonalCloudK0ContractException("invalid_request", "The source refresh payload is required.");
        PersonalCloudK0Codec.ValidateSafeSource(change.Payload.Task.Source);
    }

    public static void ValidateExternalCommandChange(PersonalCloudExternalCommandChangedChange change)
    {
        ValidateEnvelope(change, change.Payload?.Task?.Id);
        var command = change.Payload?.Task?.ExternalCommand;
        if (command is null || command.Command is not ("complete" or "reopen") ||
            command.State is not ("confirmed" or "rejected") ||
            (command.State == "confirmed" && command.ErrorCode is not null) ||
            (command.State == "rejected" && string.IsNullOrWhiteSpace(command.ErrorCode)))
            throw new PersonalCloudK0ContractException("invalid_request", "The external command change is invalid.");
    }

    private static void ValidateEnvelope(IPersonalCloudTaskChange change, string? payloadId)
    {
        if (change.ProtocolVersion != 1 || change.Product != "tasks" ||
            change.Operation != "task.updatedOpenzaFields" || change.EntityKind != "task" ||
            change.ChangeSeq <= 0 || change.EntityVersion <= 0 ||
            string.IsNullOrWhiteSpace(change.MutationId) || string.IsNullOrWhiteSpace(change.EntityId) ||
            payloadId != change.EntityId)
            throw new PersonalCloudK0ContractException("invalid_request", "The source lifecycle change envelope is invalid.");
    }
}
