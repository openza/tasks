using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Openza.Tasks.Core.Services;
using Openza.Tasks.Desktop.Dialogs;

namespace Openza.Tasks.Desktop.Shell;

public sealed partial class MainWindow
{
    private async void OnConnectMicrosoftClicked(object? sender, RoutedEventArgs e)
    {
        if (await new SignInWindow("Connect Microsoft To Do", (show, cancellation) =>
                ViewModel.ConnectMicrosoftAsync("todo", show, cancellation)).ShowDialog<bool>(this))
            await ViewModel.RunTodoistSyncAsync();
    }
    private async void OnDisconnectMicrosoftClicked(object? sender, RoutedEventArgs e)
    {
        if (await new ConfirmWindow("Disconnect Microsoft To Do?", "Existing tasks remain in Openza.", "Disconnect", true).ShowDialog<bool>(this))
            await ViewModel.DisconnectMicrosoftAsync();
    }
    private async void OnConnectOneDriveClicked(object? sender, RoutedEventArgs e)
    {
        if (await new SignInWindow("Choose OneDrive account", (show, cancellation) =>
                ViewModel.ConnectMicrosoftAsync("backup", show, cancellation)).ShowDialog<bool>(this))
            await ViewModel.RefreshCloudBackupsAsync();
    }
    private async void OnCloudEnabledChanged(object? sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not ToggleSwitch toggle || toggle.IsChecked == ViewModel.OneDriveEnabled) return;
        try { await ViewModel.SetCloudEnabledAsync(toggle.IsChecked == true); }
        catch (Exception exception) { ViewModel.ReportError(exception.Message); }
        finally { toggle.IsChecked = ViewModel.OneDriveEnabled; }
    }
    private async void OnCloudEncryptionChanged(object? sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not ToggleSwitch toggle || toggle.IsChecked == ViewModel.OneDriveEncrypted) return;
        try
        {
            var enabled = toggle.IsChecked == true;
            var passphrase = enabled ? await PromptPassphraseAsync(confirm: true) : null;
            if (!enabled || passphrase is not null) await ViewModel.SetCloudEncryptionAsync(enabled, passphrase);
        }
        catch (Exception exception) { ViewModel.ReportError(exception.Message); }
        finally { toggle.IsChecked = ViewModel.OneDriveEncrypted; }
    }
    private async void OnChangeCloudPassphraseClicked(object? sender, RoutedEventArgs e)
    {
        if (await PromptPassphraseAsync(confirm: true) is not { } passphrase) return;
        try { await ViewModel.SetCloudEncryptionAsync(true, passphrase); }
        catch (Exception exception) { ViewModel.ReportError(exception.Message); }
    }
    private async void OnUploadCloudClicked(object? sender, RoutedEventArgs e) => await ViewModel.UploadCloudBackupsAsync(createNew: true);
    private async void OnRefreshCloudClicked(object? sender, RoutedEventArgs e) => await ViewModel.RefreshCloudBackupsAsync();
    private async void OnRestoreCloudClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedCloudBackup is not { } backup) return;
        if (!await new ConfirmWindow("Restore OneDrive backup?", "Replace the local task database with this backup? A local safety restore point is created first.", "Restore", true).ShowDialog<bool>(this)) return;
        var encrypted = backup.EncryptionMode == CloudBackupEncryptionModes.Passphrase;
        var passphrase = encrypted ? await PromptPassphraseAsync(confirm: false) : null;
        if (encrypted && passphrase is null) return;
        await ViewModel.RestoreCloudBackupAsync(backup, passphrase);
    }

    private Task<string?> PromptPassphraseAsync(bool confirm)
    {
        var window = new Window { Title = "Backup passphrase", Width = 480, Height = 330, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var password = new TextBox { PasswordChar = '●', PlaceholderText = "Passphrase" };
        var repeated = new TextBox { PasswordChar = '●', PlaceholderText = "Confirm passphrase", IsVisible = confirm };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var save = new Button { Content = "Continue" }; var cancel = new Button { Content = "Cancel" };
        window.Content = new StackPanel { Margin = new Thickness(24), Spacing = 12, Children =
        { new TextBlock { Text = confirm ? "Keep this passphrase safe. It is required to restore encrypted backups. Changing it affects future backups only." : "Enter the passphrase used when this backup was created.", TextWrapping = TextWrapping.Wrap }, password, repeated, error, save, cancel } };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(password.Text) || (confirm && password.Text != repeated.Text)) { error.Text = "Enter matching, non-empty passphrases."; return; }
            window.Close(password.Text);
        };
        cancel.Click += (_, _) => window.Close(null);
        return window.ShowDialog<string?>(this);
    }
}
