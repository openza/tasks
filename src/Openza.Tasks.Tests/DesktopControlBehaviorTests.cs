using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Desktop.Dialogs;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.Shell;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

[Collection(nameof(EditorTestCollection))]
public sealed partial class DesktopControlBehaviorTests(EditorTestSession fixture)
{
    [Fact]
    public Task Every_task_sort_direction_and_group_menu_choice_updates_the_workspace() => Run(async context =>
    {
        var menus = context.Window.FindControl<WrapPanel>("TaskFilterCommands")!.Children.OfType<Button>().ToArray();
        foreach (var item in ((MenuFlyout)menus[0].Flyout!).Items.OfType<MenuItem>())
        {
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var index = int.Parse((string)item.Tag!);
            await Until(() => item.Header?.ToString() is "Ascending" or "Descending"
                ? context.Vm.SortDirectionIndex == index : context.Vm.SortIndex == index);
            await context.Vm.ApplyListOptionsAsync();
            Assert.NotEmpty(context.Vm.Tasks);
        }
        foreach (var item in ((MenuFlyout)menus[1].Flyout!).Items.OfType<MenuItem>())
        {
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => context.Vm.GroupIndex == int.Parse((string)item.Tag!));
            await context.Vm.ApplyListOptionsAsync();
            Assert.NotEmpty(context.Vm.TaskEntries);
        }
    });

    [Fact]
    public Task Every_project_sort_menu_and_filter_choice_remains_usable() => Run(async context =>
    {
        var menu = (MenuFlyout)context.Window.GetVisualDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetAutomationId(button) == "ProjectSortButton").Flyout!;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            var previous = context.Vm.ProjectSort;
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Task.Delay(35);
            var tag = (string)item.Tag!;
            if (tag == "Ascending") Assert.False(context.Vm.ProjectSort.Descending);
            else if (tag == "Descending") Assert.True(context.Vm.ProjectSort.Descending);
            else if (tag == "Favorites") Assert.NotEqual(previous.FavoritesFirst, context.Vm.ProjectSort.FavoritesFirst);
            else Assert.Equal(tag, context.Vm.ProjectSort.Mode.ToString());
        }
        var filter = context.Window.FindControl<Border>("ProjectsPane")!.GetVisualDescendants().OfType<ComboBox>().Single();
        for (var i = 0; i < filter.ItemCount; i++)
        {
            filter.SelectedIndex = i;
            await Task.Delay(20);
            Assert.Equal(i, context.Vm.ProjectFilterIndex);
        }
        filter.SelectedIndex = 0;
        context.Window.FindControl<TextBox>("ProjectSearchBox")!.Text = "missing project";
        await Until(() => context.Vm.ProjectItems.Count == 0);
        context.Window.FindControl<TextBox>("ProjectSearchBox")!.Text = "";
        await Until(() => context.Vm.ProjectItems.Count > 0);
    });

    [Fact]
    public Task Every_filter_dropdown_and_reset_chip_changes_the_bound_filter() => Run(async context =>
    {
        var trigger = context.Window.FindControl<WrapPanel>("TaskFilterCommands")!.Children.OfType<Button>().Last();
        var flyout = (Flyout)trigger.Flyout!;
        flyout.ShowAt(trigger);
        context.Window.UpdateLayout();
        var panel = (StackPanel)flyout.Content!;
        var filters = panel.Children.OfType<ComboBox>().ToArray();
        for (var i = 0; i < filters[0].ItemCount; i++)
        {
            filters[0].SelectedIndex = i;
            await Until(() => context.Vm.PriorityFilterIndex == i);
        }
        for (var i = 0; i < filters[1].ItemCount; i++)
        {
            filters[1].SelectedIndex = i;
            await Until(() => context.Vm.RepeatFilterIndex == i);
        }
        foreach (var option in context.Vm.LabelFilterOptions.ToArray())
        {
            filters[2].SelectedItem = option;
            await Until(() => context.Vm.SelectedLabelFilter == option);
        }
        Click(panel, "Reset filters");
        await Until(() => !context.Vm.HasActiveOptionFilters);
        flyout.Hide();
        context.Vm.PriorityFilterIndex = 1;
        context.Vm.RepeatFilterIndex = 1;
        context.Vm.SelectedLabelFilter = context.Vm.LabelFilterOptions.Last();
        await context.Vm.ApplyListOptionsAsync();
        var chips = context.Window.FindControl<WrapPanel>("ActiveFiltersPanel")!.Children.OfType<Button>().ToArray();
        foreach (var chip in chips.Take(3)) { chip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(30); }
        Assert.False(context.Vm.HasActiveOptionFilters);
        context.Vm.PriorityFilterIndex = 2;
        chips[3].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !context.Vm.HasActiveOptionFilters);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Persistent_toast_wraps_and_keeps_dismiss_inside_its_surface(bool dark) => Run(async context =>
    {
        context.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        context.Window.Width = 920;
        context.Vm.ReportError(string.Join(" ", Enumerable.Repeat("The backup could not be saved. Choose another location and try again.", 4)));
        context.Window.UpdateLayout();
        var toast = context.Window.FindControl<Border>("StatusOverlay")!;
        var dismiss = toast.GetVisualDescendants().OfType<Button>().Single();
        var location = dismiss.TranslatePoint(default, toast)!.Value;
        SaveFrame(context.Window, $"persistent-toast-{dark}");
        Assert.True(toast.IsVisible);
        Assert.True(location.X + dismiss.Bounds.Width <= toast.Bounds.Width - 1, "Toast dismissal overflows its surface.");
        Assert.True(location.Y + dismiss.Bounds.Height <= toast.Bounds.Height - 1);
        dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(toast.IsVisible);
        context.Vm.ReportStatus("Copied");
        Assert.True(toast.IsVisible);
        Assert.False(context.Vm.IsStatusMessagePersistent);
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public Task Project_editor_validates_name_and_color_and_saves_every_status(int status) => Run(async context =>
    {
        var dialog = new ProjectEditorWindow(new ProjectItem { Name = "", Color = "#808080" });
        var result = dialog.ShowDialog<ProjectEditDraft?>(context.Window);
        Click(dialog, "Save");
        Assert.False(result.IsCompleted);
        dialog.FindControl<TextBox>("NameBox")!.Text = "  Release 日本語 ✓  ";
        dialog.FindControl<TextBox>("ColorBox")!.Text = "invalid color";
        Click(dialog, "Save");
        Assert.True(dialog.FindControl<TextBlock>("ColorError")!.IsVisible);
        dialog.FindControl<TextBox>("ColorBox")!.Text = "#123456";
        dialog.FindControl<ComboBox>("StatusSelector")!.SelectedIndex = status;
        dialog.FindControl<CheckBox>("FavoriteBox")!.IsChecked = true;
        Click(dialog, "Save");
        var draft = await result;
        Assert.NotNull(draft);
        Assert.Equal("Release 日本語 ✓", draft.Name);
        Assert.Equal(new[] { "active", "completed", "archived" }[status], draft.Status);
        Assert.Equal("#123456", draft.Color);
        Assert.True(draft.IsFavorite);
    });

    [Fact]
    public Task Project_picker_filters_selects_existing_and_creates_new_without_duplicates() => Run(async context =>
    {
        var options = new[] { new PickerOption("", "Inbox"), new PickerOption("project", "Release") };
        var dialog = new ProjectPickerWindow(options, "project");
        var result = dialog.ShowDialog<ProjectPickerResult?>(context.Window);
        dialog.FindControl<TextBox>("SearchBox")!.Text = "Release";
        await Until(() => dialog.FindControl<ListBox>("ProjectsList")!.ItemCount == 1);
        Assert.False(dialog.FindControl<Button>("CreateProjectButton")!.IsVisible);
        Click(dialog, "Choose");
        Assert.Equal("project", (await result)!.ProjectId);
        dialog = new ProjectPickerWindow(options, "project");
        result = dialog.ShowDialog<ProjectPickerResult?>(context.Window);
        dialog.FindControl<TextBox>("SearchBox")!.Text = " New project ✓ ";
        await Until(() => dialog.FindControl<ListBox>("ProjectsList")!.ItemCount == 0);
        Assert.Empty(dialog.FindControl<ListBox>("ProjectsList")!.Items);
        dialog.FindControl<Button>("CreateProjectButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("New project ✓", (await result)!.NewProjectName);
    });

    [Fact]
    public Task Label_picker_retains_hidden_selection_and_normalizes_new_labels() => Run(async context =>
    {
        var dialog = new LabelPickerWindow(["alpha", "beta", "ALPHA"], ["alpha"]);
        var result = dialog.ShowDialog<string?>(context.Window);
        dialog.FindControl<TextBox>("SearchBox")!.Text = "beta";
        await Until(() => dialog.FindControl<ListBox>("LabelsList")!.ItemCount == 1);
        var choice = Assert.IsType<LabelChoice>(Assert.Single(dialog.FindControl<ListBox>("LabelsList")!.Items));
        choice.IsSelected = true;
        dialog.FindControl<TextBox>("NewLabelsBox")!.Text = " gamma, ALPHA, 日本語 , ";
        Click(dialog, "Apply");
        Assert.Equal("alpha, beta, gamma, 日本語", await result);
    });

    [Fact]
    public Task Option_picker_and_text_prompt_validate_selection_and_input() => Run(async context =>
    {
        var dialog = new OptionPickerWindow("Choose space", "", [], null);
        var result = dialog.ShowDialog<string?>(context.Window);
        Assert.False(FindButton(dialog, "Choose").IsEnabled);
        Click(dialog, "Cancel"); Assert.Null(await result);
        dialog = new OptionPickerWindow("Choose space", "", [new("a", "Alpha"), new("b", "Beta")], "a");
        result = dialog.ShowDialog<string?>(context.Window);
        dialog.FindControl<ListBox>("OptionsList")!.SelectedIndex = 1;
        Click(dialog, "Choose"); Assert.Equal("b", await result);
        var prompt = new TextPromptWindow("Rename", "");
        var textResult = prompt.ShowDialog<string?>(context.Window);
        var confirm = prompt.GetVisualDescendants().OfType<Button>().Single(button => button.Content?.ToString() != "Cancel");
        confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.False(textResult.IsCompleted);
        prompt.FindControl<TextBox>("ValueBox")!.Text = "  名前 ✓  ";
        confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal("名前 ✓", await textResult);
    });

    [Theory]
    [InlineData("Cancel", false)]
    [InlineData("Delete", true)]
    public Task Confirmation_returns_only_the_chosen_action(string action, bool expected) => Run(async context =>
    {
        var dialog = new ConfirmWindow("Delete synthetic task?", "Only the isolated test record is affected.", "Delete", true);
        var result = dialog.ShowDialog<bool>(context.Window);
        Click(dialog, action); Assert.Equal(expected, await result);
    });

    [Fact]
    public Task Search_scope_completed_filter_failure_and_retry_use_the_latest_result() => Run(async context =>
    {
        var calls = new List<(string, bool, bool)>();
        var dialog = new GlobalSearchWindow((query, all, completed) =>
        {
            calls.Add((query, all, completed));
            if (query == "failure") throw new IOException("Synthetic offline failure");
            return Task.FromResult<IReadOnlyList<GlobalSearchResult>>(query == "none" ? [] : [new() { Id = "task", Title = query }]);
        });
        var result = dialog.ShowDialog<GlobalSearchResult?>(context.Window);
        var input = dialog.FindControl<TextBox>("SearchBox")!;
        input.Text = "failure";
        await Until(() => dialog.FindControl<TextBlock>("ResultStatus")!.Text!.StartsWith("Search failed"));
        Assert.False(dialog.FindControl<Button>("OpenResultButton")!.IsEnabled);
        input.Text = "none"; await Until(() => dialog.FindControl<TextBlock>("ResultStatus")!.Text == "No matching tasks or projects.");
        input.Text = "Found ✓";
        dialog.FindControl<ComboBox>("ScopeSelector")!.SelectedIndex = 1;
        dialog.FindControl<CheckBox>("IncludeCompletedBox")!.IsChecked = true;
        Click(dialog, "Search");
        await Until(() => calls.Contains(("Found ✓", true, true)));
        Click(dialog, "Open selected"); Assert.Equal("Found ✓", (await result)!.Title);
    });

    [Fact]
    public Task Settings_theme_and_local_preferences_persist_and_empty_credentials_show_feedback() => Run(async context =>
    {
        context.Window.FindControl<Button>("SettingsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Window.FindControl<ScrollViewer>("SettingsWorkspace")!.IsVisible);
        var theme = context.Window.FindControl<ComboBox>("ShellThemePicker")!;
        foreach (var index in new[] { 1, 2, 0 })
        {
            theme.SelectedIndex = index;
            await Until(() => context.Preferences.Load().Theme == new[] { "System", "Light", "Dark" }[index]);
        }
        foreach (var toggle in context.Window.FindControl<ScrollViewer>("SettingsWorkspace")!.GetVisualDescendants().OfType<ToggleSwitch>()
            .Where(toggle => Equals(toggle.Content, "Automatic local restore points") || toggle.Name == "AutomaticSyncToggle"))
        {
            toggle.IsChecked = true; await Task.Delay(30);
            toggle.IsChecked = false; await Task.Delay(30);
        }
        Assert.False(context.Preferences.Load().AutomaticRestorePointsEnabled);
        Assert.False(context.Preferences.Load().AutomaticSyncEnabled);
        await context.Vm.ConnectTodoistAsync(); Assert.Contains("token", context.Vm.StatusMessage);
        await context.Vm.ConnectGitHubAsync(); Assert.Contains("token", context.Vm.StatusMessage);
        context.Vm.GitHubDefaultRepository = "invalid";
        await context.Vm.SaveGitHubDefaultRepositoryAsync(); Assert.Contains("owner/name", context.Vm.StatusMessage);
        var space = context.Window.FindControl<TextBox>("ShellNewSpaceBox")!;
        space.Text = "Control check space";
        space.GetLogicalAncestors().OfType<Grid>().First().Children.OfType<Button>().Single()
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Vm.EditableSpaceItems.Any(item => item.Title == "Control check space"));
        Assert.Equal("", space.Text);
    });

    [Fact]
    public Task Capture_exercises_every_status_priority_project_and_both_submit_actions() => Run(async context =>
    {
        for (var status = 0; status < 5; status++)
        for (var priority = 0; priority < 4; priority++)
        {
            var dialog = new AddTaskWindow(context.Vm);
            var result = dialog.ShowDialog<AddTaskDraft?>(context.Window);
            dialog.FindControl<TextBox>("TitleBox")!.Text = $"Capture {status}/{priority} ✓";
            dialog.FindControl<TextBox>("NotesBox")!.Text = "Line one\n日本語 नमस्ते";
            dialog.FindControl<ComboBox>("StatusPicker")!.SelectedIndex = status;
            dialog.FindControl<ComboBox>("PriorityPicker")!.SelectedIndex = priority;
            dialog.FindControl<ComboBox>("ProjectPicker")!.SelectedItem = context.Vm.ProjectOptions.Last();
            dialog.FindControl<CalendarDatePicker>("DatePicker")!.SelectedDate = DateTime.Today.AddDays(2);
            dialog.FindControl<AutoCompleteBox>("LabelsBox")!.Text = "review, new-label";
            var open = priority % 2 == 0;
            Click(dialog, open ? "Create and open" : "Create");
            var draft = await result;
            Assert.NotNull(draft);
            Assert.Equal(open, draft.OpenAfterCreate);
            await context.Vm.CreateTaskAsync(draft);
            var saved = Assert.Single(await context.Store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Open }), task => task.Title == $"Capture {status}/{priority} ✓");
            Assert.Equal(priority + 1, saved.Priority);
            Assert.Equal(new[] { TaskItemStatus.Inbox, TaskItemStatus.Next, TaskItemStatus.Waiting, TaskItemStatus.Someday, TaskItemStatus.None }[status], saved.Status);
            Assert.Equal(2, saved.Labels.Count);
            Assert.Equal(DateOnly.FromDateTime(DateTime.Today.AddDays(2)), saved.PlannedOn);
        }
    });

    [Fact]
    public Task Row_status_priority_and_date_controls_persist_every_choice() => Run(async context =>
    {
        Button RowButton(string action)
        {
            context.Window.UpdateLayout();
            return context.Window.FindControl<ListBox>("TaskList")!.GetVisualDescendants().OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == $"Change {action} for Control test 0");
        }
        foreach (var (text, expected) in new[] { ("Inbox", TaskItemStatus.Inbox), ("Next", TaskItemStatus.Next),
            ("Waiting For", TaskItemStatus.Waiting), ("Someday", TaskItemStatus.Someday), ("None", TaskItemStatus.None) })
        {
            var trigger = RowButton("status"); var flyout = (MenuFlyout)trigger.Flyout!;
            flyout.ShowAt(trigger);
            flyout.Items.OfType<MenuItem>().Single(item => Equals(item.Header, text)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await UntilAsync(async () => (await context.Store.GetTaskAsync("task-0"))!.Status == expected);
            flyout.Hide(); await context.Vm.ApplySearchAsync();
        }
        foreach (var (text, expected) in new[] { ("Urgent", 1), ("High", 2), ("Normal", 3), ("Low", 4) })
        {
            var trigger = RowButton("priority"); var flyout = (MenuFlyout)trigger.Flyout!;
            flyout.ShowAt(trigger);
            flyout.Items.OfType<MenuItem>().Single(item => Equals(item.Header, text)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await UntilAsync(async () => (await context.Store.GetTaskAsync("task-0"))!.Priority == expected);
            flyout.Hide(); await context.Vm.ApplySearchAsync();
        }
        var dateTrigger = RowButton("date"); var dateFlyout = (Flyout)dateTrigger.Flyout!;
        dateFlyout.ShowAt(dateTrigger);
        context.Window.UpdateLayout();
        await Task.Delay(50);
        var dateContent = (StackPanel)dateFlyout.Content!;
        var picker = dateContent.Children.OfType<CalendarDatePicker>().Single();
        Assert.IsType<TaskListEntryViewModel>(picker.DataContext);
        picker.SelectedDate = DateTime.Today.AddDays(8);
        await UntilAsync(async () => (await context.Store.GetTaskAsync("task-0"))!.PlannedOn == DateOnly.FromDateTime(DateTime.Today.AddDays(8)));
        Click(dateContent, "Clear date");
        await UntilAsync(async () => (await context.Store.GetTaskAsync("task-0"))!.PlannedOn is null);
        dateFlyout.Hide();
    });

    [Fact]
    public Task Routing_form_checks_labels_suggestions_destinations_and_no_label_mode() => Run(async context =>
    {
        var dialog = new TodoistRoutingRuleWindow(null, [new("review", "review")], [new("space", "Space")], [new("remote", "Remote project")]);
        var result = dialog.ShowDialog<Openza.Tasks.Core.Sync.TodoistRoutingRuleDraft?>(context.Window);
        Click(dialog, "Save"); Assert.False(result.IsCompleted);
        Assert.Contains("label", dialog.FindControl<TextBlock>("ValidationText")!.Text);
        dialog.FindControl<TextBox>("LabelBox")!.Text = "rev";
        await Until(() => dialog.FindControl<ListBox>("LabelSuggestionsList")!.ItemCount == 1);
        dialog.FindControl<ListBox>("LabelSuggestionsList")!.SelectedIndex = 0;
        Assert.Equal("review", dialog.FindControl<TextBox>("LabelBox")!.Text);
        dialog.FindControl<ComboBox>("MoveProjectBox")!.SelectedIndex = 1;
        Click(dialog, "Save"); var draft = await result;
        Assert.NotNull(draft); Assert.Equal("review", draft.Label); Assert.Equal("remote", draft.MoveToProjectId);
        dialog = new TodoistRoutingRuleWindow(null, [], [new("space", "Space")], []);
        result = dialog.ShowDialog<Openza.Tasks.Core.Sync.TodoistRoutingRuleDraft?>(context.Window);
        var noLabels = dialog.FindControl<CheckBox>("NoLabelsCheck")!;
        noLabels.IsChecked = true; noLabels.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(dialog.FindControl<StackPanel>("LabelSection")!.IsEnabled);
        Click(dialog, "Save"); Assert.True((await result)!.MatchNoLabels);
    });

    [Fact]
    public Task Legacy_settings_backup_selection_and_theme_use_the_same_preferences() => Run(async context =>
    {
        var settings = new Desktop.Settings.SettingsWindow(context.Vm);
        settings.Show(context.Window);
        await Task.Delay(50);
        context.Vm.SelectedRestorePoint = null;
        Assert.False(FindButton(settings, "Restore selected").IsEnabled);
        Assert.False(FindButton(settings, "Delete selected").IsEnabled);
        foreach (var (index, theme) in new[] { (1, "Light"), (2, "Dark"), (0, "System") })
        {
            settings.FindControl<ComboBox>("ThemePicker")!.SelectedIndex = index;
            await Until(() => context.Preferences.Load().Theme == theme);
        }
        Click(settings, "Create restore point");
        await Until(() => context.Vm.RestorePoints.Count > 0);
        context.Vm.SelectedRestorePoint = context.Vm.RestorePoints[0];
        var snapshot = context.Vm.SelectedRestorePoint;
        Assert.True(FindButton(settings, "Restore selected").IsEnabled);
        Assert.True(FindButton(settings, "Delete selected").IsEnabled);
        await context.Store.UpsertTaskAsync((await context.Store.GetTaskAsync("task-0"))! with { Title = "After legacy snapshot" });
        Click(settings, "Restore selected");
        await Until(() => settings.OwnedWindows.OfType<ConfirmWindow>().Any());
        Click(settings.OwnedWindows.OfType<ConfirmWindow>().Single(), "Restore");
        await Until(() => context.Vm.StatusMessage.Contains("safety restore point") && !context.Vm.IsBusy);
        Assert.Equal("Control test 0", (await context.Store.GetTaskAsync("task-0"))!.Title);
        context.Vm.SelectedRestorePoint = context.Vm.RestorePoints.Single(point => point.Backup.Path == snapshot.Backup.Path);
        Click(settings, "Delete selected");
        await Until(() => settings.OwnedWindows.OfType<ConfirmWindow>().Any());
        Click(settings.OwnedWindows.OfType<ConfirmWindow>().Single(), "Delete");
        await Until(() => !File.Exists(snapshot.Backup.Path));
        Click(settings, "Close"); Assert.False(settings.IsVisible);
    });

    [Fact]
    public Task Navigation_collapse_space_picker_and_all_navigation_destinations_remain_reachable() => Run(async context =>
    {
        var collapse = context.Window.FindControl<Button>("NavigationCollapseButton")!;
        collapse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Expand navigation", AutomationProperties.GetName(collapse));
        var compact = context.Window.FindControl<Button>("CompactSpacePickerButton")!;
        Assert.True(compact.IsVisible);
        compact.Flyout!.ShowAt(compact); context.Window.UpdateLayout(); SaveFrame(context.Window, "compact-space-picker"); compact.Flyout.Hide();
        collapse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Collapse navigation", AutomationProperties.GetName(collapse));
        foreach (var item in context.Vm.NavigationItems)
        {
            context.Window.FindControl<ListBox>("NavigationList")!.SelectedItem = item;
            await Until(() => context.Vm.SelectedNavigation == item && !context.Vm.IsBusy);
            Assert.False(string.IsNullOrWhiteSpace(context.Vm.PageTitle));
        }
        var spacePicker = context.Window.FindControl<ComboBox>("SpacePicker")!;
        foreach (var space in context.Vm.SpaceItems.ToArray())
        {
            spacePicker.SelectedItem = space;
            await Until(() => context.Vm.SelectedSpace == space);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Every_main_menu_and_filter_popup_renders_in_both_themes(bool dark) => Run(async context =>
    {
        context.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var buttons = context.Window.GetVisualDescendants().OfType<Button>().Where(button => button.Flyout is not null && button.IsEffectivelyVisible).ToArray();
        var index = 0;
        foreach (var button in buttons)
        {
            button.Flyout!.ShowAt(button);
            context.Window.UpdateLayout();
            Assert.True(button.Flyout.IsOpen);
            SaveFrame(context.Window, $"menu-{dark}-{index++}");
            button.Flyout.Hide();
        }
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Inspector_completion_button_and_checkbox_complete_and_reopen(bool checkbox) => Run(async context =>
    {
        foreach (var completed in new[] { true, false })
        {
            await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind ==
                (completed ? TaskListKind.Open : TaskListKind.Completed)));
            await context.Vm.SelectTaskAsync(context.Vm.Tasks.Single(item => item.Task.Id == "task-0"));
            context.Window.UpdateLayout();
            var pane = context.Window.FindControl<Border>("DetailsPane")!;
            Button control = checkbox ? pane.GetVisualDescendants().OfType<CheckBox>().Single() :
                FindButton(pane, completed ? "Complete" : "Reopen");
            control.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(async () => (await context.Store.GetTaskAsync("task-0"))!.IsCompleted == completed);
            await Until(() => !context.Vm.IsBusy && control.IsEnabled);
        }
    });

    [Fact]
    public Task Inspector_inputs_autosave_and_copy_actions_use_current_content() => Run(async context =>
    {
        context.Window.FindControl<ListBox>("TaskList")!.SelectedItem = context.Vm.TaskEntries.Single(item => item.Task?.Task.Id == "task-0");
        await Until(() => context.Vm.SelectedTask?.Task.Id == "task-0" && !context.Vm.IsBusy && !context.Vm.IsUpdatingDetails);
        await Task.Delay(100);
        context.Window.UpdateLayout();
        var pane = context.Window.FindControl<Border>("DetailsPane")!;
        T Named<T>(string name) where T : Control => pane.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetName(control) == name);
        foreach (var (name, value) in new[] { ("Task title", "Edited title 日本語"), ("Task notes", "Multiline notes\nSecond line नमस्ते") })
        {
            await Until(() => !context.Vm.IsBusy && !context.Vm.IsUpdatingDetails);
            var input = Named<TextBox>(name); Assert.True(input.Focus());
            await Task.Delay(50);
            input.Text = value;
            context.Window.FindControl<TextBox>("SearchBox")!.Focus();
            TaskItem? observed = null;
            await UntilAsync(async () =>
            {
                observed = await context.Store.GetTaskAsync("task-0");
                return (name == "Task title" ? observed!.Title : observed!.Notes) == value;
            }, () => $"{name}: input={input.Text}; draft={context.Vm.DetailTitle}/{context.Vm.DetailNotes}; persisted={observed?.Title}/{observed?.Notes}; selected={context.Vm.SelectedTask?.Task.Id}; status={context.Vm.StatusMessage}");
            await Task.Delay(100); // Let the saved row and editor bindings settle before the next field.
        }
        foreach (var priority in Enumerable.Range(0, 4))
        {
            await Until(() => !context.Vm.IsBusy && !context.Vm.IsUpdatingDetails);
            Named<ComboBox>("Task priority").SelectedIndex = priority;
            await UntilAsync(async () => (await context.Store.GetTaskAsync("task-0"))!.Priority == priority + 1);
        }
        foreach (var (name, deadline) in new[] { ("Task date", false), ("Task deadline", true) })
        {
            var picker = Named<CalendarDatePicker>(name);
            foreach (var date in new DateTime?[] { DateTime.Today.AddDays(15), null })
            {
                await Until(() => !context.Vm.IsBusy && !context.Vm.IsUpdatingDetails);
                picker.SelectedDate = date;
                await UntilAsync(async () =>
                {
                    var task = (await context.Store.GetTaskAsync("task-0"))!;
                    return (deadline ? task.DeadlineOn : task.PlannedOn) == (date is { } day ? DateOnly.FromDateTime(day) : null);
                });
            }
        }
        var labels = context.Window.FindControl<AutoCompleteBox>("DetailLabelsBox")!;
        labels.Text = "new, review";
        labels.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await UntilAsync(async () => (await context.Store.GetTaskAsync("task-0"))!.Labels.Any(label => label.Name == "new"));
        context.Window.UpdateLayout();
        var remove = pane.GetVisualDescendants().OfType<Button>().Single(button => button.DataContext is string label && label == "new");
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(async () => !(await context.Store.GetTaskAsync("task-0"))!.Labels.Any(label => label.Name == "new"));
        var copy = (MenuFlyout)FindButton(pane, "Copy").Flyout!;
        Assert.NotNull(context.Window.Clipboard);
        foreach (var item in copy.Items.OfType<MenuItem>().Where(item => item.IsVisible))
        {
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await UntilAsync(async () => await context.Window.Clipboard.TryGetTextAsync() == context.Vm.BuildTaskCopyText((string)item.Tag!));
            Assert.Equal("Copied", context.Vm.StatusMessage);
        }
        Named<Button>("Close task details").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !context.Vm.HasSelectedTask);
    });

    [Fact]
    public Task Row_project_labels_move_and_delete_dialogs_apply_to_the_clicked_task() => Run(async context =>
    {
        Button Row(string name)
        {
            context.Window.UpdateLayout();
            return context.Window.FindControl<ListBox>("TaskList")!.GetVisualDescendants().OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == name);
        }
        Row("Change project for Control test 0").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Window.OwnedWindows.OfType<ProjectPickerWindow>().Any());
        var project = context.Window.OwnedWindows.OfType<ProjectPickerWindow>().Single();
        project.FindControl<TextBox>("SearchBox")!.Text = "New row project";
        await Until(() => project.FindControl<Button>("CreateProjectButton")!.IsVisible);
        project.FindControl<Button>("CreateProjectButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(async () => (await context.Store.GetTaskAsync("task-0"))!.ProjectId != "project");
        await context.Vm.ApplySearchAsync();
        Row("Change labels for Control test 0").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Window.OwnedWindows.OfType<LabelPickerWindow>().Any());
        var labels = context.Window.OwnedWindows.OfType<LabelPickerWindow>().Single();
        labels.FindControl<TextBox>("NewLabelsBox")!.Text = "row-label";
        Click(labels, "Apply");
        await UntilAsync(async () => (await context.Store.GetTaskAsync("task-0"))!.Labels.Any(label => label.Name == "row-label"));
        await context.Vm.CreateSpaceAsync("Second space");
        await context.Vm.ApplySearchAsync();
        Row("Move Control test 0 to another Space").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Window.OwnedWindows.OfType<OptionPickerWindow>().Any());
        var spaces = context.Window.OwnedWindows.OfType<OptionPickerWindow>().Single();
        var options = spaces.FindControl<ListBox>("OptionsList")!;
        options.SelectedItem = options.Items.OfType<PickerOption>().Single(option => option.Title == "Second space");
        Click(spaces, "Choose");
        var target = context.Vm.EditableSpaceItems.Single(space => space.Title == "Second space").SpaceId;
        await UntilAsync(async () => (await context.Store.GetTaskAsync("task-0"))!.SpaceId == target);
        await context.Vm.SelectSpaceAsync(context.Vm.SpaceItems.Single(space => space.SpaceId == target));
        Row("Delete Control test 0").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Window.OwnedWindows.OfType<ConfirmWindow>().Any());
        Click(context.Window.OwnedWindows.OfType<ConfirmWindow>().Single(), "Cancel");
        Assert.NotNull(await context.Store.GetTaskAsync("task-0"));
        Row("Delete Control test 0").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Window.OwnedWindows.OfType<ConfirmWindow>().Any());
        Click(context.Window.OwnedWindows.OfType<ConfirmWindow>().Single(), "Delete");
        await UntilAsync(async () => await context.Store.GetTaskAsync("task-0") is null);
    });

    [Fact]
    public Task Passphrase_dialog_rejects_empty_mismatch_and_saves_only_confirmed_value() => Run(async context =>
    {
        context.Window.FindControl<Button>("SettingsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Window.FindControl<ScrollViewer>("SettingsWorkspace")!.IsVisible);
        context.Window.UpdateLayout();
        Click(context.Window, "Change passphrase");
        await Until(() => context.Window.OwnedWindows.Any(window => window.Title == "Backup passphrase"));
        var dialog = context.Window.OwnedWindows.Single(window => window.Title == "Backup passphrase");
        var inputs = dialog.GetVisualDescendants().OfType<TextBox>().ToArray();
        Assert.All(inputs, input => Assert.Equal('●', input.PasswordChar));
        Click(dialog, "Continue"); Assert.True(dialog.IsVisible);
        inputs[0].Text = "synthetic passphrase"; inputs[1].Text = "different";
        Click(dialog, "Continue"); Assert.True(dialog.IsVisible);
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Enter matching, non-empty passphrases.");
        inputs[1].Text = inputs[0].Text;
        Click(dialog, "Continue");
        await Until(() => context.Vm.OneDriveEncrypted);
        Click(context.Window, "Change passphrase");
        await Until(() => context.Window.OwnedWindows.Any(window => window.Title == "Backup passphrase"));
        Click(context.Window.OwnedWindows.Single(window => window.Title == "Backup passphrase"), "Cancel");
        Assert.True(context.Vm.OneDriveEncrypted);
    });

    [Theory]
    [InlineData("cancel")]
    [InlineData("success")]
    [InlineData("failure")]
    public Task Sign_in_dialog_handles_code_copy_completion_cancellation_and_error(string outcome) => Run(async context =>
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        var dialog = new SignInWindow("Synthetic sign-in", async (show, cancellation) =>
        {
            observedToken = cancellation;
            await show(new SignInCode("TEST-CODE", "http://invalid.example/", "Synthetic code"));
            await finish.Task.WaitAsync(cancellation);
            if (outcome == "failure") throw new IOException("Synthetic sign-in failed. Retry from Settings.");
        });
        var result = dialog.ShowDialog<bool>(context.Window);
        await Until(() => FindButton(dialog, "Copy code").IsEnabled);
        Assert.True(dialog.GetVisualDescendants().OfType<TextBox>().Single().IsReadOnly);
        Click(dialog, "Copy code");
        await UntilAsync(async () => await dialog.Clipboard!.TryGetTextAsync() == "TEST-CODE");
        Click(dialog, "Open sign-in page"); // Non-HTTPS URLs must not launch.
        SaveFrame(dialog, $"sign-in-{outcome}");
        if (outcome == "cancel") { Click(dialog, "Cancel"); Assert.False(await result); Assert.True(observedToken.IsCancellationRequested); }
        else
        {
            finish.SetResult();
            if (outcome == "success") Assert.True(await result);
            else
            {
                await Until(() => dialog.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, "Close")));
                Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), block => block.Text!.Contains("Synthetic sign-in failed"));
                Click(dialog, "Close"); Assert.False(await result);
            }
        }
    });

    [Fact]
    public Task Local_restore_buttons_confirm_restore_and_delete_only_the_selected_snapshot() => Run(async context =>
    {
        context.Window.FindControl<Button>("SettingsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var settings = context.Window.FindControl<ScrollViewer>("SettingsWorkspace")!;
        await Until(() => settings.IsVisible);
        context.Window.UpdateLayout();
        Click(settings, "Create restore point");
        await Until(() => context.Vm.RestorePoints.Count > 0);
        context.Vm.SelectedRestorePoint = context.Vm.RestorePoints[0];
        var snapshot = context.Vm.SelectedRestorePoint;
        await context.Store.UpsertTaskAsync((await context.Store.GetTaskAsync("task-0"))! with { Title = "After snapshot" });
        Click(settings, "Restore selected");
        await Until(() => context.Window.OwnedWindows.OfType<ConfirmWindow>().Any());
        Click(context.Window.OwnedWindows.OfType<ConfirmWindow>().Single(), "Cancel");
        Assert.Equal("After snapshot", (await context.Store.GetTaskAsync("task-0"))!.Title);
        Click(settings, "Restore selected");
        await Until(() => context.Window.OwnedWindows.OfType<ConfirmWindow>().Any());
        Click(context.Window.OwnedWindows.OfType<ConfirmWindow>().Single(), "Restore");
        await Until(() => context.Vm.StatusMessage == "Database restored; a safety restore point was created" && !context.Vm.IsBusy);
        await UntilAsync(async () => (await context.Store.GetTaskAsync("task-0"))!.Title == "Control test 0");
        await Until(() => !context.Vm.IsBusy);
        context.Vm.SelectedRestorePoint = context.Vm.RestorePoints.Single(point => point.Backup.Path == snapshot.Backup.Path);
        Click(settings, "Delete selected");
        await Until(() => context.Window.OwnedWindows.OfType<ConfirmWindow>().Any());
        Click(context.Window.OwnedWindows.OfType<ConfirmWindow>().Single(), "Delete");
        await Until(() => !File.Exists(snapshot.Backup.Path));
        Assert.Equal("Control test 0", (await context.Store.GetTaskAsync("task-0"))!.Title);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Search_and_filter_refresh_save_pending_editor_changes(bool filter) => Run(async context =>
    {
        await context.Vm.SelectTaskAsync(context.Vm.Tasks.Single(item => item.Task.Id == "task-0"));
        context.Vm.DetailTitle = "Pending title";
        context.Vm.DetailNotes = "Pending notes";
        if (filter) { context.Vm.PriorityFilterIndex = 1; await context.Vm.ApplyListOptionsAsync(); }
        else { context.Vm.SearchText = "Pending"; await context.Vm.ApplySearchAsync(); }
        var saved = (await context.Store.GetTaskAsync("task-0"))!;
        Assert.Equal("Pending title", saved.Title); Assert.Equal("Pending notes", saved.Notes);
        Assert.Equal("task-0", Assert.Single(context.Vm.Tasks).Task.Id);
    });

    private static async Task UntilAsync(Func<Task<bool>> condition, Func<string>? diagnostics = null)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!await condition() && DateTime.UtcNow < end) await Task.Delay(10);
        Assert.True(await condition(), diagnostics?.Invoke() ?? "Expected persisted control state was not reached.");
    }

    private Task Run(Func<Context, Task> action) => fixture.Session.Dispatch(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "openza-controls", Guid.NewGuid().ToString("N"));
        var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
        await store.InitializeAsync();
        await store.UpsertProjectAsync(new ProjectItem { Id = "project", Name = "Release", Color = "#123456" });
        for (var i = 0; i < 4; i++)
            await store.UpsertTaskAsync(new TaskItem { Id = $"task-{i}", Title = $"Control test {i}", ProjectId = "project", Priority = i + 1,
                Status = TaskItemStatus.Next, PlannedOn = DateOnly.FromDateTime(DateTime.Today).AddDays(i), Labels = [new LabelItem { Name = "review" }], Notes = "Synthetic notes" });
        var preferences = new DesktopPreferencesStore(Path.Combine(directory, "settings.json"));
        await preferences.SaveAsync(new() { AutomaticSyncEnabled = false, AutomaticRestorePointsEnabled = false, ShowGetStarted = false });
        var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences);
        var window = new MainWindow(vm) { Width = 1440, Height = 900, WindowState = WindowState.Normal };
        try
        {
            window.Show(); await Until(() => vm.StatusMessage.StartsWith("Local data") && !vm.IsBusy);
            await Task.Delay(50);
            await vm.SelectNavigationAsync(vm.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
            window.UpdateLayout();
            await action(new(window, vm, store, preferences));
        }
        finally
        {
            foreach (var child in window.OwnedWindows.ToArray()) child.Close();
            window.Close(); await Task.Delay(100); TestDirectory.Delete(directory);
        }
        return true;
    }, CancellationToken.None);

    private sealed record Context(MainWindow Window, MainWindowViewModel Vm, SqliteTaskStore Store, DesktopPreferencesStore Preferences);
    private static Button FindButton(Control root, string text) => root.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, text));
    private static void Click(Control root, string text) => FindButton(root, text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Until(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(10);
        Assert.True(condition(), "Expected control state was not reached.");
    }
    private static void SaveFrame(Window window, string name)
    {
        var output = Environment.GetEnvironmentVariable("OPENZA_CONTROL_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(output, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
