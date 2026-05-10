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
            var credential = _vault.Retrieve(Resource, key);
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
            _vault.Remove(_vault.Retrieve(Resource, key));
        }
        catch
        {
        }
    }
}
