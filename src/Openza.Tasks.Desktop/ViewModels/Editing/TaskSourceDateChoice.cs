using System.Text.Json;
using System.Text.Json.Nodes;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Desktop.ViewModels.Editing;

/// <summary>Source date comparison, explicit acknowledgement and lossless metadata editing.</summary>
public static class TaskSourceDateChoice
{
    public static Presentation? Describe(TaskItem? task, DateOnly? localDate, DateOnly? localDeadline, string sourceName, string? acknowledgementKey, bool ignoreAcknowledgement = false)
    {
        if (task is null ||
            !task.HasProviderSource ||
            IsTodoistNonRecurringSourceTask(task) ||
            !string.IsNullOrWhiteSpace(task.SourceRecurrenceRule))
        {
            return null;
        }

        var sourceDate = TaskDateValues.FromDateTimeOffset(task.SourcePlannedMoment);
        var sourceDeadline = TaskDateValues.FromDateTimeOffset(task.SourceDeadlineMoment);
        var dateMismatch = localDate != sourceDate;
        var deadlineMismatch = localDeadline != sourceDeadline;
        if (!dateMismatch && !deadlineMismatch)
        {
            return null;
        }

        var key = CreateKey(task.Id, localDate, sourceDate, localDeadline, sourceDeadline);
        if (!ignoreAcknowledgement && string.Equals(acknowledgementKey, key, StringComparison.Ordinal))
        {
            return null;
        }

        var title = dateMismatch && deadlineMismatch
            ? $"{sourceName} dates changed"
            : dateMismatch
                ? $"{sourceName} date changed"
                : $"{sourceName} deadline changed";
        var message = dateMismatch && deadlineMismatch
            ? $"{sourceName} date is {FormatDateOnly(sourceDate)} and deadline is {FormatDateOnly(sourceDeadline)}. Openza keeps {FormatDateOnly(localDate)} and {FormatDateOnly(localDeadline)} until you choose otherwise."
            : dateMismatch
                ? $"{sourceName} date is {FormatDateOnly(sourceDate)}. Openza date is {FormatDateOnly(localDate)}."
                : $"{sourceName} deadline is {FormatDateOnly(sourceDeadline)}. Openza deadline is {FormatDateOnly(localDeadline)}.";
        var useSourceText = dateMismatch && deadlineMismatch
            ? $"Use {sourceName} dates"
            : dateMismatch
                ? $"Use {sourceName} date"
                : $"Use {sourceName} deadline";
        return new Presentation(title, message, useSourceText);
    }

    public static string CreateKey(string taskId, DateOnly? localDate, DateOnly? sourceDate, DateOnly? localDeadline, DateOnly? sourceDeadline) =>
        $"{taskId}|{string.Join('|', localDate?.ToString("O") ?? "", sourceDate?.ToString("O") ?? "", localDeadline?.ToString("O") ?? "", sourceDeadline?.ToString("O") ?? "")}";

    private static string FormatDateOnly(DateOnly? date) =>
        date?.ToString("MMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture) ?? "no date";

    private static bool IsTodoistNonRecurringSourceTask(TaskItem task) =>
        string.Equals(task.SourceIntegrationId ?? task.IntegrationId, IntegrationIds.Todoist, StringComparison.Ordinal) &&
        string.IsNullOrWhiteSpace(task.SourceRecurrenceRule);

    public static string? ReadAcknowledgement(string? localMetadataJson)
    {
        if (string.IsNullOrWhiteSpace(localMetadataJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(localMetadataJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("openza", out var openza) &&
                openza.ValueKind == JsonValueKind.Object &&
                openza.TryGetProperty("sourceDateMismatchAcknowledgementKey", out var value) &&
                value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? WithAcknowledgement(string? localMetadataJson, string? acknowledgementKey)
    {
        var currentKey = ReadAcknowledgement(localMetadataJson);
        if (string.Equals(currentKey, acknowledgementKey, StringComparison.Ordinal) ||
            (string.IsNullOrWhiteSpace(currentKey) && string.IsNullOrWhiteSpace(acknowledgementKey)))
        {
            return localMetadataJson;
        }

        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(localMetadataJson)
                ? new JsonObject()
                : JsonNode.Parse(localMetadataJson) as JsonObject ?? throw new JsonException("Task metadata must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The task's existing metadata is not a JSON object, so its date choice could not be recorded safely.", exception);
        }

        var openza = root["openza"] as JsonObject;
        if (openza is null)
        {
            if (root["openza"] is not null)
            {
                throw new InvalidOperationException("The task's existing Openza metadata is incompatible, so its date choice could not be recorded safely.");
            }

            openza = new JsonObject();
            root["openza"] = openza;
        }

        if (string.IsNullOrWhiteSpace(acknowledgementKey))
        {
            openza.Remove("sourceDateMismatchAcknowledgementKey");
        }
        else
        {
            openza["sourceDateMismatchAcknowledgementKey"] = acknowledgementKey;
        }

        if (openza.Count == 0)
        {
            root.Remove("openza");
        }

        return root.Count == 0 ? null : root.ToJsonString();
    }

    public sealed record Presentation(string Title, string Message, string UseSourceText);

}
