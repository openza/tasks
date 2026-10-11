using System.Text.Json;
using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.PersonalCloud;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudK3ChangePage
{
    public required int ProtocolVersion { get; init; }
    public required string Product { get; init; }
    public required IReadOnlyList<JsonElement> Changes { get; init; }
    public required long NextCheckpoint { get; init; }
}

public sealed record PersonalCloudK3ParsedChangePage
{
    public required int ProtocolVersion { get; init; }
    public required string Product { get; init; }
    public required IReadOnlyList<IPersonalCloudTaskChange> Changes { get; init; }
    public required long NextCheckpoint { get; init; }
}

public static class PersonalCloudK3Codec
{
    public static PersonalCloudK3ParsedChangePage ParseChangePage(string json)
    {
        _ = PersonalCloudK0Codec.Canonicalize(json);
        var page = PersonalCloudK0Codec.Deserialize<PersonalCloudK3ChangePage>(json);
        if (page.ProtocolVersion != PersonalCloudK0Protocol.Version)
        {
            throw new PersonalCloudK0ContractException("unsupported_protocol", "Only Personal Cloud protocol version 1 is supported by K3.");
        }
        if (!string.Equals(page.Product, PersonalCloudK0Protocol.TasksProduct, StringComparison.Ordinal))
        {
            throw new PersonalCloudK0ContractException("unsupported_product", "This change page belongs to another Openza product.");
        }
        if (page.Changes is null || page.Changes.Count > 50 || page.NextCheckpoint < 0)
        {
            throw new PersonalCloudK0ContractException("invalid_request", "The response is not a bounded K3 change page.");
        }

        var changes = new List<IPersonalCloudTaskChange>(page.Changes.Count);
        foreach (var element in page.Changes)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("operation", out var operationElement) ||
                operationElement.ValueKind != JsonValueKind.String)
            {
                throw new PersonalCloudK0ContractException("invalid_request", "A change operation is required.");
            }

            switch (operationElement.GetString())
            {
                case PersonalCloudK0Protocol.TaskCreatedOperation:
                    var created = PersonalCloudK0Codec.Deserialize<K0TaskCreatedChange>(element.GetRawText());
                    PersonalCloudK0Codec.ValidateTaskCreatedChange(created);
                    changes.Add(created);
                    break;
                case PersonalCloudK0Protocol.TaskUpdatedOpenzaFieldsOperation:
                    changes.Add(ParseOpenzaFieldsChange(element));
                    break;
                case PersonalCloudK0Protocol.TaskCompletedOperation:
                    var completed = PersonalCloudK0Codec.Deserialize<PersonalCloudTaskCompletedChange>(element.GetRawText());
                    PersonalCloudCompletionCodec.ValidateChange(completed);
                    changes.Add(completed);
                    break;
                case PersonalCloudK0Protocol.TaskReopenedOperation:
                    var reopened = PersonalCloudK0Codec.Deserialize<PersonalCloudTaskReopenedChange>(element.GetRawText());
                    PersonalCloudReopenCodec.ValidateChange(reopened);
                    changes.Add(reopened);
                    break;
                case PersonalCloudK0Protocol.TaskDeletedOperation:
                    var deleted = PersonalCloudK0Codec.Deserialize<PersonalCloudTaskDeletedChange>(element.GetRawText());
                    PersonalCloudDeleteCodec.ValidateChange(deleted);
                    changes.Add(deleted);
                    break;
                case "task.projectSet":
                case "task.moved":
                case "task.labelAdded":
                case "task.labelRemoved":
                case "task.parentSet":
                    var relationship = PersonalCloudK0Codec.Deserialize<PersonalCloudTaskRelationshipChange>(element.GetRawText());
                    PersonalCloudRelationshipCodec.ValidateChange(relationship);
                    changes.Add(relationship);
                    break;
                default:
                    throw new PersonalCloudK0ContractException("invalid_request", "The change operation is not supported.");
            }
        }
        return new PersonalCloudK3ParsedChangePage
        {
            ProtocolVersion = page.ProtocolVersion,
            Product = page.Product,
            Changes = changes,
            NextCheckpoint = page.NextCheckpoint,
        };
    }

    private static IPersonalCloudTaskChange ParseOpenzaFieldsChange(JsonElement element)
    {
        if (!element.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("task", out var task) || task.ValueKind != JsonValueKind.Object)
        {
            throw new PersonalCloudK0ContractException("invalid_request", "The Openza-fields change payload is required.");
        }
        var properties = task.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        if (properties.SequenceEqual(["id", "title"], StringComparer.Ordinal))
        {
            var updated = PersonalCloudK0Codec.Deserialize<PersonalCloudTaskTitleUpdatedChange>(element.GetRawText());
            PersonalCloudTitleUpdateCodec.ValidateChange(updated);
            return updated;
        }
        if (properties.SequenceEqual(["id", "workflowStatus"], StringComparer.Ordinal))
        {
            var updated = PersonalCloudK0Codec.Deserialize<PersonalCloudTaskWorkflowStatusUpdatedChange>(element.GetRawText());
            PersonalCloudWorkflowStatusUpdateCodec.ValidateChange(updated);
            return updated;
        }
        if (properties.SequenceEqual(["id", "priority"], StringComparer.Ordinal))
        {
            var updated = PersonalCloudK0Codec.Deserialize<PersonalCloudTaskPriorityUpdatedChange>(element.GetRawText());
            PersonalCloudPriorityUpdateCodec.ValidateChange(updated);
            return updated;
        }
        if (properties.SequenceEqual(["id", "plannedOn"], StringComparer.Ordinal))
        {
            var updated = PersonalCloudK0Codec.Deserialize<PersonalCloudTaskPlannedOnUpdatedChange>(element.GetRawText());
            PersonalCloudPlanningCodec.ValidateChange(updated);
            return updated;
        }
        if (properties.SequenceEqual(["id", "plannedAt"], StringComparer.Ordinal))
        {
            var updated = PersonalCloudK0Codec.Deserialize<PersonalCloudTaskPlannedAtUpdatedChange>(element.GetRawText());
            PersonalCloudPlanningCodec.ValidateChange(updated);
            return updated;
        }
        if (properties.SequenceEqual(["id", "notes"], StringComparer.Ordinal))
        {
            var updated = PersonalCloudK0Codec.Deserialize<PersonalCloudTaskNotesUpdatedChange>(element.GetRawText());
            PersonalCloudNotesDeadlineCodec.ValidateChange(updated);
            return updated;
        }
        if (properties.SequenceEqual(["deadlineOn", "id"], StringComparer.Ordinal))
        {
            var updated = PersonalCloudK0Codec.Deserialize<PersonalCloudTaskDeadlineOnUpdatedChange>(element.GetRawText());
            PersonalCloudNotesDeadlineCodec.ValidateChange(updated);
            return updated;
        }
        if (properties.SequenceEqual(["id", "source"], StringComparer.Ordinal))
        {
            var updated = PersonalCloudK0Codec.Deserialize<PersonalCloudSourceRefreshedChange>(element.GetRawText());
            PersonalCloudSourceLifecycleCodec.ValidateSourceChange(updated);
            return updated;
        }
        if (properties.SequenceEqual(["externalCommand", "id"], StringComparer.Ordinal))
        {
            var updated = PersonalCloudK0Codec.Deserialize<PersonalCloudExternalCommandChangedChange>(element.GetRawText());
            PersonalCloudSourceLifecycleCodec.ValidateExternalCommandChange(updated);
            return updated;
        }
        throw new PersonalCloudK0ContractException(
            "invalid_request",
            "The change must contain exactly one supported Openza field.");
    }
}
