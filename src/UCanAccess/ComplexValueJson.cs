using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using UCanAccess.File;

namespace UCanAccess;

/// <summary>
/// JSON envelope for Access complex (multi-valued) columns.
///
/// AOT/trim compatible: all serialization goes through the source-generated
/// <see cref="ComplexValueJsonContext"/> (no reflection-based
/// <c>JsonSerializer</c> overloads). Polymorphic payloads (<c>object?</c>)
/// are encoded member-by-member in <see cref="EncodeValue"/> so the trimmer
/// and the AOT compiler can statically see every serialized shape.
/// The wire format is unchanged: {"Kind":"single|attachment|version|raw",
/// "Values":[...]}, and deserialization keeps tolerating lowercase
/// "kind"/"values" keys.
/// </summary>
internal static class ComplexValueJson
{
    /// <summary>Detached JSON null element (JsonDocument.Parse is AOT-safe).</summary>
    private static readonly JsonElement NullElement =
        JsonDocument.Parse("null").RootElement.Clone();

    public static string Serialize(object value)
    {
        return value switch
        {
            AccessSingleValue[] single => JsonSerializer.Serialize(
                new Envelope("single", single.Select(v => EncodeValue(v.Value)).ToArray()),
                ComplexValueJsonContext.Default.Envelope),
            AccessAttachment[] attachments => JsonSerializer.Serialize(
                new Envelope("attachment", attachments.Select(EncodeAttachment).ToArray()),
                ComplexValueJsonContext.Default.Envelope),
            AccessVersion[] versions => JsonSerializer.Serialize(
                new Envelope("version", versions.Select(EncodeVersion).ToArray()),
                ComplexValueJsonContext.Default.Envelope),
            _ => JsonSerializer.Serialize(
                new Envelope("raw", [EncodeValue(value)]),
                ComplexValueJsonContext.Default.Envelope),
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
            "attachment" => values.EnumerateArray().Select(DecodeAttachment).ToArray(),
            "version" => values.EnumerateArray().Select(DecodeVersion).ToArray(),
            _ => values.EnumerateArray().Select(ToObject).ToArray(),
        };
    }

    /// <summary>
    /// Encodes an arbitrary scalar to a detached <see cref="JsonElement"/>
    /// using only source-generated type metadata. Produces byte-identical
    /// JSON to the previous reflection-based implementation for every type
    /// that flows from Access rows (null, bool, integers, decimal, double,
    /// string, DateTime, byte[], Guid, ...) and for <see cref="JsonElement"/>
    /// payloads carried over from deserialized versions.
    /// </summary>
    private static JsonElement EncodeValue(object? value)
    {
        ComplexValueJsonContext ctx = ComplexValueJsonContext.Default;
        return value switch
        {
            null or DBNull => NullElement,
            string s => JsonSerializer.SerializeToElement(s, ctx.String),
            bool b => JsonSerializer.SerializeToElement(b, ctx.Boolean),
            byte n => JsonSerializer.SerializeToElement(n, ctx.Byte),
            sbyte n => JsonSerializer.SerializeToElement(n, ctx.SByte),
            short n => JsonSerializer.SerializeToElement(n, ctx.Int16),
            ushort n => JsonSerializer.SerializeToElement(n, ctx.UInt16),
            int n => JsonSerializer.SerializeToElement(n, ctx.Int32),
            uint n => JsonSerializer.SerializeToElement(n, ctx.UInt32),
            long n => JsonSerializer.SerializeToElement(n, ctx.Int64),
            ulong n => JsonSerializer.SerializeToElement(n, ctx.UInt64),
            float n => JsonSerializer.SerializeToElement(n, ctx.Single),
            double n => JsonSerializer.SerializeToElement(n, ctx.Double),
            decimal n => JsonSerializer.SerializeToElement(n, ctx.Decimal),
            char c => JsonSerializer.SerializeToElement(c, ctx.Char),
            DateTime dt => JsonSerializer.SerializeToElement(dt, ctx.DateTime),
            DateTimeOffset dto => JsonSerializer.SerializeToElement(dto, ctx.DateTimeOffset),
            DateOnly d => JsonSerializer.SerializeToElement(d, ctx.DateOnly),
            TimeOnly t => JsonSerializer.SerializeToElement(t, ctx.TimeOnly),
            Guid g => JsonSerializer.SerializeToElement(g, ctx.Guid),
            byte[] bytes => JsonSerializer.SerializeToElement(bytes, ctx.ByteArray),
            JsonElement element => element.Clone(),
            _ when value is not null && value.GetType().IsEnum => JsonSerializer.SerializeToElement(
                Convert.ToInt64(value, CultureInfo.InvariantCulture), ctx.Int64),
            // Last resort for exotic payloads: previously reflection emitted
            // a property bag; a stable string is safer under trimming.
            _ => JsonSerializer.SerializeToElement(value?.ToString() ?? "", ctx.String),
        };
    }

