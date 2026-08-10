using UCanAccess.File;
using Xunit;

namespace UCanAccess.File.Tests;

/// <summary>
/// P3.2: index write edge cases — multi-column indexes, ignore-nulls, required and
/// descending indexes — against the Java-created <c>genIndexedEdge.mdb</c>.
/// </summary>
public class IndexEdgeTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static List<byte[]> ReadEntries(IndexData idx, Database db)
    {
        var buf = new byte[db.Format.PageSize];
        db.PageChannel.ReadPage(buf, idx.RootPageNumber);
        return IndexData.ReadEntries(buf, db.Format).Select(e => e.EntryBytes!).ToList();
    }

    [Fact]
    public void Reads_multi_column_index_definitions()
    {
        using var db = Database.Open(Fixture("generated/genIndexedEdge.mdb"));
        var t = db.GetTable("t_edge")!;

        IndexImpl fullname = t.Indexes.First(i => i.Name == "idx_fullname");
        Assert.Equal(2, fullname.IndexData.Columns.Count);
        Assert.Equal("first", fullname.IndexData.Columns[0].Column.Name);
        Assert.Equal("last", fullname.IndexData.Columns[1].Column.Name);

        Assert.True(t.Indexes.First(i => i.Name == "idx_note_ignorenulls").IndexData.ShouldIgnoreNulls);
        Assert.True(t.Indexes.First(i => i.Name == "idx_code_required").IndexData.IsRequired);
        Assert.False(t.Indexes.First(i => i.Name == "idx_val_desc").IndexData.Columns[0].IsAscending);
    }

    [Fact]
    public void Multi_column_index_writes_roundtrip()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_edge_{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Fixture("generated/genIndexedEdge.mdb"), tmp, true);
        try
        {
            using (var db = Database.Open(tmp, readOnly: false))
            {
                var t = db.GetTable("t_edge")!;
                t.AddRow(new object?[] { null, "Ewa", "Lis", 7.5, "n9", "C9" });
                // update an existing row's indexed columns
                var loc = FindRow(t, "n3");
                t.UpdateRow(loc.PageNumber, loc.RowNumber, new object?[] { null, "Piotr", "Wozniak", 42.0, "n3b", "C3" });
            }

            using (var db = Database.Open(tmp))
            {
                var t = db.GetTable("t_edge")!;
                Assert.Equal(5, t.RowCount);

                IndexData fullname = t.Indexes.First(i => i.Name == "idx_fullname").IndexData;
                var entries = ReadEntries(fullname, db);
                Assert.Equal(5, entries.Count);
                // the multi-column entry for ("Ewa","Lis") must exist
                byte[] ewa = fullname.CreateEntryBytes(new object?[] { null, "Ewa", "Lis", null, null, null });
                Assert.Contains(entries, e => e.SequenceEqual(ewa));
                byte[] piotr = fullname.CreateEntryBytes(new object?[] { null, "Piotr", "Wozniak", null, null, null });
                Assert.Contains(entries, e => e.SequenceEqual(piotr));

                // descending index: the largest value sorts first
                IndexData valDesc = t.Indexes.First(i => i.Name == "idx_val_desc").IndexData;
                var valEntries = ReadEntries(valDesc, db);
                byte[] v100 = valDesc.CreateEntryBytes(new object?[] { null, null, null, 100.0, null, null });
                Assert.Equal(v100, valEntries[0]);
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void IgnoreNulls_index_skips_null_values()
    {
        using var db = Database.Open(Fixture("generated/genIndexedEdge.mdb"));
        var t = db.GetTable("t_edge")!;
        IndexData note = t.Indexes.First(i => i.Name == "idx_note_ignorenulls").IndexData;

        var entries = ReadEntries(note, db);
        // rows 2 and 4 have null notes -> only 2 entries (n1, n3)
        Assert.Equal(2, entries.Count);
        foreach (byte[] e in entries)
        {
            // the null entry flag is a single 0x00 byte; real values start with 0x7F
            Assert.NotEqual((byte)IndexCodes.AscNullFlag, e[0]);
        }
    }

    [Fact]
    public void Required_index_rejects_null_on_add()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_edge_req_{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Fixture("generated/genIndexedEdge.mdb"), tmp, true);
        try
        {
            using (var db = Database.Open(tmp, readOnly: false))
            {
                var t = db.GetTable("t_edge")!;
                Assert.Throws<DatabaseException>(() => t.AddRow(new object?[] { null, "X", "Y", 1.0, "n", null }));
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    private static Table.RowLocation FindRow(Table table, string note)
    {
        foreach (Table.RowLocation loc in table.RowLocations())
        {
            if (Equals(loc.Row["note"], note))
            {
                return loc;
            }
        }
        throw new InvalidOperationException($"note '{note}' not found");
    }
}
