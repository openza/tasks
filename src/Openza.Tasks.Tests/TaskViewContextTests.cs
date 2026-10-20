using Openza.Tasks.Core.Data;
using Openza.Tasks.Desktop.ViewModels.Navigation;

namespace Openza.Tasks.Tests;

public sealed class TaskViewContextTests
{
    [Theory]
    [InlineData(TaskListKind.Inbox, "inbox", 0)]
    [InlineData(TaskListKind.NextActions, "next", 0)]
    [InlineData(TaskListKind.Waiting, "waiting", 0)]
    [InlineData(TaskListKind.Someday, "someday", 0)]
    [InlineData(TaskListKind.Today, "today", 0)]
    [InlineData(TaskListKind.Calendar, "calendar", 1)]
    [InlineData(TaskListKind.Overdue, "overdue", 1)]
    [InlineData(TaskListKind.Open, "tasks", 2)]
    [InlineData(TaskListKind.Completed, "completed", 9)]
    [InlineData(TaskListKind.All, "inbox", 0)]
    public void Smart_list_keys_and_group_defaults_keep_the_existing_persisted_contract(TaskListKind kind, string key, int group)
    {
        var context = new TaskViewContext(null, kind);
        Assert.Equal($"all|{key}|all", context.PreferencesKey);
        Assert.Equal($"all|{kind}", context.SelectionKey);
        Assert.Equal("all|tasks|all", context.SharedTasksSettingsKey);
        Assert.Equal(group, context.DefaultGroupIndex);
        Assert.Equal(kind == TaskListKind.Open, context.IsTasksView);
    }

    [Fact]
    public void Project_rebinding_uses_identity_and_preserves_scope_casing_and_key_format()
    {
        var context = new TaskViewContext("Space-A", TaskListKind.Inbox, "Project-A");
        Assert.Equal("Space-A|tasks|Project-A", context.PreferencesKey);
        Assert.Equal("Space-A|Project-A", context.SelectionKey);
        Assert.Equal("Space-A|tasks|all", context.SharedTasksSettingsKey);
        Assert.Equal(2, context.DefaultGroupIndex);
        Assert.True(context.IsTasksView);
        var state = new TaskSelectionState { DisplayedContext = context };
        state.RememberDisplayed("selected");
        Assert.Equal("selected", state.RememberedFor(new("Space-A", TaskListKind.Open, "Project-A")));
        Assert.Null(state.RememberedFor(context with { SpaceId = "space-a" }));
    }

    [Fact]
    public void Bound_destination_does_not_steal_the_displayed_selection_and_close_clears_only_that_scope()
    {
        var inbox = new TaskViewContext("space", TaskListKind.Inbox);
        var next = new TaskViewContext("space", TaskListKind.NextActions);
        var state = new TaskSelectionState();
        state.RememberDisplayed("no displayed context");
        Assert.Null(state.RememberedFor(inbox));
        state.DisplayedContext = inbox;
        state.RememberDisplayed("original");
        // The selector has already changed to next while the displayed rows still belong to Inbox.
        state.RememberDisplayed("last Inbox selection");
        Assert.Null(state.RememberedFor(next));
        state.DisplayedContext = next;
        state.RememberDisplayed("next selection");
        state.RememberDisplayed(null);
        Assert.Equal("last Inbox selection", state.RememberedFor(inbox));
        Assert.Equal("next selection", state.RememberedFor(next));
        state.ClearDisplayed();
        Assert.Null(state.RememberedFor(next));
        Assert.Equal("last Inbox selection", state.RememberedFor(inbox));
    }
}
