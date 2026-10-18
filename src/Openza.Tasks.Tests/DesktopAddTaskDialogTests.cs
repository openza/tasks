using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Desktop.Dialogs;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

[Collection(nameof(EditorTestCollection))]
public sealed class DesktopAddTaskDialogTests(EditorTestSession fixture)
{
    [Fact]
    public async Task Owned_create_dialog_validates_and_saves_rich_draft_with_create_and_open()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "openza-dialog-parity", Guid.NewGuid().ToString("N"));
            var store = new SqliteTaskStore(Path.Combine(directory, "tasks.db"));
            var preferences = new DesktopPreferencesStore(Path.Combine(directory, "settings.json"));
            await preferences.SaveAsync(new() { AutomaticSyncEnabled = false, AutomaticRestorePointsEnabled = false });
            var vm = new MainWindowViewModel(store, new InMemoryCredentialStore(), preferencesStore: preferences);
            await vm.InitializeAsync();
            var owner = new Window { Width = 1000, Height = 800 };
            var dialog = new AddTaskWindow(vm);
            try
            {
                owner.Show();
                var result = dialog.ShowDialog<AddTaskDraft?>(owner);
                var create = dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Create and open"));
                create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(dialog.FindControl<TextBlock>("TitleValidation")!.IsVisible);
                dialog.FindControl<TextBox>("TitleBox")!.Text = "Desktop dialog parity ✓";
                dialog.FindControl<TextBox>("NotesBox")!.Text = "Synthetic dialog notes\nनमस्ते";
                dialog.FindControl<ComboBox>("StatusPicker")!.SelectedIndex = 1;
                dialog.FindControl<ComboBox>("PriorityPicker")!.SelectedIndex = 0;
                dialog.FindControl<CalendarDatePicker>("DatePicker")!.SelectedDate = new DateTime(2026, 10, 19);
                dialog.FindControl<AutoCompleteBox>("LabelsBox")!.Text = "review, desktop, REVIEW";
                create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var draft = await result;
                Assert.NotNull(draft);
                Assert.True(draft.OpenAfterCreate);
                await vm.CreateTaskAsync(draft);
                var task = Assert.Single(await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Open }));
                Assert.Equal("Desktop dialog parity ✓", task.Title);
                Assert.Equal(1, task.Priority);
                Assert.Equal(new DateOnly(2026, 10, 19), task.PlannedOn);
                Assert.Equal(2, task.Labels.Count);
                Assert.Equal(task.Id, vm.SelectedTask!.Task.Id);
            }
            finally { dialog.Close(); owner.Close(); await Task.Delay(100); TestDirectory.Delete(directory); }
            return true;
        }, CancellationToken.None);
    }
}
