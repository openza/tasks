using Avalonia.Controls;
using Avalonia.Interactivity;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Desktop.Dialogs;

public sealed partial class AddTaskWindow : Window
{
    public AddTaskWindow()
    {
        InitializeComponent();
    }

    public AddTaskWindow(MainWindowViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        LabelsBox.TextFilter = FilterLabelSuggestion;
        LabelsBox.TextSelector = SelectLabelSuggestion;
        ProjectPicker.SelectedItem = viewModel.ProjectOptions.FirstOrDefault(option => option.ProjectId == viewModel.SelectedProject?.Project.Id)
            ?? viewModel.ProjectOptions.FirstOrDefault();
        StatusPicker.SelectedIndex = viewModel.SelectedNavigation?.Kind switch
        {
            Openza.Tasks.Core.Data.TaskListKind.NextActions => 1,
            Openza.Tasks.Core.Data.TaskListKind.Waiting => 2,
            Openza.Tasks.Core.Data.TaskListKind.Someday => 3,
            _ => 0,
        };
        PriorityPicker.SelectedIndex = 2;
        if (viewModel.SelectedNavigation?.Kind == Openza.Tasks.Core.Data.TaskListKind.Today)
        {
            DatePicker.SelectedDate = DateTimeOffset.Now;
        }
    }

    private void OnOpened(object? sender, EventArgs e) => TitleBox.Focus();
    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);
    private void OnCreateClicked(object? sender, RoutedEventArgs e) => Complete(false);
    private void OnCreateAndOpenClicked(object? sender, RoutedEventArgs e) => Complete(true);

    private void Complete(bool openAfterCreate)
    {
        var title = TitleBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            TitleBox.Focus();
            return;
        }

        Close(new AddTaskDraft(
            title,
            NotesBox.Text ?? string.Empty,
            ProjectPicker.SelectedItem as ProjectOptionViewModel,
            Math.Max(0, StatusPicker.SelectedIndex),
            Math.Max(0, PriorityPicker.SelectedIndex),
            DatePicker.SelectedDate,
            LabelsBox.Text ?? string.Empty,
            openAfterCreate));
    }

    private static bool FilterLabelSuggestion(string? search, string? suggestion)
    {
        if (string.IsNullOrWhiteSpace(suggestion))
        {
            return false;
        }

        search ??= string.Empty;
        var segments = search.Split(',', StringSplitOptions.TrimEntries);
        var currentSearch = segments.LastOrDefault() ?? string.Empty;
        var alreadySelected = segments
            .Take(Math.Max(0, segments.Length - 1))
            .Any(label => string.Equals(label, suggestion, StringComparison.CurrentCultureIgnoreCase));

        return !alreadySelected &&
            suggestion.Contains(currentSearch, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string SelectLabelSuggestion(string? search, string? suggestion)
    {
        search ??= string.Empty;
        suggestion ??= string.Empty;
        var separatorIndex = search.LastIndexOf(',');
        if (separatorIndex < 0)
        {
            return suggestion;
        }

        var existingLabels = search[..separatorIndex].Trim();
        return existingLabels.Length == 0
            ? suggestion
            : $"{existingLabels}, {suggestion}";
    }
}
