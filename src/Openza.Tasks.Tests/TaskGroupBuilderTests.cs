using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Tests;

public sealed class TaskGroupBuilderTests
{
    [Fact]
    public void Label_grouping_places_multi_label_tasks_in_each_label()
    {
        var task = new TaskItem
        {
            Id = "task-1",
            Title = "Task",
            Labels =
            [
                new LabelItem { Id = "label-home", Name = "Home" },
                new LabelItem { Id = "label-office", Name = "Office" },
            ],
        };

        var groups = TaskGroupBuilder.GetAssignments(task, project: null, TaskGroupMode.Label);

        Assert.Equal(["Home", "Office"], groups.Select(group => group.Title));
    }

    [Fact]
    public void Project_grouping_prefers_openza_project_name()
    {
        var task = new TaskItem
        {
            Id = "task-1",
            Title = "Task",
            ProjectId = "project-1",
            SourceProjectName = "Todoist Inbox",
        };
        var project = new ProjectItem
        {
            Id = "project-1",
            Name = "Launch",
        };

        var group = Assert.Single(TaskGroupBuilder.GetAssignments(task, project, TaskGroupMode.Project));

        Assert.Equal("Launch", group.Title);
    }

    [Fact]
    public void Date_grouping_uses_planned_deadline_and_scheduled_values()
    {
        var planned = new TaskItem { Id = "planned", Title = "Planned", PlannedOn = new DateOnly(2030, 1, 15) };
        var deadline = new TaskItem { Id = "deadline", Title = "Deadline", DeadlineOn = new DateOnly(2030, 1, 16) };
        var scheduled = new TaskItem { Id = "scheduled", Title = "Scheduled", ScheduledStart = new DateTimeOffset(2030, 1, 17, 9, 0, 0, TimeSpan.Zero) };

        Assert.Equal("Jan 15, 2030", Assert.Single(TaskGroupBuilder.GetAssignments(planned, project: null, TaskGroupMode.Date)).Title);
        Assert.Equal("Jan 16, 2030", Assert.Single(TaskGroupBuilder.GetAssignments(deadline, project: null, TaskGroupMode.Date)).Title);
        Assert.Equal("Jan 17, 2030", Assert.Single(TaskGroupBuilder.GetAssignments(scheduled, project: null, TaskGroupMode.Date)).Title);
    }

    [Fact]
    public void Created_date_grouping_sorts_newer_dates_first()
    {
        var older = new TaskItem { Id = "older", Title = "Older", CreatedAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        var newer = new TaskItem { Id = "newer", Title = "Newer", CreatedAt = new DateTimeOffset(2030, 1, 2, 0, 0, 0, TimeSpan.Zero) };

        var olderGroup = Assert.Single(TaskGroupBuilder.GetAssignments(older, project: null, TaskGroupMode.CreatedDate));
        var newerGroup = Assert.Single(TaskGroupBuilder.GetAssignments(newer, project: null, TaskGroupMode.CreatedDate));

        Assert.True(string.CompareOrdinal(newerGroup.SortKey, olderGroup.SortKey) < 0);
    }
}
