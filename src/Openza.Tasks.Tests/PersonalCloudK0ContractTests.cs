using System.Text.Json;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudK0ContractTests
{
    private const string InstanceId = "instance_00000000000000000000000000000001";

    [Fact]
    public void Golden_mutation_matches_canonical_fixture_and_hash()
    {
        var request = ReadFixture("valid-task-create.json");
        var mutation = PersonalCloudK0Codec.ParseTaskCreateMutation(request, InstanceId);

        Assert.Equal("local_00000000000000000000000000000001", mutation.EntityId);
        Assert.Equal(ReadFixture("valid-task-create.canonical.json").Trim(), PersonalCloudK0Codec.Canonicalize(request));
        Assert.Equal(ReadFixture("valid-task-create.sha256").Trim(), PersonalCloudK0Codec.ComputeCanonicalHash(request));
    }

    [Fact]
    public void Source_linked_golden_mutation_matches_the_shared_canonical_fixture_and_hash()
    {
        var request = ReadFixture("valid-source-linked-task-create.json");
        var mutation = PersonalCloudK0Codec.ParseTaskCreateMutation(request, InstanceId);

        Assert.Equal(IntegrationIds.Todoist, mutation.Payload.Source?.IntegrationKind);
        Assert.Equal(
            ReadFixture("valid-source-linked-task-create.canonical.json").Trim(),
            PersonalCloudK0Codec.Canonicalize(request));
        Assert.Equal(
            ReadFixture("valid-source-linked-task-create.sha256").Trim(),
            PersonalCloudK0Codec.ComputeCanonicalHash(request));
    }

    [Fact]
    public void Golden_receipts_and_change_are_strictly_deserializable()
    {
        var accepted = PersonalCloudK0Codec.Deserialize<K0MutationReceipt>(ReadFixture("accepted-receipt.json"));
        var duplicate = PersonalCloudK0Codec.Deserialize<K0MutationReceipt>(ReadFixture("duplicate-receipt.json"));
        var change = PersonalCloudK0Codec.Deserialize<K0TaskCreatedChange>(ReadFixture("task-created-change.json"));

        Assert.Equal("accepted", accepted.Outcome);
        Assert.Equal(PersonalCloudK0Protocol.TasksProduct, accepted.Product);
        Assert.Equal("duplicate", duplicate.Outcome);
        Assert.Equal(accepted.ChangeSeq, duplicate.ChangeSeq);
        Assert.Equal(PersonalCloudK0Protocol.TaskCreatedOperation, change.Operation);
        Assert.Equal(accepted.EntityId, change.EntityId);
        Assert.Equal(accepted.ChangeSeq, change.ChangeSeq);
    }

    [Fact]
    public void Mapper_emits_only_the_K0_task_allowlist()
    {
        var task = new TaskItem
        {
            Id = "local_00000000000000000000000000000002",
            SpaceId = SpaceIds.Default,
            Title = "Only safe fields",
            Description = "not transported",
            Notes = "not transported",
            ProviderMetadataJson = "{\"secret\":\"provider-value\"}",
            SourceMetadataJson = "{\"secret\":\"source-value\"}",
            LocalMetadataJson = "{\"private\":\"local-value\"}",
            CreatedAt = DateTimeOffset.Parse("2026-09-13T06:30:00Z"),
        };

        var mutation = PersonalCloudK0Mapper.CreateTaskMutation(
            task,
            InstanceId,
            "device_00000000000000000000000000000001",
            "mutation_00000000000000000000000000000002",
            2,
            DateTimeOffset.Parse("2026-09-13T06:30:01Z"));
        var json = PersonalCloudK0Codec.Serialize(mutation);
        using var document = JsonDocument.Parse(json);
        var taskJson = document.RootElement.GetProperty("payload").GetProperty("task");

        Assert.Equal(
            ["id", "spaceId", "title", "completionState", "workflowStatus", "createdAt"],
            taskJson.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.DoesNotContain("provider-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("source-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("local-value", json, StringComparison.Ordinal);
        _ = PersonalCloudK0Codec.ParseTaskCreateMutation(json, InstanceId);
    }

    [Fact]
    public void Mapper_accepts_source_linked_Openza_tasks_without_provider_credentials_or_raw_identity()
    {
        var task = new TaskItem
        {
            Id = "local_00000000000000000000000000000003",
            Title = "Adopted task",
            SourceIntegrationId = IntegrationIds.Todoist,
            SourceExternalId = "provider-task-secret-id",
            SourceProviderTaskId = "provider-command-secret-id",
            SourceConnectionId = "provider-connection-secret-id",
            SourceTitle = "Original provider title",
            SourceDescription = "Safe source context",
            SourceProjectName = "Inbox",
            ProviderMetadataJson = "{\"token\":\"provider-secret\"}",
            SourceMetadataJson = "{\"snapshot\":\"source-secret\"}",
        };

        var mutation = PersonalCloudK0Mapper.CreateTaskMutation(
            task,
            InstanceId,
            "device_00000000000000000000000000000001",
            "mutation_00000000000000000000000000000003",
            3,
            DateTimeOffset.Parse("2026-09-13T06:30:01Z"));
        var json = PersonalCloudK0Codec.Serialize(mutation);

        Assert.Equal(IntegrationIds.Todoist, mutation.Payload.Source?.IntegrationKind);
        Assert.Equal(64, mutation.Payload.Source?.ExternalIdHash.Length);
        Assert.DoesNotContain("provider-task-secret-id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-command-secret-id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-connection-secret-id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("source-secret", json, StringComparison.Ordinal);
        _ = PersonalCloudK0Codec.ParseTaskCreateMutation(json, InstanceId);
    }

    [Fact]
    public void Mapper_rejects_direct_provider_task_rows()
    {
        var task = new TaskItem
        {
            Id = "todoist_00000000000000000000000000000003",
            IntegrationId = IntegrationIds.Todoist,
            ExternalId = "provider-task-1",
            Title = "Direct provider task",
        };

        var error = Assert.Throws<PersonalCloudK0ContractException>(() =>
            PersonalCloudK0Mapper.CreateTaskMutation(
                task,
                InstanceId,
                "device_00000000000000000000000000000001",
                "mutation_00000000000000000000000000000003",
                3,
                DateTimeOffset.UtcNow));

        Assert.Equal("invalid_request", error.Code);
    }

    [Fact]
    public void Mapper_rejects_task_state_outside_the_K0_boundary()
    {
        var task = new TaskItem
        {
            Id = "local_00000000000000000000000000000004",
            Title = "Already completed",
            CompletionState = TaskCompletionState.Completed,
        };

        var error = Assert.Throws<PersonalCloudK0ContractException>(() =>
            PersonalCloudK0Mapper.CreateTaskMutation(
                task,
                InstanceId,
                "device_00000000000000000000000000000001",
                "mutation_00000000000000000000000000000004",
                4,
                DateTimeOffset.UtcNow));

        Assert.Equal("invalid_request", error.Code);
    }

    [Theory]
    [InlineData("\"title\": \"Validate one task\"", "\"title\": \"Validate one task\", \"notes\": \"not allowed\"")]
    [InlineData("\"entityId\": \"local_00000000000000000000000000000001\"", "\"entityId\": \"local_different\"")]
    [InlineData("\"protocolVersion\": 1", "\"protocolVersion\": 2")]
    [InlineData("\"product\": \"tasks\"", "\"product\": \"reader\"")]
    [InlineData("\"completionState\": \"open\"", "\"completionState\": \"completed\"")]
    [InlineData("\"clientCreatedAt\": \"2026-09-13T06:30:00.000Z\"", "\"clientCreatedAt\": \"2026-09-13T06:30:00Z\"")]
    public void Invalid_mutations_fail_closed(string original, string replacement)
    {
        var json = ReadFixture("valid-task-create.json").Replace(original, replacement, StringComparison.Ordinal);

        Assert.Throws<PersonalCloudK0ContractException>(() =>
            PersonalCloudK0Codec.ParseTaskCreateMutation(json, InstanceId));
    }

    [Fact]
    public void Changed_content_changes_the_canonical_hash()
    {
        var original = ReadFixture("valid-task-create.json");
        var changed = original.Replace("Validate one task", "Changed title", StringComparison.Ordinal);

        Assert.NotEqual(
            PersonalCloudK0Codec.ComputeCanonicalHash(original),
            PersonalCloudK0Codec.ComputeCanonicalHash(changed));
    }

    [Fact]
    public void Duplicate_properties_are_rejected_before_hashing()
    {
        var duplicate = ReadFixture("valid-task-create.json").Replace(
            "\"protocolVersion\": 1,",
            "\"protocolVersion\": 1, \"protocolVersion\": 1,",
            StringComparison.Ordinal);

        var error = Assert.Throws<PersonalCloudK0ContractException>(() =>
            PersonalCloudK0Codec.ParseTaskCreateMutation(duplicate, InstanceId));

        Assert.Equal("invalid_request", error.Code);
    }

    [Fact]
    public void Title_limit_is_measured_in_strict_UTF8_bytes()
    {
        var request = ReadFixture("valid-task-create.json");
        var accepted = request.Replace("Validate one task", new string('a', 512), StringComparison.Ordinal);
        var rejected = request.Replace("Validate one task", new string('é', 257), StringComparison.Ordinal);

        _ = PersonalCloudK0Codec.ParseTaskCreateMutation(accepted, InstanceId);
        Assert.Throws<PersonalCloudK0ContractException>(() =>
            PersonalCloudK0Codec.ParseTaskCreateMutation(rejected, InstanceId));
    }

    [Fact]
    public void Missing_and_provider_metadata_fields_fail_closed()
    {
        var request = ReadFixture("valid-task-create.json");
        var missing = request.Replace("      \"spaceId\": \"space_default\",\n", "", StringComparison.Ordinal);
        var providerMetadata = request.Replace(
            "      \"title\": \"Validate one task\",",
            "      \"title\": \"Validate one task\",\n      \"providerMetadataJson\": \"{}\",",
            StringComparison.Ordinal);

        Assert.Throws<PersonalCloudK0ContractException>(() =>
            PersonalCloudK0Codec.ParseTaskCreateMutation(missing, InstanceId));
        Assert.Throws<PersonalCloudK0ContractException>(() =>
            PersonalCloudK0Codec.ParseTaskCreateMutation(providerMetadata, InstanceId));
    }

    [Fact]
    public void Instance_identifier_and_unicode_boundaries_fail_closed()
    {
        var request = ReadFixture("valid-task-create.json");
        var wrongInstance = request.Replace(InstanceId, "instance_other", StringComparison.Ordinal);
        var acceptedIdentifier = request.Replace("space_default", new string('a', 128), StringComparison.Ordinal);
        var oversizedIdentifier = request.Replace("space_default", new string('a', 129), StringComparison.Ordinal);
        var invalidUnicode = request.Replace("Validate one task", "\\uD800", StringComparison.Ordinal);

        _ = PersonalCloudK0Codec.ParseTaskCreateMutation(acceptedIdentifier, InstanceId);
        Assert.Equal(
            "instance_mismatch",
            Assert.Throws<PersonalCloudK0ContractException>(() =>
                PersonalCloudK0Codec.ParseTaskCreateMutation(wrongInstance, InstanceId)).Code);
        Assert.Throws<PersonalCloudK0ContractException>(() =>
            PersonalCloudK0Codec.ParseTaskCreateMutation(oversizedIdentifier, InstanceId));
        Assert.Throws<PersonalCloudK0ContractException>(() =>
            PersonalCloudK0Codec.ParseTaskCreateMutation(invalidUnicode, InstanceId));
    }

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PersonalCloudK0", name));
}
