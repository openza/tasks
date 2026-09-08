using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Desktop.Dialogs;

public sealed partial class ProjectEditorWindow : Window
{
    public ProjectEditorWindow()
        : this(new ProjectItem())
    {
    }

    public ProjectEditorWindow(ProjectItem project)
    {
        InitializeComponent();
        NameBox.Text = project.Name;
        StatusSelector.SelectedIndex = project.EffectiveStatus switch
        {
            ProjectLifecycleStates.Completed => 1,
            ProjectLifecycleStates.Archived => 2,
            _ => 0,
        };
        FavoriteBox.IsChecked = project.IsFavorite;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Complete();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(null);
        }
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e) => Complete();

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);

    private void Complete()
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            NameBox.Focus();
            return;
        }

        var status = StatusSelector.SelectedIndex switch
        {
            1 => ProjectLifecycleStates.Completed,
            2 => ProjectLifecycleStates.Archived,
            _ => ProjectLifecycleStates.Active,
        };
        Close(new ProjectEditDraft(name, status, FavoriteBox.IsChecked == true));
    }
}

public sealed record ProjectEditDraft(string Name, string Status, bool IsFavorite);
