using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Openza.Tasks.Core.Sync;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Desktop.Dialogs;

public sealed partial class TodoistRoutingRuleWindow : Window
{
    private readonly TodoistRoutingRuleViewModel? _existing;
    private readonly IReadOnlyList<TodoistRoutingChoiceViewModel> _labelChoices;

    public ObservableCollection<TodoistRoutingChoiceViewModel> SpaceChoices { get; } = [];
    public ObservableCollection<TodoistRoutingChoiceViewModel> MoveProjectChoices { get; } = [];

    public TodoistRoutingRuleWindow()
    {
        InitializeComponent();
        DataContext = this;
        _labelChoices = [];
    }

    public TodoistRoutingRuleWindow(
        TodoistRoutingRuleViewModel? existing,
        IEnumerable<TodoistRoutingChoiceViewModel> labels,
        IEnumerable<TodoistRoutingChoiceViewModel> spaces,
        IEnumerable<TodoistRoutingChoiceViewModel> moveProjects)
        : this()
    {
        _existing = existing;
        _labelChoices = labels.ToList();
        foreach (var space in spaces)
        {
            SpaceChoices.Add(space);
        }

        MoveProjectChoices.Add(new TodoistRoutingChoiceViewModel(string.Empty, "Do not move in Todoist"));
        foreach (var project in moveProjects)
        {
            MoveProjectChoices.Add(project);
        }

        Title = existing is null ? "Add Todoist rule" : "Edit Todoist rule";
        HeadingText.Text = Title;
        NoLabelsCheck.IsChecked = existing?.MatchNoLabels == true;
        LabelBox.Text = existing?.MatchNoLabels == true ? string.Empty : existing?.Label ?? string.Empty;
        SpaceBox.SelectedItem = SpaceChoices.FirstOrDefault(space =>
            string.Equals(space.Id, existing?.SpaceId, StringComparison.Ordinal)) ?? SpaceChoices.FirstOrDefault();
        MoveProjectBox.SelectedItem = MoveProjectChoices.FirstOrDefault(project =>
            string.Equals(project.Id, existing?.MoveToProjectId ?? string.Empty, StringComparison.Ordinal)) ?? MoveProjectChoices[0];
        UpdateLabelSection();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (NoLabelsCheck.IsChecked == true)
        {
            SpaceBox.Focus();
            return;
        }

        LabelBox.Focus();
        LabelBox.SelectAll();
        FilterLabelSuggestions();
    }

    private void OnNoLabelsChanged(object? sender, RoutedEventArgs e)
    {
        UpdateLabelSection();
        if (NoLabelsCheck.IsChecked != true)
        {
            LabelBox.Focus();
            FilterLabelSuggestions();
        }
    }

    private void UpdateLabelSection()
    {
        LabelSection.IsEnabled = NoLabelsCheck.IsChecked != true;
        LabelSuggestionsBorder.IsVisible = NoLabelsCheck.IsChecked != true &&
            LabelSuggestionsList.ItemCount > 0;
    }

    private void OnLabelTextChanged(object? sender, TextChangedEventArgs e) => FilterLabelSuggestions();

    private void FilterLabelSuggestions()
    {
        if (NoLabelsCheck.IsChecked == true)
        {
            LabelSuggestionsList.ItemsSource = null;
            LabelSuggestionsBorder.IsVisible = false;
            return;
        }

        var query = TodoistRoutingRuleCodec.NormalizeLabel(LabelBox.Text ?? string.Empty);
        var matches = _labelChoices
            .Where(label => string.IsNullOrWhiteSpace(query) ||
                label.Id.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Take(8)
            .ToList();
        LabelSuggestionsList.ItemsSource = matches;
        LabelSuggestionsBorder.IsVisible = matches.Count > 0;
    }

    private void OnLabelSuggestionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (LabelSuggestionsList.SelectedItem is not TodoistRoutingChoiceViewModel choice)
        {
            return;
        }

        LabelBox.Text = choice.Id;
        LabelSuggestionsList.SelectedItem = null;
        LabelSuggestionsBorder.IsVisible = false;
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);

    private void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        var matchNoLabels = NoLabelsCheck.IsChecked == true;
        var label = matchNoLabels
            ? string.Empty
            : TodoistRoutingRuleCodec.NormalizeLabel(LabelBox.Text ?? string.Empty);
        if (!matchNoLabels && string.IsNullOrWhiteSpace(label))
        {
            ShowValidation("Choose or enter a Todoist label.");
            return;
        }

        if (SpaceBox.SelectedItem is not TodoistRoutingChoiceViewModel space)
        {
            ShowValidation("Choose the Space where matching tasks should go.");
            return;
        }

        var moveProject = MoveProjectBox.SelectedItem as TodoistRoutingChoiceViewModel;
        Close(new TodoistRoutingRuleDraft(
            _existing?.Id,
            label,
            space.Id,
            string.IsNullOrWhiteSpace(moveProject?.Id) ? null : moveProject.Id,
            matchNoLabels));
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.IsVisible = true;
    }
}
