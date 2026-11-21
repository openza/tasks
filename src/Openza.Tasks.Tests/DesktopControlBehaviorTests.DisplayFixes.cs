using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Desktop.Dialogs;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed partial class DesktopControlBehaviorTests
{
    [Fact]
    public Task Display_fix_surfaces() => Run(async context =>
    {
        var prefix = Environment.GetEnvironmentVariable("OPENZA_DISPLAY_PHASE") ?? "after";
        var dialog = new ProjectPickerWindow([new("project", "Release")], "project");
        dialog.Show(context.Window);
        await Task.Delay(80);
        Assert.Equal("Change project", dialog.Title);
        SaveFrame(dialog, $"{prefix}_1_project");
        dialog.Close();
        var picker = context.Window.FindControl<ComboBox>("SpacePicker")!;
        var selectedTitle = picker.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == context.Vm.SelectedSpace!.Title);
        Assert.True(selectedTitle.Bounds.Width >= selectedTitle.DesiredSize.Width);
        context.Window.FindControl<Button>("NavigationCollapseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var compact = context.Window.FindControl<Button>("CompactSpacePickerButton")!;
        compact.Flyout!.ShowAt(compact);
        await Task.Delay(80);
        var menu = (ItemsControl)((Flyout)compact.Flyout).Content!;
        Assert.DoesNotContain(menu.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Avalonia.Controls.StackPanel");
        Assert.All(context.Vm.SpaceItems, space => Assert.Contains(menu.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == space.Title));
        SaveFrame(context.Window, $"{prefix}_3_space_menu");
        compact.Flyout.Hide();
        context.Window.FindControl<Button>("SettingsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var settings = context.Window.FindControl<ScrollViewer>("SettingsWorkspace")!;
        settings.ScrollToEnd();
        await Task.Delay(80);
        Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == $"App version: {context.Vm.AppVersion}");
        if (!OperatingSystem.IsWindows()) Assert.Equal("MSIX package: Not packaged", context.Vm.PackageVersionText);
        SaveFrame(context.Window, $"{prefix}_4_about");
        await context.Store.UpsertTaskAsync(new TaskItem { Id = "done", Title = "Completed example", Status = TaskItemStatus.Completed, WorkflowStatus = TaskWorkflowStatus.Next });
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Completed));
        await Task.Delay(80);
        context.Window.UpdateLayout();
        var completedRow = context.Window.FindControl<ListBox>("TaskList")!.GetVisualDescendants().OfType<Border>()
            .Single(row => row.Classes.Contains("task-row") && row.IsEffectivelyVisible
                && row.DataContext is TaskListEntryViewModel { Task.Task.Id: "done" });
        var renderedText = completedRow.GetVisualDescendants().OfType<TextBlock>()
            .Select(text => text.Text).Where(text => text is not null).ToArray();
        Assert.Contains("Completed example", renderedText);
        Assert.DoesNotContain(renderedText, text => text == "Next" || text!.Contains("· Next", StringComparison.Ordinal));
        SaveFrame(context.Window, $"{prefix}_5_completed");
    });

    [Theory]
    [InlineData("A", false)]
    [InlineData("Work", false)]
    [InlineData("A long Space name for planning projects across the whole team", true)]
    public Task Space_picker_constrains_long_names_without_clipping_short_names(string name, bool trimmed) => Run(async context =>
    {
        await context.Vm.RenameSpaceAsync(context.Vm.SelectedSpace!, name);
        await Task.Delay(80);
        context.Window.UpdateLayout();
        var picker = context.Window.FindControl<ComboBox>("SpacePicker")!;
        var title = picker.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == name);
        var content = (Grid)title.Parent!;
        Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
        Assert.InRange(title.Bounds.Width, 1, content.Bounds.Width);
        Assert.InRange(title.DesiredSize.Width, 1, content.Bounds.Width);
        Assert.True(content.Bounds.Width < picker.Bounds.Width);
        var naturalTitle = new TextBlock { Text = name, FontFamily = title.FontFamily, FontSize = title.FontSize, FontWeight = title.FontWeight };
        naturalTitle.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
        if (trimmed) Assert.True(title.Bounds.Width < naturalTitle.DesiredSize.Width);
        else Assert.True(title.Bounds.Width >= naturalTitle.DesiredSize.Width);
        if (name != "A") SaveFrame(context.Window, trimmed ? "after_2_space_long" : "after_2_space");
    });

    [Fact]
    public Task Long_space_names_trim_in_dropdown_and_collapsed_menu() => Run(async context =>
    {
        const string name = "Northern Operations and International Delivery Coordination";
        await context.Vm.RenameSpaceAsync(context.Vm.SelectedSpace!, name);
        var picker = context.Window.FindControl<ComboBox>("SpacePicker")!;
        picker.IsDropDownOpen = true;
        await Task.Delay(80);
        context.Window.UpdateLayout();
        var popup = picker.GetVisualDescendants().OfType<Popup>().Single();
        var dropdownTitle = popup.Child!.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == name);
        AssertTrimmed(dropdownTitle);
        picker.IsDropDownOpen = false;
        context.Window.FindControl<Button>("NavigationCollapseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var compact = context.Window.FindControl<Button>("CompactSpacePickerButton")!;
        compact.Flyout!.ShowAt(compact);
        await Task.Delay(80);
        context.Window.UpdateLayout();
        var menu = (ItemsControl)((Flyout)compact.Flyout).Content!;
        var row = menu.GetVisualDescendants().OfType<Button>()
            .Single(button => button.DataContext is SpaceNavigationItemViewModel { Title: name });
        var title = row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == name);
        AssertTrimmed(title);
        Assert.True(title.Bounds.Width < row.Bounds.Width);
        Assert.InRange(row.Bounds.Width, 1, 320);
        SaveFrame(context.Window, "after_3_space_menu_long");
        compact.Flyout.Hide();
    });

    private static void AssertTrimmed(TextBlock title)
    {
        Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
        var content = (Grid)title.Parent!;
        Assert.InRange(title.Bounds.Width, 1, content.Bounds.Width);
        Assert.InRange(title.DesiredSize.Width, 1, content.Bounds.Width);
        var naturalTitle = new TextBlock { Text = title.Text, FontFamily = title.FontFamily, FontSize = title.FontSize, FontWeight = title.FontWeight };
        naturalTitle.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
        Assert.True(title.Bounds.Width < naturalTitle.DesiredSize.Width);
    }

    [Theory]
    [InlineData(TaskWorkflowStatus.Next)]
    [InlineData(TaskWorkflowStatus.None)]
    [InlineData(TaskWorkflowStatus.Inbox)]
    [InlineData(TaskWorkflowStatus.Waiting)]
    [InlineData(TaskWorkflowStatus.Someday)]
    public void Completed_row_wording_preserves_workflow_status(TaskWorkflowStatus status)
    {
        var task = new TaskItem { WorkflowStatus = status, CompletionState = TaskCompletionState.Completed };
        var row = new TaskListItemViewModel(task, null);
        Assert.Empty(row.StatusText);
        Assert.Equal(status, row.Task.WorkflowStatus);
        var open = new TaskListItemViewModel(task with { CompletionState = TaskCompletionState.Open }, null);
        Assert.NotEqual("Completed", open.StatusText);
        if (status == TaskWorkflowStatus.Next) Assert.Equal("Next", open.StatusText);
        if (status == TaskWorkflowStatus.None) Assert.Empty(open.StatusText);
    }
}
