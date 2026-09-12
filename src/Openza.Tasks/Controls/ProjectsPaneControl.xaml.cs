using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Openza.Tasks.ViewModels;
using Openza.Tasks.Core.Data;
using Windows.Foundation;

namespace Openza.Tasks.Controls;

public sealed partial class ProjectsPaneControl : UserControl
{
    public static readonly DependencyProperty ProjectEntriesProperty = DependencyProperty.Register(
        nameof(ProjectEntries),
        typeof(ObservableCollection<ProjectPaneEntryViewModel>),
        typeof(ProjectsPaneControl),
        new PropertyMetadata(null));

    public event TypedEventHandler<AutoSuggestBox, AutoSuggestBoxTextChangedEventArgs>? SearchTextChanged;
    public event TypedEventHandler<ProjectsPaneControl, string>? ProjectFilterChanged;
    public event TypedEventHandler<ProjectsPaneControl, string>? ProjectGroupToggled;
    public event TypedEventHandler<ProjectsPaneControl, string?>? ProjectSelected;
    public event TypedEventHandler<ProjectsPaneControl, ProjectSortSettings>? ProjectSortChanged;
    private ProjectSortSettings _projectSort = new();
    public event RoutedEventHandler? AddProjectClicked;
    public event TypedEventHandler<ProjectsPaneControl, string>? EditProjectClicked;
    public event TypedEventHandler<ProjectsPaneControl, string>? DeleteProjectClicked;

    public ProjectsPaneControl()
    {
        InitializeComponent();
    }

    public ObservableCollection<ProjectPaneEntryViewModel>? ProjectEntries
    {
        get => (ObservableCollection<ProjectPaneEntryViewModel>?)GetValue(ProjectEntriesProperty);
        set => SetValue(ProjectEntriesProperty, value);
    }

    public string SearchText => ProjectSearchBox.Text?.Trim() ?? string.Empty;

    public string ProjectFilter => (ProjectFilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";

    public void SetProjectSortBusy(bool busy) => ProjectSortButton.IsEnabled = !busy;

    public void SetProjectSort(ProjectSortSettings settings)
    {
        _projectSort = settings;
        ProjectSortButton.Content = settings.Summary;
        ProjectSortAscending.IsEnabled = ProjectSortDescending.IsEnabled = settings.HasDirection;
        ProjectFavoritesFirst.IsChecked = settings.FavoritesFirst;
    }

    private void OnProjectSortClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        var settings = tag switch
        {
            "Ascending" => _projectSort with { Descending = false },
            "Descending" => _projectSort with { Descending = true },
            "Favorites" => _projectSort with { FavoritesFirst = !_projectSort.FavoritesFirst },
            _ when Enum.TryParse<ProjectSortMode>(tag, out var mode) => _projectSort with { Mode = mode },
            _ => _projectSort,
        };
        ProjectSortChanged?.Invoke(this, settings);
    }

    public void FocusSearch() => ProjectSearchBox.Focus(FocusState.Programmatic);

    private void OnProjectSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) =>
        SearchTextChanged?.Invoke(sender, args);

    private void OnAddProjectClicked(object sender, RoutedEventArgs e) => AddProjectClicked?.Invoke(sender, e);

    private void OnProjectFilterChanged(object sender, SelectionChangedEventArgs e) =>
        ProjectFilterChanged?.Invoke(this, ProjectFilter);

    private void OnProjectButtonClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string id)
        {
            ProjectSelected?.Invoke(this, string.IsNullOrWhiteSpace(id) ? null : id);
        }
    }

    private void OnProjectGroupHeaderClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string id && !string.IsNullOrWhiteSpace(id))
        {
            ProjectGroupToggled?.Invoke(this, id);
        }
    }

    private void OnEditProjectClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string id && !string.IsNullOrWhiteSpace(id))
        {
            EditProjectClicked?.Invoke(this, id);
        }
    }

    private void OnDeleteProjectClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string id && !string.IsNullOrWhiteSpace(id))
        {
            DeleteProjectClicked?.Invoke(this, id);
        }
    }
}
