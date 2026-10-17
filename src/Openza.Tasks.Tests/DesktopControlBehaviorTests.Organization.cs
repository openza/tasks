using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Desktop.Dialogs;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed partial class DesktopControlBehaviorTests
{
    [Fact]
    public Task Project_context_menu_edits_and_deletes_without_deleting_its_tasks() => Run(async context =>
    {
        ContextMenu Menu()
        {
            context.Window.UpdateLayout();
            var row = context.Window.FindControl<ListBox>("ProjectList")!.GetVisualDescendants().OfType<ListBoxItem>()
                .Single(item => item.DataContext is ProjectNavigationItemViewModel { Project.Id: "project" });
            row.ContextMenu!.Open(row);
            return row.ContextMenu;
        }
        var menu = Menu();
        menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Edit")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => context.Window.OwnedWindows.OfType<ProjectEditorWindow>().Any());
        var dialog = context.Window.OwnedWindows.OfType<ProjectEditorWindow>().Single();
        dialog.FindControl<TextBox>("NameBox")!.Text = "Renamed release";
        Click(dialog, "Save");
        await Until(() => context.Vm.ProjectItems.Any(item => item.Title == "Renamed release") && !context.Vm.IsBusy);
        menu.Close(); menu = Menu();
        menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Delete")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => context.Window.OwnedWindows.OfType<ConfirmWindow>().Any());
        Click(context.Window.OwnedWindows.OfType<ConfirmWindow>().Single(), "Delete");
        await Until(() => !context.Vm.ProjectItems.Any(item => item.Project.Id == "project") && !context.Vm.IsBusy);
        Assert.NotNull(await context.Store.GetTaskAsync("task-0"));
        Assert.Null((await context.Store.GetTaskAsync("task-0"))!.ProjectId);
        menu.Close();
        context.Window.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Create project")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Window.OwnedWindows.OfType<TextPromptWindow>().Any());
        var prompt = context.Window.OwnedWindows.OfType<TextPromptWindow>().Single();
        prompt.FindControl<TextBox>("ValueBox")!.Text = "Created project";
        Click(prompt, "Save");
        await Until(() => context.Vm.ProjectItems.Any(item => item.Title == "Created project"));
    });

    [Fact]
    public Task Space_rename_archive_and_disconnect_confirmations_use_correct_action_labels() => Run(async context =>
    {
        await context.Vm.CreateSpaceAsync("Disposable space");
        context.Window.FindControl<Button>("SettingsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var settings = context.Window.FindControl<ScrollViewer>("SettingsWorkspace")!;
        await Until(() => settings.IsVisible); context.Window.UpdateLayout();
        Button SpaceButton(string action) => settings.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, action) &&
            button.DataContext is SpaceNavigationItemViewModel { Title: "Disposable space" });
        SpaceButton("Rename").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Window.OwnedWindows.OfType<TextPromptWindow>().Any());
        var prompt = context.Window.OwnedWindows.OfType<TextPromptWindow>().Single();
        prompt.FindControl<TextBox>("ValueBox")!.Text = "Renamed space"; Click(prompt, "Save");
        await Until(() => context.Vm.EditableSpaceItems.Any(item => item.Title == "Renamed space") && !context.Vm.IsBusy);
        context.Window.UpdateLayout();
        settings.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Archive") &&
            button.DataContext is SpaceNavigationItemViewModel { Title: "Renamed space" }).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Window.OwnedWindows.OfType<ConfirmWindow>().Any());
        Click(context.Window.OwnedWindows.OfType<ConfirmWindow>().Single(), "Archive");
        await Until(() => !context.Vm.EditableSpaceItems.Any(item => item.Title == "Renamed space") && !context.Vm.IsBusy);
        foreach (var expander in settings.GetVisualDescendants().OfType<Expander>().ToArray()) expander.IsExpanded = true;
        context.Window.UpdateLayout();
        foreach (var button in settings.GetVisualDescendants().OfType<Button>().Where(button => Equals(button.Content, "Disconnect")).ToArray())
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => context.Window.OwnedWindows.OfType<ConfirmWindow>().Any());
            var confirm = context.Window.OwnedWindows.OfType<ConfirmWindow>().Single();
            Assert.Equal("Disconnect", confirm.FindControl<Button>("ConfirmButton")!.Content);
            Click(confirm, "Cancel");
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Source_dates_copy_expander_and_subtask_controls_preserve_provider_metadata(bool useSource) => Run(async context =>
    {
        await context.Store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "source", IntegrationId = IntegrationIds.GitHub, ProviderConnectionId = "github_default", ExternalId = "remote-task",
            ProviderTaskId = "remote-task", Title = "Remote title", Description = "Remote description", PlannedOn = new DateOnly(2026, 11, 10),
        });
        await context.Store.UpsertTaskAsync((await context.Store.GetTaskAsync("task-0"))! with
        {
            SourceIntegrationId = IntegrationIds.GitHub, SourceConnectionId = "github_default", SourceExternalId = "remote-task",
            SourceTitle = "Remote title", SourceDescription = "Remote description", SourcePlannedOn = new DateOnly(2026, 11, 10),
        });
        for (var index = 0; index < 6; index++) await context.Store.UpsertTaskAsync(new TaskItem { Id = $"child-{index}", ParentId = "task-0", Title = $"Child {index}" });
        await context.Vm.ApplySearchAsync();
        await context.Vm.SelectTaskAsync(context.Vm.Tasks.Single(item => item.Task.Id == "task-0"));
        await Task.Delay(100); context.Window.UpdateLayout();
        var pane = context.Window.FindControl<Border>("DetailsPane")!;
        Assert.True(context.Vm.HasSourceDateMismatch);
        var source = pane.GetVisualDescendants().OfType<Expander>().Single();
        source.IsExpanded = false; source.IsExpanded = true; Assert.True(source.IsExpanded);
        var copy = (MenuFlyout)FindButton(pane, "Copy").Flyout!;
        copy.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Copy source")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await UntilAsync(async () => (await context.Window.Clipboard!.TryGetTextAsync())!.Contains("Remote title"));
        Click(pane, useSource ? context.Vm.UseSourceDatesText : "Keep Openza date");
        await Until(() => !context.Vm.HasSourceDateMismatch && !context.Vm.IsBusy);
        if (useSource) Assert.Equal(new DateOnly(2026, 11, 10), (await context.Store.GetTaskAsync("task-0"))!.PlannedOn);
        Click(pane, "Show all 6 subtasks"); Assert.Equal(6, context.Vm.VisibleSubtasks.Count);
        context.Window.UpdateLayout();
        pane.GetVisualDescendants().OfType<CheckBox>().Single(box => AutomationProperties.GetName(box) == "Complete Child 5")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(async () => (await context.Store.GetTaskAsync("child-5"))!.IsCompleted);
        await Until(() => !context.Vm.IsBusy);
        Click(pane, "Show fewer"); Assert.Equal(5, context.Vm.VisibleSubtasks.Count);
    });
}
