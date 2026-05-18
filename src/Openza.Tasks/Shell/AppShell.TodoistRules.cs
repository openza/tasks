using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Controls;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Pages;
using Openza.Tasks.ViewModels;
using Windows.Foundation;

namespace Openza.Tasks.Shell;

public sealed partial class AppShell
{
    private const string TodoistRuleRouteId = "route_todoist_label_routing";

    private async Task RefreshTodoistRulesAsync()
    {
        var routes = await _store.GetSyncRoutesAsync().ConfigureAwait(true);
        var route = routes.FirstOrDefault(item => string.Equals(item.Id, TodoistRuleRouteId, StringComparison.Ordinal));
        var rules = TodoistRuleSettings.FromJson(route?.SettingsJson).LabelRoutes;
        var activeSpaces = _spaces
            .OrderBy(space => space.SortOrder)
            .ThenBy(space => space.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(space => new TodoistRoutingChoice(space.Id, space.Name))
            .ToList();
        var todoistProjects = _allProjects
            .Where(project => project.IntegrationId == IntegrationIds.Todoist && !string.IsNullOrWhiteSpace(project.ExternalId) && !project.IsArchived)
            .OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(project => new TodoistRoutingChoice(project.ExternalId!, project.Name))
            .ToList();
        var spaceNames = activeSpaces.ToDictionary(space => space.Id, space => space.Name, StringComparer.Ordinal);
        var projectNames = todoistProjects.ToDictionary(project => project.Id, project => project.Name, StringComparer.Ordinal);

        SettingsPage.SetTodoistRuleOptions(activeSpaces, todoistProjects);
        SettingsPage.SetTodoistRules(rules.Select(rule => new TodoistRoutingRuleViewModel
        {
            Id = rule.Id,
            Label = rule.Label,
            SpaceId = rule.SpaceId,
            SpaceName = spaceNames.GetValueOrDefault(rule.SpaceId, "Unknown Space"),
            MoveToProjectId = rule.PostImport?.MoveToProjectId,
            MoveToProjectName = rule.PostImport?.MoveToProjectId is { } projectId
                ? projectNames.GetValueOrDefault(projectId, "Todoist project")
                : null,
        }));
    }

    private async void OnSaveTodoistRuleRequested(SettingsPage sender, TodoistRoutingRuleDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Label))
        {
            ShowInfo("Rule needs a label", "Enter the Todoist label this rule should match.", InfoBarSeverity.Warning);
            return;
        }

        if (_spaces.All(space => !string.Equals(space.Id, draft.SpaceId, StringComparison.Ordinal)))
        {
            ShowInfo("Rule needs a Space", "Choose where matching Todoist tasks should go.", InfoBarSeverity.Warning);
            return;
        }

        var routes = await _store.GetSyncRoutesAsync().ConfigureAwait(true);
        var existingRoute = routes.FirstOrDefault(route => string.Equals(route.Id, TodoistRuleRouteId, StringComparison.Ordinal));
        var settings = TodoistRuleSettings.FromJson(existingRoute?.SettingsJson);
        var normalizedLabel = NormalizeTodoistRuleLabel(draft.Label);
        var id = string.IsNullOrWhiteSpace(draft.Id) ? $"todoist_rule_{Guid.NewGuid():N}" : draft.Id;
        var nextRule = new TodoistRuleJson(
            id,
            normalizedLabel,
            draft.SpaceId,
            string.IsNullOrWhiteSpace(draft.MoveToProjectId) ? null : new TodoistRulePostImportJson(draft.MoveToProjectId));
        var rules = settings.LabelRoutes
            .Where(rule => !string.Equals(rule.Id, id, StringComparison.Ordinal) &&
                !string.Equals(rule.Label, normalizedLabel, StringComparison.OrdinalIgnoreCase))
            .Append(nextRule)
            .OrderBy(rule => rule.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        await SaveTodoistRuleSettingsAsync(existingRoute, new TodoistRuleSettings(rules)).ConfigureAwait(true);
        await RefreshTodoistRulesAsync().ConfigureAwait(true);
        ShowInfo("Todoist rule saved", $"@{normalizedLabel} will be sent to the selected Space.", InfoBarSeverity.Success);
    }

    private async void OnDeleteTodoistRuleRequested(SettingsPage sender, string id)
    {
        var routes = await _store.GetSyncRoutesAsync().ConfigureAwait(true);
        var existingRoute = routes.FirstOrDefault(route => string.Equals(route.Id, TodoistRuleRouteId, StringComparison.Ordinal));
        var settings = TodoistRuleSettings.FromJson(existingRoute?.SettingsJson);
        var rules = settings.LabelRoutes
            .Where(rule => !string.Equals(rule.Id, id, StringComparison.Ordinal))
            .ToList();

        await SaveTodoistRuleSettingsAsync(existingRoute, new TodoistRuleSettings(rules)).ConfigureAwait(true);
        await RefreshTodoistRulesAsync().ConfigureAwait(true);
        ShowInfo("Todoist rule deleted", "New Todoist tasks will no longer use that rule.", InfoBarSeverity.Informational);
    }

    private async Task SaveTodoistRuleSettingsAsync(SyncRouteInfo? existingRoute, TodoistRuleSettings settings)
    {
        var now = DateTimeOffset.UtcNow;
        await _store.UpsertSyncRouteAsync(new SyncRouteInfo
        {
            Id = TodoistRuleRouteId,
            Name = "Todoist label rules",
            SourceConnectionId = "todoist_default",
            Mode = "one_way",
            Visibility = "optional",
            IsEnabled = settings.LabelRoutes.Count > 0,
            SettingsJson = JsonSerializer.Serialize(settings, TodoistRuleJsonContext.Default.TodoistRuleSettings),
            CreatedAt = existingRoute?.CreatedAt ?? now,
            UpdatedAt = now,
        }).ConfigureAwait(true);
    }

    private static string NormalizeTodoistRuleLabel(string value)
    {
        var label = value.Trim();
        return label.StartsWith('@') ? label[1..].Trim() : label;
    }

    private sealed record TodoistRuleSettings([property: JsonPropertyName("labelRoutes")] IReadOnlyList<TodoistRuleJson> LabelRoutes)
    {
        public static TodoistRuleSettings FromJson(string? settingsJson)
        {
            if (string.IsNullOrWhiteSpace(settingsJson))
            {
                return new TodoistRuleSettings([]);
            }

            try
            {
                return JsonSerializer.Deserialize(settingsJson, TodoistRuleJsonContext.Default.TodoistRuleSettings) ??
                    new TodoistRuleSettings([]);
            }
            catch (JsonException)
            {
                return new TodoistRuleSettings([]);
            }
        }
    }

    private sealed record TodoistRuleJson(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("spaceId")] string SpaceId,
        [property: JsonPropertyName("postImport")] TodoistRulePostImportJson? PostImport)
    {
        [JsonPropertyName("labels")]
        public IReadOnlyList<string> Labels => [Label];
    }

    private sealed record TodoistRulePostImportJson([property: JsonPropertyName("moveToProjectId")] string MoveToProjectId);

    [JsonSerializable(typeof(TodoistRuleSettings))]
    private sealed partial class TodoistRuleJsonContext : JsonSerializerContext;
}
