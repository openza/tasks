using System.Text.Json;
using Openza.Tasks.Core.Services;

namespace Openza.Tasks.Desktop.Services;

public sealed class DesktopPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(DesktopDataPaths.DataDirectory, "settings.json");

    public DesktopPreferences Load()
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

    public async Task SaveAsync(DesktopPreferences preferences)
    {
        Directory.CreateDirectory(DesktopDataPaths.DataDirectory);
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(preferences, JsonOptions));
        PrivateFilePermissions.EnsureFile(_path);
    }
}

public sealed record DesktopPreferences
{
    public string Theme { get; init; } = "System";
    public string? SelectedSpaceId { get; init; }
    public bool AutomaticSyncEnabled { get; init; } = true;
}
