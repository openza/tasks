using System.Security.Cryptography;
using System.Text;
using Openza.Tasks.Application.Sync;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Desktop.Services;

/// <summary>Resolves host credentials/accounts into the two existing connected provider adapters.</summary>
public sealed class DesktopSyncProviderResolver(
    ICredentialStore credentials,
    DesktopPreferencesStore preferences,
    IDesktopMicrosoftAuthService microsoftAuth,
    Func<string, ISyncProvider> todoist,
    Func<string, ISyncProvider> microsoft) : IConnectedSyncProviderResolver
{
    public async Task<PersonalCloudConfiguredProvider?> ResolveAsync(string integrationId, CancellationToken cancellationToken = default)
    {
        if (integrationId == IntegrationIds.Todoist)
        {
            var token = await credentials.GetAsync(ProviderCredentialKeys.TodoistToken, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(token) ? null : new(todoist(token),
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant());
        }
        if (integrationId == IntegrationIds.MicrosoftToDo)
        {
            var account = preferences.Load().MicrosoftToDoAccount;
            var token = await microsoftAuth.GetTokenAsync("todo", account, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(token) || account is null ? null : new(microsoft(token), account.HomeAccountId);
        }
        throw new ArgumentException("Unsupported connected sync provider.", nameof(integrationId));
    }
}
