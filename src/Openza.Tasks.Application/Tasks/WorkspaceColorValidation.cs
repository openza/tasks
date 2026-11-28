using System.Drawing;
using System.Globalization;

namespace Openza.Tasks.Application.Tasks;

public static class WorkspaceColorValidation
{
    public static string Normalize(string? value, string fallback, string parameterName)
    {
        var color = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (!IsValid(color))
            throw new ArgumentException("Color must be a valid hexadecimal or named color.", parameterName);
        return color;
    }

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var color = value.Trim();
        if (color.StartsWith('#'))
        {
            var hex = color.AsSpan(1);
            return hex.Length is 3 or 4 or 6 or 8 &&
                   uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _);
        }

        if (string.Equals(color, "rebeccapurple", StringComparison.OrdinalIgnoreCase))
            return false;

        var namedColor = Color.FromName(color);
        return namedColor.IsKnownColor && !namedColor.IsSystemColor;
    }
}
