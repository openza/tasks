namespace Openza.Tasks.Core.PersonalCloud;

public sealed record PersonalCloudUpgradeResult(string ArtifactVersion);

public sealed class PersonalCloudManagementCoordinator(
    CloudflareOAuthClient oauth,
    ICloudflareAuthorizationBroker authorizationBroker,
    CloudflareManagementClient management,
    ICloudflareArtifactDeploymentClient deployment,
    CloudflareProvisioningGateway provisioning,
    PersonalCloudHealthClient health,
    string artifactVersion)
{
    public Task<CloudflareD1Export> ExportAsync(
        PersonalCloudConnectionProfile connection,
        CancellationToken cancellationToken = default) =>
        WithResourcesAsync(connection,
            (access, accountId, _, database, token) =>
                management.ExportDatabaseAsync(access, accountId, database.Id, token),
            cancellationToken);

    public Task<PersonalCloudUpgradeResult> UpgradeAsync(
        PersonalCloudConnectionProfile connection,
        CancellationToken cancellationToken = default) =>
        WithResourcesAsync(connection, async (access, accountId, workerName, database, token) =>
        {
            await management.VerifyApplicationAsync(access, accountId, workerName, database.Id, connection, token).ConfigureAwait(false);
            await provisioning.VerifyMigrationHistoryAsync(access, accountId, database.Id, artifactVersion, token).ConfigureAwait(false);
            var deployed = await deployment.DeployAsync(
                access,
                accountId,
                database.Id,
                workerName,
                connection.InstanceId,
                artifactVersion,
                token).ConfigureAwait(false);
            if (NormalizeOrigin(deployed.ServiceOrigin) != NormalizeOrigin(connection.ServiceOrigin))
                throw new PersonalCloudK0ContractException("resource_mismatch", "Cloudflare returned a different Personal Cloud address during update.");
            await provisioning.ApplyMigrationsAsync(access, accountId, database.Id, artifactVersion, token).ConfigureAwait(false);
            await health.VerifyAsync(connection.ServiceOrigin, artifactVersion, token).ConfigureAwait(false);
            return new PersonalCloudUpgradeResult(artifactVersion);
        }, cancellationToken);

    public Task RemoveAsync(
        PersonalCloudConnectionProfile connection,
        CancellationToken cancellationToken = default) =>
        WithAuthorizationAsync(async (access, token) =>
        {
            var accountId = await ResolveAccountAsync(access, token).ConfigureAwait(false);
            var workerName = WorkerName(connection.ServiceOrigin);
            var databases = await management.ListDatabasesAsync(access, accountId, token).ConfigureAwait(false);
            var matches = databases.Where(item => string.Equals(item.Name, workerName, StringComparison.Ordinal)).ToArray();
            if (matches.Length > 1)
                throw new PersonalCloudK0ContractException("resource_ambiguous", "Cloudflare contains multiple Personal Cloud databases with the expected name.");
            await management.DeleteWorkerAsync(access, accountId, workerName, token).ConfigureAwait(false);
            if (matches.Length == 1)
                await management.DeleteDatabaseAsync(access, accountId, matches[0].Id, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private Task<T> WithResourcesAsync<T>(
        PersonalCloudConnectionProfile connection,
        Func<string, string, string, CloudflareD1Database, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken) =>
        WithAuthorizationAsync(async (access, token) =>
        {
            var accountId = await ResolveAccountAsync(access, token).ConfigureAwait(false);
            var workerName = WorkerName(connection.ServiceOrigin);
            var databases = await management.ListDatabasesAsync(access, accountId, token).ConfigureAwait(false);
            var matches = databases.Where(item => string.Equals(item.Name, workerName, StringComparison.Ordinal)).ToArray();
            var database = matches.Length switch
            {
                1 => matches[0],
                0 => throw new PersonalCloudK0ContractException("resource_not_found", "The Personal Cloud database was not found in the authorized Cloudflare account."),
                _ => throw new PersonalCloudK0ContractException("resource_ambiguous", "Cloudflare contains multiple Personal Cloud databases with the expected name."),
            };
            return await action(access, accountId, workerName, database, token).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<T> WithAuthorizationAsync<T>(
        Func<string, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var access = await oauth.AuthorizeAsync(authorizationBroker, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await action(access.AccessToken, cancellationToken).ConfigureAwait(false);
            await oauth.RevokeAsync(access.AccessToken, CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch
        {
            try { await oauth.RevokeAsync(access.AccessToken, CancellationToken.None).ConfigureAwait(false); }
            catch { /* Preserve the resource-operation failure. */ }
            throw;
        }
    }

    private async Task<string> ResolveAccountAsync(string accessToken, CancellationToken cancellationToken)
    {
        var accounts = await management.ListAccountsAsync(accessToken, cancellationToken).ConfigureAwait(false);
        return accounts.Count switch
        {
            1 => accounts[0].Id,
            0 => throw new PersonalCloudK0ContractException("account_unavailable", "Cloudflare did not authorize the Personal Cloud account."),
            _ => throw new PersonalCloudK0ContractException("account_selection_required", "Authorize only the Cloudflare account that contains this Personal Cloud."),
        };
    }

    internal static string WorkerName(Uri serviceOrigin)
    {
        var origin = NormalizeOrigin(serviceOrigin);
        if (!origin.IsDefaultPort || origin.Host.Split('.').Length != 4 || !origin.Host.EndsWith(".workers.dev", StringComparison.Ordinal))
            throw new PersonalCloudK0ContractException("resource_mismatch", "Personal Cloud management requires the original workers.dev address.");
        var firstDot = origin.Host.IndexOf('.');
        var name = firstDot < 0 ? origin.Host : origin.Host[..firstDot];
        if (!name.StartsWith("openza-personal-", StringComparison.Ordinal) || name.Length > 64 ||
            name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new PersonalCloudK0ContractException("resource_mismatch", "The connected address is not an Openza Personal Cloud workers.dev address.");
        return name;
    }

    private static Uri NormalizeOrigin(Uri value)
    {
        if (!value.IsAbsoluteUri || value.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(value.UserInfo) || value.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(value.Query) || !string.IsNullOrEmpty(value.Fragment))
            throw new PersonalCloudK0ContractException("connection_invalid", "The Personal Cloud address is invalid.");
        return new Uri(value.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
    }
}
