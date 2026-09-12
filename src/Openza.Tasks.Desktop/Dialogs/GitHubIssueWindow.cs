using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Desktop.Dialogs;

public sealed class GitHubIssueWindow : Window
{
    public GitHubIssueWindow(MainWindowViewModel viewModel, GitHubIssueDraft draft)
    {
        Title = "Create GitHub issue"; Width = 660; Height = 700; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var repositorySearch = new TextBox { PlaceholderText = "Find repository" };
        var repositories = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var title = new TextBox { Text = draft.Title };
        var body = new TextBox { Text = draft.Body, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 220 };
        var labels = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { "No label" }, SelectedIndex = 0 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var create = new Button { Content = "Create issue" }; var cancel = new Button { Content = "Cancel" };
        Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(24), Spacing = 10, Children =
        {
            new TextBlock { Text = "Create GitHub issue", FontSize = 24 }, repositorySearch, repositories,
            new TextBlock { Text = "Title" }, title, new TextBlock { Text = "Body" }, body,
            new TextBlock { Text = "Label" }, labels, status,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { create, cancel } },
        } } };
        var loadingVersion = 0; var submitting = false;
        Closing += (_, args) => { if (submitting) args.Cancel = true; };
        repositorySearch.TextChanged += (_, _) =>
        {
            var selected = repositories.SelectedItem as string;
            repositories.ItemsSource = draft.Repositories.Where(repo => repo.FullName.Contains(repositorySearch.Text ?? "", StringComparison.OrdinalIgnoreCase)).Select(repo => repo.FullName).ToList();
            repositories.SelectedItem = selected;
        };
        repositories.SelectionChanged += async (_, _) =>
        {
            var version = ++loadingVersion;
            labels.ItemsSource = new[] { "No label" }; labels.SelectedIndex = 0;
            var repository = draft.Repositories.FirstOrDefault(repo => repo.FullName == repositories.SelectedItem as string);
            if (repository is null) return;
            try
            {
                var choices = await viewModel.GetGitHubLabelsAsync(repository);
                if (version != loadingVersion) return;
                labels.ItemsSource = new[] { "No label" }.Concat(choices.Select(label => label.Name)).ToList();
                labels.SelectedIndex = 0; status.Text = string.Empty;
            }
            catch (Exception exception) { if (version == loadingVersion) status.Text = $"Labels unavailable: {exception.Message}"; }
        };
        repositories.ItemsSource = draft.Repositories.Select(repo => repo.FullName).ToList();
        repositories.SelectedItem = draft.Repositories.FirstOrDefault(repo => repo.FullName == draft.DefaultRepository)?.FullName;
        create.Click += async (_, _) =>
        {
            if (submitting) return;
            var repository = draft.Repositories.FirstOrDefault(repo => repo.FullName == repositories.SelectedItem as string);
            if (repository is null || string.IsNullOrWhiteSpace(title.Text)) { status.Text = "Choose a repository and enter a title."; return; }
            submitting = true; create.IsEnabled = false; cancel.IsEnabled = false;
            status.Text = "Creating issue…";
            try
            {
                var selectedLabels = labels.SelectedIndex > 0 && labels.SelectedItem is string label ? new[] { label } : [];
                var url = await viewModel.CreateGitHubIssueAsync(draft, new GitHubIssueCreateRequest(repository.Owner, repository.Name, title.Text.Trim(), body.Text ?? "", selectedLabels, []));
                submitting = false; Close(url);
            }
            catch (Exception exception) { status.Text = exception.Message; }
            finally { submitting = false; create.IsEnabled = true; cancel.IsEnabled = true; }
        };
        cancel.Click += (_, _) => Close(null);
    }
}
