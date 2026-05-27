using System.Text.Json;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Sync;

public sealed class ProviderSourceRoutingPolicy
{
    public static ProviderSourceRoutingPolicy Empty { get; } = new([]);

    private readonly IReadOnlyList<ProviderSourceLabelRoute> _labelRoutes;
    private readonly ProviderSourceUnlabeledRoute? _unlabeledRoute;

    private ProviderSourceRoutingPolicy(
        IReadOnlyList<ProviderSourceLabelRoute> labelRoutes,
        ProviderSourceUnlabeledRoute? unlabeledRoute = null)
    {
        _labelRoutes = labelRoutes;
        _unlabeledRoute = unlabeledRoute;
    }

    public static ProviderSourceRoutingPolicy FromRoutes(
        IEnumerable<SyncRouteInfo> routes,
        string providerConnectionId,
        string integrationId)
    {
        var matchingRouteSettings = routes
            .Where(route => route.IsEnabled && SourceConnectionMatches(route.SourceConnectionId, providerConnectionId, integrationId))
            .ToList();
        var labelRoutes = matchingRouteSettings
            .SelectMany(route => ParseLabelRoutes(route.SettingsJson))
            .ToList();
        var unlabeledRoute = matchingRouteSettings
            .Select(route => ParseUnlabeledRoute(route.SettingsJson))
            .FirstOrDefault(route => route is not null);

        return labelRoutes.Count == 0 && unlabeledRoute is null
            ? Empty
            : new ProviderSourceRoutingPolicy(labelRoutes, unlabeledRoute);
    }

    public ProviderSourceRouteMatch Match(TaskItem task)
    {
        return MatchLabels(task.Labels.Select(label => label.Name));
    }

    public ProviderSourceRouteMatch Match(ProviderSourceItem source)
    {
        return MatchLabels(ReadSourceLabels(source.SnapshotJson));
    }

    private ProviderSourceRouteMatch MatchLabels(IEnumerable<string> labels)
    {
        var labelSet = labels
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (labelSet.Count == 0)
        {
            return _unlabeledRoute is null
                ? ProviderSourceRouteMatch.Empty
                : new ProviderSourceRouteMatch(_unlabeledRoute.SpaceId, _unlabeledRoute.PostImportAction);
        }

        var route = _labelRoutes.FirstOrDefault(route => route.IsMatch(labelSet));
        return route is null
            ? ProviderSourceRouteMatch.Empty
            : new ProviderSourceRouteMatch(route.SpaceId, route.PostImportAction);
    }

    private static bool SourceConnectionMatches(string? sourceConnectionId, string providerConnectionId, string integrationId)
    {
        return string.IsNullOrWhiteSpace(sourceConnectionId) ||
            string.Equals(sourceConnectionId, providerConnectionId, StringComparison.Ordinal) ||
            string.Equals(sourceConnectionId, integrationId, StringComparison.Ordinal) ||
            string.Equals(sourceConnectionId, DefaultProviderConnectionId(integrationId), StringComparison.Ordinal);
    }

    private static string DefaultProviderConnectionId(string integrationId) => integrationId switch
    {
        IntegrationIds.Local => "local_default",
        IntegrationIds.Todoist => "todoist_default",
        IntegrationIds.MicrosoftToDo => "mstodo_default",
        _ => integrationId,
    };

    private static IEnumerable<ProviderSourceLabelRoute> ParseLabelRoutes(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            var root = document.RootElement;
            var routes = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : root.TryGetProperty("labelRoutes", out var labelRoutes) && labelRoutes.ValueKind == JsonValueKind.Array
                    ? labelRoutes.EnumerateArray()
                    : [];

            return routes
                .Select(ParseLabelRoute)
                .Where(route => route is not null)
                .Select(route => route!)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ProviderSourceUnlabeledRoute? ParseUnlabeledRoute(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var route = ReadObject(root, "unlabeledRoute") ?? ReadObject(root, "noLabelRoute");
            return route is null ? null : ParseUnlabeledRoute(route.Value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ProviderSourceLabelRoute? ParseLabelRoute(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var labels = ReadLabels(element).ToList();
        if (labels.Count == 0)
        {
            return null;
        }

        var matchAll = string.Equals(ReadString(element, "match"), "all", StringComparison.OrdinalIgnoreCase);
        var spaceId = ReadString(element, "spaceId") ?? ReadString(element, "suggestedSpaceId");
        var moveToProjectId =
            ReadString(element, "moveToProjectId") ??
            ReadString(element, "moveTaskToProjectId") ??
            ReadString(element, "todoistProjectId") ??
            ReadPostImportString(element, "moveToProjectId");

        var action = string.IsNullOrWhiteSpace(moveToProjectId)
            ? null
            : new ProviderPostImportAction(moveToProjectId);

        return new ProviderSourceLabelRoute(labels, matchAll, spaceId, action);
    }

    private static ProviderSourceUnlabeledRoute? ParseUnlabeledRoute(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var spaceId = ReadString(element, "spaceId") ?? ReadString(element, "suggestedSpaceId");
        var moveToProjectId =
            ReadString(element, "moveToProjectId") ??
            ReadString(element, "moveTaskToProjectId") ??
            ReadString(element, "todoistProjectId") ??
            ReadPostImportString(element, "moveToProjectId");

        var action = string.IsNullOrWhiteSpace(moveToProjectId)
            ? null
            : new ProviderPostImportAction(moveToProjectId);

        return string.IsNullOrWhiteSpace(spaceId) && action is null
            ? null
            : new ProviderSourceUnlabeledRoute(spaceId, action);
    }

    private static IEnumerable<string> ReadLabels(JsonElement element)
    {
        if (ReadString(element, "label") is { } label)
        {
            yield return label;
        }

        if (!element.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in labels.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
            {
                yield return value;
            }
        }
    }

    private static IReadOnlyList<string> ReadSourceLabels(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(snapshotJson);
            if (!document.RootElement.TryGetProperty("sourceTask", out var sourceTask) ||
                !sourceTask.TryGetProperty("labels", out var labels) ||
                labels.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return labels.EnumerateArray()
                .Where(label => label.ValueKind == JsonValueKind.String)
                .Select(label => label.GetString())
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(label => label!)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ReadString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static JsonElement? ReadObject(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;
    }

    private static string? ReadPostImportString(JsonElement element, string property)
    {
        return element.TryGetProperty("postImport", out var postImport) && postImport.ValueKind == JsonValueKind.Object
            ? ReadString(postImport, property)
            : null;
    }
}

public sealed record ProviderSourceRouteMatch(string? SpaceId, ProviderPostImportAction? PostImportAction)
{
    public static ProviderSourceRouteMatch Empty { get; } = new(null, null);
}

public sealed record ProviderPostImportAction(string MoveToProjectId);

internal sealed record ProviderSourceLabelRoute(
    IReadOnlyList<string> Labels,
    bool MatchAll,
    string? SpaceId,
    ProviderPostImportAction? PostImportAction)
{
    public bool IsMatch(IReadOnlySet<string> labels)
    {
        return MatchAll
            ? Labels.All(labels.Contains)
            : Labels.Any(labels.Contains);
    }
}

internal sealed record ProviderSourceUnlabeledRoute(
    string? SpaceId,
    ProviderPostImportAction? PostImportAction);
