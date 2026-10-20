namespace Openza.Tasks.Desktop.ViewModels.Navigation;

/// <summary>Remembers selection against displayed rows even when a selector already holds the destination.</summary>
public sealed class TaskSelectionState
{
    private readonly Dictionary<string, string> _selections = new(StringComparer.Ordinal);
    public TaskViewContext? DisplayedContext { get; set; }

    public void RememberDisplayed(string? taskId)
    {
        if (taskId is not null && DisplayedContext is { } context)
            _selections[context.SelectionKey] = taskId;
    }

    public string? RememberedFor(TaskViewContext destination) => _selections.GetValueOrDefault(destination.SelectionKey);

    public void ClearDisplayed()
    {
        if (DisplayedContext is { } context) _selections.Remove(context.SelectionKey);
    }
}
