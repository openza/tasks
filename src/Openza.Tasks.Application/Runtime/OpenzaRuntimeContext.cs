using System.Reflection;

namespace Openza.Tasks.Application.Runtime;

public sealed record OpenzaRuntimeContext
{
    private const string PublishedChannelMarkerFileName = ".openza-channel";
    public required OpenzaChannel Channel { get; init; }
    public required string DataDirectory { get; init; }

    public string DatabasePath => Path.Combine(DataDirectory, "openza-tasks.db");
    public string RestorePointDirectory => Path.Combine(DataDirectory, "restore-points");
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string RuntimeLockPath => Path.Combine(DataDirectory, ".runtime.lock");
    public string CredentialNamespace => Channel switch
    {
        OpenzaChannel.Production => "tasks",
        OpenzaChannel.Preview => "tasks-preview",
        _ => "tasks-dev",
    };

    public string DisplayName => Channel switch
    {
        OpenzaChannel.Preview => "Openza Tasks Preview",
        OpenzaChannel.Dev => "Openza Tasks Dev",
        _ => "Openza Tasks",
    };

    public static OpenzaRuntimeContext FromEntryAssembly() =>
        Create(ReadChannel(Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()));

    public static OpenzaRuntimeContext Create(OpenzaChannel channel, string? devDataDirectory = null)
    {
        var dataDirectory = channel == OpenzaChannel.Dev && !string.IsNullOrWhiteSpace(devDataDirectory)
            ? Path.GetFullPath(devDataDirectory)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Openza",
                channel switch
                {
                    OpenzaChannel.Preview => "Tasks Preview",
                    OpenzaChannel.Dev => "Tasks Dev",
                    _ => "Tasks",
                });

        return new OpenzaRuntimeContext { Channel = channel, DataDirectory = dataDirectory };
    }

    public static OpenzaChannel ReadChannel(Assembly assembly)
        => ReadChannel(assembly, AppContext.BaseDirectory);

    public static OpenzaChannel ReadChannel(Assembly assembly, string baseDirectory)
    {
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>();
        var channel = metadata.FirstOrDefault(attribute => attribute.Key == "OpenzaChannel")?.Value;
        var packagingProfile = metadata.FirstOrDefault(attribute => attribute.Key == "OpenzaPackagingProfile")?.Value;
        string? publishedChannelMarker = null;
        try
        {
            var markerPath = Path.Combine(baseDirectory, PublishedChannelMarkerFileName);
            if (File.Exists(markerPath))
            {
                publishedChannelMarker = File.ReadAllText(markerPath).Trim();
            }
        }
        catch (IOException)
        {
            // A missing or unreadable publication marker must fail closed to Dev.
        }
        catch (UnauthorizedAccessException)
        {
            // A missing or unreadable publication marker must fail closed to Dev.
        }

        return ResolvePackagedChannel(channel, packagingProfile, publishedChannelMarker);
    }

    public static OpenzaChannel ResolvePackagedChannel(
        string? channelValue,
        string? packagingProfile,
        string? publishedChannelMarker)
    {
        if (!Enum.TryParse<OpenzaChannel>(channelValue, ignoreCase: true, out var channel))
        {
            return OpenzaChannel.Dev;
        }

        if (channel == OpenzaChannel.Dev)
        {
            return OpenzaChannel.Dev;
        }

        return string.Equals(channel.ToString(), packagingProfile, StringComparison.Ordinal)
            && string.Equals(channel.ToString(), publishedChannelMarker, StringComparison.Ordinal)
            ? channel
            : OpenzaChannel.Dev;
    }
}
