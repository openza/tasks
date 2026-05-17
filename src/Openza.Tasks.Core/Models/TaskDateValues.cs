using System.Globalization;

namespace Openza.Tasks.Core.Models;

public static class TaskDateValues
{
    private const string IsoDateFormat = "yyyy-MM-dd";

    public static DateOnly? FromDateTimeOffset(DateTimeOffset? value) =>
        value is null ? null : DateOnly.FromDateTime(value.Value.LocalDateTime);

    public static DateTimeOffset? ToLocalDateTime(DateOnly? value) =>
        value is null
            ? null
            : new DateTimeOffset(value.Value.ToDateTime(TimeOnly.MinValue));

    public static DateTimeOffset? PreferredMoment(DateOnly? date, DateTimeOffset? exactTime) =>
        exactTime ?? ToLocalDateTime(date);

    public static DateOnly? PreferredDate(DateOnly? date, DateTimeOffset? exactTime) =>
        date ?? FromDateTimeOffset(exactTime);

    public static DateTimeOffset? CanonicalMoment(DateOnly? date, DateTimeOffset? exactTime)
    {
        if (date is null)
        {
            return exactTime;
        }

        if (exactTime is not null && FromDateTimeOffset(exactTime) == date)
        {
            return exactTime;
        }

        return ToLocalDateTime(date);
    }

    public static string? ToStorageValue(DateOnly? value) =>
        value?.ToString(IsoDateFormat, CultureInfo.InvariantCulture);

    public static DateOnly? FromStorageValue(string? value) =>
        DateOnly.TryParseExact(value, IsoDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
}
