using Openza.Tasks.Application.Tasks;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Desktop.ViewModels.Editing;

public sealed record TaskDetailDraft(
    string TaskId, string Title, string Notes, TaskItemStatus Status, int Priority,
    string? ProjectId, DateOnly? PlannedOn, DateOnly? DeadlineOn,
    IReadOnlyList<LabelItem> Labels, string? SourceDateMismatchAcknowledgementKey, long Version);

/// <summary>Plans the editor's explicit changes without persisting or owning async UI state.</summary>
public static class TaskDetailEditPlanner
{
    public static DateOnly? DisplayPlannedOn(TaskItem? task) => TaskDateValues.PreferredDate(task?.PlannedOn, task?.PlannedAt);
    public static DateOnly? DisplayDeadlineOn(TaskItem? task) => TaskDateValues.PreferredDate(task?.DeadlineOn, task?.DeadlineAt);

    public static UpdateTaskRequest? CreateRequest(TaskItem baseline, TaskItem current, TaskDetailDraft draft)
    {
        var completed = draft.Status == TaskItemStatus.Completed;
        var localMetadataJson = TaskSourceDateChoice.WithAcknowledgement(current.LocalMetadataJson, draft.SourceDateMismatchAcknowledgementKey);
        if (!HasChanges(baseline, draft)) return null;
        return new UpdateTaskRequest
        {
            TaskId = current.Id,
            ExpectedRevision = current.Revision,
            Title = draft.Title == baseline.Title ? default : OptionalValue<string?>.Set(draft.Title),
            Notes = NullIfEmpty(draft.Notes) == NullIfEmpty(baseline.Notes) ? default : OptionalValue<string?>.Set(NullIfEmpty(draft.Notes)),
            Status = completed || draft.Status == baseline.Status ? default : OptionalValue<TaskWorkflowStatus>.Set(draft.Status.ToWorkflowStatus()),
            Completed = completed == baseline.IsCompleted ? default : OptionalValue<bool>.Set(completed),
            Priority = draft.Priority == baseline.Priority ? default : OptionalValue<int>.Set(draft.Priority),
            Project = draft.ProjectId == baseline.ProjectId ? default : OptionalValue<string?>.Set(draft.ProjectId),
            PlannedOn = draft.PlannedOn == DisplayPlannedOn(baseline) ? default : OptionalValue<DateOnly?>.Set(draft.PlannedOn),
            DeadlineOn = draft.DeadlineOn == DisplayDeadlineOn(baseline) ? default : OptionalValue<DateOnly?>.Set(draft.DeadlineOn),
            Labels = draft.Labels.Select(x => x.Name).Order().SequenceEqual(baseline.Labels.Select(x => x.Name).Order()) ? default : OptionalValue<IReadOnlyList<string>>.Set(draft.Labels.Select(label => label.Name).ToArray()),
            LocalMetadataJson = draft.SourceDateMismatchAcknowledgementKey == TaskSourceDateChoice.ReadAcknowledgement(baseline.LocalMetadataJson) ? default : OptionalValue<string?>.Set(localMetadataJson),
        };
    }

    public static bool HasChanges(TaskItem original, TaskDetailDraft draft)
    {
        var completed = draft.Status == TaskItemStatus.Completed;
        var localMetadataJson = TaskSourceDateChoice.WithAcknowledgement(original.LocalMetadataJson, draft.SourceDateMismatchAcknowledgementKey);
        var originalLabels = original.Labels.Select(label => label.Name).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase);
        var editedLabels = draft.Labels.Select(label => label.Name).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase);
        return !string.Equals(original.Title, draft.Title, StringComparison.Ordinal)
            || !string.Equals(NullIfEmpty(original.Notes), NullIfEmpty(draft.Notes), StringComparison.Ordinal)
            || original.Status != draft.Status
            || original.IsCompleted != completed
            || original.Priority != draft.Priority
            || !string.Equals(original.ProjectId, draft.ProjectId, StringComparison.Ordinal)
            || DisplayPlannedOn(original) != draft.PlannedOn
            || DisplayDeadlineOn(original) != draft.DeadlineOn
            || !string.Equals(original.LocalMetadataJson, localMetadataJson, StringComparison.Ordinal)
            || !originalLabels.SequenceEqual(editedLabels, StringComparer.CurrentCultureIgnoreCase);
    }

    // Rebase on what the editor just submitted, never on a provider refresh that
    // could otherwise turn a second keystroke into replacement of untouched fields.
    public static TaskItem Rebase(TaskItem baseline, TaskDetailDraft draft) => baseline with
    {
        Title = draft.Title, Notes = draft.Notes, Status = draft.Status,
        Priority = draft.Priority, ProjectId = draft.ProjectId,
        PlannedOn = draft.PlannedOn == DisplayPlannedOn(baseline) ? baseline.PlannedOn : draft.PlannedOn,
        PlannedAt = draft.PlannedOn == DisplayPlannedOn(baseline) ? baseline.PlannedAt : null,
        DeadlineOn = draft.DeadlineOn == DisplayDeadlineOn(baseline) ? baseline.DeadlineOn : draft.DeadlineOn,
        DeadlineAt = draft.DeadlineOn == DisplayDeadlineOn(baseline) ? baseline.DeadlineAt : null,
        Labels = draft.Labels,
        LocalMetadataJson = TaskSourceDateChoice.WithAcknowledgement(baseline.LocalMetadataJson, draft.SourceDateMismatchAcknowledgementKey),
    };

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
