using Openza.Tasks.Core.Models;

namespace Openza.Tasks.ViewModels;

public sealed class TaskEditorViewModel
{
    public TaskItem? Task { get; init; }
    public ProjectItem? Project { get; init; }
    public bool IsNew => Task is null;
    public bool IsProviderOwned => Task?.IsProviderTask == true || Task?.HasProviderSource == true;
    public bool CanEditProviderFields => IsNew || !IsProviderOwned;
    public string SourceText => Task is null ? "Openza Tasks" : TaskListItemViewModel.SourceName(Task.SourceIntegrationId ?? Task.IntegrationId);
    public string ProviderOwnershipText => IsProviderOwned
        ? $"{SourceText} owns the source task. Openza Tasks keeps status, project, date, deadline, priority, labels, notes, and completion state local."
        : "This is a local Openza task. All fields are editable.";
}
