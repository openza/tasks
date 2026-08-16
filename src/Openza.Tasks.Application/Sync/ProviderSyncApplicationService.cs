using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Sync;

namespace Openza.Tasks.Application.Sync;

public static class ProviderCredentialKeys
{
    public const string TodoistToken = "todoist-token";
}

public sealed record ProviderSyncPreflight(
    string Provider,
    string Direction,
    string Scope,
    bool Supported,
    bool Configured,
    bool Active,
    bool CredentialAvailable,
    DateTimeOffset? LastFullSyncAt,
    PendingProviderWriteSummary Pending)
{
    public bool Ready => Supported && Configured && Active && CredentialAvailable;
}

public sealed record ProviderPushResult(
    string Provider,
    string Direction,
    string Scope,
    bool Success,
    PendingProviderWriteSummary Planned,
    PendingProviderWriteSummary Applied,
    PendingProviderWriteSummary Remaining,
    string? Error = null);

public sealed class ProviderSyncApplicationService(
    ITaskStore store,
    ICredentialStore credentials,
    Func<string, string, ISyncProvider> providerFactory)
{
    public const string TodoistProvider = IntegrationIds.Todoist;
    public const string PushDirection = "push";
    public const string PendingScope = "pending";
    private const string TodoistConnectionId = "todoist_default";

    public async Task<ProviderSyncPreflight> GetPreflightAsync(
        string provider,
        string direction,
        string scope,
        CancellationToken cancellationToken = default)
    {
        ValidateContract(provider, direction, scope);
        var integration = (await store.GetIntegrationsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => string.Equals(item.Id, provider, StringComparison.Ordinal));
        var credential = await TryGetCredentialAsync(cancellationToken).ConfigureAwait(false);
        var pending = await store.GetPendingProviderWriteSummaryAsync(provider, cancellationToken).ConfigureAwait(false);
        return new ProviderSyncPreflight(
            provider,
            direction,
            scope,
            Supported: true,
            Configured: integration?.IsConfigured == true,
            Active: integration?.IsActive == true,
            CredentialAvailable: !string.IsNullOrWhiteSpace(credential),
            integration?.LastSyncAt,
            pending);
    }

    public async Task<ProviderPushResult> PushPendingAsync(
        string provider,
        string direction,
        string scope,
        CancellationToken cancellationToken = default)
    {
        var preflight = await GetPreflightAsync(provider, direction, scope, cancellationToken).ConfigureAwait(false);
        if (preflight.Pending.Total == 0)
        {
            return new ProviderPushResult(
                provider,
                direction,
                scope,
                Success: true,
                preflight.Pending,
                new PendingProviderWriteSummary(0, 0, 0),
                preflight.Pending);
        }
        if (!preflight.Ready)
        {
            throw new InvalidOperationException(
                "Todoist is not ready for CLI sync. Connect and enable it in Openza Tasks Settings first.");
        }

        var credential = await credentials.GetAsync(ProviderCredentialKeys.TodoistToken, cancellationToken).ConfigureAwait(false);
        var engine = new TaskSyncEngine(store);
        try
        {
            var syncProvider = providerFactory(credential!, TodoistConnectionId);
            await engine.SyncPendingTaskDateUpdatesAsync(syncProvider, cancellationToken).ConfigureAwait(false);
            await engine.SyncPendingCompletionsAsync(syncProvider, cancellationToken).ConfigureAwait(false);
            var remaining = await store.GetPendingProviderWriteSummaryAsync(provider, cancellationToken).ConfigureAwait(false);
            return new ProviderPushResult(
                provider,
                direction,
                scope,
                Success: true,
                preflight.Pending,
                Difference(preflight.Pending, remaining),
                remaining);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var remaining = await store.GetPendingProviderWriteSummaryAsync(provider, cancellationToken).ConfigureAwait(false);
            return new ProviderPushResult(
                provider,
                direction,
                scope,
                Success: false,
                preflight.Pending,
                Difference(preflight.Pending, remaining),
                remaining,
                exception.Message);
        }
    }

    public static void ValidateContract(string provider, string direction, string scope)
    {
        if (!string.Equals(provider, TodoistProvider, StringComparison.Ordinal))
        {
            throw new ArgumentException("--provider must be todoist. Other providers are not supported by CLI sync yet.");
        }
        if (!string.Equals(direction, PushDirection, StringComparison.Ordinal))
        {
            throw new ArgumentException("--direction must be push. Pull and bidirectional sync remain GUI-only.");
        }
        if (!string.Equals(scope, PendingScope, StringComparison.Ordinal))
        {
            throw new ArgumentException("--scope must be pending. CLI sync sends only queued task changes.");
        }
    }

    private async Task<string?> TryGetCredentialAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await credentials.GetAsync(ProviderCredentialKeys.TodoistToken, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static PendingProviderWriteSummary Difference(
        PendingProviderWriteSummary before,
        PendingProviderWriteSummary after) =>
        new(
            Math.Max(0, before.Completions - after.Completions),
            Math.Max(0, before.Reopens - after.Reopens),
            Math.Max(0, before.DateUpdates - after.DateUpdates));
}
