using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Desktop.Dialogs;
using Openza.Tasks.Desktop.Shell;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed partial class DesktopControlBehaviorTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Create_and_open_preserves_saved_filters_through_shell_events_and_restart(bool completed, bool changeView) => Run(async context =>
    {
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.NextActions));
        var key = $"{context.Vm.SelectedSpace?.SpaceId ?? "all"}|{(completed ? "completed" : "tasks")}|all";
        var label = context.Vm.LabelFilterOptions.First(item => item.LabelId is not null);
        await context.Preferences.UpdateAsync(preferences =>
        {
            preferences.TaskViewSettings[key] = new() { PriorityFilterIndex = 1, RepeatFilterIndex = 2, LabelFilterId = label.LabelId };
            return preferences;
        });
        if (!changeView)
        {
            await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item =>
                item.Kind == (completed ? TaskListKind.Completed : TaskListKind.Open)));
        }
        await Until(() => ListOptionsIdle(context.Window) && !context.Vm.IsBusy);
        var trigger = context.Window.FindControl<WrapPanel>("TaskFilterCommands")!.Children.OfType<Button>().Last();
        var flyout = (Flyout)trigger.Flyout!;
        flyout.ShowAt(trigger);
        context.Window.UpdateLayout();
        var filters = ((StackPanel)flyout.Content!).Children.OfType<ComboBox>().ToArray();
        var selectionEvents = 0;
        foreach (var filter in filters) filter.SelectionChanged += (_, _) => selectionEvents++;

        await context.Vm.CreateTaskAsync(new("Reveal outside current list", "", null, completed ? 4 : 0, 2, null, "", true));
        await Until(() => ListOptionsIdle(context.Window) && !context.Vm.IsBusy);
        Assert.True(selectionEvents >= 3, "Bound controls must exercise the coalesced shell event path.");
        Assert.Equal("Reveal outside current list", context.Vm.SelectedTask!.Task.Title);
        Assert.Equal(0, context.Vm.PriorityFilterIndex);
        Assert.Equal(0, context.Vm.RepeatFilterIndex);
        var saved = context.Preferences.Load().TaskViewSettings[key];
        Assert.Equal(1, saved.PriorityFilterIndex);
        Assert.Equal(2, saved.RepeatFilterIndex);
        Assert.Equal(label.LabelId, saved.LabelFilterId);
        flyout.Hide();
        await Until(() => ListOptionsIdle(context.Window) && !context.Vm.IsBusy);
        Assert.Equal(1, context.Preferences.Load().TaskViewSettings[key].PriorityFilterIndex);
        context.Window.Close();
        await Until(() => !context.Window.IsVisible);
        Assert.Equal(1, context.Preferences.Load().TaskViewSettings[key].PriorityFilterIndex);

        var restarted = new MainWindowViewModel(context.Store, new InMemoryCredentialStore(), preferencesStore: context.Preferences);
        var window = new MainWindow(restarted) { Width = 1440, Height = 900 };
        try
        {
            window.Show();
            await Until(() => restarted.StatusMessage.StartsWith("Local data") && !restarted.IsBusy && ListOptionsIdle(window));
            Assert.Equal(1, restarted.PriorityFilterIndex);
            Assert.Equal(2, restarted.RepeatFilterIndex);
            Assert.Equal(label.LabelId, restarted.SelectedLabelFilter?.LabelId);
            var button = window.FindControl<WrapPanel>("TaskFilterCommands")!.Children.OfType<Button>().Last();
            var picker = (Flyout)button.Flyout!;
            picker.ShowAt(button);
            window.UpdateLayout();
            var choices = ((StackPanel)picker.Content!).Children.OfType<ComboBox>().ToArray();
            // Ordinary rapid user selections still coalesce and persist after the reveal.
            choices[0].SelectedIndex = 2;
            choices[1].SelectedIndex = 1;
            choices[2].SelectedItem = restarted.LabelFilterOptions.First(item => item.LabelId is null);
            await Until(() => ListOptionsIdle(window) && !restarted.IsBusy);
            var updated = context.Preferences.Load().TaskViewSettings[key];
            Assert.Equal(2, updated.PriorityFilterIndex);
            Assert.Equal(1, updated.RepeatFilterIndex);
            Assert.Null(updated.LabelFilterId);
            var nextRestart = new MainWindowViewModel(context.Store, new InMemoryCredentialStore(), preferencesStore: context.Preferences);
            await nextRestart.InitializeAsync();
            Assert.Equal(2, nextRestart.PriorityFilterIndex);
            Assert.Equal(1, nextRestart.RepeatFilterIndex);
            Assert.Null(nextRestart.SelectedLabelFilter?.LabelId);
            picker.Hide();
            await Until(() => ListOptionsIdle(window) && !restarted.IsBusy);
            Assert.Equal(2, context.Preferences.Load().TaskViewSettings[key].PriorityFilterIndex);
            Assert.Equal(1, context.Preferences.Load().TaskViewSettings[key].RepeatFilterIndex);
        }
        finally { window.Close(); }
    });

    private static bool ListOptionsIdle(MainWindow window) =>
        new[] { "_applyingListOptions", "_changingNavigation" }.All(field =>
            !(bool)typeof(MainWindow).GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!);

    [Fact]
    public Task Getting_started_commands_sync_feedback_and_narrow_project_toggle_are_reachable() => Run(async context =>
    {
        await context.Preferences.UpdateAsync(value => value with { ShowGetStarted = true });
        await context.Vm.CreateSpaceAsync("Empty workspace");
        await context.Vm.SelectSpaceAsync(context.Vm.EditableSpaceItems.Single(item => item.Title == "Empty workspace"));
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Inbox));
        context.Window.UpdateLayout();
        var banner = context.Window.FindControl<Border>("GetStartedPanel")!;
        Assert.True(banner.IsVisible);
        Click(banner, "Sync");
        await Until(() => context.Vm.StatusMessage == "Connect an app in Settings before syncing." && !context.Vm.IsSyncing);
        Click(banner, "Add task");
        await Until(() => context.Window.OwnedWindows.OfType<AddTaskWindow>().Any());
        Click(context.Window.OwnedWindows.OfType<AddTaskWindow>().Single(), "Cancel");
        Click(banner, "Dismiss");
        await Until(() => !banner.IsVisible);
        Assert.False(context.Preferences.Load().ShowGetStarted);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        context.Window.Width = 920; await Task.Delay(100); context.Window.UpdateLayout();
        var projects = context.Window.FindControl<Border>("ProjectsPane")!;
        var command = context.Window.FindControl<Button>("ProjectsCommand")!;
        Assert.True(command.IsVisible, $"Window={context.Window.Bounds}; Workbench={context.Window.FindControl<Grid>("WorkbenchGrid")!.Bounds}; navigation={context.Vm.SelectedNavigation?.Kind}; selected={context.Vm.SelectedTask?.Task.Id}");
        var before = projects.IsVisible;
        command.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.NotEqual(before, projects.IsVisible);
        command.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(before, projects.IsVisible);
        context.Window.FindControl<Button>("SyncNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Window.FindControl<ScrollViewer>("SyncWorkspace")!.IsVisible);
        context.Window.UpdateLayout(); context.Vm.ReportStatus(""); Click(context.Window.FindControl<ScrollViewer>("SyncWorkspace")!, "Sync now");
        await Until(() => context.Vm.StatusMessage == "Connect an app in Settings before syncing." && !context.Vm.IsSyncing);
    });

    [Fact]
    public Task Shell_launchers_search_empty_state_and_compact_space_selection_work() => Run(async context =>
    {
        context.Window.GetVisualDescendants().OfType<Button>().First(button => AutomationProperties.GetName(button) == "Add task")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Window.OwnedWindows.OfType<AddTaskWindow>().Any());
        Click(context.Window.OwnedWindows.OfType<AddTaskWindow>().Single(), "Cancel");
        context.Window.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Search tasks and projects")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Window.OwnedWindows.OfType<GlobalSearchWindow>().Any());
        context.Window.OwnedWindows.OfType<GlobalSearchWindow>().Single().Close();
        var search = context.Window.FindControl<TextBox>("SearchBox")!;
        search.Text = "missing title";
        search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await Until(() => context.Vm.Tasks.Count == 0 && !context.Vm.IsBusy);
        context.Window.UpdateLayout();
        Click(context.Window, context.Vm.EmptyStateActionText);
        await Until(() => context.Vm.Tasks.Count == 4 && !context.Vm.IsBusy);
        Assert.Equal("", search.Text);
        await context.Vm.CreateSpaceAsync("Compact destination");
        context.Window.FindControl<Button>("NavigationCollapseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var trigger = context.Window.FindControl<Button>("CompactSpacePickerButton")!;
        trigger.Flyout!.ShowAt(trigger); context.Window.UpdateLayout();
        var content = (ItemsControl)((Flyout)trigger.Flyout).Content!;
        content.GetVisualDescendants().OfType<Button>().Single(button => button.DataContext is SpaceNavigationItemViewModel { Title: "Compact destination" })
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Vm.SelectedSpace?.Title == "Compact destination" && !context.Vm.IsBusy);
        Assert.False(trigger.Flyout.IsOpen);
        context.Window.FindControl<Button>("SyncNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Window.FindControl<ScrollViewer>("SyncWorkspace")!.IsVisible);
        Assert.Equal("Sync", context.Preferences.Load().LastView);
    });

    [Fact]
    public Task Project_navigation_group_collapse_and_row_completion_preserve_the_selected_task() => Run(async context =>
    {
        var projectList = context.Window.FindControl<ListBox>("ProjectList")!;
        projectList.SelectedItem = context.Vm.ProjectItems.Single(item => item.Project.Id == "project");
        await Until(() => context.Vm.SelectedProject?.Project.Id == "project" && !context.Vm.IsBusy);
        Assert.Equal(4, context.Vm.Tasks.Count);
        context.Vm.GroupIndex = 1;
        await context.Vm.ApplyListOptionsAsync(); context.Window.UpdateLayout();
        var list = context.Window.FindControl<ListBox>("TaskList")!;
        var header = list.GetVisualDescendants().OfType<Button>().First(button => button.DataContext is TaskListEntryViewModel { IsHeader: true });
        var before = context.Vm.TaskEntries.Count;
        header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(context.Vm.TaskEntries.Count < before);
        context.Window.UpdateLayout();
        list.GetVisualDescendants().OfType<Button>().First(button => button.DataContext is TaskListEntryViewModel { IsHeader: true })
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(before, context.Vm.TaskEntries.Count);
        context.Window.UpdateLayout();
        list.GetVisualDescendants().OfType<CheckBox>().Single(box => AutomationProperties.GetName(box) == "Complete Control test 0")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(async () => (await context.Store.GetTaskAsync("task-0"))!.IsCompleted);
    });

    [Fact]
    public Task Inspector_status_project_move_and_delete_controls_persist_changes() => Run(async context =>
    {
        foreach (var (index, expected) in new[] { (0, TaskItemStatus.Inbox), (1, TaskItemStatus.Next), (2, TaskItemStatus.Waiting),
            (3, TaskItemStatus.Someday), (4, TaskItemStatus.Completed), (5, TaskItemStatus.None) })
        {
            var task = (await context.Store.GetTaskAsync("task-0"))!;
            await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == (task.IsCompleted ? TaskListKind.Completed : TaskListKind.Open)));
            await context.Vm.SelectTaskAsync(context.Vm.Tasks.Single(item => item.Task.Id == "task-0"));
            await Task.Delay(60); context.Window.UpdateLayout();
            context.Window.FindControl<Border>("DetailsPane")!.GetVisualDescendants().OfType<ComboBox>()
                .Single(box => AutomationProperties.GetName(box) == "Task status").SelectedIndex = index;
            await UntilAsync(async () => (await context.Store.GetTaskAsync("task-0"))!.Status == expected);
            await Until(() => !context.Vm.IsBusy && !context.Vm.IsUpdatingDetails);
        }
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
        await context.Vm.SelectTaskAsync(context.Vm.Tasks.Single(item => item.Task.Id == "task-0"));
        context.Window.UpdateLayout();
        var pane = context.Window.FindControl<Border>("DetailsPane")!;
        pane.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Find or create project")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Window.OwnedWindows.OfType<ProjectPickerWindow>().Any());
        var picker = context.Window.OwnedWindows.OfType<ProjectPickerWindow>().Single();
        picker.FindControl<ListBox>("ProjectsList")!.SelectedIndex = 0;
        Click(picker, "Choose");
        await UntilAsync(async () => (await context.Store.GetTaskAsync("task-0"))!.ProjectId is null);
        await context.Vm.CreateSpaceAsync("Move destination");
        var trigger = FindButton(pane, "Move to space");
        trigger.Flyout!.ShowAt(trigger); context.Window.UpdateLayout();
        var content = (ItemsControl)((Flyout)trigger.Flyout).Content!;
        content.GetVisualDescendants().OfType<Button>().Single(button => button.DataContext is SpaceNavigationItemViewModel { Title: "Move destination" })
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var destination = context.Vm.EditableSpaceItems.Single(item => item.Title == "Move destination");
        await UntilAsync(async () => (await context.Store.GetTaskAsync("task-0"))!.SpaceId == destination.SpaceId);
        trigger.Flyout.Hide(); await Until(() => !context.Vm.IsBusy);
        await context.Vm.SelectSpaceAsync(destination);
        await context.Vm.SelectTaskAsync(context.Vm.Tasks.Single(item => item.Task.Id == "task-0"));
        Click(pane, "Delete");
        await Until(() => context.Window.OwnedWindows.OfType<ConfirmWindow>().Any());
        Click(context.Window.OwnedWindows.OfType<ConfirmWindow>().Single(), "Cancel");
        Assert.NotNull(await context.Store.GetTaskAsync("task-0"));
        Click(pane, "Delete");
        await Until(() => context.Window.OwnedWindows.OfType<ConfirmWindow>().Any());
        Click(context.Window.OwnedWindows.OfType<ConfirmWindow>().Single(), "Delete");
        await UntilAsync(async () => await context.Store.GetTaskAsync("task-0") is null);
    });

    [Fact]
    public Task Routing_rule_settings_add_edit_cancel_and_delete_update_the_list() => Run(async context =>
    {
        context.Window.FindControl<Button>("SettingsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var settings = context.Window.FindControl<ScrollViewer>("SettingsWorkspace")!;
        await Until(() => settings.IsVisible); context.Window.UpdateLayout();
        foreach (var expander in settings.GetVisualDescendants().OfType<Expander>()) expander.IsExpanded = true;
        context.Window.UpdateLayout();
        Click(settings, "Add rule");
        await Until(() => context.Window.OwnedWindows.OfType<TodoistRoutingRuleWindow>().Any());
        var dialog = context.Window.OwnedWindows.OfType<TodoistRoutingRuleWindow>().Single();
        dialog.FindControl<TextBox>("LabelBox")!.Text = "review";
        Click(dialog, "Save");
        await Until(() => context.Vm.TodoistRoutingRules.Count == 1 && !context.Vm.IsBusy);
        context.Window.UpdateLayout(); Click(settings, "Edit");
        await Until(() => context.Window.OwnedWindows.OfType<TodoistRoutingRuleWindow>().Any());
        dialog = context.Window.OwnedWindows.OfType<TodoistRoutingRuleWindow>().Single();
        Assert.Equal("review", dialog.FindControl<TextBox>("LabelBox")!.Text);
        dialog.FindControl<TextBox>("LabelBox")!.Text = "release"; Click(dialog, "Save");
        await Until(() => context.Vm.TodoistRoutingRules.Single().LabelText.Contains("release") && !context.Vm.IsBusy);
        context.Window.UpdateLayout(); Click(settings, "Delete");
        await Until(() => context.Window.OwnedWindows.OfType<ConfirmWindow>().Any());
        Click(context.Window.OwnedWindows.OfType<ConfirmWindow>().Single(), "Cancel");
        Assert.Single(context.Vm.TodoistRoutingRules);
        Click(settings, "Delete");
        await Until(() => context.Window.OwnedWindows.OfType<ConfirmWindow>().Any());
        Click(context.Window.OwnedWindows.OfType<ConfirmWindow>().Single(), "Delete");
        await Until(() => context.Vm.TodoistRoutingRules.Count == 0);
    });

    [Fact]
    public Task Disconnected_cloud_backup_controls_recover_from_error_and_cancel() => Run(async context =>
    {
        context.Window.FindControl<Button>("SettingsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var settings = context.Window.FindControl<ScrollViewer>("SettingsWorkspace")!;
        await Until(() => settings.IsVisible); context.Window.UpdateLayout();
        var automatic = settings.GetVisualDescendants().OfType<ToggleSwitch>().Single(toggle => Equals(toggle.Content, "Automatic OneDrive backup"));
        automatic.IsChecked = true;
        await Until(() => automatic.IsChecked == false && context.Vm.StatusMessage.Contains("Connect a OneDrive account first"));
        Assert.False(context.Preferences.Load().OneDriveEnabled);
        Assert.False(FindButton(settings, "Back up now").IsEnabled);
        Assert.False(FindButton(settings, "Restore selected cloud backup").IsEnabled);
        Click(settings, "Refresh backups");
        await Until(() => context.Vm.CloudStatus.StartsWith("Cloud backup failed:"));
        Assert.True(context.Vm.IsStatusMessagePersistent);
        var encryption = settings.GetVisualDescendants().OfType<ToggleSwitch>().Single(toggle => Equals(toggle.Content, "Encrypt backups with a passphrase"));
        encryption.IsChecked = true;
        await Until(() => context.Window.OwnedWindows.Any(window => window.Title == "Backup passphrase"));
        Click(context.Window.OwnedWindows.Single(window => window.Title == "Backup passphrase"), "Cancel");
        await Until(() => encryption.IsChecked == false);
        Assert.False(context.Vm.OneDriveEncrypted);
    });

    [Theory]
    [InlineData("Done")]
    [InlineData("Open issue")]
    [InlineData("Replace link")]
    [InlineData("Remove link")]
    public Task GitHub_link_dialog_actions_preserve_tasks_and_report_missing_connection(string action) => Run(async context =>
    {
        await context.Store.UpsertTaskExternalLinkAsync(new TaskExternalLinkInfo
        {
            Id = "link", TaskId = "task-0", IntegrationId = IntegrationIds.GitHub, ExternalId = "invalid-link",
            Kind = TaskExternalLinkKinds.Issue, DisplayName = "example/repo#1", Url = "http://invalid.example/",
        });
        await context.Vm.SelectTaskAsync(context.Vm.Tasks.Single(item => item.Task.Id == "task-0"));
        context.Window.UpdateLayout();
        Click(context.Window.FindControl<Border>("DetailsPane")!, context.Vm.GitHubActionText);
        await Until(() => context.Window.OwnedWindows.Any(window => window.Title == "GitHub issue"));
        var dialog = context.Window.OwnedWindows.Single(window => window.Title == "GitHub issue");
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text!.Contains("cannot access this issue"));
        Click(dialog, action);
        await Until(() => !dialog.IsVisible);
        if (action == "Remove link") await UntilAsync(async () => (await context.Store.GetTaskExternalLinksAsync("task-0")).Count == 0);
        else Assert.Single(await context.Store.GetTaskExternalLinksAsync("task-0"));
        if (action == "Replace link") await UntilAsync(
            () => Task.FromResult(context.Vm.StatusMessage == "Connect GitHub in Settings first."),
            () => $"Replace link feedback: {context.Vm.StatusMessage}; selected task: {context.Vm.SelectedTask?.Task.Id}; busy: {context.Vm.IsBusy}");
        Assert.NotNull(await context.Store.GetTaskAsync("task-0"));
    });

    [Fact]
    public Task Legacy_settings_space_and_provider_buttons_validate_and_preserve_local_tasks() => Run(async context =>
    {
        var settings = new Desktop.Settings.SettingsWindow(context.Vm); settings.Show(context.Window);
        await Task.Delay(50); settings.UpdateLayout();
        var input = settings.FindControl<TextBox>("NewSpaceBox")!;
        input.Text = "Legacy destination";
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await Until(() => context.Vm.EditableSpaceItems.Any(item => item.Title == "Legacy destination") && !context.Vm.IsBusy);
        Assert.Equal("", input.Text);
        input.Text = "Button destination"; Click(settings, "Add space");
        await Until(() => context.Vm.EditableSpaceItems.Any(item => item.Title == "Button destination") && !context.Vm.IsBusy);
        settings.UpdateLayout();
        settings.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Rename") &&
            button.DataContext is SpaceNavigationItemViewModel { Title: "Legacy destination" }).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => settings.OwnedWindows.OfType<TextPromptWindow>().Any());
        var prompt = settings.OwnedWindows.OfType<TextPromptWindow>().Single();
        prompt.FindControl<TextBox>("ValueBox")!.Text = "Legacy renamed"; Click(prompt, "Save");
        await Until(() => context.Vm.EditableSpaceItems.Any(item => item.Title == "Legacy renamed") && !context.Vm.IsBusy);
        settings.UpdateLayout();
        settings.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Archive") &&
            button.DataContext is SpaceNavigationItemViewModel { Title: "Legacy renamed" }).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => settings.OwnedWindows.OfType<ConfirmWindow>().Any());
        Click(settings.OwnedWindows.OfType<ConfirmWindow>().Single(), "Archive");
        await Until(() => !context.Vm.EditableSpaceItems.Any(item => item.Title == "Legacy renamed") && !context.Vm.IsBusy);
        foreach (var button in settings.GetVisualDescendants().OfType<Button>().Where(button => Equals(button.Content, "Connect")).ToArray())
        {
            context.Vm.ReportStatus(""); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => context.Vm.StatusMessage.Contains("token", StringComparison.OrdinalIgnoreCase));
        }
        foreach (var button in settings.GetVisualDescendants().OfType<Button>().Where(button => Equals(button.Content, "Disconnect")).ToArray())
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => settings.OwnedWindows.OfType<ConfirmWindow>().Any());
            Click(settings.OwnedWindows.OfType<ConfirmWindow>().Single(), "Disconnect");
            await Until(() => !context.Vm.IsBusy);
        }
        var repo = settings.GetVisualDescendants().OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "Default GitHub repository");
        repo.Text = "invalid"; Click(settings, "Save repository");
        await Until(() => context.Vm.IsStatusMessagePersistent);
        Assert.NotNull(await context.Store.GetTaskAsync("task-0"));
        Click(settings, "Close");
    });
}
