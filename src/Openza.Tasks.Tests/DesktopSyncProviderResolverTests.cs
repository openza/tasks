using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Sync;
using Openza.Tasks.Desktop.Services;

namespace Openza.Tasks.Tests;

public sealed class DesktopSyncProviderResolverTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-resolver-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Missing_credentials_do_not_construct_adapters_and_Microsoft_keeps_feature_specific_resolution()
    {
        var auth = new MicrosoftAuth();
        var resolver = new DesktopSyncProviderResolver(new InMemoryCredentialStore(),
            new(Path.Combine(_directory, "settings.json")), auth,
            _ => throw new InvalidOperationException("Unexpected Todoist factory"),
            _ => throw new InvalidOperationException("Unexpected Microsoft factory"));
        Assert.Null(await resolver.ResolveAsync(IntegrationIds.Todoist));
        Assert.Null(await resolver.ResolveAsync(IntegrationIds.MicrosoftToDo));
        Assert.Equal("todo", auth.Feature);
        Assert.Null(auth.Account);
        await Assert.ThrowsAsync<ArgumentException>(() => resolver.ResolveAsync("unsupported"));
    }

    [Fact]
    public async Task Account_identifiers_and_Todoist_fingerprint_remain_local_only_adapter_bindings()
    {
        var credentials = new InMemoryCredentialStore();
        await credentials.SaveAsync("todoist-token", "synthetic-token");
        var preferences = new DesktopPreferencesStore(Path.Combine(_directory, "settings.json"));
        var account = new MicrosoftAccount("synthetic-home-account", "synthetic@example.invalid");
        await preferences.SaveAsync(new() { MicrosoftToDoAccount = account });
        var auth = new MicrosoftAuth { Token = "synthetic-microsoft-token" };
        var todoist = new Provider(IntegrationIds.Todoist);
        var microsoft = new Provider(IntegrationIds.MicrosoftToDo);
        var resolver = new DesktopSyncProviderResolver(credentials, preferences, auth,
            token => { Assert.Equal("synthetic-token", token); return todoist; },
            token => { Assert.Equal("synthetic-microsoft-token", token); return microsoft; });
        var resolvedTodoist = (await resolver.ResolveAsync(IntegrationIds.Todoist))!;
        var resolvedMicrosoft = (await resolver.ResolveAsync(IntegrationIds.MicrosoftToDo))!;
        Assert.Same(todoist, resolvedTodoist.Provider);
        Assert.Equal("synthetic-home-account", resolvedMicrosoft.AccountBinding);
        Assert.Same(microsoft, resolvedMicrosoft.Provider);
        Assert.Equal(account, auth.Account);
        Assert.Equal("f06e194de9d2ad49576b401e6890ab835f65e522c25aaabb8a48e95a0186cfea", resolvedTodoist.AccountBinding);
        Assert.Equal(resolvedTodoist.AccountBinding, (await resolver.ResolveAsync(IntegrationIds.Todoist))!.AccountBinding);
        await credentials.SaveAsync("todoist-token", "different-synthetic-token");
        var changedResolver = new DesktopSyncProviderResolver(credentials, preferences, auth, _ => todoist, _ => microsoft);
        Assert.NotEqual(resolvedTodoist.AccountBinding, (await changedResolver.ResolveAsync(IntegrationIds.Todoist))!.AccountBinding);
    }

    [Fact]
    public async Task Microsoft_token_without_an_account_is_not_a_configuration_and_auth_errors_propagate_to_the_sync_boundary()
    {
        var auth = new MicrosoftAuth { Token = "synthetic-token" };
        var resolver = new DesktopSyncProviderResolver(new InMemoryCredentialStore(),
            new(Path.Combine(_directory, "settings.json")), auth,
            _ => throw new InvalidOperationException("Unexpected provider"),
            _ => throw new InvalidOperationException("Unexpected provider"));
        Assert.Null(await resolver.ResolveAsync(IntegrationIds.MicrosoftToDo));
        auth.Fail = true;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(IntegrationIds.MicrosoftToDo));
        Assert.Equal("Synthetic auth unavailable", exception.Message);
    }

    private sealed class MicrosoftAuth : IDesktopMicrosoftAuthService
    {
        public string? Token { get; init; }
        public bool Fail { get; set; }
        public string? Feature { get; private set; }
        public MicrosoftAccount? Account { get; private set; }
        public Task<string?> GetTokenAsync(string feature, MicrosoftAccount? account, CancellationToken cancellationToken = default)
        {
            Feature = feature;
            Account = account;
            if (Fail) throw new InvalidOperationException("Synthetic auth unavailable");
            return Task.FromResult(Token);
        }
        public Task<MicrosoftAccess> ConnectAsync(string feature, Func<SignInCode, Task> showCode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DisconnectAsync(string feature) => throw new NotSupportedException();
    }

    private sealed class Provider(string integrationId) : ISyncProvider
    {
        public string IntegrationId => integrationId;
        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Resolver must not fetch");
        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Resolver must not write");
    }

    public void Dispose() => TestDirectory.Delete(_directory);
}
