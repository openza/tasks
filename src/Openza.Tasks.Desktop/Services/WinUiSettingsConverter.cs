using System.Text.Json;
using Openza.Tasks.Core.Data;

namespace Openza.Tasks.Desktop.Services;

/// <summary>A preparation result, never automatically installed over a settings file.</summary>
public sealed class WinUiSettingsConversion(
    DesktopPreferences preferences, string originalSettingsJson, IReadOnlyList<string> retainedFields)
{
    public const int FormatVersion = 1;
    public DesktopPreferences Preferences { get; } = preferences;
    // Retain the exact input alongside any future converted file. Unsupported fields
    // and legacy account metadata must survive even when Desktop cannot use them yet.
    public string OriginalSettingsJson { get; } = originalSettingsJson;
    public IReadOnlyList<string> RetainedFields { get; } = retainedFields;
    public bool RequiresMicrosoftAuthenticationValidation => Preferences.MicrosoftToDoAccount is not null || Preferences.OneDriveAccount is not null;
}

/// <summary>Pure WinUI settings conversion. No profile discovery, file writes, vault access or cache migration.</summary>
public static class WinUiSettingsConverter
{
    private static readonly Dictionary<string, string> Views = new(StringComparer.Ordinal)
    {
        ["inbox"] = "Inbox", ["next"] = "NextActions", ["today"] = "Today",
        ["calendar"] = "Calendar", ["overdue"] = "Overdue", ["waiting"] = "Waiting",
        ["someday"] = "Someday", ["tasks"] = "Open", ["completed"] = "Completed",
    };
    private static readonly HashSet<string> MappedFields = new(StringComparer.Ordinal)
    {
        "ProjectSortSettings", "Theme", "LastView", "LastSpaceId", "WindowWidth", "WindowHeight",
        "WindowIsMaximized", "AutoBackupEnabled", "AutoSyncEnabled", "ShowGetStarted", "TaskGroupModes",
        "TaskViewSettings", "OneDriveBackupEnabled", "OneDriveBackupEncryptionEnabled", "MicrosoftToDoAccount", "OneDriveBackupAccount",
    };
    private static readonly HashSet<string> MappedViewFields = new(StringComparer.Ordinal)
    {
        "SortMode", "SortDirection", "GroupMode", "Priority", "DateScope", "RepeatScope", "LabelId",
    };
    private static readonly HashSet<string> MappedProjectSortFields = new(StringComparer.Ordinal)
    {
        "Mode", "Descending", "FavoritesFirst",
    };

