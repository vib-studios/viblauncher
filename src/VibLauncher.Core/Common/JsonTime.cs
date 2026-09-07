using System.Globalization;
using System.Text.Json;

namespace VibLauncher.Core.Common;

/// <summary>
/// Reads timestamps out of JSON the launcher did not write.
/// </summary>
/// <remarks>
/// System.Text.Json accepts only the extended ISO 8601 form, where the UTC
/// offset carries a colon. Fabric and Quilt serve <c>+0000</c> instead, and
/// <see cref="JsonElement.GetDateTimeOffset"/> answers that with a bare
/// <see cref="FormatException"/> whose message names neither the field nor the
/// value. One malformed timestamp should not cost a whole profile, so the
/// strict read is tried first and a lenient parse picks up the rest.
/// </remarks>
public static class JsonTime
{
    /// <summary>Reads a timestamp property, falling back when it is absent or unreadable.</summary>
    public static DateTimeOffset Read(JsonElement element, string property, DateTimeOffset fallback = default)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }

        if (value.TryGetDateTimeOffset(out var strict))
        {
            return strict;
        }

        return DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var lenient)
            ? lenient
            : fallback;
    }
}
