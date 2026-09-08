using Avalonia.Controls;
using Avalonia.Interactivity;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Desktop.Connected;

public sealed partial class ConnectedTasksWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public ConnectedTasksWindow()
        : this(null!)
    {
    }

    public ConnectedTasksWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private async void OnOpened(object? sender, EventArgs e) => await _viewModel.LoadConnectedTasksAsync();

    private async void OnRefreshClicked(object? sender, RoutedEventArgs e) => await _viewModel.LoadConnectedTasksAsync();

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) =>
        _viewModel.FilterConnectedTasks(SearchBox.Text ?? string.Empty);

    private async void OnPrimaryActionClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ConnectedTaskViewModel item })
        {
            if (item.Source.IsSkipped)
            {
                await _viewModel.UnskipConnectedTaskAsync(item);
            }
            else
            {
                await _viewModel.AdoptConnectedTaskAsync(item);
            }
        }
    }

    private async void OnSkipClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ConnectedTaskViewModel item })
        {
            await _viewModel.SkipConnectedTaskAsync(item);
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
