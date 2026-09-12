using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.Shell;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public static class EditorTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Desktop.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class EditorTestSession : IDisposable
{
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(EditorTestAppBuilder));
    public void Dispose() => Session.Dispose();
}

public sealed class DesktopEditorNavigationTests(EditorTestSession fixture) : IClassFixture<EditorTestSession>
{
    [Fact]
    public async Task Getting_started_banner_is_only_visible_in_empty_unfiltered_Inbox()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "openza-onboarding-ui", Guid.NewGuid().ToString("N"));
            var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
            await store.InitializeAsync();
            var preferences = new DesktopPreferencesStore(Path.Combine(directory, "settings.json"));
            await preferences.SaveAsync(new() { AutomaticSyncEnabled = false, AutomaticRestorePointsEnabled = false });
            var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences);
            var window = new MainWindow(vm) { Width = 1600, Height = 1000 };
            try
            {
                window.Show();
                var banner = window.FindControl<Border>("GetStartedPanel")!;
                await WaitForAsync(() => vm.SelectedNavigation is not null && !vm.IsBusy && banner.IsVisible);
                vm.SearchText = "filter";
                await vm.ApplySearchAsync();
                Assert.False(banner.IsVisible);
                vm.SearchText = "";
                await vm.SelectNavigationAsync(vm.NavigationItems.Single(item => item.Kind == TaskListKind.NextActions));
                Assert.False(banner.IsVisible);
                await vm.SelectNavigationAsync(vm.NavigationItems.Single(item => item.Kind == TaskListKind.Inbox));
                Assert.True(banner.IsVisible);
                await store.UpsertTaskAsync(new TaskItem { Id = "task", Title = "Captured task" });
                await vm.ApplySearchAsync();
                Assert.False(banner.IsVisible);
            }
            finally { window.Close(); await Task.Delay(100); TestDirectory.Delete(directory); }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Settings_write_failure_during_project_navigation_does_not_disable_later_navigation()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "openza-project-write-failure", Guid.NewGuid().ToString("N"));
            var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
            await store.InitializeAsync();
            await store.UpsertProjectAsync(new ProjectItem { Id = "a", Name = "Project A" });
            await store.UpsertProjectAsync(new ProjectItem { Id = "b", Name = "Project B" });
            await store.UpsertTaskAsync(new TaskItem { Id = "first", Title = "First", ProjectId = "a" });
            await store.UpsertTaskAsync(new TaskItem { Id = "second", Title = "Second", ProjectId = "b" });
            var preferencesPath = Path.Combine(directory, "settings.json");
            var preferences = new DesktopPreferencesStore(preferencesPath);
            await preferences.SaveAsync(new() { AutomaticSyncEnabled = false, AutomaticRestorePointsEnabled = false });
            var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences);
            var window = new MainWindow(vm) { Width = 1600, Height = 1000 };
            try
            {
                window.Show();
                await WaitForAsync(() => vm.ProjectItems.Count == 2 && !vm.IsBusy);
                await Task.Delay(100);
                window.FindControl<ListBox>("NavigationList")!.SelectedItem = vm.NavigationItems.Single(item => item.Kind == TaskListKind.Open);
                await WaitForAsync(() => vm.SelectedNavigation?.Kind == TaskListKind.Open && vm.Tasks.Count == 2 && !vm.IsBusy);
                await Task.Delay(100);
                var list = window.FindControl<ListBox>("ProjectList")!;
                File.Delete(preferencesPath); Directory.CreateDirectory(preferencesPath);
                list.SelectedItem = vm.ProjectItems.Single(item => item.Project.Id == "a");
                await WaitForAsync(() => vm.IsStatusMessagePersistent);
                Assert.Equal("Tasks", vm.PageTitle);
                Assert.Equal(TaskListKind.Open, vm.SelectedNavigation?.Kind);
                Directory.Delete(preferencesPath);
                list.SelectedItem = vm.ProjectItems.Single(item => item.Project.Id == "b");
                await WaitForAsync(() => vm.Tasks.Count == 1 && vm.Tasks[0].Task.Id == "second" && !vm.IsBusy);
            }
            finally { window.Close(); await Task.Delay(100); TestDirectory.Delete(directory); }
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Returning_to_project_through_bound_list_restores_its_task_selection(bool projectView, bool closeDetails, bool connectedDrawer)
    {
        await fixture.Session.Dispatch(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "openza-project-selection-tests", Guid.NewGuid().ToString("N"));
            var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
            await store.InitializeAsync();
            await store.UpsertProjectAsync(new ProjectItem { Id = "a", Name = "Project A" });
            await store.UpsertProjectAsync(new ProjectItem { Id = "b", Name = "Project B" });
            await store.UpsertTaskAsync(new TaskItem { Id = "first", Title = "First", ProjectId = projectView ? "a" : null });
            await store.UpsertTaskAsync(new TaskItem { Id = "second", Title = "Second", ProjectId = projectView ? "b" : null, Status = projectView ? TaskItemStatus.Inbox : TaskItemStatus.Next });
            var preferences = new DesktopPreferencesStore(Path.Combine(directory, "settings.json"));
            await preferences.SaveAsync(new() { AutomaticSyncEnabled = false, AutomaticRestorePointsEnabled = false });
            var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences);
            var window = new MainWindow(vm) { Width = 1600, Height = 1000 };
            try
            {
                window.Show();
                await WaitForAsync(() => vm.ProjectItems.Count == 2 && !vm.IsBusy);
                var projects = window.FindControl<ListBox>(projectView ? "ProjectList" : "NavigationList")!;
                object Destination(string id) => projectView ? vm.ProjectItems.Single(item => item.Project.Id == id)
                    : vm.NavigationItems.Single(item => item.Kind == (id == "a" ? TaskListKind.Inbox : TaskListKind.NextActions));
                var tasks = window.FindControl<ListBox>("TaskList")!;
                projects.SelectedItem = Destination("a");
                await WaitForAsync(() => vm.Tasks.Count == 1 && vm.Tasks[0].Task.Id == "first" && !vm.IsBusy);
                tasks.SelectedItem = vm.TaskEntries.Single(item => item.Task?.Task.Id == "first");
                await WaitForAsync(() => vm.SelectedTask?.Task.Id == "first" && !vm.IsBusy);
                if (connectedDrawer)
                {
                    window.FindControl<Button>("ConnectedTasksCommand")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    await WaitForAsync(() => vm.SelectedTask is null && !vm.IsBusy);
                    Assert.Null(tasks.SelectedItem);
                    var closeDrawer = window.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Close connected app tasks");
                    closeDrawer.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                }
                if (closeDetails)
                {
                    var close = window.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Close task details");
                    close.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    await WaitForAsync(() => vm.SelectedTask is null && !vm.IsBusy);
                }
                projects.SelectedItem = Destination("b");
                await WaitForAsync(() => vm.Tasks.Count == 1 && vm.Tasks[0].Task.Id == "second" && !vm.IsBusy);
                tasks.SelectedItem = vm.TaskEntries.Single(item => item.Task?.Task.Id == "second");
                await WaitForAsync(() => vm.SelectedTask?.Task.Id == "second" && !vm.IsBusy);
                projects.SelectedItem = Destination("a");
                await WaitForAsync(() => vm.Tasks.Count == 1 && vm.Tasks[0].Task.Id == "first" && !vm.IsBusy);
                Assert.Equal(closeDetails ? null : "first", vm.SelectedTask?.Task.Id);
            }
            finally { window.Close(); await Task.Delay(100); TestDirectory.Delete(directory); }
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Clicking_destination_from_editor_saves_and_navigates(bool projectClick, bool notes, bool edited)
    {
        await fixture.Session.Dispatch(async () =>
        {
            await RunScenarioAsync(projectClick, notes, edited);
            return true;
        }, CancellationToken.None);
    }

    private static async Task RunScenarioAsync(bool projectClick, bool notes, bool edited)
    {
        var directory = Path.Combine(Path.GetTempPath(), "openza-editor-navigation-tests", Guid.NewGuid().ToString("N"));
        var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
        await store.InitializeAsync();
        await store.UpsertProjectAsync(new ProjectItem { Id = "a", Name = "Project A" });
        await store.UpsertProjectAsync(new ProjectItem { Id = "b", Name = "Project B" });
        await store.UpsertTaskAsync(new TaskItem { Id = "first", Title = "First", Notes = "Description", ProjectId = "a" });
        await store.UpsertTaskAsync(new TaskItem { Id = "second", Title = "Second", ProjectId = "a" });
        await store.UpsertTaskAsync(new TaskItem { Id = "third", Title = "Third", ProjectId = "b" });
        var preferences = new DesktopPreferencesStore(Path.Combine(directory, "settings.json"));
        await preferences.SaveAsync(new() { AutomaticSyncEnabled = false });
        var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences);
        var window = new MainWindow(vm) { Width = 1600, Height = 1000 };
        try
        {
            window.Show();
            await WaitForAsync(() => vm.ProjectItems.Count == 2 && !vm.IsBusy);
            window.FindControl<ListBox>("ProjectList")!.SelectedItem = vm.ProjectItems.Single(item => item.Project.Id == "a");
            await WaitForAsync(() => vm.SelectedProject?.Project.Id == "a" && vm.Tasks.Count == 2 && !vm.IsBusy);
            window.FindControl<ListBox>("TaskList")!.SelectedItem = vm.TaskEntries.Single(item => item.Task?.Task.Id == "first");
            await WaitForAsync(() => vm.SelectedTask?.Task.Id == "first" && !vm.IsUpdatingDetails && !vm.IsBusy);
            await Task.Delay(100);
            var editor = window.GetVisualDescendants().OfType<TextBox>().Single(box => notes
                ? box.AcceptsReturn && box.Text == "Description"
                : AutomationProperties.GetName(box) == "Task title");
            Assert.True(editor.Focus());
            await Task.Delay(50);
            if (edited) editor.Text += " edited";
            var target = window.GetVisualDescendants().OfType<ListBoxItem>().Single(item => projectClick
                ? item.DataContext is ProjectNavigationItemViewModel { Project.Id: "b" }
                : item.DataContext is TaskListEntryViewModel { Task.Task.Id: "second" });
            var point = target.TranslatePoint(new Point(100, target.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            await Task.Delay(40);
            window.MouseUp(point, MouseButton.Left);
            await WaitForAsync(() => !vm.IsBusy && (projectClick
                ? vm.SelectedProject?.Project.Id == "b"
                : vm.SelectedTask?.Task.Id == "second"));

            var saved = (await store.GetTaskAsync("first"))!;
            Assert.Equal(edited && !notes ? "First edited" : "First", saved.Title);
            Assert.Equal(edited && notes ? "Description edited" : "Description", saved.Notes);
            Assert.Equal("a", saved.ProjectId);
            if (projectClick) Assert.Equal("third", Assert.Single(vm.Tasks).Task.Id);
            else Assert.Equal(2, vm.Tasks.Count);
        }
        finally
        {
            window.Close();
            await Task.Delay(100);
            TestDirectory.Delete(directory);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition(), "The expected UI state was not reached.");
    }
}
