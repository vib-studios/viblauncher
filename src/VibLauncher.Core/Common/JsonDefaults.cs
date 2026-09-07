using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibLauncher.Core.Common;

/// <summary>
/// One set of JSON options for everything the launcher reads and writes.
/// </summary>
/// <remarks>
/// Instance and server files are meant to be readable and hand-editable, so
/// output is indented and camel-cased. Reads are deliberately lenient about
/// casing and trailing commas: these files survive across launcher versions and
/// people do edit them.
/// </remarks>
public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
