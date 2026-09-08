using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Openza.Tasks.Desktop.Dialogs;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Desktop.Settings;

public sealed partial class SettingsWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly DesktopPreferencesStore _preferencesStore = new();
    private bool _initialized;

    public SettingsWindow()
        : this(null!)
    {
    }

    public SettingsWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        var preferences = _preferencesStore.Load();
        ThemePicker.SelectedIndex = preferences.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0,
        };
        _initialized = true;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        _viewModel.LoadRestorePoints();
        await _viewModel.RefreshTodoistConnectionAsync();
        await _viewModel.RefreshGitHubConnectionAsync();
    }

    private async void OnThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || Avalonia.Application.Current is null)
        {
            return;
        }

        var theme = ThemePicker.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System",
        };
        Avalonia.Application.Current.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        await _preferencesStore.UpdateAsync(preferences => preferences with { Theme = theme });
    }

    private async void OnAddSpaceClicked(object? sender, RoutedEventArgs e)
    {
        await _viewModel.CreateSpaceAsync(NewSpaceBox.Text ?? string.Empty);
        NewSpaceBox.Clear();
    }

    private async void OnNewSpaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await _viewModel.CreateSpaceAsync(NewSpaceBox.Text ?? string.Empty);
        NewSpaceBox.Clear();
    }

    private async void OnArchiveSpaceClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SpaceNavigationItemViewModel item })
        {
            return;
        }

        var dialog = new ConfirmWindow(
            "Archive space?",
            $"{item.Title} will be hidden from normal navigation. Its tasks and projects will remain in the database.");
        if (await dialog.ShowDialog<bool>(this))
        {
            await _viewModel.ArchiveSpaceAsync(item);
        }
    }

    private async void OnRenameSpaceClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SpaceNavigationItemViewModel item })
        {
            return;
        }

        var prompt = new TextPromptWindow("Rename space", item.Title);
        var name = await prompt.ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await _viewModel.RenameSpaceAsync(item, name);
        }
    }

    private async void OnCreateRestorePointClicked(object? sender, RoutedEventArgs e) =>
        await _viewModel.CreateRestorePointAsync();

    private async void OnConnectTodoistClicked(object? sender, RoutedEventArgs e) =>
        await _viewModel.ConnectTodoistAsync();

    private async void OnDisconnectTodoistClicked(object? sender, RoutedEventArgs e)
    {
        var dialog = new ConfirmWindow(
            "Disconnect Todoist?",
            "The secure token will be removed from this Linux installation. Existing local and connected-task data will remain.");
        if (await dialog.ShowDialog<bool>(this))
        {
            await _viewModel.DisconnectTodoistAsync();
        }
    }

    private async void OnConnectGitHubClicked(object? sender, RoutedEventArgs e) =>
        await _viewModel.ConnectGitHubAsync();

    private async void OnDisconnectGitHubClicked(object? sender, RoutedEventArgs e)
    {
        var dialog = new ConfirmWindow(
            "Disconnect GitHub?",
            "The secure token will be removed from this Linux installation. Existing task-to-issue links will remain.");
        if (await dialog.ShowDialog<bool>(this))
        {
            await _viewModel.DisconnectGitHubAsync();
        }
    }

    private async void OnSaveGitHubRepositoryClicked(object? sender, RoutedEventArgs e) =>
        await _viewModel.SaveGitHubDefaultRepositoryAsync();

    private async void OnExportDatabaseClicked(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Openza Tasks backup",
            SuggestedFileName = $"openza-tasks-backup-{DateTime.Now:yyyy-MM-dd}.db",
            DefaultExtension = "db",
            FileTypeChoices = [new FilePickerFileType("SQLite database") { Patterns = ["*.db"] }],
        });
        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await _viewModel.ExportDatabaseAsync(path);
        }
    }

    private async void OnRestoreDatabaseClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Restore Openza Tasks backup",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("SQLite database") { Patterns = ["*.db"] }],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var dialog = new ConfirmWindow(
            "Restore this database?",
            "The current database will be replaced after Openza creates a safety restore point.");
        if (await dialog.ShowDialog<bool>(this))
        {
            await _viewModel.RestoreDatabaseAsync(path);
        }
    }

    private async void OnRestoreSelectedPointClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedRestorePoint is null)
        {
            return;
        }

        var dialog = new ConfirmWindow(
            "Restore selected point?",
            "The current database will be replaced after Openza creates a new safety restore point.");
        if (await dialog.ShowDialog<bool>(this))
        {
            await _viewModel.RestoreSelectedPointAsync();
        }
    }

    private async void OnDeleteSelectedPointClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedRestorePoint is null)
        {
            return;
        }

        var dialog = new ConfirmWindow(
            "Delete restore point?",
            "This restore point and its metadata will be permanently removed.");
        if (await dialog.ShowDialog<bool>(this))
        {
            await _viewModel.DeleteSelectedRestorePointAsync();
        }
    }

    private async void OnOpenDataFolderClicked(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(DesktopDataPaths.DataDirectory);
        await Launcher.LaunchUriAsync(new Uri(DesktopDataPaths.DataDirectory));
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
