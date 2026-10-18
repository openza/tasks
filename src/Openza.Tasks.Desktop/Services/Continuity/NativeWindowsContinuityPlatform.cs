#if WINDOWS
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AppInstance = Microsoft.Windows.AppLifecycle.AppInstance;
using Openza.Tasks.Core.Credentials;
using Windows.ApplicationModel;
using Windows.Security.Credentials;
using Windows.Storage;

namespace Openza.Tasks.Desktop.Services.Continuity;

public sealed class NativeWindowsContinuityPlatform : IWindowsContinuityPlatform
{
    public WindowsPackageEvidence GetCurrentPackage()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var id = Package.Current.Id;
        // Check identity before asking for package app data. No profile enumeration.
        if (id.Name != "Openza.OpenzaTasks.Dev" || id.Publisher != "CN=Openza")
            throw new WindowsContinuityException("Windows continuity is enabled only for the matching signed Dev package.");
        return new(id.Name, id.Publisher, id.FamilyName, ApplicationData.Current.LocalFolder.Path);
    }

    public IDisposable AcquireSourceExclusion()
    {
        var instance = AppInstance.FindOrRegisterForKey("Openza.Tasks.Main");
        if (!instance.IsCurrent)
            throw new WindowsContinuityException("Close the other Openza Tasks Dev window before retrying the transition. No data was changed.");
        return new Registration(instance);
    }

    public ICredentialStore CreatePackageCredentialStore(WindowsPackageEvidence package)
    {
        package.ValidateDev();
        return new VaultStore($"Openza.Tasks.Desktop/{package.FamilyName}/tasks-dev", false);
    }

    public ICredentialStore OpenLegacyCloudCredentialReader() => new VaultStore("Openza.Tasks", true);
    public byte[] UnprotectMicrosoftCache(byte[] protectedBytes) =>
        ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);

    private sealed class Registration(AppInstance instance) : IDisposable
    {
        // Registration is process-owned. Do not unregister in a window/Exit
        // callback while background work could still finish; Windows releases it
        // when the process actually exits.
        public void Dispose() => GC.KeepAlive(instance);
    }

    private sealed class VaultStore(string resource, bool legacyCloudOnly) : ICredentialStore
    {
        private readonly PasswordVault _vault = new();
        private static bool Missing(COMException exception) => exception.HResult == unchecked((int)0x80070490);
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (legacyCloudOnly && (!key.StartsWith("personal-cloud-device-", StringComparison.Ordinal) ||
                key.Length != "personal-cloud-device-".Length + 64 || !key["personal-cloud-device-".Length..].All(Uri.IsHexDigit)))
                throw new WindowsContinuityException("Legacy credentials without channel ownership cannot be imported. Reconnect the provider.");
            try
            {
                var credential = _vault.Retrieve(resource, key);
                credential.RetrievePassword();
                return Task.FromResult<string?>(credential.Password);
            }
            catch (COMException exception) when (Missing(exception)) { return Task.FromResult<string?>(null); }
        }
        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (legacyCloudOnly) throw new InvalidOperationException("The legacy credential reader is read-only.");
            // PasswordVault.Add replaces the same resource/userName entry. Never
            // remove first: a failed replacement must leave the old entry intact.
            _vault.Add(new PasswordCredential(resource, key, value));
            return Task.CompletedTask;
        }
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (legacyCloudOnly) throw new InvalidOperationException("The legacy credential reader is read-only.");
            try { _vault.Remove(_vault.Retrieve(resource, key)); }
            catch (COMException exception) when (Missing(exception)) { }
            return Task.CompletedTask;
        }
    }
}
#endif
