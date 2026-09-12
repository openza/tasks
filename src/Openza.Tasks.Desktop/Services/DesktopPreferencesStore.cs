using System.Collections.Concurrent;
using System.Text.Json;
using Openza.Tasks.Core.Services;

namespace Openza.Tasks.Desktop.Services;

public sealed class DesktopPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, PathState> PathStates = new(StringComparer.Ordinal);
    private readonly string _path;
    private readonly PathState _state;

    public DesktopPreferencesStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(DesktopDataPaths.DataDirectory, "settings.json"));
        _state = PathStates.GetOrAdd(_path, static _ => new PathState());
    }

    public DesktopPreferences Load()
    {
        lock (_state.Gate)
        {
            return Clone(LoadCached());
        }
    }

    public Task SaveAsync(DesktopPreferences preferences)
    {
        lock (_state.Gate)
        {
            SaveCore(preferences);
            _state.Preferences = Clone(preferences);
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(Func<DesktopPreferences, DesktopPreferences> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_state.Gate)
        {
            var preferences = update(Clone(LoadCached()));
            SaveCore(preferences);
            _state.Preferences = Clone(preferences);
        }

        return Task.CompletedTask;
    }

    private DesktopPreferences LoadCached() => _state.Preferences ??= LoadCore();

    private static DesktopPreferences Clone(DesktopPreferences preferences) => preferences with
    {
        ProjectSortSettings = new Dictionary<string, Openza.Tasks.Core.Data.ProjectSortSettings>(
            preferences.ProjectSortSettings ?? new Dictionary<string, Openza.Tasks.Core.Data.ProjectSortSettings>(),
            StringComparer.Ordinal),
        TaskViewSettings = new Dictionary<string, DesktopTaskViewPreferences>(
            preferences.TaskViewSettings ?? new Dictionary<string, DesktopTaskViewPreferences>(),
            StringComparer.Ordinal),
    };

    private DesktopPreferences LoadCore()
    {
        PrivateFilePermissions.EnsureFile(_path);
        if (!File.Exists(_path))
        {
            return new DesktopPreferences();
        }

        try
        {
            return JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(_path)) ?? new DesktopPreferences();
        }
        catch (JsonException)
        {
            return new DesktopPreferences();
        }
    }

    private void SaveCore(DesktopPreferences preferences)
    {
        var directory = Path.GetDirectoryName(_path) ?? ".";
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences, JsonOptions));
            PrivateFilePermissions.EnsureFile(temporaryPath);
            File.Move(temporaryPath, _path, overwrite: true);
            PrivateFilePermissions.EnsureFile(_path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed class PathState
    {
        public object Gate { get; } = new();
        public DesktopPreferences? Preferences { get; set; }
    }
}

public sealed record DesktopPreferences
{
    public Dictionary<string, Openza.Tasks.Core.Data.ProjectSortSettings> ProjectSortSettings { get; init; } = new(StringComparer.Ordinal);
    public string Theme { get; init; } = "System";
    public string? SelectedSpaceId { get; init; }
    public bool AutomaticSyncEnabled { get; init; } = true;
    public bool AutomaticRestorePointsEnabled { get; init; } = true;
    public bool ShowGetStarted { get; init; } = true;
    public string LastView { get; init; } = "Inbox";
    public double WindowWidth { get; init; } = 1440;
    public double WindowHeight { get; init; } = 800;
    public bool WindowMaximized { get; init; } = true;
    public MicrosoftAccount? MicrosoftToDoAccount { get; init; }
    public MicrosoftAccount? OneDriveAccount { get; init; }
    public bool OneDriveEnabled { get; init; }
    public bool OneDriveEncrypted { get; init; }
    public Dictionary<string, DesktopTaskViewPreferences> TaskViewSettings { get; init; } = new(StringComparer.Ordinal);
}

public sealed record DesktopTaskViewPreferences
{
    public int SortIndex { get; init; }
    public int SortDirectionIndex { get; init; }
    public int GroupIndex { get; init; }
    public int PriorityFilterIndex { get; init; }
    public int RepeatFilterIndex { get; init; }
    public string? LabelFilterId { get; init; }
}
