using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Openza.Tasks.Core.Services;
using System.Collections.ObjectModel;

namespace Openza.Tasks.Pages;

public sealed partial class SettingsPage : UserControl
{
    public event SelectionChangedEventHandler? ThemeChanged;
    public event RoutedEventHandler? ConnectTodoistClicked;
    public event RoutedEventHandler? ConnectMicrosoftClicked;
    public event RoutedEventHandler? DisconnectTodoistClicked;
    public event RoutedEventHandler? DisconnectMicrosoftClicked;
    public event RoutedEventHandler? CreateBackupClicked;
    public event RoutedEventHandler? RestoreBackupClicked;
    public event RoutedEventHandler? RefreshBackupsClicked;
    public event RoutedEventHandler? ExportBackupClicked;
    public event RoutedEventHandler? RestoreSelectedBackupClicked;
    public event RoutedEventHandler? DeleteBackupClicked;
    public event RoutedEventHandler? AutoBackupToggled;

    public ObservableCollection<BackupInfo> Backups { get; } = [];

    public SettingsPage()
    {
        InitializeComponent();
    }

    public string SelectedTheme => (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "System";

    public string TodoistToken => TodoistTokenBox.Password;

    public string MicrosoftClientId => MicrosoftClientIdBox.Text.Trim();

    public string MicrosoftTenantId => string.IsNullOrWhiteSpace(MicrosoftTenantIdBox.Text) ? "common" : MicrosoftTenantIdBox.Text.Trim();

    public BackupInfo? SelectedBackup => BackupsList.SelectedItem as BackupInfo;

    public bool AutoBackupEnabled
    {
        get => AutoBackupSwitch.IsOn;
        set => AutoBackupSwitch.IsOn = value;
    }

    public void SetProviderStatus(bool todoistConnected, bool microsoftConnected)
    {
        ProviderStatusText.Text = $"Todoist: {(todoistConnected ? "connected" : "not connected")}  |  Microsoft To Do: {(microsoftConnected ? "connected" : "not connected")}";
    }

    public void SetBackups(IEnumerable<BackupInfo> backups)
    {
        Backups.Clear();
        foreach (var backup in backups)
        {
            Backups.Add(backup);
        }
    }

    public void SelectTheme(string theme)
    {
        foreach (var item in ThemeCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), theme, StringComparison.Ordinal))
            {
                ThemeCombo.SelectedItem = item;
                return;
            }
        }
    }

    public void ClearTodoistToken() => TodoistTokenBox.Password = string.Empty;

    public void SetMicrosoftConfig(string clientId, string tenantId)
    {
        MicrosoftClientIdBox.Text = clientId;
        MicrosoftTenantIdBox.Text = string.IsNullOrWhiteSpace(tenantId) ? "common" : tenantId;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e) => ThemeChanged?.Invoke(sender, e);

    private void OnConnectTodoistClicked(object sender, RoutedEventArgs e) => ConnectTodoistClicked?.Invoke(sender, e);

    private void OnConnectMicrosoftClicked(object sender, RoutedEventArgs e) => ConnectMicrosoftClicked?.Invoke(sender, e);

    private void OnDisconnectTodoistClicked(object sender, RoutedEventArgs e) => DisconnectTodoistClicked?.Invoke(sender, e);

    private void OnDisconnectMicrosoftClicked(object sender, RoutedEventArgs e) => DisconnectMicrosoftClicked?.Invoke(sender, e);

    private void OnCreateBackupClicked(object sender, RoutedEventArgs e) => CreateBackupClicked?.Invoke(sender, e);

    private void OnRestoreBackupClicked(object sender, RoutedEventArgs e) => RestoreBackupClicked?.Invoke(sender, e);

    private void OnRefreshBackupsClicked(object sender, RoutedEventArgs e) => RefreshBackupsClicked?.Invoke(sender, e);

    private void OnExportBackupClicked(object sender, RoutedEventArgs e) => ExportBackupClicked?.Invoke(sender, e);

    private void OnRestoreSelectedBackupClicked(object sender, RoutedEventArgs e) => RestoreSelectedBackupClicked?.Invoke(sender, e);

    private void OnDeleteBackupClicked(object sender, RoutedEventArgs e) => DeleteBackupClicked?.Invoke(sender, e);

    private void OnAutoBackupToggled(object sender, RoutedEventArgs e) => AutoBackupToggled?.Invoke(sender, e);
}
