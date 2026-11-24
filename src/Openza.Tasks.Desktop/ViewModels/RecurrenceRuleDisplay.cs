using System.Globalization;
using System.Text.Json;

namespace Openza.Tasks.Desktop.ViewModels;

internal static class RecurrenceRuleDisplay
{
    public static string Format(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) return "Not recurring";
        if (!rule.StartsWith('{')) return rule;
        try
        {
            using var document = JsonDocument.Parse(rule);
            var pattern = Property(document.RootElement, "pattern");
            var days = Property(pattern, "daysOfWeek");
            var weekdays = days.ValueKind == JsonValueKind.Array
                ? string.Join(", ", days.EnumerateArray().Where(day => day.ValueKind == JsonValueKind.String)
                    .Select(day => Capitalize(day.GetString())))
                : string.Empty;
            var interval = Number(pattern, "interval") is > 0 and var count ? count : 1;
            var month = Number(pattern, "month") is >= 1 and <= 12 and var number
                ? CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(number) : null;
            var dayOfMonth = Number(pattern, "dayOfMonth");
            var index = Text(pattern, "index");
            var relativeDay = weekdays.Contains(',') ? $"matching weekday ({weekdays})" : weekdays;
            var summary = Text(pattern, "type") switch
            {
                "daily" => Every(interval, "day"),
                "weekly" => Every(interval, "week") + (weekdays.Length == 0 ? "" : $" on {weekdays}"),
                "absoluteMonthly" when dayOfMonth > 0 => $"{Every(interval, "month")} on day {dayOfMonth}",
                "relativeMonthly" when index is not null && weekdays.Length > 0 => $"{Every(interval, "month")} on the {index} {relativeDay}",
                "absoluteYearly" when month is not null && dayOfMonth > 0 => $"{Every(interval, "year")} on {month} {dayOfMonth}",
                "relativeYearly" when month is not null && index is not null && weekdays.Length > 0 => $"{Every(interval, "year")} on the {index} {relativeDay} in {month}",
                _ => null,
            };
            if (summary is null) return rule;

            var range = Property(document.RootElement, "range");
            var details = new List<string>();
            if (Text(pattern, "type") == "weekly" && Text(pattern, "firstDayOfWeek") is { } weekStart)
                details.Add($"Week starts {Capitalize(weekStart)}");
            if (Text(range, "startDate") is { } startDate) details.Add($"Starts {startDate}");
            if (Text(range, "type") == "endDate" && Text(range, "endDate") is { } endDate)
                details.Add($"Ends {endDate}");
            else if (Text(range, "type") == "numbered" && Number(range, "numberOfOccurrences") is > 0 and var occurrences)
                details.Add(occurrences == 1 ? "1 occurrence" : $"{occurrences} occurrences");
            if (Text(range, "recurrenceTimeZone") is { Length: > 0 } zone) details.Add(zone);
            return details.Count == 0 ? summary : summary + "\n" + string.Join(" · ", details);
        }
        catch (JsonException)
        {
            return rule;
        }
    }

    private static string Every(int count, string unit) => count == 1 ? $"Every {unit}" : $"Every {count} {unit}s";
    private static string Capitalize(string? value) => string.IsNullOrEmpty(value) ? "" : char.ToUpperInvariant(value[0]) + value[1..];
    private static JsonElement Property(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) ? property : default;
    private static string? Text(JsonElement value, string key) =>
        Property(value, key) is { ValueKind: JsonValueKind.String } property ? property.GetString() : null;
    private static int Number(JsonElement value, string key) =>
        Property(value, key) is { ValueKind: JsonValueKind.Number } property && property.TryGetInt32(out var number) ? number : 0;
}