    private static JsonElement EncodeAttachment(AccessAttachment attachment)
    {
        // Written manually (like AccessVersion below): the source-generated
        // byte[] fast path emits "" instead of null for a null payload
        // (Utf8JsonWriter.WriteBase64String asymmetry), while the
        // reflection serializer historically wrote null. Explicit encoding
        // keeps the wire format byte-identical.
        ComplexValueJsonContext ctx = ComplexValueJsonContext.Default;
        JsonElement fileData = attachment.FileData is byte[] bytes
            ? JsonSerializer.SerializeToElement(Convert.ToBase64String(bytes), ctx.String)
            : NullElement;
        JsonElement fileFlags = attachment.FileFlags is int flags
            ? JsonSerializer.SerializeToElement(flags, ctx.Int32)
            : NullElement;
        JsonElement fileName = attachment.FileName is string name
            ? JsonSerializer.SerializeToElement(name, ctx.String)
            : NullElement;
        JsonElement fileTimeStamp = attachment.FileTimeStamp is DateTime timeStamp
            ? JsonSerializer.SerializeToElement(timeStamp, ctx.DateTime)
            : NullElement;
        JsonElement fileType = attachment.FileType is string type
            ? JsonSerializer.SerializeToElement(type, ctx.String)
            : NullElement;
        JsonElement fileUrl = attachment.FileURL is string url
            ? JsonSerializer.SerializeToElement(url, ctx.String)
            : NullElement;

        // Property order follows the record constructor, as emitted before.
        return WriteObject(
            ("FileData", fileData),
            ("FileFlags", fileFlags),
            ("FileName", fileName),
            ("FileTimeStamp", fileTimeStamp),
            ("FileType", fileType),
            ("FileURL", fileUrl));
    }

    /// <summary>
    /// Encodes <see cref="AccessVersion"/> manually: its <c>Value</c> is
    /// <c>object?</c> (or a <see cref="JsonElement"/> carried over from a
    /// previous deserialization), which source generation cannot model, so
    /// the {"Value":...,"Modified":...} shape is written explicitly with
    /// <see cref="Utf8JsonWriter"/> (AOT-safe). Property names stay
    /// PascalCase, exactly as the reflection serializer emitted them.
    /// </summary>
    private static JsonElement EncodeVersion(AccessVersion version)
    {
        JsonElement valueElement = EncodeValue(version.Value);
        JsonElement modifiedElement = version.Modified is DateTime modified
            ? JsonSerializer.SerializeToElement(modified, ComplexValueJsonContext.Default.DateTime)
            : NullElement;

        return WriteObject(
            ("Value", valueElement),
            ("Modified", modifiedElement));
    }

    /// <summary>
    /// Writes a flat {"Name":element,...} object with
    /// <see cref="Utf8JsonWriter"/> (AOT-safe) and returns it as a detached
    /// <see cref="JsonElement"/>. Property names stay PascalCase, exactly as
    /// the reflection serializer emitted them.
    /// </summary>
    private static JsonElement WriteObject(params (string Name, JsonElement Value)[] properties)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach ((string name, JsonElement value) in properties)
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Decodes <see cref="AccessAttachment"/> without reflection, mirroring
    /// <see cref="EncodeAttachment"/>: member lookup stays case-sensitive,
    /// base64 payloads decode to <c>byte[]</c>, JSON null (or a missing
    /// member) maps to CLR null — exactly as before.
    /// </summary>
    private static AccessAttachment DecodeAttachment(JsonElement element)
    {
        using JsonDocument document = JsonDocument.Parse(element.GetRawText());
        JsonElement root = document.RootElement;
        return new AccessAttachment(
            GetNullableBytes(root, "FileData"),
            GetNullableInt32(root, "FileFlags"),
            GetNullableString(root, "FileName"),
            GetNullableDateTime(root, "FileTimeStamp"),
            GetNullableString(root, "FileType"),
            GetNullableString(root, "FileURL"));
    }

    private static byte[]? GetNullableBytes(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement element) && element.ValueKind != JsonValueKind.Null
            ? Convert.FromBase64String(element.GetString() ?? "")
            : null;

    private static int? GetNullableInt32(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement element) && element.ValueKind != JsonValueKind.Null
            ? element.GetInt32()
            : null;

    private static string? GetNullableString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement element) && element.ValueKind != JsonValueKind.Null
            ? element.GetString()
            : null;

    private static DateTime? GetNullableDateTime(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement element) && element.ValueKind != JsonValueKind.Null
            ? element.GetDateTime()
            : null;

    /// <summary>
    /// Decodes <see cref="AccessVersion"/> without reflection: member lookup
    /// stays case-sensitive ("Value"/"Modified"), matching the previous
    /// <c>Deserialize&lt;AccessVersion&gt;</c> behavior. The value is kept as
    /// a detached <see cref="JsonElement"/>, exactly as before.
    /// </summary>
    private static AccessVersion DecodeVersion(JsonElement element)
    {
        using JsonDocument document = JsonDocument.Parse(element.GetRawText());
        JsonElement root = document.RootElement;
        object? value = root.TryGetProperty("Value", out JsonElement valueElement)
            ? valueElement.Clone()
            : null;
        DateTime? modified = root.TryGetProperty("Modified", out JsonElement modifiedElement)
            && modifiedElement.ValueKind != JsonValueKind.Null
            ? modifiedElement.GetDateTime()
            : null;
        return new AccessVersion(value, modified);
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

    internal sealed record Envelope(string Kind, JsonElement[] Values);
}

/// <summary>
/// Source-generated JSON metadata for complex-value envelopes. Every
/// serialized shape is declared here so trimming and Native AOT neither
/// warn (IL2026/IL3050) nor break at runtime.
/// </summary>
[JsonSerializable(typeof(ComplexValueJson.Envelope))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(sbyte))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(ushort))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(char))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(DateOnly))]
[JsonSerializable(typeof(TimeOnly))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(byte[]))]
internal sealed partial class ComplexValueJsonContext : JsonSerializerContext
{
}
