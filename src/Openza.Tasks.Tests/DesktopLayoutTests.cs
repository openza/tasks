using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Input;
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
public sealed class DesktopLayoutTests(EditorTestSession fixture)
{
    [Theory]
    [InlineData(520, 460)]
    [InlineData(560, 660)]
    public async Task Capture_date_fits_its_column_and_actions_remain_visible(double width, double height)
    {
        await fixture.Session.Dispatch(() =>
        {
            var window = new AddTaskWindow { Width = width, Height = height };
            try
            {
                window.Show();
                window.UpdateLayout();
                var date = window.FindControl<CalendarDatePicker>("DatePicker")!;
                var position = date.TranslatePoint(default, window)!.Value;
                Assert.True(position.X + date.Bounds.Width <= window.ClientSize.Width - 20,
                    "The date field must fit inside the dialog, including at minimum width.");
                foreach (var button in window.GetVisualDescendants().OfType<Button>()
                    .Where(button => button.Content is "Cancel" or "Create" or "Create and open"))
                {
                    var point = button.TranslatePoint(default, window)!.Value;
                    Assert.InRange(point.Y + button.Bounds.Height, 1, window.ClientSize.Height);
                }
            }
            finally { window.Close(); }
            return Task.FromResult(true);
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(920)]
    [InlineData(1200)]
    [InlineData(1440)]
    [InlineData(1600)]
    public async Task Opening_task_details_preserves_a_usable_workspace(double width)
    {
        await fixture.Session.Dispatch(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "openza-layout", Guid.NewGuid().ToString("N"));
            var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
            await store.InitializeAsync();
            await store.UpsertProjectAsync(new ProjectItem { Id = "project", Name = "Design review" });
            await store.UpsertTaskAsync(new TaskItem { Id = "task", Title = "Review a long task title without losing the editor or task list", ProjectId = "project" });
            var preferences = new DesktopPreferencesStore(Path.Combine(directory, "settings.json"));
            await preferences.SaveAsync(new() { AutomaticSyncEnabled = false, AutomaticRestorePointsEnabled = false, ShowGetStarted = false });
            var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences);
            var window = new MainWindow(vm) { Width = width, Height = 760, WindowState = WindowState.Normal };
            try
            {
                window.Show();
                await WaitForAsync(() => vm.NavigationItems.Count > 0 && !vm.IsBusy);
                await vm.SelectNavigationAsync(vm.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
                window.UpdateLayout();
                var title = window.GetVisualDescendants().OfType<SelectableTextBlock>()
                    .Single(text => text.Text == "Review a long task title without losing the editor or task list");
                var titlePoint = title.TranslatePoint(new Point(30, 8), window)!.Value;
                window.MouseDown(titlePoint, MouseButton.Left);
                window.MouseUp(titlePoint, MouseButton.Left);
                await WaitForAsync(() => vm.HasSelectedTask);
                window.UpdateLayout();
                var list = window.FindControl<Grid>("TaskListPane")!;
                var details = window.FindControl<Border>("DetailsPane")!;
                Assert.True(details.IsVisible);
                Assert.True(details.Bounds.Width >= 420);
                if (width >= 1200)
                {
                    Assert.True(list.IsVisible);
                    Assert.True(list.Bounds.Width >= 420, $"Task list was squeezed to {list.Bounds.Width}.");
                }
                else Assert.False(list.IsVisible);

                window.FindControl<Button>("SyncNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitForAsync(() => window.FindControl<ScrollViewer>("SyncWorkspace")!.IsVisible);
                Assert.Contains("active", window.FindControl<Button>("SyncNavigationButton")!.Classes);
                Assert.Contains("navigation-inactive", window.FindControl<ListBox>("NavigationList")!.Classes);
            }
            finally { window.Close(); await Task.Delay(100); TestDirectory.Delete(directory); }
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task Dialog_surfaces_layout_in_both_themes(string theme)
    {
        await fixture.Session.Dispatch(() =>
        {
            Window[] windows = [new AddTaskWindow(), new ConfirmWindow("Restore backup?", "A safety copy will be created first.", "Restore", true),
                new TextPromptWindow("Rename space", "Design review"), new ProjectEditorWindow(), new ProjectPickerWindow(),
                new LabelPickerWindow(["design", "review"], ["design"]), new OptionPickerWindow(), new GlobalSearchWindow(),
                new PersonalCloudSetupWindow(), new PersonalCloudManageWindow(), new TodoistRoutingRuleWindow()];
            try
            {
                foreach (var window in windows)
                {
                    window.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
                    window.Show();
                    window.UpdateLayout();
                    Assert.True(window.ClientSize.Width > 0 && window.ClientSize.Height > 0);
                    foreach (var title in window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.Classes.Contains("dialog-title")))
                        Assert.Equal(22, title.FontSize);
                    var output = Environment.GetEnvironmentVariable("OPENZA_DESIGN_SCREENSHOTS");
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        Directory.CreateDirectory(output);
                        using var frame = window.CaptureRenderedFrame();
                        Assert.NotNull(frame);
                        frame.Save(Path.Combine(output, $"{window.GetType().Name}-{theme}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                    }
                    window.Close();
                }
            }
            finally { foreach (var window in windows) window.Close(); }
            return Task.FromResult(true);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Search_cannot_open_a_stale_result_while_the_next_query_is_pending()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var pending = new TaskCompletionSource<IReadOnlyList<GlobalSearchResult>>();
            var dialog = new GlobalSearchWindow((query, _, _) => query == "first"
                ? Task.FromResult<IReadOnlyList<GlobalSearchResult>>([new() { Id = "task", Title = "First result" }])
                : pending.Task);
            try
            {
                dialog.Show();
                var open = dialog.FindControl<Button>("OpenResultButton")!;
                Assert.False(open.IsEnabled);
                dialog.FindControl<TextBox>("SearchBox")!.Text = "first";
                await WaitForAsync(() => open.IsEnabled);
                dialog.FindControl<TextBox>("SearchBox")!.Text = "second";
                await WaitForAsync(() => !open.IsEnabled);
                Assert.Empty(dialog.FindControl<ListBox>("TaskResultsList")!.Items);
                pending.SetResult([]);
                await WaitForAsync(() => dialog.FindControl<TextBlock>("ResultStatus")!.Text == "No matching tasks or projects.");
                Assert.False(open.IsEnabled);
            }
            finally { pending.TrySetResult([]); dialog.Close(); }
            return true;
        }, CancellationToken.None);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition(), "The expected UI state was not reached.");
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task Navigation_and_settings_remain_reachable_with_realistic_content(string theme)
    {
        await fixture.Session.Dispatch(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "openza-screen-coverage", Guid.NewGuid().ToString("N"));
            var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
            await store.InitializeAsync();
            await store.UpsertProjectAsync(new ProjectItem { Id = "project", Name = "Product launch" });
            foreach (var (id, status, day) in new[] { ("Capture the launch checklist", TaskItemStatus.Inbox, 0),
                ("Review the release notes", TaskItemStatus.Next, 0), ("Confirm the final artwork", TaskItemStatus.Next, -1),
                ("Plan the next milestone", TaskItemStatus.Next, 3), ("Supplier confirmation", TaskItemStatus.Waiting, 2),
                ("Explore a new direction", TaskItemStatus.Someday, 6), ("Publish the first draft", TaskItemStatus.Completed, 0) })
                await store.UpsertTaskAsync(new TaskItem { Id = id, Title = id, Status = status, ProjectId = "project",
                    PlannedOn = DateOnly.FromDateTime(DateTime.Today).AddDays(day), Notes = "Synthetic interface verification data." });
            var preferences = new DesktopPreferencesStore(Path.Combine(directory, "settings.json"));
            await preferences.SaveAsync(new() { AutomaticSyncEnabled = false, AutomaticRestorePointsEnabled = false, ShowGetStarted = false });
            var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences);
            var window = new MainWindow(vm) { Width = 1280, Height = 800, WindowState = WindowState.Normal,
                RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light };
            try
            {
                window.Show();
                await WaitForAsync(() => vm.NavigationItems.Count > 0 && !vm.IsBusy);
                foreach (var item in vm.NavigationItems)
                {
                    window.FindControl<ListBox>("NavigationList")!.SelectedItem = item;
                    await WaitForAsync(() => vm.SelectedNavigation == item && !vm.IsBusy);
                    window.UpdateLayout();
                    Assert.True(window.FindControl<Grid>("TaskListPane")!.IsVisible);
                    Assert.False(string.IsNullOrWhiteSpace(vm.PageTitle));
                    SaveFrame(window, $"Workspace-{item.Kind}-{theme}");
                }
                foreach (var page in new[] { "Sync", "Settings" })
                {
                    window.FindControl<Button>($"{page}NavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var scroll = window.FindControl<ScrollViewer>($"{page}Workspace")!;
                    await WaitForAsync(() => scroll.IsVisible);
                    window.UpdateLayout();
                    SaveFrame(window, $"Workspace-{page}-{theme}");
                    if (page == "Settings")
                    {
                        window.Width = 920;
                        window.UpdateLayout();
                        var input = window.FindControl<TextBox>("ShellNewSpaceBox")!;
                        var point = input.TranslatePoint(default, window)!.Value;
                        Assert.True(point.X + input.Bounds.Width <= window.ClientSize.Width);
                        SaveFrame(window, $"Workspace-Settings-narrow-{theme}");
                        scroll.Offset = new Vector(0, scroll.Extent.Height);
                        window.UpdateLayout();
                        SaveFrame(window, $"Workspace-Recovery-{theme}");
                    }
                }
                Window[] secondary = [new Desktop.Settings.SettingsWindow(vm), new Desktop.Connected.ConnectedTasksWindow(vm),
                    new GitHubIssueWindow(vm, new("task", "Review the release notes", "Synthetic issue draft", "", [], null)),
                    new SignInWindow("Sign in", async (show, cancellation) => {
                        await show(new("DEMO-CODE", "https://example.com", "Synthetic sign-in preview"));
                        await Task.Delay(Timeout.Infinite, cancellation);
                    })];
                foreach (var dialog in secondary)
                {
                    try
                    {
                        dialog.RequestedThemeVariant = window.RequestedThemeVariant;
                        dialog.Show();
                        await Task.Delay(50);
                        dialog.UpdateLayout();
                        SaveFrame(dialog, $"{dialog.GetType().Name}-{theme}");
                    }
                    finally { dialog.Close(); }
                }
            }
            finally { window.Close(); await Task.Delay(100); TestDirectory.Delete(directory); }
            return true;
        }, CancellationToken.None);
    }

    private static void SaveFrame(Window window, string name)
    {
        var output = Environment.GetEnvironmentVariable("OPENZA_DESIGN_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(output, $"{name}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
