using Openza.Tasks.Core.Credentials;

namespace Openza.Tasks.Core.PersonalCloud;

public static class PersonalCloudSetupFactory
{
    // A locally built artifact directory (Dev builds and the frozen Windows app).
    public static IPersonalCloudSetupService Create(
        string clientId,
        IReadOnlyList<string> scopes,
        Uri redirectUri,
        string artifactDirectory,
        string checkpointPath,
        ICredentialStore credentials,
        Openza.Tasks.Core.Data.SqliteTaskStore taskStore,
        string deviceName,
        Func<Uri, CancellationToken, Task> openBrowser,
        IPersonalCloudConnectionStore? connectionStoreOverride = null)
    {
        var source = new DirectoryPersonalCloudArtifactSource(artifactDirectory);
        _ = source.Artifact; // Fail early, as before, when the local artifact is invalid.
        return Create(clientId, scopes, redirectUri, source, checkpointPath, credentials, taskStore, deviceName, openBrowser, connectionStoreOverride);
    }

    // Signed releases (or a local artifact) resolved when setup or upgrade runs.
    public static IPersonalCloudSetupService Create(
        string clientId,
        IReadOnlyList<string> scopes,
        Uri redirectUri,
        IPersonalCloudArtifactSource artifactSource,
        string checkpointPath,
        ICredentialStore credentials,
        Openza.Tasks.Core.Data.SqliteTaskStore taskStore,
        string deviceName,
        Func<Uri, CancellationToken, Task> openBrowser,
        IPersonalCloudConnectionStore? connectionStoreOverride = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(openBrowser);
        ArgumentNullException.ThrowIfNull(artifactSource);
        var stateStore = new JsonPersonalCloudProvisioningStateStore(checkpointPath);
        var slot = new PersonalCloudArtifactSlot(artifactSource, stateStore);
        var oauth = new CloudflareOAuthClient(
            new HttpClient(),
            new CloudflareOAuthConfiguration(clientId, redirectUri, scopes));
        var authorization = new LoopbackCloudflareAuthorizationBroker(openBrowser);
        var management = new CloudflareManagementClient(new HttpClient());
        var deployment = slot.DeploymentClient(new HttpClient());
        var dispatch = new PersonalCloudDispatchPolicy(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(checkpointPath))!, "personal-cloud-dispatch"));
        var health = new PersonalCloudHealthClient(PersonalCloudDispatchHttp.Create(dispatch));
        var gateway = new CloudflareProvisioningGateway(
            management,
            deployment,
            slot.MigrationBundle(),
            health,
            deviceName,
            providerBridge: true);
        var identityHttp = PersonalCloudDispatchHttp.Create(dispatch);
        var identity = new PersonalCloudDeviceIdentityClient(identityHttp, credentials);
        var coordinator = new PersonalCloudProvisioningCoordinator(
            oauth,
            authorization,
            gateway,
            stateStore,
            identity);
        var connectionPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(checkpointPath))!, "personal-cloud-connection.json");
        return new PersonalCloudSetupService(
            coordinator,
            string.Empty,
            openBrowser,
            connectionStoreOverride ?? new JsonPersonalCloudConnectionStore(connectionPath),
            identity,
            PersonalCloudDispatchHttp.Create(dispatch),
            taskStore,
            new PersonalCloudManagementCoordinator(
                oauth,
                authorization,
                management,
                deployment,
                gateway,
                health,
                () => slot.Version),
            slot);
    }
}
