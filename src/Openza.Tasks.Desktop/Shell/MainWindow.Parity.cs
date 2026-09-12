using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Openza.Tasks.Desktop.Shell;

public sealed partial class MainWindow
{
    private bool? _organizeIsNarrow;
    private void OnOrganizeSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid grid) return;
        var narrow = e.NewSize.Width < 560;
        if (_organizeIsNarrow == narrow) return;
        _organizeIsNarrow = narrow;
        grid.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,*");
        grid.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto,Auto,Auto,Auto,Auto" : "Auto,Auto,Auto");
        for (var index = 0; index < grid.Children.Count; index++)
        {
            Grid.SetRow(grid.Children[index], narrow ? index : index / 2);
            Grid.SetColumn(grid.Children[index], narrow ? 0 : index % 2);
        }
    }
    private async void OnAutomaticRestorePointsChanged(object? sender, RoutedEventArgs e)
    {
        if (_initialized && sender is ToggleSwitch toggle)
            await ViewModel.SetAutomaticRestorePointsAsync(toggle.IsChecked == true);
    }

    private async void OnDismissGetStartedClicked(object? sender, RoutedEventArgs e) => await ViewModel.DismissGetStartedAsync();

    private async void OnExportSelectedPointClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedRestorePoint is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export selected restore point",
            SuggestedFileName = "openza-restore-point.db",
            DefaultExtension = "db",
        });
        if (file?.TryGetLocalPath() is { } path) await ViewModel.ExportSelectedRestorePointAsync(path);
    }

    private async void OnCopyTaskFieldClicked(object? sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasSelectedTask || sender is not MenuItem item) return;
        var text = ViewModel.BuildTaskCopyText(item.Tag?.ToString() ?? "");
        if (Clipboard is null || string.IsNullOrWhiteSpace(text)) return;
        try { await Clipboard.SetValueAsync(DataFormat.Text, text); ViewModel.ReportStatus("Copied"); }
        catch (Exception exception) { ViewModel.ReportError($"Copy failed: {exception.Message}"); }
    }
}
