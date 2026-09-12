using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Openza.Tasks.Desktop.Dialogs;

namespace Openza.Tasks.Desktop.Shell;

public sealed partial class MainWindow
{
    private async void OnGitHubSignInClicked(object? sender, RoutedEventArgs e) =>
        await new SignInWindow("Sign in to GitHub", ViewModel.SignInGitHubAsync).ShowDialog<bool>(this);

    private async void OnGitHubActionClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!await ViewModel.SaveSelectedAsync()) return;
            if (ViewModel.SelectedGitHubLink is { } link)
            {
                var dialog = new Window { Title = "GitHub issue", Width = 560, Height = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var statusText = "This task is linked to a GitHub issue.";
                try
                {
                    var status = await ViewModel.CheckGitHubLinkAsync(link);
                    if (status.Unavailable) statusText = "Openza cannot access this issue. It may be deleted, private, or no longer authorized.";
                }
                catch (Exception exception) { statusText = $"Issue status unavailable: {exception.Message}"; }
                var open = new Button { Content = "Open issue" }; var replace = new Button { Content = "Replace link" };
                var remove = new Button { Content = "Remove link" }; var done = new Button { Content = "Done" };
                dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 14, Children =
                { new TextBlock { Text = statusText, TextWrapping = TextWrapping.Wrap }, new TextBlock { Text = link.DisplayName },
                  new TextBlock { Text = link.Url, TextWrapping = TextWrapping.Wrap },
                  new WrapPanel { Orientation = Orientation.Horizontal, Children = { open, replace, remove, done } } } };
                open.Click += (_, _) => dialog.Close("open"); replace.Click += (_, _) => dialog.Close("replace");
                remove.Click += (_, _) => dialog.Close("remove"); done.Click += (_, _) => dialog.Close(null);
                var action = await dialog.ShowDialog<string?>(this);
                if (action == "open") { await OpenIssueUrlAsync(link.Url); return; }
                if (action == "remove") { await ViewModel.RemoveGitHubLinkAsync(link); return; }
                if (action != "replace") return;
            }
            if (await ViewModel.PrepareGitHubIssueAsync() is { } draft)
            {
                var url = await new GitHubIssueWindow(ViewModel, draft).ShowDialog<string?>(this);
                if (url is not null) await OpenIssueUrlAsync(url);
            }
        }
        catch (Exception exception) { ViewModel.ReportError(exception.Message); }
    }

    private async Task OpenIssueUrlAsync(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https") await Launcher.LaunchUriAsync(uri);
    }
}
