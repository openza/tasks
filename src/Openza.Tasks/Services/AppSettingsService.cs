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
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool AutoBackupEnabled { get; set; } = true;
    public DateTimeOffset? LastAutoBackupAt { get; set; }
    public string MicrosoftToDoClientId { get; set; } = string.Empty;
    public string MicrosoftToDoTenantId { get; set; } = "common";
}
