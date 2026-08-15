using Openza.Tasks.Core.Services;

namespace Openza.Tasks.Desktop.ViewModels;

public sealed class RestorePointViewModel
{
    public RestorePointViewModel(BackupInfo backup)
    {
        Backup = backup;
    }

    public BackupInfo Backup { get; }
    public string Title => Backup.DisplayName;
    public string Details
    {
        get
        {
            var counts = Backup.TaskCount is null
                ? string.Empty
                : $" · {Backup.TaskCount} tasks · {Backup.ProjectCount ?? 0} projects";
            return $"{Backup.CreatedAt:g}{counts}";
        }
    }
}
