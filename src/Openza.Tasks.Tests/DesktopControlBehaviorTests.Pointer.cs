using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    public Task Sidebar_commands_respond_to_pointer_at_row_end(bool compact) => Run(async context =>
    {
        var window = context.Window;
        await Until(() => !context.Vm.IsBusy && !context.Vm.IsUpdatingDetails);
        if (compact) await PointerClick(window, window.FindControl<Button>("NavigationCollapseButton")!);
        foreach (var (name, workspace) in new[] { ("Sync", "SyncWorkspace"), ("Settings", "SettingsWorkspace") })
        {
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);
            await PointerClick(window, button, rowWidth: compact ? 47 : 191);
            await Until(() => window.FindControl<ScrollViewer>(workspace)!.IsVisible);
            await Until(() => !context.Vm.IsBusy && !context.Vm.IsUpdatingDetails);
            await Task.Delay(100);
        }
        var search = window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Search tasks and projects");
        await PointerClick(window, search, rowWidth: compact ? 47 : 191);
        await Until(() => window.OwnedWindows.OfType<GlobalSearchWindow>().Any());
        window.OwnedWindows.OfType<GlobalSearchWindow>().Single().Close();
    });

    [Fact]
    public Task Group_header_blank_right_side_collapses_and_expands_tasks() => Run(async context =>
    {
        context.Vm.GroupIndex = 3;
        await context.Vm.ApplyListOptionsAsync();
        await Until(() => !context.Vm.IsBusy && !context.Vm.IsUpdatingDetails);
        // Let the bound grouping selector finish refreshing the virtualized rows.
        await Task.Delay(150);
        var list = context.Window.FindControl<ListBox>("TaskList")!;
        context.Window.UpdateLayout();
        Button Header() => list.GetVisualDescendants().OfType<Button>().First(b => b.DataContext is TaskListEntryViewModel { IsHeader: true } && b.IsVisible);
        var before = context.Vm.TaskEntries.Count;
        Assert.True(Header().Bounds.Width >= list.Bounds.Width - 30, "Group headers must fill the row up to the list's scrollbar gutter.");
        await PointerClick(context.Window, Header, rightInset: 100);
        await Until(() => context.Vm.TaskEntries.Count < before);
        await Until(() => !context.Vm.IsBusy && !context.Vm.IsUpdatingDetails);
        await Task.Delay(100);
        context.Window.UpdateLayout();
        await PointerClick(context.Window, Header, rightInset: 100);
        await Until(() => context.Vm.TaskEntries.Count == before);
    });

    [Fact]
    public Task Label_row_blank_area_toggles_the_label_and_Apply_persists_it() => Run(async context =>
    {
        var dialog = new LabelPickerWindow(["review"], []);
        var result = dialog.ShowDialog<string?>(context.Window);
        await Task.Delay(60); dialog.UpdateLayout();
        var list = dialog.FindControl<ListBox>("LabelsList")!;
        var row = list.GetVisualDescendants().OfType<ListBoxItem>().Single();
        await PointerClick(dialog, row);
        var choice = Assert.IsType<LabelChoice>(Assert.Single(list.Items));
        Assert.True(choice.IsSelected, "Clicking the label row's blank area must toggle the label, not just select its container.");
        await PointerClick(dialog, FindButton(dialog, "Apply"));
        Assert.Equal("review", await result);
    });

    [Fact]
    public Task Popup_actions_cover_the_width_of_their_menu() => Run(async context =>
    {
        var more = context.Window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "More actions");
        more.Flyout!.ShowAt(more); await Task.Delay(60);
        var panel = Assert.IsType<StackPanel>(((Flyout)more.Flyout).Content);
        foreach (var button in panel.Children.OfType<Button>())
            Assert.True(button.Bounds.Width >= panel.Bounds.Width - 1, $"{button.Content} leaves an unclickable strip in its menu.");
        more.Flyout.Hide();
        await context.Vm.CreateSpaceAsync("A much longer destination name for menu width");
        await context.Vm.SelectTaskAsync(context.Vm.Tasks.First());
        context.Window.UpdateLayout();
        var move = FindButton(context.Window.FindControl<Border>("DetailsPane")!, "Move to space");
        move.Flyout!.ShowAt(move); await Task.Delay(60);
        var choices = Assert.IsType<ItemsControl>(((Flyout)move.Flyout).Content);
        foreach (var button in choices.GetVisualDescendants().OfType<Button>())
            Assert.True(button.Bounds.Width >= choices.Bounds.Width - 1, $"{button.Content} leaves an unclickable strip in the space menu.");
        move.Flyout.Hide();
    });

    [Theory]
    [InlineData(920)]
    [InlineData(1200)]
    [InlineData(1440)]
    public Task Connected_filters_stay_inside_the_available_pane(double width) => Run(async context =>
    {
        context.Window.Width = width;
        await context.Store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "pointer-source", IntegrationId = IntegrationIds.Todoist, ProviderConnectionId = "todoist_default",
            ExternalId = "pointer-external", ProviderTaskId = "pointer-external", Title = "Synthetic connected task",
            SuggestedSpaceId = SpaceIds.Default, SnapshotJson = "{}",
        });
        await context.Vm.SelectNavigationAsync(context.Vm.NavigationItems.Single(item => item.Kind == TaskListKind.Inbox));
        await Until(() => !context.Vm.IsBusy && !context.Vm.IsUpdatingDetails);
        await Task.Delay(100);
        context.Window.FindControl<Button>("ConnectedTasksCommand")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var pane = context.Window.FindControl<Border>("ConnectedPane")!;
        await Until(() => pane.IsVisible); await Task.Delay(60); context.Window.UpdateLayout();
        foreach (var control in pane.GetVisualDescendants().OfType<Control>().Where(c => c is ComboBox or ToggleSwitch))
        {
            var origin = control.TranslatePoint(default, pane)!.Value;
            Assert.True(origin.X >= 0 && origin.X + control.Bounds.Width <= pane.Bounds.Width,
                $"{AutomationProperties.GetName(control)} is clipped: {origin.X} + {control.Bounds.Width} > {pane.Bounds.Width}");
        }
        SaveFrame(context.Window, $"connected-filters-{width}");
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Picker_rows_select_from_the_blank_right_edge(bool project) => Run(async context =>
    {
        Window dialog = project
            ? new ProjectPickerWindow([new("a", "Alpha"), new("b", "Beta")], "a")
            : new OptionPickerWindow("Choose", "", [new("a", "Alpha"), new("b", "Beta")], "a");
        dialog.Show(context.Window); await Task.Delay(60); dialog.UpdateLayout();
        var list = dialog.GetVisualDescendants().OfType<ListBox>().Single();
        var row = list.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.DataContext is PickerOption { Id: "b" });
        await PointerClick(dialog, row);
        Assert.Equal("b", Assert.IsType<PickerOption>(list.SelectedItem).Id);
        dialog.Close();
    });

    private static Task PointerClick(Window window, Control target, double? rowWidth = null) =>
        PointerClick(window, () => target, rowWidth);

    private static async Task PointerClick(Window window, Func<Control> findTarget, double? rowWidth = null, double rightInset = 5)
    {
        await Task.Delay(30);
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        // Resolve virtualized rows after rendering, which may recycle their controls.
        var target = findTarget();
        var point = target.TranslatePoint(new Point((rowWidth ?? target.Bounds.Width) - rightInset, target.Bounds.Height / 2), window)!.Value;
        var hit = window.InputHitTest(point) as Visual;
        Assert.True(hit == target || hit?.GetVisualAncestors().Contains(target) == true,
            $"Pointer at {point} missed {target.GetType().Name} ({target.Bounds}); hit {hit?.GetType().Name}.");
        // Move away first: collapsing a group replaces the control under the pointer.
        window.MouseMove(new Point(1, 1));
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        await Task.Delay(60);
    }
}
