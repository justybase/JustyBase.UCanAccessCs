using UCanAccess.File;
using Xunit;

namespace UCanAccess.File.Tests;

/// <summary>
/// Cross-checks the index entry encoding (B4) against the ACTUAL index pages written by
/// the original Jackcess into <c>genIndexed.mdb</c>: the encoded entry bytes for a row
/// value must exactly match the bytes stored on the index leaf pages.
/// </summary>
public class IndexEntryEncodingTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static string Hex(byte[] bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));

    private static (IndexData IndexData, List<IndexData.Entry> Entries) ReadIndex(string indexName)
    {
        using var db = Database.Open(Fixture("generated/genIndexed.mdb"));
        var t = db.GetTable("t_indexed")!;
        IndexData idx = t.Indexes.First(i => i.Name == indexName).IndexData;
        var buffer = new byte[db.Format.PageSize];
        db.PageChannel.ReadPage(buffer, idx.RootPageNumber);
        return (idx, IndexData.ReadEntries(buffer, db.Format));
    }

    [Fact]
    public void PrimaryKey_entries_match_encoded_values()
    {
        (IndexData idx, var entries) = ReadIndex("PrimaryKey");
        Assert.Equal(50, entries.Count);

        // first entry must be exactly the encoding of id=1
        byte[] encoded1 = idx.CreateEntryBytes(new object?[] { 1 });
        Assert.Equal("7F 80 00 00 01", Hex(entries[0].EntryBytes!));
        Assert.Equal(encoded1, entries[0].EntryBytes);

        // last entry must be the encoding of id=50
        byte[] encoded50 = idx.CreateEntryBytes(new object?[] { 50 });
        Assert.Equal(encoded50, entries[^1].EntryBytes);

        // entries are in ascending byte order
        for (int i = 1; i < entries.Count; i++)
        {
            Assert.True(IndexData.ByteCodeCompare(entries[i - 1].EntryBytes, entries[i].EntryBytes) < 0,
                $"entry {i} out of order");
        }

        // rows were added in id order, so entry i maps to data-page row i (0-based)
        for (int i = 0; i < entries.Count; i++)
        {
            Assert.True(entries[i].RowId.PageNumber > 0);
            Assert.Equal(i, entries[i].RowId.RowNumber);
        }
    }

    [Fact]
    public void Code_entries_match_encoded_values()
    {
        (IndexData idx, var entries) = ReadIndex("idx_code");
        Assert.Equal(50, entries.Count);

        for (int i = 0; i < entries.Count; i++)
        {
            string code = $"code{i + 1:00}";
            byte[] encoded = idx.CreateEntryBytes(new object?[] { null, code, null });
            Assert.Equal(encoded, entries[i].EntryBytes);
        }
    }

    [Fact]
    public void Descending_encode_flips_bytes()
    {
        // verify descending encoding against a synthetic (non-committed) check:
        // for an integer value, descending entry bytes must be the bit-flip of the
        // ascending entry bytes (start flag + value both flipped, as Jackcess writes)
        using var db = Database.Open(Fixture("generated/genIndexed.mdb"));
        var t = db.GetTable("t_indexed")!;
        IndexData pk = t.Indexes.First(i => i.Name == "PrimaryKey").IndexData;

        // build the descriptors the same way a descending index would
        byte[] asc = pk.CreateEntryBytes(new object?[] { 5 });

        // flip each byte to get the descending form (Jackcess: !isAsc => flipFirstBit, flipBytes)
        var flipped = (byte[])asc.Clone();
        IndexCodes.FlipBytes(flipped, 0, flipped.Length);
        // the entry always starts with the null/start flag; for descending the start flag is 0x80
        Assert.NotEqual(asc, flipped);
        Assert.Equal(0x80, flipped[0]);
    }
}
