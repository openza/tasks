using Microsoft.UI.Xaml;
using Openza.Tasks.Core.Models;
using FontWeight = Windows.UI.Text.FontWeight;
using WinUIFontWeights = Microsoft.UI.Text.FontWeights;

namespace Openza.Tasks.ViewModels;

public sealed class TaskListItemViewModel(TaskItem task, ProjectItem? project, string view = "tasks", bool isProjectView = false, int nestingLevel = 0, string subtaskProgressText = "", string matchingSubtaskText = "")
{
    public TaskItem Task { get; } = task;
    public ProjectItem? Project { get; } = project;
    public string View { get; } = view;
    public bool IsProjectView { get; } = isProjectView;
    public int NestingLevel { get; } = nestingLevel;
    public string SubtaskProgressText { get; } = subtaskProgressText;
    public string MatchingSubtaskText { get; } = matchingSubtaskText;

    public string Id => Task.Id;
    public string Title => Task.Title;
    public string Notes => Task.Notes ?? string.Empty;
    public string SourceDescription => Task.SourceDescription ?? string.Empty;
    public string ProjectName => Project?.Name ?? string.Empty;
    public string SourceText => SourceName(Task.SourceIntegrationId ?? Task.IntegrationId);
    public string PriorityText => Task.Priority switch
    {
        1 => "Urgent",
        2 => "High",
        3 => "Normal",
        _ => "Low",
    };
    public string PriorityCueText => Task.Priority switch
    {
        1 => "Urgent",
        2 => "High",
        _ => string.Empty,
    };
    public string DateText => BuildDateText();
    public string LabelText => Task.Labels.Count == 0 ? string.Empty : string.Join(", ", Task.Labels.Select(label => label.Name));
    public string LabelSummaryText
    {
        get
        {
            if (Task.Labels.Count == 0)
            {
                return string.Empty;
            }

            var visibleLabels = Task.Labels
                .OrderBy(label => label.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(2)
                .Select(label => $"@{label.Name}")
                .ToList();
            return Task.Labels.Count <= 2
                ? string.Join(", ", visibleLabels)
                : $"{string.Join(", ", visibleLabels)} +{Task.Labels.Count - 2}";
        }
    }
    public string StatusText => Task.Status switch
    {
        TaskItemStatus.Next when !string.Equals(View, "next", StringComparison.Ordinal) => "Next",
        TaskItemStatus.Waiting when !string.Equals(View, "waiting", StringComparison.Ordinal) => "Waiting",
        TaskItemStatus.Someday when !string.Equals(View, "someday", StringComparison.Ordinal) => "Someday",
        _ => string.Empty,
    };
    public string SummaryText => string.Join("  ", new[] { ProjectName, PriorityCueText, DateText, SourceText }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public IReadOnlyList<TaskMetadataPartViewModel> MetadataItems => BuildMetadataItems();
    public string MetadataText => string.Join("  \u2022  ", BuildMetadataParts());
    public string DetailsSubtaskMetadataText => string.Join("  \u2022  ", BuildDetailsSubtaskMetadataParts());
    public bool HasLabels => !string.IsNullOrWhiteSpace(LabelText);
    public Visibility StatusVisibility => string.IsNullOrWhiteSpace(StatusText) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PriorityCueVisibility => string.IsNullOrWhiteSpace(PriorityCueText) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DueVisibility => string.IsNullOrWhiteSpace(DateText) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SourceVisibility => IsProviderTask ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailsSubtaskMetadataVisibility => string.IsNullOrWhiteSpace(DetailsSubtaskMetadataText) ? Visibility.Collapsed : Visibility.Visible;
    public bool IsCompleted => Task.IsCompleted;
    public bool IsSubtask => NestingLevel > 0 || !string.IsNullOrWhiteSpace(Task.ParentId);
    public bool IsProviderTask => Task.IsProviderTask || Task.HasProviderSource;
    public bool IsOverdue => Task.DeadlineMoment is not null && !Task.IsCompleted && Task.DeadlineMoment.Value.LocalDateTime.Date < DateTimeOffset.Now.Date;
    public string CompletionAutomationName => Task.IsCompleted ? "Reopen task" : "Complete task";
    public double TitleOpacity => IsSubtask ? 0.92 : 1;
    public double RowMinHeight => IsSubtask ? 46 : 54;
    public double CheckboxSize => IsSubtask ? 22 : 24;
    public FontWeight TitleWeight => IsSubtask ? WinUIFontWeights.Normal : WinUIFontWeights.SemiBold;
    public double MetadataOpacity => IsSubtask ? 0.88 : 1;

    public static string SourceName(string integrationId) => IntegrationIds.DisplayName(integrationId);

    private IReadOnlyList<TaskMetadataPartViewModel> BuildMetadataItems()
    {
        return BuildMetadataParts()
            .Select((text, index) => new TaskMetadataPartViewModel
            {
                Text = text,
                ShowSeparator = index > 0,
            })
            .ToList();
    }

    private IEnumerable<string> BuildMetadataParts()
    {
        if (!IsProjectView)
        {
            yield return ProjectName;
        }

        if (!string.IsNullOrWhiteSpace(DateText))
        {
            yield return DateText;
        }

        if (IsProviderTask)
        {
            yield return SourceText;
        }

        if (!string.IsNullOrWhiteSpace(LabelSummaryText))
        {
            yield return LabelSummaryText;
        }

        if (!string.IsNullOrWhiteSpace(SubtaskProgressText))
        {
            yield return SubtaskProgressText;
        }

        if (!string.IsNullOrWhiteSpace(MatchingSubtaskText))
        {
            yield return MatchingSubtaskText;
        }
    }

    private IEnumerable<string> BuildDetailsSubtaskMetadataParts()
    {
        if (!string.IsNullOrWhiteSpace(DateText))
        {
            yield return DateText;
        }

        if (!string.IsNullOrWhiteSpace(PriorityCueText))
        {
            yield return PriorityCueText;
        }

        if (!string.IsNullOrWhiteSpace(LabelSummaryText))
        {
            yield return LabelSummaryText;
        }
    }

    private string BuildDateText()
    {
        var isRoutine = !string.IsNullOrWhiteSpace(Task.RecurrenceRule);
        if (isRoutine)
        {
            if (Task.PlannedMoment is { } routineDate)
            {
                return $"Repeating {FormatDate(routineDate)}";
            }

            if (Task.ScheduledStart is { } routineScheduledStart)
            {
                return $"Repeating {FormatDate(routineScheduledStart)}";
            }

            return "Repeating";
        }

        if (Task.PlannedMoment is { } plannedDate)
        {
            return FormatDate(plannedDate);
        }

        if (Task.DeadlineMoment is { } deadline)
        {
            return $"Deadline {FormatDate(deadline)}";
        }

        if (Task.ScheduledStart is { } scheduledStart)
        {
            return $"Scheduled {FormatDate(scheduledStart)}";
        }

        return string.Empty;
    }

    private static string FormatDate(DateTimeOffset dateValue)
    {
        var date = dateValue.LocalDateTime.Date;
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

        return dateValue.ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture);
    }
}
