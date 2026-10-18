using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Desktop.Connected;
using Openza.Tasks.Desktop.Dialogs;
using Openza.Tasks.Desktop.Settings;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed partial class DesktopControlBehaviorTests
{
    [Fact]
    public Task Unchanged_editor_save_preserves_action_error_feedback() => Run(async context =>
    {
        await context.Vm.SelectTaskAsync(context.Vm.Tasks.First());
        await context.Vm.SaveSelectedAsync();
        context.Vm.ReportError("Connect GitHub in Settings first.");
        Assert.True(await context.Vm.SaveSelectedAsync());
        Assert.Equal("Connect GitHub in Settings first.", context.Vm.StatusMessage);
        Assert.True(context.Vm.IsStatusMessagePersistent);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Minimum_size_dialogs_keep_content_and_actions_reachable(bool dark) => Run(async context =>
    {
        var theme = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var failures = new List<string>();
        const string longName = "A long project or label name used to check the entire control at a small window size";
        await context.Store.UpsertProjectAsync(new ProjectItem { Id = "long-project", Name = longName });
        context.Vm.ProjectOptions.Add(new ProjectOptionViewModel(new ProjectItem { Id = "long-project", Name = longName }));
        Window[] dialogs = [new AddTaskWindow(context.Vm), new ProjectEditorWindow(),
            new ProjectPickerWindow([new("long", longName)], "long"),
            new OptionPickerWindow("Choose a space", "Move the selected task.", [new("long", longName)], "long"),
            new LabelPickerWindow([longName, "review"], [longName]),
            new GlobalSearchWindow((_, _, _) => Task.FromResult<IReadOnlyList<GlobalSearchResult>>([
                new() { Id = "long", Title = longName, Subtitle = longName, Snippet = longName }])),
            new GitHubIssueWindow(context.Vm, new("task-0", longName, "Synthetic issue draft", "", [], null)),
            new TodoistRoutingRuleWindow(null, Enumerable.Range(0, 10).Select(i => new TodoistRoutingChoiceViewModel($"review-{i}", $"review-{i}")),
                [new("space", longName)], [new("project", longName)]),
            new ConnectedTasksWindow(context.Vm), new SettingsWindow(context.Vm)];
        foreach (var dialog in dialogs)
        {
            dialog.RequestedThemeVariant = theme;
            if (dialog.CanResize)
            {
                dialog.Width = Math.Max(400, dialog.MinWidth);
                dialog.Height = Math.Max(340, dialog.MinHeight);
            }
            dialog.Show(context.Window);
            await Task.Delay(80);
            if (dialog is ProjectEditorWindow)
            {
                dialog.FindControl<TextBox>("NameBox")!.Text = longName;
                dialog.FindControl<TextBox>("ColorBox")!.Text = "invalid";
                Click(dialog, "Save");
            }
            if (dialog is AddTaskWindow)
            {
                Click(dialog, "Create");
                var labels = dialog.FindControl<AutoCompleteBox>("LabelsBox")!;
                labels.Text = longName + longName;
                labels.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                labels.BringIntoView();
            }
            if (dialog is GlobalSearchWindow)
            {
                dialog.FindControl<TextBox>("SearchBox")!.Text = "project";
                await Until(() => dialog.FindControl<Button>("OpenResultButton")!.IsEnabled);
            }
            if (dialog is GitHubIssueWindow) Click(dialog, "Create issue");
            if (dialog is TodoistRoutingRuleWindow) dialog.FindControl<TextBox>("LabelBox")!.Text = "review";
            dialog.UpdateLayout();
            SaveFrame(dialog, $"minimum-{dialog.GetType().Name}-{dark}");
            if (dialog is GitHubIssueWindow)
            {
                var error = dialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Choose a repository and enter a title.");
                var errorBottom = error.TranslatePoint(new Point(0, error.Bounds.Height), dialog)!.Value.Y;
                if (errorBottom > FindButton(dialog, "Create issue").TranslatePoint(default, dialog)!.Value.Y)
                    failures.Add("GitHub validation is hidden below the form viewport.");
            }
            foreach (var button in dialog.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible &&
                b.Content is "Cancel" or "Save" or "Create" or "Create and open" or "Choose" or "Apply" or "Close" or "Open selected" or "Create issue"))
            {
                var point = button.TranslatePoint(default, dialog)!.Value;
                if (point.X < 0 || point.Y < 0 || point.X + button.Bounds.Width > dialog.ClientSize.Width + 1 || point.Y + button.Bounds.Height > dialog.ClientSize.Height + 1)
                    failures.Add($"{dialog.GetType().Name}: {button.Content} is outside the window.");
            }
            if (dialog is ProjectEditorWindow)
            {
                var favorite = dialog.FindControl<CheckBox>("FavoriteBox")!;
                var save = FindButton(dialog, "Save");
                if (favorite.TranslatePoint(new Point(0, favorite.Bounds.Height), dialog)!.Value.Y > save.TranslatePoint(default, dialog)!.Value.Y)
                    failures.Add("Project validation crowds the Favorite option into the action bar.");
            }
            if (dialog is AddTaskWindow)
            {
                var chip = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("labelChip"));
                chip.BringIntoView();
                await Task.Delay(50); dialog.UpdateLayout();
                SaveFrame(dialog, $"minimum-long-label-{dark}");
                var point = chip.TranslatePoint(default, dialog)!.Value;
                if (point.X + chip.Bounds.Width > dialog.ClientSize.Width) failures.Add("Long label chip is clipped horizontally.");
                var removeMark = chip.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "×");
                if (removeMark.TranslatePoint(new Point(removeMark.Bounds.Width, 0), chip)!.Value.X > chip.Bounds.Width)
                    failures.Add("Long label hides its remove mark.");
                Assert.StartsWith("Remove label ", AutomationProperties.GetName(chip));
            }
            dialog.Close();
        }
        var signIn = new SignInWindow("Sign-in failed", (_, _) => Task.FromException(new IOException(string.Join(" ", Enumerable.Repeat("The provider could not be reached. Check your connection and try again.", 10)))))
        { Width = 480, Height = 340, RequestedThemeVariant = theme };
        signIn.Show(context.Window);
        await Until(() => signIn.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "Close")));
        signIn.UpdateLayout();
        SaveFrame(signIn, $"minimum-sign-in-error-{dark}");
        var close = FindButton(signIn, "Close");
        if (close.TranslatePoint(new Point(0, close.Bounds.Height), signIn)!.Value.Y > signIn.ClientSize.Height)
            failures.Add("Sign-in error pushes Close outside the window.");
        signIn.Close();
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Minimum_workspace_settings_and_navigation_remain_reachable(bool dark) => Run(async context =>
    {
        var window = context.Window;
        window.Width = 920; window.Height = 600;
        window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        foreach (var item in context.Vm.NavigationItems)
        {
            window.FindControl<ListBox>("NavigationList")!.SelectedItem = item;
            await Until(() => context.Vm.SelectedNavigation == item && !context.Vm.IsBusy);
            window.UpdateLayout();
            SaveFrame(window, $"minimum-workspace-{item.Kind}-{dark}");
        }
        window.FindControl<Button>("SettingsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => window.FindControl<ScrollViewer>("SettingsWorkspace")!.IsVisible);
        window.UpdateLayout();
        foreach (var expander in window.FindControl<ScrollViewer>("SettingsWorkspace")!.GetVisualDescendants().OfType<Expander>()) expander.IsExpanded = true;
        await Task.Delay(200);
        window.UpdateLayout();
        foreach (var name in new[] { "Theme", "Todoist API token", "Default GitHub repository", "New space name", "Local restore points", "OneDrive backups" })
        {
            var control = window.GetVisualDescendants().OfType<Control>().Single(c => AutomationProperties.GetName(c) == name);
            control.BringIntoView(); window.UpdateLayout();
            SaveFrame(window, $"minimum-settings-{name.Replace(' ', '-')}-{dark}");
            var point = control.TranslatePoint(default, window)!.Value;
            Assert.InRange(point.X + control.Bounds.Width, 1, window.ClientSize.Width);
        }
    });
}