    public static WinUiSettingsConversion Convert(string winUiSettingsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(winUiSettingsJson);
        try
        {
            using var document = JsonDocument.Parse(winUiSettingsJson);
            ValidateObjects(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw Invalid();
            var source = JsonSerializer.Deserialize<LegacySettings>(winUiSettingsJson) ?? throw Invalid();
            if (!Views.TryGetValue(source.LastView, out var lastView) || source.Theme is not ("System" or "Light" or "Dark") ||
                !double.IsFinite(source.WindowWidth) || source.WindowWidth <= 0 ||
                !double.IsFinite(source.WindowHeight) || source.WindowHeight <= 0 ||
                source.ProjectSortSettings is null || source.TaskViewSettings is null || source.TaskGroupModes is null)
                throw Invalid();
            if (source.ProjectSortSettings.Values.Any(value => value is null || !Enum.IsDefined(value.Mode)))
                throw Invalid();

            var views = new Dictionary<string, DesktopTaskViewPreferences>(StringComparer.Ordinal);
            foreach (var (key, value) in source.TaskViewSettings)
            {
                if (value is null) throw Invalid();
                var parts = key.Split('|');
                if (parts.Length != 3 || !Views.ContainsKey(parts[1]) || string.IsNullOrWhiteSpace(parts[2])) throw Invalid();
                var space = string.IsNullOrEmpty(parts[0]) ? "all" : parts[0];
                var mappedKey = $"{space}|{parts[1]}|{parts[2]}";
                var converted = new DesktopTaskViewPreferences
                {
                    SortIndex = EnumIndex<TaskSortMode>(value.SortMode),
                    SortDirectionIndex = EnumIndex<TaskSortDirection>(value.SortDirection),
                    GroupIndex = EnumIndex<TaskGroupMode>(value.GroupMode),
                    PriorityFilterIndex = value.Priority switch { null => 0, >= 1 and <= 4 => value.Priority.Value, _ => throw Invalid() },
                    RepeatFilterIndex = EnumIndex<TaskRepeatScope>(value.RepeatScope),
                    LabelFilterId = value.LabelId,
                };
                if (value.DateScope != "All" || !views.TryAdd(mappedKey, converted)) throw Invalid();
            }
            // WinUI's older per-view grouping is a fallback only when a modern view
            // record is absent; preserve that precedence in the converted settings.
            foreach (var (view, group) in source.TaskGroupModes)
            {
                if (!Views.ContainsKey(view)) throw Invalid();
                // Tasks grouping uses its explicit shared record or the Project
                // default in both hosts, never this older per-view fallback.
                if (view == "tasks") continue;
                var groupIndex = EnumIndex<TaskGroupMode>(group);
                var space = string.IsNullOrEmpty(source.LastSpaceId) ? "all" : source.LastSpaceId;
                views.TryAdd($"{space}|{view}|all", new DesktopTaskViewPreferences { GroupIndex = groupIndex });
            }

            var preferences = new DesktopPreferences
            {
                ProjectSortSettings = source.ProjectSortSettings,
                Theme = source.Theme,
                SelectedSpaceId = string.IsNullOrEmpty(source.LastSpaceId) ? null : source.LastSpaceId,
                HasSelectedSpace = !string.IsNullOrEmpty(source.LastSpaceId),
                LastView = lastView,
                WindowWidth = source.WindowWidth,
                WindowHeight = source.WindowHeight,
                WindowMaximized = source.WindowIsMaximized,
                AutomaticRestorePointsEnabled = source.AutoBackupEnabled,
                AutomaticSyncEnabled = source.AutoSyncEnabled,
                ShowGetStarted = source.ShowGetStarted,
                MicrosoftToDoAccount = Account(source.MicrosoftToDoAccount),
                OneDriveAccount = Account(source.OneDriveBackupAccount),
                OneDriveEnabled = source.OneDriveBackupEnabled,
                OneDriveEncrypted = source.OneDriveBackupEncryptionEnabled,
                TaskViewSettings = views,
            };
            var retained = document.RootElement.EnumerateObject().Select(item => item.Name)
                .Where(name => !MappedFields.Contains(name)).Order(StringComparer.Ordinal).ToList();
            CollectRetainedRecordFields(document.RootElement, "TaskViewSettings", MappedViewFields, retained);
            CollectRetainedRecordFields(document.RootElement, "ProjectSortSettings", MappedProjectSortFields, retained);
            // Feature account fields such as ConnectedAt/LastAuthStatus are retained
            // in the original even though Desktop only consumes identity/username.
            if (source.MicrosoftToDoAccount is not null) retained.Add("MicrosoftToDoAccount metadata");
            if (source.OneDriveBackupAccount is not null) retained.Add("OneDriveBackupAccount metadata");
            if (source.TaskGroupModes.Keys.Any(view => view != "tasks")) retained.Add("TaskGroupModes fallback for other spaces");
            if (source.TaskGroupModes.ContainsKey("tasks")) retained.Add("TaskGroupModes.tasks");
            return new WinUiSettingsConversion(preferences, winUiSettingsJson, retained.AsReadOnly());
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or OverflowException)
        {
            throw Invalid();
        }
    }

    private static MicrosoftAccount? Account(LegacyAccount? source) => string.IsNullOrWhiteSpace(source?.HomeAccountId)
        ? null : new MicrosoftAccount(source.HomeAccountId, source.Username ?? string.Empty);

    private static int EnumIndex<T>(string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) && value == parsed.ToString()
            ? System.Convert.ToInt32(parsed) : throw Invalid();

    private static void CollectRetainedRecordFields(
        JsonElement root, string collection, IReadOnlySet<string> mappedFields, List<string> retained)
    {
        if (!root.TryGetProperty(collection, out var records)) return;
        foreach (var record in records.EnumerateObject())
            foreach (var property in record.Value.EnumerateObject())
                if (!mappedFields.Contains(property.Name))
                    retained.Add($"{collection}[{JsonSerializer.Serialize(record.Name)}][{JsonSerializer.Serialize(property.Name)}]");
    }

    private static void ValidateObjects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid();
                ValidateObjects(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateObjects(item);
    }

    private static InvalidDataException Invalid() => new("WinUI settings cannot be converted without losing or reinterpreting known values. The original must be retained.");

    private sealed class LegacySettings
    {
        public Dictionary<string, ProjectSortSettings> ProjectSortSettings { get; init; } = new();
        public string Theme { get; init; } = "System";
        public string LastView { get; init; } = "inbox";
        public string LastSpaceId { get; init; } = string.Empty;
        public double WindowWidth { get; init; } = 1280;
        public double WindowHeight { get; init; } = 800;
        public bool WindowIsMaximized { get; init; } = true;
        public bool AutoBackupEnabled { get; init; } = true;
        public bool AutoSyncEnabled { get; init; } = true;
        public bool ShowGetStarted { get; init; } = true;
        public Dictionary<string, string> TaskGroupModes { get; init; } = new();
        public Dictionary<string, LegacyView?> TaskViewSettings { get; init; } = new();
        public bool OneDriveBackupEnabled { get; init; }
        public bool OneDriveBackupEncryptionEnabled { get; init; }
        public LegacyAccount? MicrosoftToDoAccount { get; init; }
        public LegacyAccount? OneDriveBackupAccount { get; init; }
    }

    private sealed class LegacyAccount
    {
        public string HomeAccountId { get; init; } = string.Empty;
        public string? Username { get; init; }
    }

    private sealed class LegacyView
    {
        public string SortMode { get; init; } = "PriorityThenDate";
        public string SortDirection { get; init; } = "Ascending";
        public string GroupMode { get; init; } = "None";
        public int? Priority { get; init; }
        public string DateScope { get; init; } = "All";
        public string RepeatScope { get; init; } = "Include";
        public string? LabelId { get; init; }
    }
}
