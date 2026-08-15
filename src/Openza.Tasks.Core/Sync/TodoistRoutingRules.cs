using System.Text.Json;
using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.Sync;

public sealed record TodoistRoutingRuleDraft(
    string? Id,
    string Label,
    string SpaceId,
    string? MoveToProjectId,
    bool MatchNoLabels = false);

public sealed record TodoistRoutingRule(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("spaceId")] string SpaceId,
    [property: JsonPropertyName("postImport")] TodoistRoutingPostImport? PostImport)
{
    [JsonPropertyName("labels")]
    public IReadOnlyList<string> Labels => string.IsNullOrWhiteSpace(Label) ? [] : [Label];
}

public sealed record TodoistRoutingPostImport(
    [property: JsonPropertyName("moveToProjectId")] string MoveToProjectId);

public sealed record TodoistRoutingRuleSettings(
    [property: JsonPropertyName("labelRoutes")] IReadOnlyList<TodoistRoutingRule> LabelRoutes,
    [property: JsonPropertyName("unlabeledRoute")] TodoistRoutingRule? UnlabeledRoute)
{
    public static TodoistRoutingRuleSettings Empty { get; } = new([], null);
}

public static class TodoistRoutingRuleCodec
{
    public const string RouteId = "route_todoist_label_routing";
    public const string UnlabeledRuleId = "todoist_rule_no_labels";

    public static TodoistRoutingRuleSettings Read(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return TodoistRoutingRuleSettings.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<TodoistRoutingRuleSettings>(settingsJson) ??
                TodoistRoutingRuleSettings.Empty;
        }
        catch (JsonException)
        {
            return TodoistRoutingRuleSettings.Empty;
        }
    }

    public static string Write(TodoistRoutingRuleSettings settings) =>
        JsonSerializer.Serialize(settings);

    public static string NormalizeLabel(string value)
    {
        var label = value.Trim();
        return label.StartsWith('@') ? label[1..].Trim() : label;
    }
}
