using UCanAccess.File;
using Xunit;

namespace UCanAccess.File.Tests;

/// <summary>
/// Validates parsing of index definitions from the table-definition buffer (B2)
/// against the <c>genIndexed.mdb</c> fixture created by the original Jackcess
/// (<c>tools/JavaOracle/DbGen.java</c>: PrimaryKey on <c>id</c>, unique <c>idx_code</c> on
/// <c>code</c>, 50 rows).
/// </summary>
public class IndexDefinitionTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void genIndexed_has_two_named_logical_indexes()
    {
        using var db = Database.Open(Fixture("generated/genIndexed.mdb"));
        var t = db.GetTable("t_indexed")!;
        Assert.Equal(2, t.IndexCount);
        Assert.Equal(2, t.LogicalIndexCount);
        Assert.Equal(2, t.Indexes.Count);
        Assert.Equal(new[] { "PrimaryKey", "idx_code" }, t.Indexes.Select(i => i.Name));
    }

    [Fact]
    public void primary_key_index_is_unique_on_id_ascending()
    {
        using var db = Database.Open(Fixture("generated/genIndexed.mdb"));
        var t = db.GetTable("t_indexed")!;
        IndexImpl pk = t.Indexes.First(i => i.Name == "PrimaryKey");
        Assert.True(pk.IsPrimaryKey);
        Assert.True(pk.IndexData.IsUnique);
        IndexData.ColumnDescriptor col = Assert.Single(pk.IndexData.ColumnDescriptors);
        Assert.Equal("id", col.Column.Name);
        Assert.True(col.IsAscending);
        Assert.Equal(50, pk.IndexData.UniqueEntryCount);
        Assert.True(pk.IndexData.RootPageNumber > 0);
        Assert.NotNull(pk.IndexData.OwnedPages);
    }

    [Fact]
    public void code_index_is_unique_on_code_ascending()
    {
        using var db = Database.Open(Fixture("generated/genIndexed.mdb"));
        var t = db.GetTable("t_indexed")!;
        IndexImpl idx = t.Indexes.First(i => i.Name == "idx_code");
        Assert.False(idx.IsPrimaryKey);
        Assert.True(idx.IndexData.IsUnique);
        IndexData.ColumnDescriptor col = Assert.Single(idx.IndexData.ColumnDescriptors);
        Assert.Equal("code", col.Column.Name);
        Assert.True(col.IsAscending);
        Assert.Equal(50, idx.IndexData.UniqueEntryCount);
        Assert.True(idx.IndexData.RootPageNumber > 0);
    }

    [Fact]
    public void table_without_indexes_has_none()
    {
        using var db = Database.Open(Fixture("generated/genEmpty.mdb"));
        var t = db.GetTable("t_empty")!;
        Assert.Equal(0, t.IndexCount);
        Assert.Empty(t.IndexDatas);
        Assert.Empty(t.Indexes);
    }
}
