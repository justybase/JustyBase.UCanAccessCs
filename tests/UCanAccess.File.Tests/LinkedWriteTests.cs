using UCanAccess.File;
using Xunit;

namespace UCanAccess.File.Tests;

/// <summary>
/// P2.4: writes to a native linked table route through to the linkee database
/// (<c>genLinked.mdb</c> links <c>t_linked</c> to <c>t_linkee</c> in
/// <c>genLinkee.mdb</c>, resolved relative to the main database's directory).
/// </summary>
public class LinkedWriteTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Opens_linked_table_through_linkee()
    {
        using var db = Database.Open(Fixture("generated/genLinked.mdb"));
        var t = db.GetLinkedTable("t_linked");
        Assert.NotNull(t);
        Assert.Equal("t_linkee", t!.Name);
        Assert.Equal(2, t.RowCount);
        var names = t.Rows().Select(r => (string)r["name"]!).ToList();
        Assert.Equal(new[] { "linkee one", "linkee two" }, names);
    }

    [Fact]
    public void Writes_route_to_linkee()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), $"ucanaccess_link_{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(tmpDir);
        string main = Path.Combine(tmpDir, "main.mdb");
        string linkee = Path.Combine(tmpDir, "linkee.mdb");
        try
        {
            using (var db = Database.Create(linkee))
            {
                db.CreateTable("t_linkee",
                    new[] { new ColumnBuilder("id", DataType.Long).WithAutoNumber(true), new ColumnBuilder("name", DataType.Text).WithLength(50) });
                var t = db.GetTable("t_linkee")!;
                t.AddRow(new object?[] { null, "seed" });
            }

            using (var mainDb = Database.Create(main))
            {
                // link the new database to the linkee (createLinkedTable equivalent)
                AddLinkedTableRow(mainDb, "t_linked", "linkee.mdb", "t_linkee");
            }

            using (var db = Database.Open(main, readOnly: false))
            {
                var t = db.GetLinkedTable("t_linked")!;
                t.AddRow(new object?[] { null, "via link" });
                Table.RowLocation seed = FindRowId(t, "seed");
                t.UpdateRow(seed.PageNumber, seed.RowNumber, new object?[] { null, "seed updated" });
                Table.RowLocation link = FindRowId(t, "via link");
                t.DeleteRow(link.PageNumber, link.RowNumber);
            }

            // the linkee itself reflects the writes
            using (var db = Database.Open(linkee))
            {
                var t = db.GetTable("t_linkee")!;
                Assert.Equal(1, t.RowCount);
                Assert.Equal("seed updated", t.Rows().Single()["name"]);
            }
        }
        finally
        {
            System.IO.Directory.Delete(tmpDir, true);
        }
    }

    /// <summary>adds a linked-table catalog row (Id = a synthetic page number not used by a real tdef)</summary>
    private static void AddLinkedTableRow(Database db, string name, string linkedDbName, string linkedTableName)
    {
        int objectId = 0x00FFFFFF;
        var values = new object?[db.SystemCatalog.Columns.Count];
        for (int i = 0; i < db.SystemCatalog.Columns.Count; i++)
        {
            string colName = db.SystemCatalog.Columns[i].Name;
            values[i] = colName switch
            {
                "Id" => objectId,
                "Name" => name,
                "Type" => (byte)Database.TypeLinkedTable,
                "DateUpdate" or "DateCreate" => DateTime.Now,
                "Flags" => 0,
                "ParentId" => db.TablesParentId,
                "Database" => linkedDbName,
                "ForeignName" => linkedTableName,
                _ => null,
            };
        }
        db.SystemCatalog.AddRow(values);
    }

    private static Table.RowLocation FindRowId(Table table, string name)
    {
        foreach (Table.RowLocation loc in table.RowLocations())
        {
            if (Equals(loc.Row["name"], name))
            {
                return loc;
            }
        }
        throw new InvalidOperationException($"row '{name}' not found");
    }
}
