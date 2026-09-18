using System.Text;
using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.PersonalCloud;

public interface IPersonalCloudTaskChange
{
    int ProtocolVersion { get; }
    string Product { get; }
    long ChangeSeq { get; }
    string MutationId { get; }
    string Operation { get; }
    string EntityKind { get; }
    string EntityId { get; }
    long EntityVersion { get; }
    string ServerAcceptedAt { get; }
}

public interface IPersonalCloudOpenzaFieldsMutation
{
    int ProtocolVersion { get; }
    string Product { get; }
    string InstanceId { get; }
    string DeviceId { get; }
    string MutationId { get; }
    long DeviceSequence { get; }
    string Operation { get; }
    string EntityId { get; }
    long BaseEntityVersion { get; }
    string ClientCreatedAt { get; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskTitleUpdateMutation : IPersonalCloudOpenzaFieldsMutation
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
    public required PersonalCloudTaskTitleUpdatePayload Payload { get; init; }
    public required string ClientCreatedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskTitleUpdatePayload
{
    public required string Title { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskTitleUpdatedChange : IPersonalCloudTaskChange
{
    public required int ProtocolVersion { get; init; }
    public required string Product { get; init; }
    public required long ChangeSeq { get; init; }
    public required string MutationId { get; init; }
    public required string Operation { get; init; }
    public required string EntityKind { get; init; }
    public required string EntityId { get; init; }
    public required long EntityVersion { get; init; }
    public required PersonalCloudTaskTitleUpdatedPayload Payload { get; init; }
    public required string ServerAcceptedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskTitleUpdatedPayload
{
    public required PersonalCloudTaskTitleRepresentation Task { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskTitleRepresentation
{
    public required string Id { get; init; }
    public required string Title { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskWorkflowStatusUpdateMutation : IPersonalCloudOpenzaFieldsMutation
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
    public required PersonalCloudTaskWorkflowStatusUpdatePayload Payload { get; init; }
    public required string ClientCreatedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskWorkflowStatusUpdatePayload
{
    public required string WorkflowStatus { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskWorkflowStatusUpdatedChange : IPersonalCloudTaskChange
{
    public required int ProtocolVersion { get; init; }
    public required string Product { get; init; }
    public required long ChangeSeq { get; init; }
    public required string MutationId { get; init; }
    public required string Operation { get; init; }
    public required string EntityKind { get; init; }
    public required string EntityId { get; init; }
    public required long EntityVersion { get; init; }
    public required PersonalCloudTaskWorkflowStatusUpdatedPayload Payload { get; init; }
    public required string ServerAcceptedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskWorkflowStatusUpdatedPayload
{
    public required PersonalCloudTaskWorkflowStatusRepresentation Task { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskWorkflowStatusRepresentation
{
    public required string Id { get; init; }
    public required string WorkflowStatus { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskPriorityUpdateMutation : IPersonalCloudOpenzaFieldsMutation
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
    public required PersonalCloudTaskPriorityUpdatePayload Payload { get; init; }
    public required string ClientCreatedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskPriorityUpdatePayload
{
    public required int Priority { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskPriorityUpdatedChange : IPersonalCloudTaskChange
{
    public required int ProtocolVersion { get; init; }
    public required string Product { get; init; }
    public required long ChangeSeq { get; init; }
    public required string MutationId { get; init; }
    public required string Operation { get; init; }
    public required string EntityKind { get; init; }
    public required string EntityId { get; init; }
    public required long EntityVersion { get; init; }
    public required PersonalCloudTaskPriorityUpdatedPayload Payload { get; init; }
    public required string ServerAcceptedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskPriorityUpdatedPayload
{
    public required PersonalCloudTaskPriorityRepresentation Task { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudTaskPriorityRepresentation
{
    public required string Id { get; init; }
    public required int Priority { get; init; }
}

public static class PersonalCloudTitleUpdateCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static PersonalCloudTaskTitleUpdateMutation CreateMutation(
        string instanceId,
        string deviceId,
        string mutationId,
        long deviceSequence,
        string entityId,
        long baseEntityVersion,
        string title,
        string clientCreatedAt)
    {
        var mutation = new PersonalCloudTaskTitleUpdateMutation
        {
            ProtocolVersion = PersonalCloudK0Protocol.Version,
            Product = PersonalCloudK0Protocol.TasksProduct,
            InstanceId = instanceId,
            DeviceId = deviceId,
            MutationId = mutationId,
            DeviceSequence = deviceSequence,
            Operation = PersonalCloudK0Protocol.UpdateOpenzaFieldsOperation,
            EntityId = entityId,
            BaseEntityVersion = baseEntityVersion,
            Payload = new PersonalCloudTaskTitleUpdatePayload { Title = title },
            ClientCreatedAt = clientCreatedAt,
        };
        ValidateMutation(mutation, instanceId);
        return mutation;
    }

    public static PersonalCloudTaskTitleUpdateMutation ParseMutation(string json, string expectedInstanceId)
    {
        _ = PersonalCloudK0Codec.Canonicalize(json);
        var mutation = PersonalCloudK0Codec.Deserialize<PersonalCloudTaskTitleUpdateMutation>(json);
        ValidateMutation(mutation, expectedInstanceId);
        return mutation;
    }

    public static void ValidateMutation(PersonalCloudTaskTitleUpdateMutation mutation, string expectedInstanceId)
    {
        if (mutation.ProtocolVersion != PersonalCloudK0Protocol.Version)
        {
            throw Invalid("unsupported_protocol", "Only Personal Cloud protocol version 1 is supported.");
        }
        if (!string.Equals(mutation.Product, PersonalCloudK0Protocol.TasksProduct, StringComparison.Ordinal))
        {
            throw Invalid("unsupported_product", "This mutation belongs to another Openza product.");
        }
        PersonalCloudK0Codec.ValidateIdentifier(mutation.InstanceId, nameof(mutation.InstanceId));
        PersonalCloudK0Codec.ValidateIdentifier(mutation.DeviceId, nameof(mutation.DeviceId));
        PersonalCloudK0Codec.ValidateIdentifier(mutation.MutationId, nameof(mutation.MutationId));
        PersonalCloudK0Codec.ValidateIdentifier(mutation.EntityId, nameof(mutation.EntityId));
        if (!string.Equals(mutation.InstanceId, expectedInstanceId, StringComparison.Ordinal))
        {
            throw Invalid("instance_mismatch", "The mutation targets another Personal Cloud instance.");
        }
        if (mutation.DeviceSequence <= 0 || mutation.BaseEntityVersion <= 0 ||
            !string.Equals(mutation.Operation, PersonalCloudK0Protocol.UpdateOpenzaFieldsOperation, StringComparison.Ordinal))
        {
            throw Invalid("invalid_request", "The mutation is not a valid title-only update.");
        }
        ValidateTitle(mutation.Payload?.Title);
        PersonalCloudK0Codec.ValidateTimestamp(mutation.ClientCreatedAt, nameof(mutation.ClientCreatedAt));
    }

    public static void ValidateChange(PersonalCloudTaskTitleUpdatedChange change)
    {
        if (change.ProtocolVersion != 1 ||
            !string.Equals(change.Product, PersonalCloudK0Protocol.TasksProduct, StringComparison.Ordinal) ||
            change.ChangeSeq <= 0 || change.EntityVersion <= 1 ||
            !string.Equals(change.Operation, PersonalCloudK0Protocol.TaskUpdatedOpenzaFieldsOperation, StringComparison.Ordinal) ||
            !string.Equals(change.EntityKind, "task", StringComparison.Ordinal) || change.Payload?.Task is null)
        {
            throw Invalid("invalid_request", "The change is not a title-only task update.");
        }
        PersonalCloudK0Codec.ValidateIdentifier(change.MutationId, nameof(change.MutationId));
        PersonalCloudK0Codec.ValidateIdentifier(change.EntityId, nameof(change.EntityId));
        PersonalCloudK0Codec.ValidateIdentifier(change.Payload.Task.Id, "payload.task.id");
        if (!string.Equals(change.EntityId, change.Payload.Task.Id, StringComparison.Ordinal))
        {
            throw Invalid("invalid_request", "The change entity ID must match the task ID.");
        }
        ValidateTitle(change.Payload.Task.Title);
        PersonalCloudK0Codec.ValidateTimestamp(change.ServerAcceptedAt, nameof(change.ServerAcceptedAt));
    }

    private static void ValidateTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw Invalid("invalid_request", "Task title must not be empty.");
        }
        try
        {
            if (StrictUtf8.GetByteCount(title) > PersonalCloudK0Protocol.MaximumTitleUtf8Bytes)
            {
                throw Invalid("invalid_request", "Task title exceeds the checkpoint size limit.");
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw new PersonalCloudK0ContractException("invalid_request", "Task title contains invalid Unicode.", exception);
        }
    }

    private static PersonalCloudK0ContractException Invalid(string code, string message) => new(code, message);
}

public static class PersonalCloudWorkflowStatusUpdateCodec
{
    private static readonly HashSet<string> SupportedStatuses = new(StringComparer.Ordinal)
    {
        "none", "inbox", "next", "waiting", "someday",
    };

    public static PersonalCloudTaskWorkflowStatusUpdateMutation CreateMutation(
        string instanceId,
        string deviceId,
        string mutationId,
        long deviceSequence,
        string entityId,
        long baseEntityVersion,
        string workflowStatus,
        string clientCreatedAt)
    {
        var mutation = new PersonalCloudTaskWorkflowStatusUpdateMutation
        {
            ProtocolVersion = PersonalCloudK0Protocol.Version,
            Product = PersonalCloudK0Protocol.TasksProduct,
            InstanceId = instanceId,
            DeviceId = deviceId,
            MutationId = mutationId,
            DeviceSequence = deviceSequence,
            Operation = PersonalCloudK0Protocol.UpdateOpenzaFieldsOperation,
            EntityId = entityId,
            BaseEntityVersion = baseEntityVersion,
            Payload = new PersonalCloudTaskWorkflowStatusUpdatePayload { WorkflowStatus = workflowStatus },
            ClientCreatedAt = clientCreatedAt,
        };
        ValidateMutation(mutation, instanceId);
        return mutation;
    }

    public static PersonalCloudTaskWorkflowStatusUpdateMutation ParseMutation(string json, string expectedInstanceId)
    {
        _ = PersonalCloudK0Codec.Canonicalize(json);
        var mutation = PersonalCloudK0Codec.Deserialize<PersonalCloudTaskWorkflowStatusUpdateMutation>(json);
        ValidateMutation(mutation, expectedInstanceId);
        return mutation;
    }

    public static void ValidateMutation(PersonalCloudTaskWorkflowStatusUpdateMutation mutation, string expectedInstanceId)
    {
        ValidateEnvelope(mutation, expectedInstanceId);
        ValidateWorkflowStatus(mutation.Payload?.WorkflowStatus);
    }

    public static void ValidateChange(PersonalCloudTaskWorkflowStatusUpdatedChange change)
    {
        if (change.ProtocolVersion != PersonalCloudK0Protocol.Version ||
            !string.Equals(change.Product, PersonalCloudK0Protocol.TasksProduct, StringComparison.Ordinal) ||
            change.ChangeSeq <= 0 || change.EntityVersion <= 1 ||
            !string.Equals(change.Operation, PersonalCloudK0Protocol.TaskUpdatedOpenzaFieldsOperation, StringComparison.Ordinal) ||
            !string.Equals(change.EntityKind, "task", StringComparison.Ordinal) || change.Payload?.Task is null)
        {
            throw Invalid("invalid_request", "The change is not a workflow-status task update.");
        }
        PersonalCloudK0Codec.ValidateIdentifier(change.MutationId, nameof(change.MutationId));
        PersonalCloudK0Codec.ValidateIdentifier(change.EntityId, nameof(change.EntityId));
        PersonalCloudK0Codec.ValidateIdentifier(change.Payload.Task.Id, "payload.task.id");
        if (!string.Equals(change.EntityId, change.Payload.Task.Id, StringComparison.Ordinal))
        {
            throw Invalid("invalid_request", "The change entity ID must match the task ID.");
        }
        ValidateWorkflowStatus(change.Payload.Task.WorkflowStatus);
        PersonalCloudK0Codec.ValidateTimestamp(change.ServerAcceptedAt, nameof(change.ServerAcceptedAt));
    }

    private static void ValidateEnvelope(IPersonalCloudOpenzaFieldsMutation mutation, string expectedInstanceId)
    {
        if (mutation.ProtocolVersion != PersonalCloudK0Protocol.Version)
        {
            throw Invalid("unsupported_protocol", "Only Personal Cloud protocol version 1 is supported.");
        }
        if (!string.Equals(mutation.Product, PersonalCloudK0Protocol.TasksProduct, StringComparison.Ordinal))
        {
            throw Invalid("unsupported_product", "This mutation belongs to another Openza product.");
        }
        PersonalCloudK0Codec.ValidateIdentifier(mutation.InstanceId, nameof(mutation.InstanceId));
        PersonalCloudK0Codec.ValidateIdentifier(mutation.DeviceId, nameof(mutation.DeviceId));
        PersonalCloudK0Codec.ValidateIdentifier(mutation.MutationId, nameof(mutation.MutationId));
        PersonalCloudK0Codec.ValidateIdentifier(mutation.EntityId, nameof(mutation.EntityId));
        if (!string.Equals(mutation.InstanceId, expectedInstanceId, StringComparison.Ordinal))
        {
            throw Invalid("instance_mismatch", "The mutation targets another Personal Cloud instance.");
        }
        if (mutation.DeviceSequence <= 0 || mutation.BaseEntityVersion <= 0 ||
            !string.Equals(mutation.Operation, PersonalCloudK0Protocol.UpdateOpenzaFieldsOperation, StringComparison.Ordinal))
        {
            throw Invalid("invalid_request", "The mutation is not a valid Openza-fields update.");
        }
        PersonalCloudK0Codec.ValidateTimestamp(mutation.ClientCreatedAt, nameof(mutation.ClientCreatedAt));
    }

    private static void ValidateWorkflowStatus(string? workflowStatus)
    {
        if (workflowStatus is null || !SupportedStatuses.Contains(workflowStatus))
        {
            throw Invalid("invalid_request", "The workflow status is not supported by this checkpoint.");
        }
    }

    private static PersonalCloudK0ContractException Invalid(string code, string message) => new(code, message);
}

public static class PersonalCloudOpenzaFieldsUpdateCodec
{
    public static IPersonalCloudOpenzaFieldsMutation ParseMutation(string json, string expectedInstanceId)
    {
        _ = PersonalCloudK0Codec.Canonicalize(json);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("payload", out var payload) || payload.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            throw new PersonalCloudK0ContractException("invalid_request", "The Openza-fields payload is required.");
        }
        var properties = payload.EnumerateObject().ToArray();
        return properties.Length == 1 && properties[0].NameEquals("title")
            ? PersonalCloudTitleUpdateCodec.ParseMutation(json, expectedInstanceId)
            : properties.Length == 1 && properties[0].NameEquals("workflowStatus")
                ? PersonalCloudWorkflowStatusUpdateCodec.ParseMutation(json, expectedInstanceId)
                : properties.Length == 1 && properties[0].NameEquals("priority")
                    ? PersonalCloudPriorityUpdateCodec.ParseMutation(json, expectedInstanceId)
                : properties.Length == 1 && properties[0].NameEquals("plannedOn")
                    ? PersonalCloudPlanningCodec.ParsePlannedOnMutation(json, expectedInstanceId)
                : properties.Length == 1 && properties[0].NameEquals("plannedAt")
                    ? PersonalCloudPlanningCodec.ParsePlannedAtMutation(json, expectedInstanceId)
                : properties.Length == 1 && properties[0].NameEquals("notes")
                    ? PersonalCloudNotesDeadlineCodec.ParseNotesMutation(json, expectedInstanceId)
                : properties.Length == 1 && properties[0].NameEquals("deadlineOn")
                    ? PersonalCloudNotesDeadlineCodec.ParseDeadlineOnMutation(json, expectedInstanceId)
                : throw new PersonalCloudK0ContractException(
                    "invalid_request",
                    "Exactly one supported Openza field is required.");
    }
}

public static class PersonalCloudPriorityUpdateCodec
{
    public static PersonalCloudTaskPriorityUpdateMutation CreateMutation(
        string instanceId,
        string deviceId,
        string mutationId,
        long deviceSequence,
        string entityId,
        long baseEntityVersion,
        int priority,
        string clientCreatedAt)
    {
        var mutation = new PersonalCloudTaskPriorityUpdateMutation
        {
            ProtocolVersion = PersonalCloudK0Protocol.Version,
            Product = PersonalCloudK0Protocol.TasksProduct,
            InstanceId = instanceId,
            DeviceId = deviceId,
            MutationId = mutationId,
            DeviceSequence = deviceSequence,
            Operation = PersonalCloudK0Protocol.UpdateOpenzaFieldsOperation,
            EntityId = entityId,
            BaseEntityVersion = baseEntityVersion,
            Payload = new PersonalCloudTaskPriorityUpdatePayload { Priority = priority },
            ClientCreatedAt = clientCreatedAt,
        };
        ValidateMutation(mutation, instanceId);
        return mutation;
    }

    public static PersonalCloudTaskPriorityUpdateMutation ParseMutation(string json, string expectedInstanceId)
    {
        _ = PersonalCloudK0Codec.Canonicalize(json);
        var mutation = PersonalCloudK0Codec.Deserialize<PersonalCloudTaskPriorityUpdateMutation>(json);
        ValidateMutation(mutation, expectedInstanceId);
        return mutation;
    }

    public static void ValidateMutation(PersonalCloudTaskPriorityUpdateMutation mutation, string expectedInstanceId)
    {
        ValidateEnvelope(mutation, expectedInstanceId);
        ValidatePriority(mutation.Payload?.Priority ?? 0);
    }

    public static void ValidateChange(PersonalCloudTaskPriorityUpdatedChange change)
    {
        if (change.ProtocolVersion != PersonalCloudK0Protocol.Version ||
            !string.Equals(change.Product, PersonalCloudK0Protocol.TasksProduct, StringComparison.Ordinal) ||
            change.ChangeSeq <= 0 || change.EntityVersion <= 1 ||
            !string.Equals(change.Operation, PersonalCloudK0Protocol.TaskUpdatedOpenzaFieldsOperation, StringComparison.Ordinal) ||
            !string.Equals(change.EntityKind, "task", StringComparison.Ordinal) || change.Payload?.Task is null)
        {
            throw Invalid("invalid_request", "The change is not a task-priority update.");
        }
        PersonalCloudK0Codec.ValidateIdentifier(change.MutationId, nameof(change.MutationId));
        PersonalCloudK0Codec.ValidateIdentifier(change.EntityId, nameof(change.EntityId));
        PersonalCloudK0Codec.ValidateIdentifier(change.Payload.Task.Id, "payload.task.id");
        if (!string.Equals(change.EntityId, change.Payload.Task.Id, StringComparison.Ordinal))
        {
            throw Invalid("invalid_request", "The change entity ID must match the task ID.");
        }
        ValidatePriority(change.Payload.Task.Priority);
        PersonalCloudK0Codec.ValidateTimestamp(change.ServerAcceptedAt, nameof(change.ServerAcceptedAt));
    }

    private static void ValidateEnvelope(IPersonalCloudOpenzaFieldsMutation mutation, string expectedInstanceId)
    {
        if (mutation.ProtocolVersion != PersonalCloudK0Protocol.Version)
        {
            throw Invalid("unsupported_protocol", "Only Personal Cloud protocol version 1 is supported.");
        }
        if (!string.Equals(mutation.Product, PersonalCloudK0Protocol.TasksProduct, StringComparison.Ordinal))
        {
            throw Invalid("unsupported_product", "This mutation belongs to another Openza product.");
        }
        PersonalCloudK0Codec.ValidateIdentifier(mutation.InstanceId, nameof(mutation.InstanceId));
        PersonalCloudK0Codec.ValidateIdentifier(mutation.DeviceId, nameof(mutation.DeviceId));
        PersonalCloudK0Codec.ValidateIdentifier(mutation.MutationId, nameof(mutation.MutationId));
        PersonalCloudK0Codec.ValidateIdentifier(mutation.EntityId, nameof(mutation.EntityId));
        if (!string.Equals(mutation.InstanceId, expectedInstanceId, StringComparison.Ordinal))
        {
            throw Invalid("instance_mismatch", "The mutation targets another Personal Cloud instance.");
        }
        if (mutation.DeviceSequence <= 0 || mutation.BaseEntityVersion <= 0 ||
            !string.Equals(mutation.Operation, PersonalCloudK0Protocol.UpdateOpenzaFieldsOperation, StringComparison.Ordinal))
        {
            throw Invalid("invalid_request", "The mutation is not a valid Openza-fields update.");
        }
        PersonalCloudK0Codec.ValidateTimestamp(mutation.ClientCreatedAt, nameof(mutation.ClientCreatedAt));
    }

    private static void ValidatePriority(int priority)
    {
        if (priority is < 1 or > 4)
        {
            throw Invalid("invalid_request", "Priority must be an integer from 1 through 4.");
        }
    }

    private static PersonalCloudK0ContractException Invalid(string code, string message) => new(code, message);
}
