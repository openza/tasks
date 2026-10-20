using Openza.Tasks.Application.Runtime;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Desktop.Services.Continuity;

namespace Openza.Tasks.Desktop.Services;

public static class DesktopCredentialStore
{
    public static ICredentialStore Create(OpenzaRuntimeContext runtime) => WindowsContinuityHost.Session is { } session && session.Data.Runtime == runtime
        ? session.AppCredentials : OperatingSystem.IsWindows()
        ? new WindowsCredentialStore(runtime.CredentialNamespace)
        : new SecretToolCredentialStore(runtime.CredentialNamespace, runtime.DisplayName);
}
