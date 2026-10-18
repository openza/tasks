using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Desktop.Dialogs;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed partial class DesktopControlBehaviorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Picker_long_names_wrap_and_empty_subtitles_do_not_reserve_a_line(bool project) => Run(async context =>
    {
        const string title = "A very long project or space name that must remain readable when the picker is at its minimum width";
        Window dialog = project
            ? new ProjectPickerWindow([new("long", title), new("short", "Short name")], "long")
            : new OptionPickerWindow("Move to space", "Choose a destination.", [new("long", title), new("short", "Short name")], "long");
        dialog.Width = 400;
        dialog.Show(context.Window);
        await Task.Delay(60);
        dialog.UpdateLayout();
        var list = dialog.GetVisualDescendants().OfType<ListBox>().Single();
        var rows = list.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
        var longRow = rows.Single(row => row.DataContext is PickerOption { Id: "long" });
        var shortRow = rows.Single(row => row.DataContext is PickerOption { Id: "short" });
        var longText = longRow.GetVisualDescendants().OfType<TextBlock>().First();
        var shortText = shortRow.GetVisualDescendants().OfType<TextBlock>().First();
        Assert.True(longText.Bounds.Height > shortText.Bounds.Height * 2);
        Assert.All(rows, row => Assert.False(row.GetVisualDescendants().OfType<TextBlock>().Last().IsVisible));
        Assert.InRange(longText.Bounds.Width, 1, list.Bounds.Width);
        SaveFrame(dialog, $"picker-long-name-{project}");
        dialog.Close();
    });

    [Theory]
    [InlineData(920, false)]
    [InlineData(1200, false)]
    [InlineData(1440, false)]
    [InlineData(920, true)]
    [InlineData(1200, true)]
    [InlineData(1440, true)]
    public Task Task_rows_and_inspector_actions_align_with_real_content(double width, bool dark) => Run(async context =>
    {
        var window = context.Window;
        window.Width = width;
        window.Height = 720;
        window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        foreach (var task in new[]
        {
            new TaskItem { Id = "single", Title = "A short task", Status = TaskItemStatus.Inbox },
            new TaskItem { Id = "metadata", Title = "A task with metadata", Status = TaskItemStatus.Inbox,
                Labels = [new LabelItem { Name = "review" }], PlannedOn = DateOnly.FromDateTime(DateTime.Today) },
            new TaskItem { Id = "wrapped", Title = "A long task title that should wrap naturally without moving the completion checkbox away from the text block", Status = TaskItemStatus.Inbox },
        }) await context.Store.UpsertTaskAsync(task);
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Inbox));
        await Until(() => !context.Vm.IsBusy && !context.Vm.IsUpdatingDetails);
        await Task.Delay(100);
        window.UpdateLayout();
        var list = window.FindControl<ListBox>("TaskList")!;
        var rows = list.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("task-row") && border.IsEffectivelyVisible).ToArray();
        Assert.Equal(3, rows.Length);
        foreach (var row in rows)
        {
            var checkbox = row.GetVisualDescendants().OfType<CheckBox>().Single();
            var glyph = checkbox.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "NormalRectangle");
            var glyphTop = glyph.TranslatePoint(default, checkbox)!.Value.Y;
            Assert.InRange(glyphTop, 0, checkbox.Bounds.Height - glyph.Bounds.Height);
            var title = row.GetVisualDescendants().OfType<SelectableTextBlock>().First();
            var metadata = row.GetVisualDescendants().OfType<SelectableTextBlock>().Last();
            var textBlock = (StackPanel)title.Parent!;
            var checkboxCenter = checkbox.TranslatePoint(new Point(0, checkbox.Bounds.Height / 2), row)!.Value.Y;
            var textCenter = textBlock.TranslatePoint(new Point(0, textBlock.Bounds.Height / 2), row)!.Value.Y;
            Assert.InRange(Math.Abs(checkboxCenter - textCenter), 0, 1);
            Assert.Equal(!string.IsNullOrEmpty(metadata.Text), metadata.IsVisible);
            if (!metadata.IsVisible)
                Assert.InRange(Math.Abs(checkboxCenter - title.TranslatePoint(new Point(0, title.Bounds.Height / 2), row)!.Value.Y), 0, 1);
        }
        SaveFrame(window, $"rows-aligned-{width}-{dark}");

        await context.Vm.SelectTaskAsync(context.Vm.Tasks.Single(task => task.Task.Id == "wrapped"));
        await Until(() => !context.Vm.IsUpdatingDetails);
        await Task.Delay(100); window.UpdateLayout();
        var pane = window.FindControl<Border>("DetailsPane")!;
        var bar = window.FindControl<Grid>("DetailActionBar")!;
        Assert.True(pane.IsVisible);
        var icons = new[] { "DetailCopyButton", "DetailGitHubButton", "DetailMoveButton" }
            .Select(name => window.FindControl<Button>(name)!).ToArray();
        foreach (var button in icons)
        {
            Assert.True(button.Focusable);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
            Assert.NotNull(ToolTip.GetTip(button));
            Assert.IsNotType<string>(button.Content);
            Assert.Equal(36, button.Bounds.Width);
            Assert.Equal(36, button.Bounds.Height);
        }
        foreach (var button in bar.Children.OfType<Button>())
        {
            var point = button.TranslatePoint(default, pane)!.Value;
            Assert.InRange(point.X, 0, pane.Bounds.Width - button.Bounds.Width);
            Assert.InRange(point.Y, 0, pane.Bounds.Height - button.Bounds.Height);
        }
        var toast = window.FindControl<Border>("StatusOverlay")!;
        toast.IsVisible = true;
        window.UpdateLayout();
        var toastTop = toast.TranslatePoint(default, window)!.Value.Y;
        var paneBottom = pane.TranslatePoint(new Point(0, pane.Bounds.Height), window)!.Value.Y;
        Assert.True(toastTop >= paneBottom, "Notifications must not cover any inspector content or commands.");
        Assert.DoesNotContain(pane.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Create a GitHub issue from this task.");
        SaveFrame(window, $"inspector-actions-{width}-{dark}");
    });
}
