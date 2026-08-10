using UCanAccess.File;
using Xunit;

namespace UCanAccess.File.Tests;

/// <summary>
/// Validates the byte-order-aware column serialization used when encoding index
/// entries (B1, port of Jackcess <c>ColumnImpl.write(obj, 0, ENTRY_BYTE_ORDER)</c>).
/// Expected bytes are hand-traced from the Java implementation semantics.
/// </summary>
public class ColumnIndexSerializationTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static string Hex(byte[] bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));

    private static void AssertIndexBytes(string columnName, object? value, string expectedHex)
    {
        using var db = Database.Open(Fixture("generated/genAllTypes.mdb"));
        var table = db.GetTable("t_alltypes")!;
        var column = table.Columns.First(c => c.Name == columnName);
        Assert.Equal(expectedHex, Hex(column.WriteIndexValue(value)));
    }

    [Fact]
    public void Long_index_value_is_big_endian()
        => AssertIndexBytes("id", 305419896, "12 34 56 78");

    [Fact]
    public void Int_index_value_is_big_endian()
        => AssertIndexBytes("i", (short)1000, "03 E8");

    [Fact]
    public void Double_index_value_is_big_endian()
        => AssertIndexBytes("d", 1.5, "3F F8 00 00 00 00 00 00");

    [Fact]
    public void Float_index_value_is_big_endian()
        => AssertIndexBytes("f", 1.5f, "3F C0 00 00");

    [Fact]
    public void Money_index_value_is_big_endian()
        => AssertIndexBytes("m", 12.3456m, "00 00 00 00 00 01 E2 40");

    [Fact]
    public void Numeric_index_value_is_big_endian()
        => AssertIndexBytes("num", 1234.56m, "00 00 00 00 00 00 00 00 00 00 00 00 00 00 01 E2 40");

    [Fact]
    public void Negative_numeric_index_value_has_negative_sign_byte()
        => AssertIndexBytes("num", -1234.56m, "80 00 00 00 00 00 00 00 00 00 00 00 00 00 01 E2 40");

    [Fact]
    public void Date_index_value_is_a_big_endian_double()
        => AssertIndexBytes("dt", new DateTime(1899, 12, 30), "00 00 00 00 00 00 00 00");

    [Fact]
    public void Guid_index_value_is_in_natural_hex_order()
        => AssertIndexBytes("guid", "{12345678-1234-5678-9ABC-DEF012345678}",
            "12 34 56 78 12 34 56 78 9A BC DE F0 12 34 56 78");
}
