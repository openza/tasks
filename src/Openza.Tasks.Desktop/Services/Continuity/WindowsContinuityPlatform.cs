using Openza.Tasks.Application.Continuity;
using Openza.Tasks.Application.Runtime;
using Openza.Tasks.Core.Credentials;

namespace Openza.Tasks.Desktop.Services.Continuity;

public sealed record WindowsPackageEvidence(string Name, string Publisher, string FamilyName, string LocalFolder)
{
    public WindowsPackageDataContext ValidateDev()
    {
        if (Name != "Openza.OpenzaTasks.Dev" || Publisher != "CN=Openza" ||
            !FamilyName.StartsWith(Name + "_", StringComparison.Ordinal) || FamilyName.Length <= Name.Length + 1)
            throw new WindowsContinuityException("This transition is limited to the matching signed Dev package.");
        return WindowsPackageDataContext.FromLocalFolder(OpenzaChannel.Dev, Name, LocalFolder);
    }
}

public sealed class WindowsContinuityException(string message) : Exception(message);

public interface IWindowsContinuityPlatform
{
    WindowsPackageEvidence GetCurrentPackage();
    // Must acquire the SAME package-local AppInstance key checked by old WinUI
    // before OpenMainWindowAsync. Retain registration until the new host exits.
    IDisposable AcquireSourceExclusion();
    ICredentialStore CreatePackageCredentialStore(WindowsPackageEvidence package);
    ICredentialStore OpenLegacyCloudCredentialReader();
    byte[] UnprotectMicrosoftCache(byte[] protectedBytes);
}

public sealed class WindowsContinuitySession : IDisposable
{
    private readonly IDisposable _sourceExclusion;
    private bool _disposed;
    public WindowsPackageEvidence Package { get; }
    public WindowsPackageDataContext Data { get; }
    public ICredentialStore Credentials { get; }
    public string SettingsPath => Path.Combine(Data.Runtime.DataDirectory, "settings.avalonia.json");

    public WindowsContinuitySession(IWindowsContinuityPlatform platform)
    {
        Package = platform.GetCurrentPackage();
        Data = Package.ValidateDev();
        _sourceExclusion = platform.AcquireSourceExclusion();
        try { Credentials = platform.CreatePackageCredentialStore(Package); }
        catch { _sourceExclusion.Dispose(); throw; }
    }

    public void EnsureActive()
    {
        if (_disposed) throw new WindowsContinuityException("The source-exclusion session has ended. Restart preparation before accessing Windows data.");
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sourceExclusion.Dispose();
    }
}
