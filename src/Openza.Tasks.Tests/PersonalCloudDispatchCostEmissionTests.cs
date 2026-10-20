using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

internal static class DispatchCostAssertions
{
    public static void Known(PersonalCloudK1OutboxEntry entry)
    {
        Assert.True(PersonalCloudMutationCost.Weights.ContainsKey(entry.Operation), $"Missing weight for emitted {entry.Operation}");
        using var json = JsonDocument.Parse(entry.CanonicalJson);
        Assert.InRange(PersonalCloudMutationCost.Estimate(json.RootElement), 1, PersonalCloudMutationCost.DailyWriteCredits);
    }
}

public sealed class PersonalCloudDispatchCostEmissionTests : IDisposable
{
    private const string Instance = "instance_cost_synthetic", Device = "device_cost_synthetic";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-emission-cost", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Actual_ordinary_authoring_emits_weighted_immutable_envelopes_for_every_relationship_and_lifecycle_action()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        var observed = new HashSet<string>(StringComparer.Ordinal);
        async Task Ack()
        {
            foreach (var entry in await store.GetPendingPersonalCloudOutboxAsync(Instance, Device))
            {
                DispatchCostAssertions.Known(entry);
                observed.Add(entry.Operation);
                using var json = JsonDocument.Parse(entry.CanonicalJson);
                var basis = json.RootElement.GetProperty("baseEntityVersion");
                await store.AcknowledgePersonalCloudMutationAsync(Instance, Device, PersonalCloudK0Codec.Serialize(new K0MutationReceipt
                {
                    ProtocolVersion = 1, Product = "tasks", MutationId = entry.MutationId, EntityId = entry.EntityId,
                    Outcome = "accepted", EntityVersion = basis.ValueKind == JsonValueKind.Null ? 1 : basis.GetInt64() + 1,
                    ChangeSeq = entry.DeviceSequence, ServerAcceptedAt = "2026-10-19T12:00:00.000Z",
                }));
            }
        }
        const string id = "local_cost_synthetic";
        await store.CreateTaskWithPersonalCloudIntentAsync(new() { Id = id, Title = "Synthetic", SpaceId = SpaceIds.Default }, Instance, Device);
        await Ack();
        await store.UpdateTaskTitleWithPersonalCloudIntentAsync(id, "Updated", 1, Instance, Device);
        await Ack();
        await store.UpsertSpaceAsync(new() { Id = "space_cost", Name = "Synthetic" });
        await store.UpsertProjectAsync(new() { Id = "project_cost", Name = "Synthetic", SpaceId = SpaceIds.Default });
        long version = 2;
        foreach (var (operation, relatedId, relatedName) in new (string, string?, string?)[] {
            ("task.setProject", "project_cost", "Synthetic"), ("task.addLabel", "label_cost", "Synthetic"),
            ("task.removeLabel", "label_cost", null), ("task.setParent", null, null), ("task.move", "space_cost", "Synthetic"),
        })
        {
            await store.MutateTaskRelationshipWithPersonalCloudIntentAsync(id, operation, relatedId, relatedName, version++, Instance, Device);
            await Ack();
        }
        await store.CompleteTaskWithPersonalCloudIntentAsync(id, DateTimeOffset.UtcNow, version++, Instance, Device); await Ack();
        await store.ReopenTaskWithPersonalCloudIntentAsync(id, DateTimeOffset.UtcNow, version++, Instance, Device); await Ack();
        await store.DeleteTaskWithPersonalCloudIntentAsync(id, DateTimeOffset.UtcNow, version, Instance, Device); await Ack();
        Assert.Equal(new[] { "task.addLabel", "task.complete", "task.create", "task.delete", "task.move", "task.removeLabel", "task.reopen", "task.setParent", "task.setProject", "task.updateOpenzaFields" }, observed.Order(StringComparer.Ordinal));
        Assert.Empty(await store.GetPendingPersonalCloudOutboxAsync(Instance, Device));
    }

    [Fact]
    public void Compiled_desktop_store_operation_literals_and_outbound_constants_all_have_weights()
    {
        // Inspect the actual compiled authoring/store code, including async state
        // machines, so adding an emitted operation cannot silently miss the table.
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(code => unchecked((ushort)code.Value));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in new[] { typeof(SqliteTaskStore) }.Concat(typeof(SqliteTaskStore).GetNestedTypes(BindingFlags.NonPublic)))
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var bytes = method.GetMethodBody()?.GetILAsByteArray();
            if (bytes is null) continue;
            for (var offset = 0; offset < bytes.Length;)
            {
                ushort value = bytes[offset++];
                if (value == 0xfe) value = (ushort)(0xfe00 | bytes[offset++]);
                var code = codes[value];
                if (code == OpCodes.Ldstr)
                {
                    var text = method.Module.ResolveString(BitConverter.ToInt32(bytes, offset));
                    if (Regex.IsMatch(text, @"^(task|space|project|provider|source|externalCommand)(\.[A-Za-z]+)+$")) names.Add(text);
                }
                offset += code.OperandType switch {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2, OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, offset), _ => 4,
                };
            }
        }
        names.ExceptWith(["task.created", "task.updatedOpenzaFields", "task.completed", "task.reopened", "task.deleted",
            "task.projectSet", "task.moved", "task.labelAdded", "task.labelRemoved"]); // Incoming change operations.
        Assert.Contains("project.delete", names); Assert.Contains("provider.catalog.put", names);
        Assert.Contains("externalCommand.confirm", names); Assert.Contains("task.create", names);
        Assert.All(names, operation => Assert.True(PersonalCloudMutationCost.Weights.ContainsKey(operation), $"Missing cost for {operation}"));
    }

    [Theory]
    [InlineData("fanout-budget-v1.json", "a696ad137ba19a235d8ed22e256ec9f792a2d245f622b23906003ead0aee1971")]
    [InlineData("dispatch-budget-v1.json", "2b63f6281a83a5477a9cce5750def63522007c2e05efd467f98d98dba7630f2d")]
    [InlineData("catchup-status-v1.json", "a7e244e35417fd86f13667a6492ce728db86a497f8aaeb57c7f2a80f79573d2c")]
    public void Paired_companion_cost_fixtures_are_exact(string name, string expected)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PersonalCloudQuota", name);
        Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        Assert.StartsWith(expected + "  ", File.ReadAllText(Path.ChangeExtension(path, ".sha256")));
    }

    public void Dispose() => TestDirectory.Delete(_directory);
}
