using UCanAccess.File;
using Xunit;

namespace UCanAccess.File.Tests;

public class DatabaseTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Opens_and_lists_tables()
    {
        using var db = Database.Open(Fixture("accessLike.mdb"));
        var names = db.GetTableNames();
        Assert.Contains("t_like1", names);
        Assert.Contains("t_like2", names);
    }

    [Fact]
    public void System_catalog_is_read()
    {
        using var db = Database.Open(Fixture("accessLike.mdb"));
        var systemNames = db.GetSystemTableNames();
        Assert.Contains("MSysObjects", systemNames);
        Assert.Equal("MSysObjects", db.SystemCatalog.Name);
        Assert.True(db.SystemCatalog.RowCount > 0);
    }

    [Fact]
    public void Table_metadata_is_exposed()
    {
        using var db = Database.Open(Fixture("functionsV2003.mdb"));
        var t = db.GetTable("t_funcs")!;
        Assert.Equal(4, t.Columns.Count);
        Assert.Equal(new[] { "id", "descr", "num", "date0" }, t.Columns.Select(c => c.Name));
        Assert.Equal(DataType.Int, t.Columns[0].Type);
        Assert.Equal(DataType.Memo, t.Columns[1].Type);
        Assert.Equal(DataType.Numeric, t.Columns[2].Type);
        Assert.Equal(DataType.ShortDateTime, t.Columns[3].Type);
        Assert.Equal((byte)12, t.Columns[2].Precision);
        Assert.Equal((byte)3, t.Columns[2].Scale);
    }

    [Fact]
    public void Reads_rows_with_autonumber()
    {
        using var db = Database.Open(Fixture("accessLike.mdb"));
        var t = db.GetTable("t_like2")!;
        var rows = t.Rows().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0]["ID"]);
        Assert.Equal("dd", rows[0]["Campo1"]);
        Assert.True(t.Columns[0].AutoNumber);
    }

    [Fact]
    public void Reads_currency_and_datetime()
    {
        using var db = Database.Open(Fixture("pivot.mdb"));
        var t = db.GetTable("t_pivot")!;
        var rows = t.Rows().ToList();
        Assert.Equal(4, rows.Count);
        Assert.Equal(4444.0000m, rows[0]["c_val"]);
        Assert.Equal(new DateTime(2013, 5, 30, 13, 18, 14), rows[0]["c_dt"]);
    }

    [Fact]
    public void Reads_jet3_access97()
    {
        using var db = Database.Open(Fixture("size97.mdb"));
        Assert.Equal("table1", db.GetTableNames().Single());
        var t = db.GetTable("table1")!;
        Assert.Equal(2, t.Columns.Count);
        Assert.Empty(t.Rows());
    }

    [Fact]
    public void Database_is_readonly_shared_handle()
    {
        // the file should stay readable even while open
        using var db = Database.Open(Fixture("accessLike.mdb"));
        var bytes = System.IO.File.ReadAllBytes(Fixture("accessLike.mdb"));
        Assert.True(bytes.Length > 0);
    }
}
