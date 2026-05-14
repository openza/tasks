using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Openza.Tasks.ViewModels;
using Windows.Foundation;

namespace Openza.Tasks.Controls;

public sealed partial class ProjectsPaneControl : UserControl
{
    public static readonly DependencyProperty ProjectGroupsProperty = DependencyProperty.Register(
        nameof(ProjectGroups),
        typeof(ObservableCollection<ProjectGroupViewModel>),
        typeof(ProjectsPaneControl),
        new PropertyMetadata(null));

    public event TypedEventHandler<AutoSuggestBox, AutoSuggestBoxTextChangedEventArgs>? SearchTextChanged;
    public event TypedEventHandler<ProjectsPaneControl, string>? ProjectFilterChanged;
    public event TypedEventHandler<ProjectsPaneControl, string?>? ProjectSelected;
    public event RoutedEventHandler? AddProjectClicked;
    public event TypedEventHandler<ProjectsPaneControl, string>? EditProjectClicked;
    public event TypedEventHandler<ProjectsPaneControl, string>? DeleteProjectClicked;

    public ProjectsPaneControl()
    {
        InitializeComponent();
    }

    public ObservableCollection<ProjectGroupViewModel>? ProjectGroups
    {
        get => (ObservableCollection<ProjectGroupViewModel>?)GetValue(ProjectGroupsProperty);
        set => SetValue(ProjectGroupsProperty, value);
    }

    public string SearchText => ProjectSearchBox.Text?.Trim() ?? string.Empty;

    public string ProjectFilter => (ProjectFilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";

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
        if ((sender as FrameworkElement)?.Tag is not string id || ProjectGroups is null)
        {
            return;
        }

        var group = ProjectGroups.FirstOrDefault(group => string.Equals(group.Id, id, StringComparison.Ordinal));
        if (group is null)
        {
            return;
        }

        group.IsExpanded = !group.IsExpanded;
        ProjectGroupsList.ItemsSource = null;
        ProjectGroupsList.ItemsSource = ProjectGroups;
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
