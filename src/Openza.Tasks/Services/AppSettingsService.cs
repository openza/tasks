using System.Text.Json;
using Windows.Storage;

namespace Openza.Tasks.Services;

public sealed class AppSettingsService
{
    private readonly string _settingsPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "settings.json");

    public AppSettings Settings { get; private set; } = new();

    public async Task LoadAsync()
    {
        if (!File.Exists(_settingsPath))
        {
            return;
        }

        await using var stream = File.OpenRead(_settingsPath);
        Settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream).ConfigureAwait(false) ?? new AppSettings();
    }

    public async Task SaveAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath) ?? ".");
        await using var stream = File.Create(_settingsPath);
        await JsonSerializer.SerializeAsync(stream, Settings, new JsonSerializerOptions { WriteIndented = true }).ConfigureAwait(false);
    }
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "System";
    public string LastView { get; set; } = "inbox";
    public string LastSpaceId { get; set; } = string.Empty;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool WindowIsMaximized { get; set; } = true;
    public bool AutoBackupEnabled { get; set; } = true;
    public bool AutoSyncEnabled { get; set; } = true;
    public int AutoSyncIntervalMinutes { get; set; } = 5;
    public bool ShowGetStarted { get; set; } = true;
    public Dictionary<string, string> TaskGroupModes { get; set; } = new();
    public Dictionary<string, TaskViewSettings> TaskViewSettings { get; set; } = new();
    public DateTimeOffset? LastAutoBackupAt { get; set; }
    public string MicrosoftToDoClientId { get; set; } = string.Empty;
    public string MicrosoftToDoTenantId { get; set; } = "common";
}

public sealed class TaskViewSettings
{
    public string SortMode { get; set; } = "PriorityThenDate";
    public string GroupMode { get; set; } = "None";
    public int? Priority { get; set; }
    public string DateScope { get; set; } = "All";
    public string RepeatScope { get; set; } = "Include";
    public string? LabelId { get; set; }
}
