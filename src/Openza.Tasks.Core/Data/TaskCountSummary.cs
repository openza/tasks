namespace Openza.Tasks.Core.Data;

public sealed record TaskCountSummary(
    int Inbox,
    int NextActions,
    int Waiting,
    int Someday,
    int Today,
    int Overdue,
    int Open,
    int All,
    int Completed,
    IReadOnlyDictionary<string, int> ActiveByProject);
