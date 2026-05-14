namespace Openza.Tasks.Core.Models;

public sealed record ProviderSourceItem
{
    public string Id { get; init; } = string.Empty;
    public string IntegrationId { get; init; } = string.Empty;
    public string ProviderConnectionId { get; init; } = string.Empty;
    public string ExternalId { get; init; } = string.Empty;
    public string ProviderTaskId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? SourceProjectId { get; init; }
    public string? SourceProjectName { get; init; }
    public string? SuggestedSpaceId { get; init; }
    public int Priority { get; init; } = 2;
    public TaskCompletionState CompletionState { get; init; } = TaskCompletionState.Open;
    public DateOnly? PlannedOn { get; init; }
    public DateTimeOffset? PlannedAt { get; init; }
    public DateOnly? DeadlineOn { get; init; }
    public DateTimeOffset? DeadlineAt { get; init; }
    public string? RecurrenceRule { get; init; }
    public string? SourceUrl { get; init; }
    public string? SnapshotJson { get; init; }
    public string AdoptionState { get; init; } = ProviderSourceAdoptionStates.NotAdopted;
    public string? AdoptedTaskId { get; init; }
    public DateTimeOffset FirstSeenAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }

    public string SourceName => IntegrationIds.DisplayName(IntegrationId);

    public string SourceText => string.IsNullOrWhiteSpace(SourceProjectName)
        ? SourceName
        : $"{SourceName} \u2022 {SourceProjectName}";

    public string IntakeMetadataText => string.Join("  \u2022  ", IntakeMetadataParts());

    public bool IsSkipped => AdoptionState == ProviderSourceAdoptionStates.Ignored;

    public string IntakeActionText => IsSkipped ? "Unskip" : "Add";

    public bool CanSkip => !IsSkipped;

    public string SecondaryIntakeActionText => IsSkipped ? "Skipped" : "Skip";

    private IEnumerable<string> IntakeMetadataParts()
    {
        if (!string.IsNullOrWhiteSpace(SourceProjectName))
        {
            yield return SourceProjectName;
        }

        if (PlannedOn is not null || PlannedAt is not null)
        {
            yield return FormatDate(TaskDateValues.PreferredMoment(PlannedOn, PlannedAt));
        }

        if (DeadlineOn is not null || DeadlineAt is not null)
        {
            yield return $"Deadline {FormatDate(TaskDateValues.PreferredMoment(DeadlineOn, DeadlineAt))}";
        }

        var priority = Priority switch
        {
            1 => "Urgent",
            2 => "High",
            3 => "Normal",
            _ => "Low",
        };
        yield return priority;
    }

    private static string FormatDate(DateTimeOffset? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var date = value.Value.LocalDateTime.Date;
        var today = DateTimeOffset.Now.Date;
        if (date == today)
        {
            return "Today";
        }

        if (date == today.AddDays(1))
        {
            return "Tomorrow";
        }

        if (date < today)
        {
            var days = (today - date).Days;
            return days == 1 ? "1 day ago" : $"{days} days ago";
        }

        return value.Value.ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture);
    }
}

public static class ProviderSourceAdoptionStates
{
    public const string NotAdopted = "not_adopted";
    public const string Adopted = "adopted";
    public const string Ignored = "ignored";
}
