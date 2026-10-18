using System.Text.Json;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Desktop.ViewModels.Editing;

namespace Openza.Tasks.Tests;

public sealed class TaskDetailEditPlannerTests
{
    private static TaskItem Task() => new()
    {
        Id = "task-test", Title = "Task", Notes = "Notes", Revision = 2, Priority = 2,
        ProjectId = "project-test", PlannedOn = new(2026, 6, 17), DeadlineOn = new(2026, 6, 20),
        Labels = [new() { Id = "label-test", Name = "Example" }],
    };
    private static TaskDetailDraft Draft(TaskItem task) => new(task.Id, task.Title, task.Notes ?? "", task.Status,
        task.Priority, task.ProjectId, TaskDetailEditPlanner.DisplayPlannedOn(task), TaskDetailEditPlanner.DisplayDeadlineOn(task), task.Labels,
        TaskSourceDateChoice.ReadAcknowledgement(task.LocalMetadataJson), 1);

    [Fact]
    public void Unchanged_editor_does_not_replace_concurrent_provider_or_planning_fields()
    {
        var baseline = Task();
        var current = baseline with { Title = "Provider refresh", Revision = 3, PlannedOn = new(2026, 7, 1), SourceDescription = "Updated externally" };
        Assert.False(TaskDetailEditPlanner.HasChanges(baseline, Draft(baseline)));
        Assert.Null(TaskDetailEditPlanner.CreateRequest(baseline, current, Draft(baseline)));
    }

    [Fact]
    public void Selective_request_uses_current_revision_and_preserves_unspecified_fields()
    {
        var baseline = Task();
        var current = baseline with { Revision = 5, Priority = 1, LocalMetadataJson = "{\"external\":true}" };
        var request = TaskDetailEditPlanner.CreateRequest(baseline, current, Draft(baseline) with { Title = "Edited" })!;
        Assert.Equal(5, request.ExpectedRevision);
        Assert.Equal("Edited", request.Title.Value);
        Assert.False(request.Priority.IsSpecified);
        Assert.False(request.PlannedOn.IsSpecified);
        Assert.False(request.DeadlineOn.IsSpecified);
        Assert.False(request.LocalMetadataJson.IsSpecified);
        Assert.False(request.Project.IsSpecified);
    }

    [Fact]
    public void Explicit_clearing_and_completion_are_distinct_from_omission_and_workflow_replacement()
    {
        var baseline = Task();
        var request = TaskDetailEditPlanner.CreateRequest(baseline, baseline, Draft(baseline) with
        { Notes = "  ", ProjectId = null, PlannedOn = null, DeadlineOn = null, Labels = [], Status = TaskItemStatus.Completed })!;
        Assert.True(request.Notes.IsSpecified); Assert.Null(request.Notes.Value);
        Assert.True(request.Project.IsSpecified); Assert.Null(request.Project.Value);
        Assert.True(request.PlannedOn.IsSpecified); Assert.Null(request.PlannedOn.Value);
        Assert.True(request.DeadlineOn.IsSpecified); Assert.Null(request.DeadlineOn.Value);
        Assert.True(request.Labels.IsSpecified); Assert.Empty(request.Labels.Value!);
        Assert.True(request.Completed.Value);
        Assert.False(request.Status.IsSpecified);
    }

    [Fact]
    public void Rebase_tracks_only_submitted_editor_values_so_later_edit_does_not_replay_stale_fields()
    {
        var baseline = Task();
        var first = Draft(baseline) with { Title = "First edit" };
        var rebased = TaskDetailEditPlanner.Rebase(baseline, first);
        var concurrent = baseline with { Title = "First edit", Priority = 4, PlannedOn = new(2026, 7, 1), Revision = 8 };
        var second = first with { Notes = "Second edit", Version = 2 };
        var request = TaskDetailEditPlanner.CreateRequest(rebased, concurrent, second)!;
        Assert.Equal(8, request.ExpectedRevision);
        Assert.False(request.Title.IsSpecified);
        Assert.False(request.Priority.IsSpecified);
        Assert.False(request.PlannedOn.IsSpecified);
        Assert.Equal("Second edit", request.Notes.Value);
    }

