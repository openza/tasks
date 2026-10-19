using Openza.Tasks.Core.Credentials;

namespace Openza.Tasks.Core.PersonalCloud;

public static class PersonalCloudSetupFactory
{
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
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(openBrowser);
        var artifact = PersonalCloudArtifactDirectory.Load(artifactDirectory);
        var oauth = new CloudflareOAuthClient(
            new HttpClient(),
            new CloudflareOAuthConfiguration(clientId, redirectUri, scopes));
        var authorization = new LoopbackCloudflareAuthorizationBroker(openBrowser);
        var management = new CloudflareManagementClient(new HttpClient());
        var deployment = new CloudflareArtifactDeploymentClient(new HttpClient(), artifact.Deployment);
        var dispatch = new PersonalCloudDispatchPolicy(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(checkpointPath))!, "personal-cloud-dispatch"));
        var health = new PersonalCloudHealthClient(PersonalCloudDispatchHttp.Create(dispatch));
        var gateway = new CloudflareProvisioningGateway(
            management,
            deployment,
            artifact.Migrations,
            health,
            deviceName,
            providerBridge: true);
        var identityHttp = PersonalCloudDispatchHttp.Create(dispatch);
        var identity = new PersonalCloudDeviceIdentityClient(identityHttp, credentials);
        var coordinator = new PersonalCloudProvisioningCoordinator(
            oauth,
            authorization,
            gateway,
            new JsonPersonalCloudProvisioningStateStore(checkpointPath),
            identity);
        var connectionPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(checkpointPath))!, "personal-cloud-connection.json");
        return new PersonalCloudSetupService(
            coordinator,
            artifact.Deployment.Version,
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
                artifact.Deployment.Version));
    }
}
