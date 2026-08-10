using System.Text.Json;
using UCanAccess.File;

namespace UCanAccess;

internal static class ComplexValueJson
{
    public static string Serialize(object value)
    {
        return value switch
        {
            AccessSingleValue[] single => JsonSerializer.Serialize(new Envelope("single",
                single.Select(v => JsonSerializer.SerializeToElement(v.Value)).ToArray())),
            AccessAttachment[] attachments => JsonSerializer.Serialize(new Envelope("attachment",
                attachments.Select(v => JsonSerializer.SerializeToElement(v)).ToArray())),
            AccessVersion[] versions => JsonSerializer.Serialize(new Envelope("version",
                versions.Select(v => JsonSerializer.SerializeToElement(v)).ToArray())),
            _ => JsonSerializer.Serialize(new Envelope("raw", new[] { JsonSerializer.SerializeToElement(value) })),
        };
    }

    public static object? Deserialize(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string kind = root.TryGetProperty("Kind", out JsonElement kindElement)
            ? kindElement.GetString() ?? ""
            : root.TryGetProperty("kind", out kindElement) ? kindElement.GetString() ?? "" : "";
        JsonElement values = root.TryGetProperty("Values", out JsonElement valuesElement)
            ? valuesElement
            : root.GetProperty("values");

        return kind.ToLowerInvariant() switch
        {
            "single" => values.EnumerateArray().Select(v => new AccessSingleValue(ToObject(v))).ToArray(),
            "attachment" => values.EnumerateArray()
                .Select(v => JsonSerializer.Deserialize<AccessAttachment>(v.GetRawText())!)
                .ToArray(),
            "version" => values.EnumerateArray()
                .Select(v => JsonSerializer.Deserialize<AccessVersion>(v.GetRawText())!)
                .ToArray(),
            _ => values.EnumerateArray().Select(ToObject).ToArray(),
        };
    }

    private static object? ToObject(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out long integer) => integer,
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String => value.GetString(),
            _ => value.GetRawText(),
        };

    private sealed record Envelope(string Kind, JsonElement[] Values);
}