    [Fact]
    public void Exact_only_dates_display_local_day_without_dirty_state_or_planning_request()
    {
        var planned = DateTimeOffset.Parse("2026-06-17T00:15:00+14:00");
        var deadline = DateTimeOffset.Parse("2026-06-20T23:45:00-12:00");
        var baseline = Task() with { PlannedOn = null, DeadlineOn = null, PlannedAt = planned, DeadlineAt = deadline };
        var draft = Draft(baseline);
        Assert.Equal(TaskDateValues.FromDateTimeOffset(planned), draft.PlannedOn);
        Assert.Equal(TaskDateValues.FromDateTimeOffset(deadline), draft.DeadlineOn);
        Assert.False(TaskDetailEditPlanner.HasChanges(baseline, draft));
        Assert.Null(TaskDetailEditPlanner.CreateRequest(baseline, baseline, draft));
        var titleEdit = draft with { Title = "Changed title" };
        var request = TaskDetailEditPlanner.CreateRequest(baseline, baseline, titleEdit)!;
        Assert.False(request.PlannedOn.IsSpecified); Assert.False(request.DeadlineOn.IsSpecified);
        var rebased = TaskDetailEditPlanner.Rebase(baseline, titleEdit);
        Assert.Null(rebased.PlannedOn); Assert.Null(rebased.DeadlineOn);
        Assert.Equal(planned, rebased.PlannedAt); Assert.Equal(deadline, rebased.DeadlineAt);
        Assert.Null(TaskDetailEditPlanner.CreateRequest(rebased, rebased, titleEdit));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exact_only_explicit_clear_or_replacement_cannot_reappear_after_rebase(bool replace)
    {
        var baseline = Task() with
        {
            PlannedOn = null, DeadlineOn = null, PlannedAt = DateTimeOffset.Parse("2026-06-17T12:00:00Z"),
            DeadlineAt = DateTimeOffset.Parse("2026-06-20T12:00:00Z"),
        };
        DateOnly? selected = replace ? new DateOnly(2026, 7, 1) : null;
        var first = Draft(baseline) with { PlannedOn = selected, DeadlineOn = selected };
        var request = TaskDetailEditPlanner.CreateRequest(baseline, baseline, first)!;
        Assert.True(request.PlannedOn.IsSpecified); Assert.Equal(selected, request.PlannedOn.Value);
        Assert.True(request.DeadlineOn.IsSpecified); Assert.Equal(selected, request.DeadlineOn.Value);
        var rebased = TaskDetailEditPlanner.Rebase(baseline, first);
        Assert.Null(rebased.PlannedAt); Assert.Null(rebased.DeadlineAt);
        Assert.Equal(selected, TaskDetailEditPlanner.DisplayPlannedOn(rebased));
        Assert.Equal(selected, TaskDetailEditPlanner.DisplayDeadlineOn(rebased));
        Assert.False(TaskDetailEditPlanner.HasChanges(rebased, first));
        var second = TaskDetailEditPlanner.CreateRequest(rebased, rebased with { Revision = 3 }, first with { Notes = "Second edit", Version = 2 })!;
        Assert.Equal("Second edit", second.Notes.Value);
        Assert.False(second.PlannedOn.IsSpecified); Assert.False(second.DeadlineOn.IsSpecified);
    }

    [Fact]
    public void Displayed_stored_day_keeps_precedence_over_mismatched_exact_time()
    {
        var task = Task() with { PlannedAt = DateTimeOffset.Parse("2025-01-01T12:00:00Z"), DeadlineAt = DateTimeOffset.Parse("2025-01-02T12:00:00Z") };
        Assert.Equal(task.PlannedOn, TaskDetailEditPlanner.DisplayPlannedOn(task));
        Assert.Equal(task.DeadlineOn, TaskDetailEditPlanner.DisplayDeadlineOn(task));
        Assert.False(TaskDetailEditPlanner.HasChanges(task, Draft(task)));
    }

    [Fact]
    public void Acknowledgement_merges_current_metadata_without_dropping_unknown_members()
    {
        var baseline = Task();
        var current = baseline with { LocalMetadataJson = "{\"external\":{\"keep\":1},\"openza\":{\"other\":true}}", Revision = 7 };
        var request = TaskDetailEditPlanner.CreateRequest(baseline, current, Draft(baseline) with { SourceDateMismatchAcknowledgementKey = "choice-test" })!;
        using var json = JsonDocument.Parse(request.LocalMetadataJson.Value!);
        Assert.Equal(1, json.RootElement.GetProperty("external").GetProperty("keep").GetInt32());
        Assert.True(json.RootElement.GetProperty("openza").GetProperty("other").GetBoolean());
        Assert.Equal("choice-test", TaskSourceDateChoice.ReadAcknowledgement(request.LocalMetadataJson.Value));
        Assert.Throws<InvalidOperationException>(() => TaskSourceDateChoice.WithAcknowledgement("[]", "choice-test"));
    }

    [Fact]
    public void Date_choice_is_acknowledged_only_for_that_specific_mismatch_and_recurring_sources_are_excluded()
    {
        var task = Task() with { SourceIntegrationId = IntegrationIds.MicrosoftToDo, SourceExternalId = "synthetic-remote", SourcePlannedOn = new(2026, 6, 18) };
        var key = TaskSourceDateChoice.CreateKey(task.Id, task.PlannedOn, task.SourcePlannedOn, task.DeadlineOn, null);
        Assert.NotNull(TaskSourceDateChoice.Describe(task, task.PlannedOn, task.DeadlineOn, "Microsoft To Do", null));
        Assert.Null(TaskSourceDateChoice.Describe(task, task.PlannedOn, task.DeadlineOn, "Microsoft To Do", key));
        Assert.NotNull(TaskSourceDateChoice.Describe(task, task.PlannedOn, task.DeadlineOn, "Microsoft To Do", key, ignoreAcknowledgement: true));
        Assert.Null(TaskSourceDateChoice.Describe(task with { SourceMetadataJson = "{\"recurrenceRule\":\"every month\"}" }, task.PlannedOn, task.DeadlineOn, "Microsoft To Do", null));
        Assert.Null(TaskSourceDateChoice.Describe(task with { SourceIntegrationId = IntegrationIds.Todoist }, task.PlannedOn, task.DeadlineOn, "Todoist", null));
    }
}
