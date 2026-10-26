namespace Openza.Tasks.Core.PersonalCloud;

// Holds the cloud artifact for the current setup or upgrade. Desktop resolves
// it from a signed release (or a local Dev artifact) just before the operation;
// the deployment client, migration bundle and management coordinator read it
// at that time. PersonalCloudSetupService runs setup and upgrade one at a time.
public sealed class PersonalCloudArtifactSlot(IPersonalCloudArtifactSource source, IPersonalCloudProvisioningStateStore stateStore)
{
    private LoadedPersonalCloudArtifact? _current;

    public LoadedPersonalCloudArtifact Current => _current
        ?? throw new PersonalCloudK0ContractException("invalid_state", "No Personal Cloud release has been prepared for this operation.");

    public string Version => Current.Deployment.Version;

    public async Task PrepareForSetupAsync(CancellationToken cancellationToken = default)
    {
        // An interrupted setup resumes with exactly the artifact it started with.
        var pending = await stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        _current = await source.ResolveAsync(pending?.ArtifactVersion, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task PrepareForUpgradeAsync(PersonalCloudConnectionProfile connection, CancellationToken cancellationToken = default) =>
        _current = await source.ResolveAsync(null, Installed(connection), cancellationToken).ConfigureAwait(false);

    public Task<PersonalCloudReleaseStatus> CheckAsync(PersonalCloudConnectionProfile? connection, CancellationToken cancellationToken = default) =>
        source.CheckAsync(connection is null ? null : Installed(connection), cancellationToken);

    // Best effort and local only: a failure to record only weakens rollback
    // protection; the deployed version is also checked before every update.
    public async Task RecordInstalledAsync(PersonalCloudConnectionProfile connection)
    {
        try { await source.RecordInstalledAsync(Installed(connection), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is PersonalCloudK0ContractException or IOException or UnauthorizedAccessException) { }
    }

    private static PersonalCloudInstalledRelease Installed(PersonalCloudConnectionProfile connection) =>
        new(connection.InstanceId, connection.ArtifactVersion);

    public ICloudflareArtifactDeploymentClient DeploymentClient(HttpClient http) => new SlotDeploymentClient(this, http);
    public IPersonalCloudMigrationBundle MigrationBundle() => new SlotMigrationBundle(this);

    private sealed class SlotDeploymentClient(PersonalCloudArtifactSlot slot, HttpClient http) : ICloudflareArtifactDeploymentClient
    {
        public Task<CloudflareProvisioningApplication> DeployAsync(string accessToken, string accountId, string databaseId,
            string resourceName, string attemptId, string artifactVersion, CancellationToken cancellationToken = default) =>
            new CloudflareArtifactDeploymentClient(http, slot.Current.Deployment)
                .DeployAsync(accessToken, accountId, databaseId, resourceName, attemptId, artifactVersion, cancellationToken);
    }

    private sealed class SlotMigrationBundle(PersonalCloudArtifactSlot slot) : IPersonalCloudMigrationBundle
    {
        public string ArtifactVersion => slot.Current.Migrations.ArtifactVersion;
        public IReadOnlyList<PersonalCloudMigration> Migrations => slot.Current.Migrations.Migrations;
    }
}

public interface IPersonalCloudReleaseStatusService
{
    Task<PersonalCloudReleaseStatus> CheckForCloudUpdateAsync(CancellationToken cancellationToken = default);
}
