using Openza.Tasks.Core.Models;

namespace Openza.Tasks.ViewModels;

public sealed class QuickAddViewModel
{
    public string Title { get; init; } = string.Empty;
    public string? ProjectId { get; init; }
    public TaskItemStatus Status { get; init; } = TaskItemStatus.Inbox;
    public int Priority { get; init; } = 3;
    public DateOnly? PlannedOn { get; init; }
    public string LabelsText { get; init; } = string.Empty;
    public bool OpenAfterCreate { get; init; }

    public IReadOnlyList<LabelItem> BuildLabels(IReadOnlyList<LabelItem> existingLabels, string integrationId)
    {
        return LabelsText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Select(name =>
                existingLabels.FirstOrDefault(label =>
                    string.Equals(label.Name, name, StringComparison.CurrentCultureIgnoreCase) &&
                    string.Equals(label.IntegrationId, integrationId, StringComparison.Ordinal)) ??
                existingLabels.FirstOrDefault(label =>
                    string.Equals(label.Name, name, StringComparison.CurrentCultureIgnoreCase)) ??
                new LabelItem
                {
                    Id = $"label_{Guid.NewGuid():N}",
                    IntegrationId = integrationId,
                    Name = name,
                    Color = "#808080",
                    CreatedAt = DateTimeOffset.UtcNow,
                })
            .ToList();
    }
}
