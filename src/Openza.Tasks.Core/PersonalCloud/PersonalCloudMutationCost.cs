using System.Collections.Frozen;
using System.Text.Json;

namespace Openza.Tasks.Core.PersonalCloud;

// Mirrors dispatch-budget-v1.json. Workload credits are not D1 billing metadata.
internal static class PersonalCloudMutationCost
{
    internal const int DailyWriteCredits = 45000;
    internal const int UnknownOperationCredits = 45001;
    internal const string FanoutHeader = "x-openza-fanout-bound";
    internal static readonly System.Net.Http.HttpRequestOptionsKey<long> GuardedFanoutBound = new("Openza.GuardedFanoutBound.v1");
    internal static readonly FrozenDictionary<string, int> Weights = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["task.patch"] = 25, ["source.refreshSnapshot"] = 16,
        ["task.create"] = 50, ["task.createRich"] = 50, ["task.updateOpenzaFields"] = 50,
        ["task.complete"] = 50, ["task.reopen"] = 50, ["task.delete"] = 50,
        ["task.setProject"] = 50, ["task.move"] = 50, ["task.addLabel"] = 50, ["task.removeLabel"] = 50, ["task.setParent"] = 50,
        ["space.put"] = 50, ["project.put"] = 50, ["provider.scope.put"] = 50, ["provider.catalog.put"] = 50,
        ["provider.intent.request"] = 50, ["provider.intent.start"] = 50, ["provider.intent.resolve"] = 50,
        ["externalCommand.confirm"] = 50, ["externalCommand.reject"] = 50,
        ["project.delete"] = DailyWriteCredits, ["task.moveTree"] = DailyWriteCredits,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    internal static long Estimate(JsonElement mutation)
    {
        if (mutation.ValueKind != JsonValueKind.Object || !mutation.TryGetProperty("operation", out var operation) ||
            operation.ValueKind != JsonValueKind.String || !Weights.TryGetValue(operation.GetString()!, out var weight)) return UnknownOperationCredits;
        long cost = weight;
        var hasPayload = mutation.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object;
        if (operation.GetString() == "task.createRich")
        {
            if (!hasPayload || !payload.TryGetProperty("task", out var task) || task.ValueKind != JsonValueKind.Object ||
                !task.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array) return UnknownOperationCredits;
            cost += 10L * labels.GetArrayLength();
        }
        if (operation.GetString() is "task.create" or "task.createRich" && hasPayload &&
            payload.TryGetProperty("source", out var source) && source.ValueKind is not (JsonValueKind.Null or JsonValueKind.False)) cost += 16;
        return cost;
    }

    internal static bool IsFanout(string? operation) => operation is "project.delete" or "task.moveTree";

    internal static long ForBudget(JsonElement mutation, long dailyBudget, long? fanoutBound = null)
    {
        if (mutation.ValueKind == JsonValueKind.Object && mutation.TryGetProperty("operation", out var operation) &&
            operation.ValueKind == JsonValueKind.String && IsFanout(operation.GetString()) &&
            fanoutBound is >= 0 and <= 9007199254740991)
            return checked(100 + 12 * fanoutBound.Value);
        var cost = Estimate(mutation);
        if (cost >= UnknownOperationCredits) return Math.Max(cost, dailyBudget + 1);
        if (mutation.ValueKind == JsonValueKind.Object && mutation.TryGetProperty("operation", out operation) && IsFanout(operation.GetString()))
            return Math.Max(cost, dailyBudget); // Fan-out never silently receives a flat single-record charge.
        return cost;
    }
}
