using UCanAccess.File;
using Xunit;

namespace UCanAccess.File.Tests;

/// <summary>
/// Validates the index entry primitives (B3): byte-code comparison, rowId ordering,
/// and the on-disk entry layout (entry bytes + 3-byte big-endian page + 1-byte row,
/// plus a 4-byte big-endian sub-page for node entries), all ported from Jackcess.
/// </summary>
public class IndexEntryTests
{
    private static byte[] BuildEntryBuffer(IndexData.Entry entry, byte[]? prefix = null)
    {
        var bout = new ByteStream();
        entry.Write(bout, prefix ?? Array.Empty<byte>());
        return bout.ToByteArray();
    }

    [Fact]
    public void ByteCodeCompare_orders_lexicographically()
    {
        Assert.True(IndexData.ByteCodeCompare(new byte[] { 0x01, 0x02 }, new byte[] { 0x01, 0x03 }) < 0);
        Assert.True(IndexData.ByteCodeCompare(new byte[] { 0x01, 0x02 }, new byte[] { 0x01, 0x02 }) == 0);
        Assert.True(IndexData.ByteCodeCompare(new byte[] { 0x01, 0x02, 0x00 }, new byte[] { 0x01, 0x02 }) > 0);
        Assert.True(IndexData.ByteCodeCompare(null, new byte[] { 0x01 }) < 0);
        Assert.True(IndexData.ByteCodeCompare(new byte[] { 0x01 }, null) > 0);
        Assert.True(IndexData.ByteCodeCompare(null, null) == 0);
    }

    [Fact]
    public void RowId_orders_first_normal_last()
    {
        Assert.True(RowId.FirstRowId.CompareTo(new RowId(5, 1)) < 0);
        Assert.True(new RowId(5, 1).CompareTo(RowId.LastRowId) < 0);
        Assert.True(new RowId(5, 1).CompareTo(new RowId(6, 1)) < 0);
        Assert.True(new RowId(5, 1).CompareTo(new RowId(5, 2)) < 0);
        Assert.False(RowId.FirstRowId.IsValid);
        Assert.True(new RowId(5, 1).IsValid);
    }

    [Fact]
    public void Entry_roundtrip_preserves_bytes_and_rowid()
    {
        var rowId = new RowId(0x010203, 0x0A);
        var entryBytes = new byte[] { 0x00, 0x00, 0x00, 0x12, 0x34 };
        var entry = new IndexData.Entry(entryBytes, rowId);
        Assert.True(entry.IsValid);
        Assert.Equal(IndexData.EntryType.Normal, entry.Type);
        Assert.Equal(9, entry.Size);

        byte[] buf = BuildEntryBuffer(entry);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x12, 0x34, 0x01, 0x02, 0x03, 0x0A }, buf);

        var parsed = new IndexData.Entry(buf, 0, buf.Length);
        Assert.Equal(rowId, parsed.RowId);
        Assert.Equal(entryBytes, parsed.EntryBytes);
        Assert.Equal(0, parsed.CompareTo(entry));
    }

    [Fact]
    public void Entry_write_omits_prefix()
    {
        var entry = new IndexData.Entry(new byte[] { 0x01, 0x02, 0x03, 0x04 }, new RowId(5, 1));
        byte[] buf = BuildEntryBuffer(entry, new byte[] { 0x01, 0x02 });
        Assert.Equal(new byte[] { 0x03, 0x04, 0x00, 0x00, 0x05, 0x01 }, buf);
    }

    [Fact]
    public void Entry_prefix_overlaps_page_number()
    {
        // prefix longer than the entry bytes, overlapping the 3-byte page number
        var entry = new IndexData.Entry(new byte[] { 0x01, 0x02 }, new RowId(0x000102, 3));
        byte[] buf = BuildEntryBuffer(entry, new byte[] { 0x01, 0x02, 0x00 });
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, buf);
    }

    [Fact]
    public void NodeEntry_roundtrip()
    {
        var entry = new IndexData.NodeEntry(new byte[] { 0xAA, 0xBB }, new RowId(7, 2), IndexData.EntryType.Normal, 0x00010203);
        Assert.Equal(10, entry.Size);
        byte[] buf = BuildEntryBuffer(entry);
        var parsed = new IndexData.NodeEntry(buf, 0, buf.Length);
        Assert.Equal(0x00010203, parsed.SubPageNumber);
        Assert.False(parsed.IsLeafEntry);
        Assert.Equal(0, parsed.CompareTo(entry));
    }

    [Fact]
    public void Entry_special_types_compare_around_valid_entries()
    {
        var alwaysFirst = new IndexData.Entry(null, RowId.FirstRowId);
        var normal = new IndexData.Entry(new byte[] { 0x01 }, new RowId(1, 1));
        var alwaysLast = new IndexData.Entry(null, RowId.LastRowId);

        Assert.True(alwaysFirst.CompareTo(normal) < 0);
        Assert.True(normal.CompareTo(alwaysLast) < 0);
        Assert.Equal(IndexData.EntryType.AlwaysFirst, alwaysFirst.Type);
        Assert.Equal(IndexData.EntryType.AlwaysLast, alwaysLast.Type);

        var firstValid = new IndexData.Entry(new byte[] { 0x01 }, RowId.FirstRowId);
        var lastValid = new IndexData.Entry(new byte[] { 0x01 }, RowId.LastRowId);
        Assert.Equal(IndexData.EntryType.FirstValid, firstValid.Type);
        Assert.Equal(IndexData.EntryType.LastValid, lastValid.Type);
        Assert.True(firstValid.CompareTo(normal) < 0);
        Assert.True(normal.CompareTo(lastValid) < 0);
    }
}
