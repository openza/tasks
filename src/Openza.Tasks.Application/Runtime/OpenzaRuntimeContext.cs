using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Openza.Tasks.Application.Runtime;

public sealed record OpenzaRuntimeContext
{
    private const string PublishedChannelMarkerFileName = ".openza-channel";
    public required OpenzaChannel Channel { get; init; }
    public required string DataDirectory { get; init; }

    public string DatabasePath => Path.Combine(DataDirectory, "openza-tasks.db");
    public string RestorePointDirectory => Path.Combine(DataDirectory, "restore-points");
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string RuntimeLockPath => Path.Combine(GetCoordinationLockDirectory(), $"{StableHash(Path.GetFullPath(DataDirectory))}.lock");
    public string DatabaseReplacementLockPath => Path.Combine(
        GetCoordinationLockDirectory(), $"{StableHash(Path.GetFullPath(DataDirectory))}.replacement.lock");
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

    private static string GetCoordinationLockDirectory()
    {
        if (OperatingSystem.IsLinux())
        {
            const string linuxTemporaryRoot = "/tmp";
            var linuxCandidate = Path.Combine(linuxTemporaryRoot, $"openza-runtime-{GetEffectiveUserId()}", "locks");
            if (TryPrepareCoordinationDirectory(linuxCandidate, linuxTemporaryRoot))
            {
                return linuxCandidate;
            }

            throw new InvalidOperationException("The deterministic Linux runtime coordination directory is unavailable or unsafe.");
        }

        var localDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localDataDirectory))
        {
            var localCandidate = Path.Combine(localDataDirectory, "Openza", "runtime-locks");
            if (TryPrepareCoordinationDirectory(localCandidate, localDataDirectory))
            {
                return localCandidate;
            }
        }

        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var userIdentity = $"{Environment.UserName}|{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}";
        var temporaryCandidate = Path.Combine(temporaryRoot, $"openza-runtime-{StableHash(userIdentity)}", "locks");
        if (TryPrepareCoordinationDirectory(temporaryCandidate, temporaryRoot))
        {
            return temporaryCandidate;
        }

        throw new InvalidOperationException("A writable per-user runtime coordination directory could not be resolved.");
    }

    private static bool TryPrepareCoordinationDirectory(string path, string trustedBasePath)
    {
        try
        {
            if (ContainsSymbolicLink(path, trustedBasePath))
            {
                return false;
            }
            Directory.CreateDirectory(path);
            if (ContainsSymbolicLink(path, trustedBasePath))
            {
                return false;
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var probePath = Path.Combine(path, $".write-probe-{Guid.NewGuid():N}");
            using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool ContainsSymbolicLink(string path, string trustedBasePath)
    {
        var trustedBase = Path.GetFullPath(trustedBasePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Directory.Exists(trustedBase) && new DirectoryInfo(trustedBase).LinkTarget is not null)
        {
            return true;
        }

        var fullPath = Path.GetFullPath(path);
        var relativePath = Path.GetRelativePath(trustedBase, fullPath);
        if (relativePath == ".." || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return true;
        }

        var current = trustedBase;
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current)) && new FileInfo(current).LinkTarget is not null)
            {
                return true;
            }
        }
        return false;
    }

    private static string StableHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
