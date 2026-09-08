namespace Openza.Tasks.Desktop.ViewModels;

public sealed record TodoistRoutingChoiceViewModel(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed class TodoistRoutingRuleViewModel
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string SpaceId { get; init; } = string.Empty;
    public string SpaceName { get; init; } = string.Empty;
    public string? MoveToProjectId { get; init; }
    public string? MoveToProjectName { get; init; }
    public bool MatchNoLabels { get; init; }

    public string LabelText => MatchNoLabels ? "No Todoist labels" : $"@{Label}";

    public string SummaryText => string.IsNullOrWhiteSpace(MoveToProjectName)
        ? $"Send to {SpaceName}"
        : $"Send to {SpaceName}, then move in Todoist to {MoveToProjectName}";
}
