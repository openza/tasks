using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.PersonalCloud;

public static class PersonalCloudK0Protocol
{
    public const int Version = 1;
    public const string TasksProduct = "tasks";
    public const string CreateTaskOperation = "task.create";
    public const string TaskCreatedOperation = "task.created";
    public const string UpdateOpenzaFieldsOperation = "task.updateOpenzaFields";
    public const string TaskUpdatedOpenzaFieldsOperation = "task.updatedOpenzaFields";
    public const string CompleteTaskOperation = "task.complete";
    public const string TaskCompletedOperation = "task.completed";
    public const string ReopenTaskOperation = "task.reopen";
    public const string TaskReopenedOperation = "task.reopened";
    public const string DeleteTaskOperation = "task.delete";
    public const string TaskDeletedOperation = "task.deleted";
    public const int MaximumIdentifierLength = 128;
    public const int MaximumTitleUtf8Bytes = 512;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record K0TaskCreateMutation
{
    public required int ProtocolVersion { get; init; }
    public required string Product { get; init; }
    public required string InstanceId { get; init; }
    public required string DeviceId { get; init; }
    public required string MutationId { get; init; }
    public required long DeviceSequence { get; init; }
    public required string Operation { get; init; }
    public required string EntityId { get; init; }
    public required long? BaseEntityVersion { get; init; }
    public required K0TaskCreatePayload Payload { get; init; }
    public required string ClientCreatedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record K0TaskCreatePayload
{
    public required K0TaskRepresentation Task { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public K0SafeSourceDescriptor? Source { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record K0SafeSourceDescriptor
{
    public required string IntegrationKind { get; init; }
    public required string ExternalIdHash { get; init; }
    public string? SourceUrl { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string? ProjectName { get; init; }
    public int? Priority { get; init; }
    public string? PlannedOn { get; init; }
    public string? PlannedAt { get; init; }
    public string? DeadlineOn { get; init; }
    public string? DeadlineAt { get; init; }
    public string? RecurrenceRule { get; init; }
    public required string RefreshedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record K0TaskRepresentation
{
    public required string Id { get; init; }
    public required string SpaceId { get; init; }
    public required string Title { get; init; }
    public required string CompletionState { get; init; }
    public required string WorkflowStatus { get; init; }
    public required string CreatedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record K0MutationReceipt
{
    public required int ProtocolVersion { get; init; }
    public required string Product { get; init; }
    public required string MutationId { get; init; }
    public required string Outcome { get; init; }
    public required string EntityId { get; init; }
    public required long EntityVersion { get; init; }
    public required long ChangeSeq { get; init; }
    public required string ServerAcceptedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record K0TaskCreatedChange : IPersonalCloudTaskChange
{
    public required int ProtocolVersion { get; init; }
    public required string Product { get; init; }
    public required long ChangeSeq { get; init; }
    public required string MutationId { get; init; }
    public required string Operation { get; init; }
    public required string EntityKind { get; init; }
    public required string EntityId { get; init; }
    public required long EntityVersion { get; init; }
    public required K0TaskCreatePayload Payload { get; init; }
    public required string ServerAcceptedAt { get; init; }
}

public sealed class PersonalCloudK0ContractException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
    public PersonalCloudDispatchStatus? DispatchStatus { get; init; }
}

public static class PersonalCloudK0Mapper
{
    public static K0TaskCreateMutation CreateTaskMutation(
        TaskItem task,
        string instanceId,
        string deviceId,
        string mutationId,
        long deviceSequence,
        DateTimeOffset clientCreatedAt)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.IntegrationId != IntegrationIds.Local || !string.IsNullOrWhiteSpace(task.ProviderConnectionId))
        {
            throw new PersonalCloudK0ContractException(
                "invalid_request",
                "Personal Cloud accepts rows from the Openza task collection only.");
        }

        var mutation = new K0TaskCreateMutation
        {
            ProtocolVersion = PersonalCloudK0Protocol.Version,
            Product = PersonalCloudK0Protocol.TasksProduct,
            InstanceId = instanceId,
            DeviceId = deviceId,
            MutationId = mutationId,
            DeviceSequence = deviceSequence,
            Operation = PersonalCloudK0Protocol.CreateTaskOperation,
            EntityId = task.Id,
            BaseEntityVersion = null,
            Payload = new K0TaskCreatePayload
            {
                Task = new K0TaskRepresentation
                {
                    Id = task.Id,
                    SpaceId = task.SpaceId,
                    Title = task.Title,
                    CompletionState = task.CompletionState.ToStorageValue(),
                    WorkflowStatus = task.WorkflowStatus.ToStorageValue(),
                    CreatedAt = FormatTimestamp(task.CreatedAt),
                },
                Source = CreateSafeSourceDescriptor(task),
            },
            ClientCreatedAt = FormatTimestamp(clientCreatedAt),
        };

        PersonalCloudK0Codec.ValidateTaskCreateMutation(mutation, instanceId);
        return mutation;
    }

    public static K0SafeSourceDescriptor? CreateSafeSourceDescriptor(TaskItem task)
    {
        if (!task.HasProviderSource) return null;
        var identity=Encoding.UTF8.GetBytes($"{task.SourceIntegrationId}:{task.SourceExternalId}");
        return new K0SafeSourceDescriptor
        {
            IntegrationKind=task.SourceIntegrationId!, ExternalIdHash=Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant(),
            SourceUrl=task.SourceUrl, Title=task.SourceTitle ?? task.Title, Description=task.SourceDescription,
            ProjectName=task.SourceProjectName, Priority=task.SourcePriority,
            PlannedOn=task.SourcePlannedOn?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
            PlannedAt=task.SourcePlannedAt is null?null:FormatTimestamp(task.SourcePlannedAt.Value),
            DeadlineOn=task.SourceDeadlineOn?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
            DeadlineAt=task.SourceDeadlineAt is null?null:FormatTimestamp(task.SourceDeadlineAt.Value),
            RecurrenceRule=task.SourceRecurrenceRule, RefreshedAt=FormatTimestamp(task.UpdatedAt ?? task.CreatedAt),
        };
    }

    public static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

public static class PersonalCloudK0Codec
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static K0TaskCreateMutation ParseTaskCreateMutation(string json, string expectedInstanceId)
    {
        _ = Canonicalize(json);
        var mutation = Deserialize<K0TaskCreateMutation>(json);
        ValidateTaskCreateMutation(mutation, expectedInstanceId);
        return mutation;
    }

    public static K0MutationReceipt ParseMutationReceipt(string json)
    {
        _ = Canonicalize(json);
        var receipt = Deserialize<K0MutationReceipt>(json);
        ValidateMutationReceipt(receipt);
        return receipt;
    }

    public static void ValidateMutationReceipt(K0MutationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.ProtocolVersion != PersonalCloudK0Protocol.Version)
        {
            throw Invalid("unsupported_protocol", "Only Personal Cloud protocol version 1 is supported.");
        }
        if (!string.Equals(receipt.Product, PersonalCloudK0Protocol.TasksProduct, StringComparison.Ordinal))
        {
            throw Invalid("unsupported_product", "This receipt belongs to another Openza product.");
        }
        ValidateIdentifier(receipt.MutationId, nameof(receipt.MutationId));
        ValidateIdentifier(receipt.EntityId, nameof(receipt.EntityId));
        if (receipt.Outcome is not "accepted" and not "duplicate" ||
            receipt.EntityVersion <= 0 || receipt.ChangeSeq <= 0)
        {
            throw Invalid("invalid_request", "The response is not a K4 task.create receipt.");
        }
        ValidateTimestamp(receipt.ServerAcceptedAt, nameof(receipt.ServerAcceptedAt));
    }

    public static void ValidateTaskCreateMutation(K0TaskCreateMutation mutation, string expectedInstanceId)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ValidateMutation(mutation, expectedInstanceId);
    }

    public static void ValidateTaskCreatedChange(K0TaskCreatedChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.ProtocolVersion != PersonalCloudK0Protocol.Version)
        {
            throw Invalid("unsupported_protocol", "Only Personal Cloud protocol version 1 is supported.");
        }
        if (!string.Equals(change.Product, PersonalCloudK0Protocol.TasksProduct, StringComparison.Ordinal))
        {
            throw Invalid("unsupported_product", "This change belongs to another Openza product.");
        }
        if (change.ChangeSeq <= 0 ||
            !string.Equals(change.Operation, PersonalCloudK0Protocol.TaskCreatedOperation, StringComparison.Ordinal) ||
            !string.Equals(change.EntityKind, "task", StringComparison.Ordinal) ||
            change.EntityVersion != 1 ||
            change.Payload?.Task is null)
        {
            throw Invalid("invalid_request", "The change is not a K3 task.created record.");
        }

        ValidateIdentifier(change.MutationId, nameof(change.MutationId));
        ValidateIdentifier(change.EntityId, nameof(change.EntityId));
        ValidateIdentifier(change.Payload.Task.Id, "payload.task.id");
        ValidateIdentifier(change.Payload.Task.SpaceId, "payload.task.spaceId");
        if (!string.Equals(change.EntityId, change.Payload.Task.Id, StringComparison.Ordinal))
        {
            throw Invalid("invalid_request", "The change entity ID must match the task ID.");
        }
        if (string.IsNullOrWhiteSpace(change.Payload.Task.Title))
        {
            throw Invalid("invalid_request", "Task title must not be empty.");
        }
        try
        {
            if (StrictUtf8.GetByteCount(change.Payload.Task.Title) > PersonalCloudK0Protocol.MaximumTitleUtf8Bytes)
            {
                throw Invalid("invalid_request", "Task title exceeds the K3 size limit.");
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw new PersonalCloudK0ContractException("invalid_request", "Task title contains invalid Unicode.", exception);
        }
        if (!string.Equals(change.Payload.Task.CompletionState, "open", StringComparison.Ordinal) ||
            !string.Equals(change.Payload.Task.WorkflowStatus, "inbox", StringComparison.Ordinal))
        {
            throw Invalid("invalid_request", "K3 reconstructs open Inbox tasks only.");
        }
        ValidateTimestamp(change.Payload.Task.CreatedAt, "payload.task.createdAt");
        if(change.Payload.Source is not null) ValidateSafeSource(change.Payload.Source);
        ValidateTimestamp(change.ServerAcceptedAt, nameof(change.ServerAcceptedAt));
    }

    public static T Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new PersonalCloudK0ContractException("invalid_request", "The JSON body is empty.");
        }
        catch (JsonException exception)
        {
            throw new PersonalCloudK0ContractException("invalid_request", "The JSON body does not match the K0 contract.", exception);
        }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public static string Canonicalize(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            ValidateNoDuplicateProperties(document.RootElement);

            var builder = new StringBuilder(json.Length);
            AppendCanonical(builder, document.RootElement);
            return builder.ToString();
        }
        catch (JsonException exception)
        {
            throw new PersonalCloudK0ContractException("invalid_request", "The JSON body cannot be canonicalized.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new PersonalCloudK0ContractException("invalid_request", "The JSON body contains invalid Unicode.", exception);
        }
    }

    public static string ComputeCanonicalHash(string json)
    {
        var canonical = StrictUtf8.GetBytes(Canonicalize(json));
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private static void ValidateMutation(K0TaskCreateMutation mutation, string expectedInstanceId)
    {
        if (mutation.ProtocolVersion != PersonalCloudK0Protocol.Version)
        {
            throw Invalid("unsupported_protocol", "Only Personal Cloud protocol version 1 is supported by K0.");
        }

        if (!string.Equals(mutation.Product, PersonalCloudK0Protocol.TasksProduct, StringComparison.Ordinal))
        {
            throw Invalid("unsupported_product", "This client operation belongs to the Openza Tasks product.");
        }

        ValidateIdentifier(mutation.InstanceId, nameof(mutation.InstanceId));
        ValidateIdentifier(mutation.DeviceId, nameof(mutation.DeviceId));
        ValidateIdentifier(mutation.MutationId, nameof(mutation.MutationId));
        ValidateIdentifier(mutation.EntityId, nameof(mutation.EntityId));
        ValidateIdentifier(mutation.Payload.Task.Id, "payload.task.id");
        ValidateIdentifier(mutation.Payload.Task.SpaceId, "payload.task.spaceId");

        if (!string.Equals(mutation.InstanceId, expectedInstanceId, StringComparison.Ordinal))
        {
            throw Invalid("instance_mismatch", "The mutation targets another Personal Cloud instance.");
        }

        if (mutation.DeviceSequence <= 0)
        {
            throw Invalid("invalid_request", "Device sequence must be positive.");
        }

        if (!string.Equals(mutation.Operation, PersonalCloudK0Protocol.CreateTaskOperation, StringComparison.Ordinal))
        {
            throw Invalid("invalid_request", "K0 supports task.create only.");
        }

        if (mutation.BaseEntityVersion is not null)
        {
            throw Invalid("invalid_request", "A task.create mutation must not have a base entity version.");
        }

        if (!string.Equals(mutation.EntityId, mutation.Payload.Task.Id, StringComparison.Ordinal))
        {
            throw Invalid("invalid_request", "The mutation entity ID must match the task ID.");
        }

        if (string.IsNullOrWhiteSpace(mutation.Payload.Task.Title))
        {
            throw Invalid("invalid_request", "Task title must not be empty.");
        }

        try
        {
            if (StrictUtf8.GetByteCount(mutation.Payload.Task.Title) > PersonalCloudK0Protocol.MaximumTitleUtf8Bytes)
            {
                throw Invalid("invalid_request", "Task title exceeds the K0 size limit.");
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw new PersonalCloudK0ContractException("invalid_request", "Task title contains invalid Unicode.", exception);
        }

        if (!string.Equals(mutation.Payload.Task.CompletionState, "open", StringComparison.Ordinal) ||
            !string.Equals(mutation.Payload.Task.WorkflowStatus, "inbox", StringComparison.Ordinal))
        {
            throw Invalid("invalid_request", "K0 creates open Inbox tasks only.");
        }

        ValidateTimestamp(mutation.Payload.Task.CreatedAt, "payload.task.createdAt");
        if(mutation.Payload.Source is not null) ValidateSafeSource(mutation.Payload.Source);
        ValidateTimestamp(mutation.ClientCreatedAt, nameof(mutation.ClientCreatedAt));
    }

    internal static void ValidateSafeSource(K0SafeSourceDescriptor source)
    {
        if(string.IsNullOrWhiteSpace(source.IntegrationKind)||source.IntegrationKind.Length>64||
           source.ExternalIdHash.Length!=64||source.ExternalIdHash.Any(c=>!char.IsAsciiHexDigit(c)||char.IsUpper(c))||
           string.IsNullOrWhiteSpace(source.Title)||StrictUtf8.GetByteCount(source.Title)>PersonalCloudK0Protocol.MaximumTitleUtf8Bytes||source.Priority is <1 or >4)
            throw Invalid("invalid_request","The safe source descriptor is invalid.");
        ValidateTimestamp(source.RefreshedAt,"payload.source.refreshedAt");
        if(source.PlannedAt is not null)ValidateTimestamp(source.PlannedAt,"payload.source.plannedAt");
        if(source.DeadlineAt is not null)ValidateTimestamp(source.DeadlineAt,"payload.source.deadlineAt");
        if(source.PlannedOn is not null&&!DateOnly.TryParseExact(source.PlannedOn,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _))throw Invalid("invalid_request","The source planned date is invalid.");
        if(source.DeadlineOn is not null&&!DateOnly.TryParseExact(source.DeadlineOn,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _))throw Invalid("invalid_request","The source deadline date is invalid.");
    }

    internal static void ValidateIdentifier(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > PersonalCloudK0Protocol.MaximumIdentifierLength)
        {
            throw Invalid("invalid_request", $"{field} is not a valid K0 identifier.");
        }

        foreach (var character in value)
        {
            if (!(character is >= 'a' and <= 'z') &&
                !(character is >= 'A' and <= 'Z') &&
                !(character is >= '0' and <= '9') &&
                character is not '_' and not '-' and not '.' and not ':')
            {
                throw Invalid("invalid_request", $"{field} is not a valid K0 identifier.");
            }
        }
    }

    internal static void ValidateTimestamp(string value, string field)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _))
        {
            throw Invalid("invalid_request", $"{field} must be a millisecond-precision UTC timestamp.");
        }
    }

    private static void ValidateNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw Invalid("invalid_request", $"Duplicate JSON property '{property.Name}' is not allowed.");
                }

                ValidateNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateNoDuplicateProperties(item);
            }
        }
    }

    private static void AppendCanonical(StringBuilder builder, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var firstProperty = true;
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty)
                    {
                        builder.Append(',');
                    }

                    firstProperty = false;
                    AppendCanonicalString(builder, property.Name);
                    builder.Append(':');
                    AppendCanonical(builder, property.Value);
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        builder.Append(',');
                    }

                    firstItem = false;
                    AppendCanonical(builder, item);
                }
                builder.Append(']');
                break;
            case JsonValueKind.String:
                AppendCanonicalString(builder, element.GetString()!);
                break;
            case JsonValueKind.Number:
                if (!element.TryGetInt64(out var integer))
                {
                    throw Invalid("invalid_request", "K0 accepts integer JSON numbers only.");
                }
                builder.Append(integer.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            default:
                throw Invalid("invalid_request", "Unsupported JSON value.");
        }
    }

    private static void AppendCanonicalString(StringBuilder builder, string value)
    {
        builder.Append('"');
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\t': builder.Append("\\t"); break;
                case '\n': builder.Append("\\n"); break;
                case '\f': builder.Append("\\f"); break;
                case '\r': builder.Append("\\r"); break;
                default:
                    if (character < 0x20)
                    {
                        builder.Append("\\u");
                        builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(character))
                    {
                        if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                        {
                            throw Invalid("invalid_request", "JSON contains invalid Unicode.");
                        }

                        builder.Append(character);
                        builder.Append(value[++index]);
                    }
                    else if (char.IsLowSurrogate(character))
                    {
                        throw Invalid("invalid_request", "JSON contains invalid Unicode.");
                    }
                    else
                    {
                        builder.Append(character);
                    }
                    break;
            }
        }
        builder.Append('"');
    }

    private static PersonalCloudK0ContractException Invalid(string code, string message) => new(code, message);
}
