using Avalonia.Controls;
using Avalonia.Interactivity;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Desktop.Dialogs;

public sealed partial class PersonalCloudManageWindow : Window
{
    private readonly MainWindowViewModel? _viewModel;

    public PersonalCloudManageWindow() => InitializeComponent();

    public PersonalCloudManageWindow(MainWindowViewModel viewModel) : this()
    {
        _viewModel = viewModel;
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
        if (_viewModel is null) return;
        SetBusy(true);
        var success = await _viewModel.ExportPersonalCloudAsync();
        ResultText.Text = _viewModel.PersonalCloudSummaryText;
        SetBusy(false);
        if (success) RemoveConfirmationText.Text = string.Empty;
    }

    private async void OnUpdateClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        SetBusy(true);
        await _viewModel.UpgradePersonalCloudAsync();
        ResultText.Text = _viewModel.PersonalCloudSummaryText;
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
            ResultText.Text = _viewModel.PersonalCloudSummaryText;
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
            ResultText.Text = _viewModel.PersonalCloudSummaryText;
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
