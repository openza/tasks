using Openza.Tasks.Application.Runtime;
using Openza.Tasks.Core.Credentials;

namespace Openza.Tasks.Desktop.Services;

public static class DesktopCredentialStore
{
    public static ICredentialStore Create(OpenzaRuntimeContext runtime) => OperatingSystem.IsWindows()
        ? new WindowsCredentialStore(runtime.CredentialNamespace)
        : new SecretToolCredentialStore(runtime.CredentialNamespace, runtime.DisplayName);
}
