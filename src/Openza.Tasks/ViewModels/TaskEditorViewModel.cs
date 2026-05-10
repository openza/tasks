using Openza.Tasks.Core.Models;

namespace Openza.Tasks.ViewModels;

public sealed class TaskEditorViewModel
{
    public TaskItem? Task { get; init; }
    public ProjectItem? Project { get; init; }
    public bool IsNew => Task is null;
    public bool IsProviderOwned => Task?.IsProviderTask == true;
    public bool CanEditProviderFields => IsNew || !IsProviderOwned;
    public string SourceText => Task is null ? "Openza Tasks" : TaskListItemViewModel.SourceName(Task.IntegrationId);
    public string ProviderOwnershipText => IsProviderOwned
        ? $"{SourceText} owns title, project, due date, and priority. Openza Tasks can save local notes and labels, plus completion state."
        : "This is a local Openza task. All fields are editable.";
}
