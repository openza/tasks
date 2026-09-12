using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Desktop.Dialogs;

public sealed partial class AddTaskWindow : Window
{
    private readonly ObservableCollection<string> _selectedLabels = [];

    public AddTaskWindow()
    {
        InitializeComponent();
        SelectedLabelChips.ItemsSource = _selectedLabels;
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

    private void OnTitleTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            TitleValidation.IsVisible = false;
        }
    }

    private void OnLabelBoxGotFocus(object? sender, RoutedEventArgs e) => LabelsBox.IsDropDownOpen = true;

    private void OnLabelSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LabelsBox.SelectedItem is string label)
        {
            AddLabels(label);
            LabelsBox.SelectedItem = null;
            LabelsBox.Text = string.Empty;
        }
    }

    private void OnLabelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.OemComma)
        {
            AddLabels(LabelsBox.Text);
            LabelsBox.Text = string.Empty;
            LabelsBox.SelectedItem = null;
            LabelsBox.IsDropDownOpen = false;
            e.Handled = true;
        }
    }

    private void OnRemoveLabelClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string label })
        {
            _selectedLabels.Remove(label);
            LabelsBox.Focus();
        }
    }

    private void Complete(bool openAfterCreate)
    {
        var title = TitleBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            TitleValidation.IsVisible = true;
            TitleBox.Focus();
            return;
        }

        AddLabels(LabelsBox.Text);

        Close(new AddTaskDraft(
            title,
            NotesBox.Text ?? string.Empty,
            ProjectPicker.SelectedItem as ProjectOptionViewModel,
            int.TryParse((StatusPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var statusIndex) ? statusIndex : 0,
            Math.Max(0, PriorityPicker.SelectedIndex),
            DatePicker.SelectedDate,
            string.Join(", ", _selectedLabels),
            openAfterCreate));
    }

    private void AddLabels(string? labels)
    {
        foreach (var label in (labels ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!_selectedLabels.Any(selected => string.Equals(selected, label, StringComparison.CurrentCultureIgnoreCase)))
            {
                _selectedLabels.Add(label);
            }
        }
    }

    private bool FilterLabelSuggestion(string? search, string? suggestion)
    {
        if (string.IsNullOrWhiteSpace(suggestion))
        {
            return false;
        }

        search ??= string.Empty;
        return !_selectedLabels.Any(label => string.Equals(label, suggestion, StringComparison.CurrentCultureIgnoreCase)) &&
            suggestion.Contains(search, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string SelectLabelSuggestion(string? search, string? suggestion)
        => suggestion ?? string.Empty;
}
