using UCanAccess.File;
using Xunit;

namespace UCanAccess.File.Tests;

public class LinkedTableTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Linked_tables_are_discovered_in_catalog()
    {
        using var db = Database.Open(Fixture("linked.mdb"));
        var names = db.GetTableNames();
        Assert.Contains("Table1", names);
        Assert.Contains("table2", names);

        var metas = db.GetTableMetaData().Where(m => !m.IsSystem).ToList();
        Assert.Equal(2, metas.Count);
        Assert.All(metas, m => Assert.True(m.IsLinked));
        // linked targets point at the other fixtures
        Assert.NotNull(metas.First(m => m.Name == "Table1").LinkedDbName);
    }

    [Fact]
    public void Opening_linked_table_throws()
    {
        using var db = Database.Open(Fixture("linked.mdb"));
        var ex = Assert.Throws<DatabaseException>(() => db.GetTable("Table1"));
        Assert.Contains("linked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
