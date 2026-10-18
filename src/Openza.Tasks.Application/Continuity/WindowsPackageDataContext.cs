using Openza.Tasks.Application.Runtime;

namespace Openza.Tasks.Application.Continuity;

/// <summary>
/// Explicit package-local layout. The Windows host must supply the identity and
/// LocalFolder returned by platform APIs; this type never discovers profiles or opens files.
/// </summary>
public sealed class WindowsPackageDataContext
{
    private WindowsPackageDataContext(OpenzaRuntimeContext runtime) => Runtime = runtime;

    public OpenzaRuntimeContext Runtime { get; }
    public string ProvisioningPath => Path.Combine(Runtime.DataDirectory, "personal-cloud-provisioning.json");
    public string ConnectionPath => Path.Combine(Runtime.DataDirectory, "personal-cloud-connection.json");
    public string MicrosoftCachePath => Path.Combine(Runtime.DataDirectory, "msal-cache.bin");

    public static WindowsPackageDataContext FromLocalFolder(
        OpenzaChannel channel, string packageIdentityName, string packageLocalFolder)
    {
        var expected = channel switch
        {
            OpenzaChannel.Dev => "Openza.OpenzaTasks.Dev",
            OpenzaChannel.Preview => "Openza.OpenzaTasks.Preview",
            OpenzaChannel.Production => "Openza.OpenzaTasks",
            _ => throw new ArgumentOutOfRangeException(nameof(channel)),
        };
        if (!string.Equals(expected, packageIdentityName, StringComparison.Ordinal))
            throw new ArgumentException("The Windows package identity does not match the requested channel.", nameof(packageIdentityName));
        ArgumentException.ThrowIfNullOrWhiteSpace(packageLocalFolder);
        if (!Path.IsPathFullyQualified(packageLocalFolder))
            throw new ArgumentException("A platform-resolved absolute LocalFolder is required.", nameof(packageLocalFolder));

        return new WindowsPackageDataContext(new OpenzaRuntimeContext
        {
            Channel = channel,
            DataDirectory = Path.GetFullPath(packageLocalFolder),
        });
    }
}
