using System.Collections.Concurrent;
using System.Text.Json;
using Openza.Tasks.Core.Services;

namespace Openza.Tasks.Desktop.Services;

public sealed class DesktopPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> PathGates = new(StringComparer.Ordinal);
    private readonly string _path;
    private readonly object _gate;

    public DesktopPreferencesStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(DesktopDataPaths.DataDirectory, "settings.json"));
        _gate = PathGates.GetOrAdd(_path, static _ => new object());
    }

    public DesktopPreferences Load()
    {
        lock (_gate)
        {
            return LoadCore();
        }
    }

    public Task SaveAsync(DesktopPreferences preferences)
    {
        lock (_gate)
        {
            SaveCore(preferences);
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(Func<DesktopPreferences, DesktopPreferences> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            SaveCore(update(LoadCore()));
        }

        return Task.CompletedTask;
    }

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
}

public sealed record DesktopPreferences
{
    public string Theme { get; init; } = "System";
    public string? SelectedSpaceId { get; init; }
    public bool AutomaticSyncEnabled { get; init; } = true;
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
