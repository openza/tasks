using Openza.Tasks.Core.Data;

namespace Openza.Tasks.Desktop.ViewModels.Navigation;

/// <summary>Existing navigation scopes and persisted keys, independent of bound selector objects.</summary>
public sealed record TaskViewContext(string? SpaceId, TaskListKind Kind, string? ProjectId = null)
{
    public bool IsTasksView => ProjectId is not null || Kind == TaskListKind.Open;
    public string SelectionKey => $"{SpaceId ?? "all"}|{ProjectId ?? Kind.ToString()}";
    public string SharedTasksSettingsKey => $"{SpaceId ?? "all"}|tasks|all";
    public string PreferencesKey
    {
        get
        {
            var view = ProjectId is not null ? "tasks" : ViewKey(Kind);
            var project = string.Equals(view, "tasks", StringComparison.Ordinal) ? ProjectId ?? "all" : "all";
            return $"{SpaceId ?? "all"}|{view}|{project}";
        }
    }

    public int DefaultGroupIndex => ProjectId is not null ? 2 : Kind switch
    {
        TaskListKind.Overdue or TaskListKind.Calendar => 1,
        TaskListKind.Completed => 9,
        TaskListKind.Open => 2,
        _ => 0,
    };

    private static string ViewKey(TaskListKind kind) => kind switch
    {
        TaskListKind.Inbox => "inbox",
        TaskListKind.NextActions => "next",
        TaskListKind.Today => "today",
        TaskListKind.Calendar => "calendar",
        TaskListKind.Overdue => "overdue",
        TaskListKind.Waiting => "waiting",
        TaskListKind.Someday => "someday",
        TaskListKind.Open => "tasks",
        TaskListKind.Completed => "completed",
        _ => "inbox",
    };
}
