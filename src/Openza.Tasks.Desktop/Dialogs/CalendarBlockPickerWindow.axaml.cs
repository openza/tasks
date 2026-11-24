using Avalonia.Controls;
using Avalonia.Interactivity;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Desktop.Dialogs;

public sealed partial class CalendarBlockPickerWindow : Window
{
    public CalendarBlockPickerWindow() => InitializeComponent();

    public CalendarBlockPickerWindow(
        CalendarDayViewModel calendarDay,
        DateTimeOffset start,
        TaskListItemViewModel? task = null,
        int? durationMinutes = null) : this()
    {
        DataContext = new CalendarBlockPickerViewModel(calendarDay, start, task, durationMinutes);
    }

    private CalendarBlockPickerViewModel ViewModel => (CalendarBlockPickerViewModel)DataContext!;

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);

    private void OnAddBlockClicked(object? sender, RoutedEventArgs e) => Close(ViewModel.CreateResult());
}
