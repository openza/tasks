using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Data;

public sealed class ProviderLinkedTaskDeleteException(string taskId, string provider) : InvalidOperationException(
    $"This task is still linked to {IntegrationIds.DisplayName(provider)}. Delete it in {IntegrationIds.DisplayName(provider)} first, then sync Openza before removing it here.")
{
    public string TaskId { get; } = taskId;

    public string Provider { get; } = provider;
}
