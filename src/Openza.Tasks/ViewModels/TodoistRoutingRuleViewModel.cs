namespace Openza.Tasks.ViewModels;

public sealed record TodoistRoutingChoice(string Id, string Name);

public sealed record TodoistRoutingRuleDraft(
    string? Id,
    string Label,
    string SpaceId,
    string? MoveToProjectId);

public sealed class TodoistRoutingRuleViewModel
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string SpaceId { get; set; } = string.Empty;
    public string SpaceName { get; set; } = string.Empty;
    public string? MoveToProjectId { get; set; }
    public string? MoveToProjectName { get; set; }

    public string LabelText => $"@{Label}";

    public string SummaryText => string.IsNullOrWhiteSpace(MoveToProjectName)
        ? $"Send to {SpaceName}"
        : $"Send to {SpaceName}, then move in Todoist to {MoveToProjectName}";
}
