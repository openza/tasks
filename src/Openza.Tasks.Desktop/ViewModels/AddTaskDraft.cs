namespace Openza.Tasks.Desktop.ViewModels;

public sealed record AddTaskDraft(
    string Title,
    string Notes,
    ProjectOptionViewModel? Project,
    int StatusIndex,
    int PriorityIndex,
    DateTimeOffset? PlannedDate,
    string LabelsText,
    bool OpenAfterCreate);
