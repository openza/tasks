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
    internal ICredentialStore Credentials { get; }
    public ICredentialStore AppCredentials { get; }
    private bool _prepared;
    public bool CloudAccessInhibited { get; private set; }
    public const string CloudAccessNotice = "Credential access is temporarily unavailable. Local tasks remain available; cloud operations are paused for this session. Restore credential access and restart Openza to retry.";
    public string SettingsPath => Path.Combine(Data.Runtime.DataDirectory, "settings.avalonia.json");

    public WindowsContinuitySession(IWindowsContinuityPlatform platform)
    {
        Package = platform.GetCurrentPackage();
        Data = Package.ValidateDev();
        _sourceExclusion = platform.AcquireSourceExclusion();
        try { Credentials = platform.CreatePackageCredentialStore(Package); AppCredentials = new SessionCredentials(this); }
        catch { _sourceExclusion.Dispose(); throw; }
    }

    public void EnsureActive()
    {
        if (_disposed) throw new WindowsContinuityException("The source-exclusion session has ended. Restart preparation before accessing Windows data.");
    }
    internal void CompletePreparation() { EnsureActive(); _prepared = true; }
    internal void InhibitCloudAccess() { EnsureActive(); CloudAccessInhibited = true; _prepared = true; }
    public void EnsureCloudAccess()
    {
        EnsureActive();
        if (!_prepared || CloudAccessInhibited) throw new InvalidOperationException(CloudAccessNotice);
    }
    public Openza.Tasks.Core.PersonalCloud.IPersonalCloudSetupService GuardCloudService(Openza.Tasks.Core.PersonalCloud.IPersonalCloudSetupService service) =>
        service is WindowsSessionCloudService guarded && guarded.Session == this ? service : new WindowsSessionCloudService(this, service);

    private sealed class SessionCredentials(WindowsContinuitySession session) : ICredentialStore
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.EnsureActive();
            // Connection indicators can render offline without retrying an unavailable vault.
            // Never interpret this as authorization to replace or remove saved credentials.
            if (session.CloudAccessInhibited) return Task.FromResult<string?>(null);
            session.EnsureCloudAccess();
            return session.Credentials.GetAsync(key, cancellationToken);
        }
        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
        { session.EnsureCloudAccess(); return session.Credentials.SaveAsync(key, value, cancellationToken); }
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        { session.EnsureCloudAccess(); return session.Credentials.RemoveAsync(key, cancellationToken); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sourceExclusion.Dispose();
    }
}
