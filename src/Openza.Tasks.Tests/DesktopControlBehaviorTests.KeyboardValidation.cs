using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Openza.Tasks.Desktop.Dialogs;

namespace Openza.Tasks.Tests;

public sealed partial class DesktopControlBehaviorTests
{
    [Fact]
    public Task Labels_picker_space_toggles_focused_checkbox() => Run(async context =>
    {
        var dialog = new LabelPickerWindow(["Example label"], []);
        var result = dialog.ShowDialog<string?>(context.Window);
        dialog.UpdateLayout();
        var box = dialog.GetVisualDescendants().OfType<CheckBox>().Single();
        dialog.FindControl<ListBox>("LabelsList")!.GetVisualDescendants().OfType<ListBoxItem>().Single().Focus();
        dialog.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        dialog.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        SaveFrame(dialog, "1-labels-space");
        Assert.True(box.IsChecked);
        Assert.True(((LabelChoice)box.DataContext!).IsSelected);
        box.Focus();
        dialog.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        dialog.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Assert.False(box.IsChecked);
        Assert.False(((LabelChoice)box.DataContext!).IsSelected);
        dialog.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        dialog.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Click(dialog, "Apply");
        Assert.Equal("Example label", await result);
    });

    [Fact]
    public Task Space_picker_enter_confirms_selected_option() => Run(async context =>
    {
        var dialog = new OptionPickerWindow("Move to Space", "Select a destination.", [new("example", "Example")], "example");
        var result = dialog.ShowDialog<string?>(context.Window);
        dialog.UpdateLayout();
        dialog.FindControl<ListBox>("OptionsList")!.GetVisualDescendants().OfType<ListBoxItem>().Single().Focus();
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        if (!result.IsCompleted) dialog.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        SaveFrame(result.IsCompleted ? context.Window : dialog, "2-space-enter");
        Assert.True(result.IsCompleted);
        Assert.Equal("example", await result);
    });

    [Fact]
    public Task Empty_space_picker_enter_does_not_confirm() => Run(async context =>
    {
        var dialog = new OptionPickerWindow("Move to Space", "Select a destination.", [], null);
        var result = dialog.ShowDialog<string?>(context.Window);
        dialog.FindControl<ListBox>("OptionsList")!.Focus();
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.False(result.IsCompleted);
        Click(dialog, "Cancel");
        Assert.Null(await result);
    });

    [Fact]
    public Task Backup_switch_reverts_when_no_account_is_connected() => Run(async context =>
    {
        context.Window.FindControl<Button>("SettingsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var settings = context.Window.FindControl<ScrollViewer>("SettingsWorkspace")!;
        foreach (var expander in settings.GetVisualDescendants().OfType<Expander>()) expander.IsExpanded = true;
        context.Window.UpdateLayout();
        var toggle = settings.GetVisualDescendants().OfType<ToggleSwitch>().Single(control => Equals(control.Content, "Automatic OneDrive backup"));
        toggle.BringIntoView();
        await Task.Delay(100);
        settings.Offset += new Avalonia.Vector(0, 250);
        context.Window.UpdateLayout();
        var knob = toggle.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_SwitchKnob");
        var offPosition = knob.TranslatePoint(default, toggle)!.Value.X;
        var point = toggle.TranslatePoint(new Avalonia.Point(15, toggle.Bounds.Height - 10), context.Window)!.Value;
        context.Window.MouseMove(point);
        context.Window.MouseDown(point, MouseButton.Left);
        context.Window.MouseUp(point, MouseButton.Left);
        await Task.Delay(100);
        SaveFrame(context.Window, "4-backup-switch");
        Assert.False(context.Vm.OneDriveEnabled);
        Assert.False(toggle.IsChecked);
        Assert.Equal(offPosition, knob.TranslatePoint(default, toggle)!.Value.X);
        Assert.Contains("Connect a OneDrive account first", context.Vm.StatusMessage);
    });
}
