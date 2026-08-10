using UCanAccess.File;
using Xunit;

namespace UCanAccess.File.Tests;

/// <summary>
/// Validates the text index entry encoder (K1.1) against manually-traced byte sequences
/// derived from the <c>index_codes_*.txt</c> tables. Full round-trip validation happens in
/// <see cref="WritePathTests"/> against original Jackcess via <c>tools/JavaOracle</c>.
/// </summary>
public class IndexCodesTests
{
    private static byte[] EncodeText(string text, GeneralLegacyIndexCodes codes, bool ascending = true)
    {
        var bout = new ByteStream();
        codes.WriteNonNullIndexTextValue(text, bout, ascending);
        return bout.ToByteArray();
    }

    private static string Hex(byte[] bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));

    [Fact]
    public void Simple_ascii_legacy()
    {
        // 'A' -> 0x4A, 'B' -> 0x4C (per index_codes_genleg.txt); END_TEXT=01, END_EXTRA_TEXT=00
        Assert.Equal("4A 4C 01 00", Hex(EncodeText("AB", GeneralLegacyIndexCodes.GenLegacyInstance)));
    }

    [Fact]
    public void International_legacy()
    {
        // 'e' -> 0x51, 'e-acute' extra -> 0x0E  (table: "I51,E"); order: inline, END_TEXT, extra, END_EXTRA
        Assert.Equal("51 01 0E 00", Hex(EncodeText("\u00E9", GeneralLegacyIndexCodes.GenLegacyInstance)));
        Assert.Equal("51 4A 01 0E 00", Hex(EncodeText("\u00E9A", GeneralLegacyIndexCodes.GenLegacyInstance)));
    }

    [Fact]
    public void Trailing_spaces_trimmed()
    {
        Assert.Equal("4A 01 00", Hex(EncodeText("A   ", GeneralLegacyIndexCodes.GenLegacyInstance)));
    }

    [Fact]
    public void Descending_flips_bytes()
    {
        // ascending "AB" = 4A 4C 01 00; descending inverts every byte then appends final 0x00
        Assert.Equal("B5 B3 FE FF 00", Hex(EncodeText("AB", GeneralLegacyIndexCodes.GenLegacyInstance, ascending: false)));
    }

    [Fact]
    public void General_2010_instance_encodes()
    {
        // 'A' in the gen (Access 2010+) table sorts differently than legacy; just ensure it runs
        byte[] bytes = EncodeText("Access", GeneralIndexCodes.GenInstance);
        Assert.NotEmpty(bytes);
        Assert.Contains((byte)0x01, bytes);
    }

    [Fact]
    public void Gen_97_instance_encodes()
    {
        // nibble buffer path
        byte[] bytes = EncodeText("caf\u00e9", General97IndexCodes.Gen97Instance);
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public void High_ascii_does_not_throw()
    {
        // chars beyond 0x7F use the ext table
        byte[] bytes = EncodeText("\u0100\u20AC", GeneralLegacyIndexCodes.GenLegacyInstance);
        Assert.NotEmpty(bytes);
    }
}