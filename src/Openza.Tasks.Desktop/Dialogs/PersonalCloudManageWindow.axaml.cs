using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Desktop.Dialogs;

public sealed partial class PersonalCloudManageWindow : Window
{
    private readonly MainWindowViewModel? _viewModel;
    private CancellationTokenSource? _backupCancellation;

    public PersonalCloudManageWindow() => InitializeComponent();

    public PersonalCloudManageWindow(MainWindowViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        Opened += async (_, _) => await viewModel.CheckPersonalCloudReleaseAsync();
    }

    private async void OnOpenTasksClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) await _viewModel.OpenPersonalCloudAsync();
    }

    private async void OnOpenSettingsClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) await _viewModel.OpenPersonalCloudSettingsAsync();
    }

    private async void OnExportClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _backupCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _backupCancellation = cancellation;
        SetBusy(true);
        CancelBackupButton.IsVisible = true;
        try
        {
            var success = await _viewModel.ExportPersonalCloudAsync(async (export, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save Personal Cloud backup",
                    SuggestedFileName = $"openza-personal-cloud-{DateTime.Now:yyyy-MM-dd}.sql",
                    DefaultExtension = "sql",
                    FileTypeChoices = [new FilePickerFileType("SQL backup") { Patterns = ["*.sql"] }],
                    ShowOverwritePrompt = true,
                });
                cancellationToken.ThrowIfCancellationRequested();
                if (file is null) throw new OperationCanceledException();
                var path = file.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path)) throw new IOException("Choose a local destination for the backup.");
                await PersonalCloudBackupDownloader.Default.SaveAsync(export, path, cancellationToken);
            }, cancellation.Token);
            if (success) RemoveConfirmationText.Text = string.Empty;
        }
        finally
        {
            _backupCancellation = null;
            CancelBackupButton.IsVisible = false;
            SetBusy(false);
        }
    }

    private void OnCancelBackupClicked(object? sender, RoutedEventArgs e) => _backupCancellation?.Cancel();

    protected override void OnClosed(EventArgs e)
    {
        _backupCancellation?.Cancel();
        base.OnClosed(e);
    }

    private async void OnUpdateClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        SetBusy(true);
        await _viewModel.UpgradePersonalCloudAsync();
        SetBusy(false);
    }

    private void OnDisconnectClicked(object? sender, RoutedEventArgs e) => DisconnectConfirmation.IsVisible = true;
    private void OnCancelDisconnectClicked(object? sender, RoutedEventArgs e) => DisconnectConfirmation.IsVisible = false;

    private async void OnConfirmDisconnectClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        SetBusy(true);
        if (await _viewModel.DisconnectPersonalCloudAsync()) Close(true);
        else
        {
            SetBusy(false);
        }
    }

    private void OnRemoveConfirmationChanged(object? sender, TextChangedEventArgs e) =>
        RemoveButton.IsEnabled = string.Equals(RemoveConfirmationText.Text, "REMOVE", StringComparison.Ordinal);

    private async void OnRemoveClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || !string.Equals(RemoveConfirmationText.Text, "REMOVE", StringComparison.Ordinal)) return;
        SetBusy(true);
        if (await _viewModel.RemovePersonalCloudAsync()) Close(true);
        else
        {
            SetBusy(false);
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close(false);

    private void SetBusy(bool busy)
    {
        ExportButton.IsEnabled = !busy;
        UpdateButton.IsEnabled = !busy;
        ConfirmDisconnectButton.IsEnabled = !busy;
        RemoveConfirmationText.IsEnabled = !busy;
        RemoveButton.IsEnabled = !busy && string.Equals(RemoveConfirmationText.Text, "REMOVE", StringComparison.Ordinal);
    }
}
