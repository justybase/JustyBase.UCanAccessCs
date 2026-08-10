using UCanAccess.File;
using Xunit;

namespace UCanAccess.File.Tests;

/// <summary>
/// P1.1: the index cursor API — rows enumerated in index order and in a value range.
/// </summary>
public class IndexCursorTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Rows_in_index_order_follow_the_primary_key()
    {
        using var db = Database.Open(Fixture("generated/genIndexed.mdb"));
        var t = db.GetTable("t_indexed")!;

        var ids = t.RowsInIndexOrder("PrimaryKey").Select(r => (int)r["id"]!).ToList();
        Assert.Equal(50, ids.Count);
        Assert.Equal(Enumerable.Range(1, 50), ids);
    }

    [Fact]
    public void Rows_in_index_range_are_bounded_and_inclusive()
    {
        using var db = Database.Open(Fixture("generated/genIndexed.mdb"));
        var t = db.GetTable("t_indexed")!;

        // sparse row: only the indexed column (id, column 0) is set
        var ids = t.RowsInIndexRange("PrimaryKey", new object?[] { 10 }, true, new object?[] { 20 }, true)
            .Select(r => (int)r["id"]!).ToList();
        Assert.Equal(Enumerable.Range(10, 11), ids);
    }

    [Fact]
    public void Rows_in_index_range_exclusive_bounds()
    {
        using var db = Database.Open(Fixture("generated/genIndexed.mdb"));
        var t = db.GetTable("t_indexed")!;

        var ids = t.RowsInIndexRange("PrimaryKey", new object?[] { 10 }, false, new object?[] { 20 }, false)
            .Select(r => (int)r["id"]!).ToList();
        Assert.Equal(Enumerable.Range(11, 9), ids);
    }

    [Fact]
    public void Rows_in_text_index_order()
    {
        using var db = Database.Open(Fixture("generated/genIndexed.mdb"));
        var t = db.GetTable("t_indexed")!;

        // code column is at index 1
        var codes = t.RowsInIndexOrder("idx_code").Select(r => (string)r["code"]!).ToList();
        Assert.Equal(50, codes.Count);
        Assert.Equal(Enumerable.Range(1, 50).Select(i => $"code{i:00}"), codes);
    }

    [Fact]
    public void Rows_in_descending_index_order()
    {
        using var db = Database.Open(Fixture("generated/genIndexedAllTypes.mdb"));
        var t = db.GetTable("t_idx_alltypes")!;

        // l column is at index 2; the descending index returns largest first
        var longs = t.RowsInIndexOrder("idx_l_desc").Select(r => (int)r["l"]!).ToList();
        Assert.Equal(new[] { 500, 400, 300, 200, 100 }, longs);
    }
}
