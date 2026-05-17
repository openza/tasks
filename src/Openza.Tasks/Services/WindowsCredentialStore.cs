using Openza.Tasks.Core.Credentials;
using Windows.Security.Credentials;

namespace Openza.Tasks.Services;

public sealed class WindowsCredentialStore : ICredentialStore
{
    private const string Resource = "Openza.Tasks";
    private readonly PasswordVault _vault = new();

    public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        RemoveIfExists(key);
        _vault.Add(new PasswordCredential(Resource, key, value));
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var credential = _vault.FindAllByResource(Resource)
                .FirstOrDefault(item => string.Equals(item.UserName, key, StringComparison.Ordinal));
            if (credential is null)
            {
                return Task.FromResult<string?>(null);
            }

            credential.RetrievePassword();
            return Task.FromResult<string?>(credential.Password);
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        RemoveIfExists(key);
        return Task.CompletedTask;
    }

    private void RemoveIfExists(string key)
    {
        try
        {
            var credential = _vault.FindAllByResource(Resource)
                .FirstOrDefault(item => string.Equals(item.UserName, key, StringComparison.Ordinal));
            if (credential is not null)
            {
                _vault.Remove(credential);
            }
        }
        catch
        {
        }
    }
}
