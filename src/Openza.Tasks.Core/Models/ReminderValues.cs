namespace Openza.Tasks.Core.Models;

public static class ReminderValues
{
    public static DateTimeOffset Maximum { get; } = DateTimeOffset.FromUnixTimeSeconds(100_000_000_000);

    public static bool IsInRange(DateTimeOffset? value) => value is null || value <= Maximum;

    public static DateTimeOffset? Validate(DateTimeOffset? value)
    {
        if (!IsInRange(value))
            throw new ArgumentOutOfRangeException(nameof(value), "Reminder must be on or before 5138-11-16 09:46:40 UTC.");
        return value?.ToUniversalTime();
    }
}
