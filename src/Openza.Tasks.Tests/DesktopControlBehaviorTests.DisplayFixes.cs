using Avalonia.Controls;
using Avalonia.Interactivity;
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
        SaveFrame(context.Window, $"{prefix}_2_space");
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
        SaveFrame(context.Window, $"{prefix}_5_completed");
    });

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
