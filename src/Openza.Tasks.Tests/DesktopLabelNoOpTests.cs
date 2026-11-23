using Avalonia.Controls;
using Avalonia.Input;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.Shell;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

[Collection(nameof(EditorTestCollection))]
public sealed class DesktopLabelNoOpTests(EditorTestSession fixture)
{
    [Theory]
    [InlineData("none")]
    [InlineData("title")]
    [InlineData("add")]
    [InlineData("remove")]
    public async Task Existing_comma_label_survives_editor_saves_by_identity(string edit)
    {
        await fixture.Session.Dispatch(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), $"openza-comma-label-{Guid.NewGuid():N}");
            var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
            await store.InitializeAsync();
            var label = new LabelItem { Id = "person", Name = "Smith, John", Color = "#123456" };
            await store.UpsertTaskAsync(new() { Id = "a", Title = "Synthetic task", Labels = [label] });
            var preferences = new DesktopPreferencesStore(Path.Combine(directory, "settings.json"));
            await preferences.SaveAsync(new() { AutomaticSyncEnabled = false, AutomaticRestorePointsEnabled = false, ShowGetStarted = false });
            var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences);
            var window = new MainWindow(vm);
            try
            {
                window.Show();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (vm.SelectedNavigation is null || vm.IsBusy) await Task.Delay(10, deadline.Token);
                await vm.SelectNavigationAsync(vm.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
                await vm.SelectTaskAsync(vm.Tasks.Single(item => item.Task.Id == "a"));
                var before = (await store.GetTaskAsync("a"))!;
                Assert.Equal(label.Name, vm.DetailLabels);
                Assert.Equal(label.Name, Assert.Single(vm.DetailLabelItems));
                if (edit == "title") vm.DetailTitle = "Synthetic edited title";
                if (edit == "add") Assert.True(vm.AddDetailLabels("Planning"));
                if (edit == "remove") Assert.True(vm.RemoveDetailLabel(label.Name));

                Assert.True(await vm.SaveSelectedAsync());

                var saved = (await store.GetTaskAsync("a"))!;
                if (edit == "remove") Assert.Empty(saved.Labels);
                else
                {
                    Assert.Equal(Assert.Single(before.Labels), Assert.Single(saved.Labels, item => item.Id == label.Id));
                    Assert.Equal(edit == "add" ? 2 : 1, saved.Labels.Count);
                }
                Assert.DoesNotContain(await store.GetLabelsAsync(), item => item.Name is "Smith" or "John");
                if (edit == "none") Assert.Equivalent(before, saved, strict: true);
                if (edit == "title") Assert.Equal("Synthetic edited title", saved.Title);
                if (edit == "add") Assert.Contains(saved.Labels, item => item.Name == "Planning");
            }
            finally { window.Close(); await Task.Delay(100); TestDirectory.Delete(directory); }
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("Focus", "FOCUS", false)]
    [InlineData("Focus", "FOCUS", true)]
    [InlineData("équipe", "ÉQUIPE", false)]
    [InlineData("équipe", "ÉQUIPE", true)]
    public async Task Equivalent_label_input_does_not_rename_or_rewrite_shared_labels(
        string storedName, string typedName, bool editTitle)
    {
        await fixture.Session.Dispatch(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), $"openza-label-noop-{Guid.NewGuid():N}");
            var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
            await store.InitializeAsync();
            var label = new LabelItem { Id = "shared", Name = storedName, Color = "#123456", Description = "Synthetic label" };
            await store.UpsertLabelAsync(label);
            await store.UpsertTaskAsync(new() { Id = "a", Title = "Synthetic task A", Labels = [label] });
            await store.UpsertTaskAsync(new() { Id = "b", Title = "Synthetic task B", Labels = [label] });
            var preferences = new DesktopPreferencesStore(Path.Combine(directory, "settings.json"));
            await preferences.SaveAsync(new() { AutomaticSyncEnabled = false, AutomaticRestorePointsEnabled = false, ShowGetStarted = false });
            var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences);
            var window = new MainWindow(vm);
            try
            {
                window.Show();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (vm.SelectedNavigation is null || vm.IsBusy) await Task.Delay(10, deadline.Token);
                await vm.SelectNavigationAsync(vm.NavigationItems.Single(item => item.Kind == TaskListKind.Open));
                await vm.SelectTaskAsync(vm.Tasks.Single(item => item.Task.Id == "a"));
                var before = (await store.GetTaskAsync("a"))!;
                var other = (await store.GetTaskAsync("b"))!;
                var storedLabel = Assert.Single(await store.GetLabelsAsync());

                // Exercise the actual chip input's Enter handler and its autosave decision.
                var box = window.FindControl<AutoCompleteBox>("DetailLabelsBox")!;
                box.Text = typedName;
                box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                Assert.Equal(storedName, Assert.Single(vm.DetailLabelItems));
                Assert.Equivalent(before, await store.GetTaskAsync("a"), strict: true);

                // Also exercise serialized editor input, with and without another real edit.
                vm.DetailLabels = $"  {typedName}  ";
                if (editTitle) vm.DetailTitle = "Synthetic edited title";
                Assert.True(await vm.SaveSelectedAsync());
                var saved = (await store.GetTaskAsync("a"))!;
                Assert.Equal(storedLabel, Assert.Single(await store.GetLabelsAsync()));
                Assert.Equal(label.Id, Assert.Single(saved.Labels).Id);
                Assert.Equal(storedName, Assert.Single(saved.Labels).Name);
                Assert.Equivalent(other, await store.GetTaskAsync("b"), strict: true);
                if (editTitle)
                    Assert.Equal("Synthetic edited title", saved.Title);
                else
                    Assert.Equivalent(before, saved, strict: true);
            }
            finally { window.Close(); await Task.Delay(100); TestDirectory.Delete(directory); }
            return true;
        }, CancellationToken.None);
    }
}
