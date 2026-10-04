using System.Text.Json;
using UCanAccess.File;
using Xunit;

namespace UCanAccess.Tests;

/// <summary>
/// Round-trip contract for <see cref="ComplexValueJson"/> (internal).
/// These tests pin the JSON wire format and CLR semantics BEFORE the
/// AOT-friendly rewrite (System.Text.Json source generation), so the
/// refactor can prove byte-level and behavior compatibility.
/// </summary>
public sealed class ComplexValueJsonTests
{
    private static readonly DateTime SampleDate =
        new(2024, 2, 29, 12, 34, 56, 789, DateTimeKind.Unspecified);

    [Fact]
    public void Single_strings_round_trip_including_unicode()
    {
        var input = new[]
        {
            new AccessSingleValue("alpha"),
            new AccessSingleValue("zażółć gęślą jaźń"),
            new AccessSingleValue("emoji🚀test"),
            new AccessSingleValue(""),
            new AccessSingleValue(null),
        };

        string json = ComplexValueJson.Serialize(input);

        Assert.Contains("\"Kind\":\"single\"", json);
        var result = Assert.IsType<AccessSingleValue[]>(ComplexValueJson.Deserialize(json));
        Assert.Equal(
            new object?[] { "alpha", "zażółć gęślą jaźń", "emoji🚀test", "", null },
            result.Select(v => v.Value));
    }

    [Fact]
    public void Single_scalars_round_trip_with_documented_normalization()
    {
        var input = new AccessSingleValue?[]
        {
            new(true),
            new(false),
            new(5L),
            new(5), // int normalizes to long via JSON numbers, same as before
            new(1.5m),
            new(2.5d), // double normalizes to decimal via JSON numbers, same as int->long
            new(null),
        };

        string json = ComplexValueJson.Serialize(input);
        var result = Assert.IsType<AccessSingleValue[]>(ComplexValueJson.Deserialize(json));

        Assert.Equal(new object?[] { true, false, 5L, 5L, 1.5m, 2.5m, null },
            result.Select(v => v.Value));
    }

    [Fact]
    public void Attachment_round_trip_preserves_all_fields()
    {
        var input = new[]
        {
            new AccessAttachment(
                new byte[] { 1, 2, 3, 250, 255 },
                7,
                "załącznik raport.bin",
                SampleDate,
                "bin",
                "file:///tmp/raport.bin"),
            new AccessAttachment(null, null, null, null, null, null),
        };

        string json = ComplexValueJson.Serialize(input);

        Assert.Contains("\"Kind\":\"attachment\"", json);
        var result = Assert.IsType<AccessAttachment[]>(ComplexValueJson.Deserialize(json));
        Assert.Equal(2, result.Length);
        Assert.Equal(input[0].FileData, result[0].FileData);
        Assert.Equal(input[0].FileFlags, result[0].FileFlags);
        Assert.Equal(input[0].FileName, result[0].FileName);
        Assert.Equal(input[0].FileTimeStamp, result[0].FileTimeStamp);
        Assert.Equal(input[0].FileType, result[0].FileType);
        Assert.Equal(input[0].FileURL, result[0].FileURL);
        Assert.Null(result[1].FileData);
        Assert.Null(result[1].FileName);
        Assert.Null(result[1].FileTimeStamp);
    }

    [Fact]
    public void Version_round_trip_preserves_modified_and_value_text()
    {
        var input = new[]
        {
            new AccessVersion("v1 treść", SampleDate),
            new AccessVersion(42L, null),
        };

        string json = ComplexValueJson.Serialize(input);

        Assert.Contains("\"Kind\":\"version\"", json);
        var result = Assert.IsType<AccessVersion[]>(ComplexValueJson.Deserialize(json));
        Assert.Equal(2, result.Length);
        Assert.Equal("v1 treść", VersionValueText(result[0].Value));
        Assert.Equal(SampleDate, result[0].Modified);
        Assert.Equal("42", VersionValueText(result[1].Value));
        Assert.Null(result[1].Modified);
    }

    [Fact]
    public void Raw_scalar_uses_raw_envelope()
    {
        string json = ComplexValueJson.Serialize("plain text");

        Assert.Contains("\"Kind\":\"raw\"", json);
        var result = Assert.IsType<object?[]>(ComplexValueJson.Deserialize(json));
        Assert.Equal(new object?[] { "plain text" }, result);
    }

    [Fact]
    public void Empty_arrays_round_trip()
    {
        foreach (object input in new object[]
                 {
                     Array.Empty<AccessSingleValue>(),
                     Array.Empty<AccessAttachment>(),
                     Array.Empty<AccessVersion>(),
                 })
        {
            string json = ComplexValueJson.Serialize(input);
            Assert.Contains("\"Values\":[]", json);
        }
    }

    [Fact]
    public void Lowercase_envelope_keys_are_tolerated()
    {
        var result = Assert.IsType<AccessSingleValue[]>(
            ComplexValueJson.Deserialize("{\"kind\":\"single\",\"values\":[\"a\",1]}"));
        Assert.Equal(new object?[] { "a", 1L }, result.Select(v => v.Value));
    }

    /// <summary>
    /// Normalizes AccessVersion.Value across implementations: the current
    /// reflection-based code materializes it as <see cref="JsonElement"/>,
    /// the AOT-friendly rewrite maps it through the same scalar conversion
    /// as single values. Both must carry the same text.
    /// </summary>
    private static string? VersionValueText(object? value)
        => value switch
        {
            null => null,
            string s => s,
            JsonElement element => element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : element.GetRawText(),
            _ => value.ToString(),
        };
}
