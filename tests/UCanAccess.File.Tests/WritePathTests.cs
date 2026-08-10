using UCanAccess.File;
using Xunit;

namespace UCanAccess.File.Tests;

/// <summary>
/// Write-path tests: mutations are applied to COPIES of the fixtures so the
/// committed binaries stay pristine.
/// </summary>
public class WritePathTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static string TempCopy(string fixtureName)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_write_{Guid.NewGuid():N}_{Path.GetFileName(fixtureName)}");
        System.IO.File.Copy(Fixture(fixtureName), tmp, true);
        return tmp;
    }

    [Fact]
    public void AddRow_appends_and_is_readable_again()
    {
        string tmp = TempCopy("generated/genEmpty.mdb");
        try
        {
            using (var db = Database.Open(tmp, readOnly: false))
            {
                var t = db.GetTable("t_empty")!;
                Assert.Equal(0, t.RowCount);
                t.AddRow(new object?[] { null, "first row" });
                t.AddRow(new object?[] { null, "second row" });
                Assert.Equal(2, t.RowCount);
                Assert.Equal(2, t.LastLongAutoNumber);
            }

            // re-read with the port
            using (var db = Database.Open(tmp))
            {
                var t = db.GetTable("t_empty")!;
                Assert.Equal(2, t.RowCount);
                var rows = t.Rows().ToList();
                Assert.Equal(2, rows.Count);
                Assert.Equal(1, rows[0]["id"]);
                Assert.Equal("first row", rows[0]["name"]);
                Assert.Equal(2, rows[1]["id"]);
                Assert.Equal("second row", rows[1]["name"]);
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Write_batch_defers_flush_and_reopens_with_all_rows()
    {
        string tmp = TempCopy("generated/genEmpty.mdb");
        try
        {
            using (var db = Database.Open(tmp, readOnly: false))
            {
                Table table = db.GetTable("t_empty")!;
                using (WriteBatch batch = db.BeginWriteBatch())
                {
                    table.AddRow(new object?[] { null, "batch one" });
                    table.AddRow(new object?[] { null, "batch two" });
                    batch.Commit();
                }
            }

            using var reopened = Database.Open(tmp);
            Assert.Equal(2, reopened.GetTable("t_empty")!.RowCount);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void AddRow_with_all_common_types_roundtrips()
    {
        string tmp = TempCopy("generated/genAllTypes.mdb");
        try
        {
            using (var db = Database.Open(tmp, readOnly: false))
            {
                var t = db.GetTable("t_alltypes")!;
                t.AddRow(new object?[]
                {
                    null,                    // id (auto-number)
                    "written row",           // name
                    "a memo with some text", // memo (long value, inline)
                    (short)42,               // i
                    123456789,               // l
                    2.718281828,             // d
                    1.25f,                   // f
                    987.6543m,               // m
                    1234.56m,                // num
                    new DateTime(2024, 2, 29, 8, 30, 15), // dt
                    true,                    // b
                    "{ABCDEF01-2345-6789-ABCD-EF0123456789}", // guid
                    new byte[] { 9, 8, 7, 6, 5 },  // bin (long value)
                });
            }

            using (var db = Database.Open(tmp))
            {
                var t = db.GetTable("t_alltypes")!;
                Assert.Equal(7, t.RowCount);
                var row = t.Rows().Last();
                Assert.Equal(7, row["id"]);
                Assert.Equal("written row", row["name"]);
                Assert.Equal("a memo with some text", row["memo"]);
                Assert.Equal(123456789, row["l"]);
                Assert.Equal(2.718281828, row["d"]);
                Assert.Equal(1.25f, row["f"]);
                Assert.Equal(987.6543m, row["m"]);
                Assert.Equal(1234.56m, row["num"]);
                Assert.Equal(new DateTime(2024, 2, 29, 8, 30, 15), row["dt"]);
                Assert.True((bool)row["b"]!);
                Assert.Equal("{ABCDEF01-2345-6789-ABCD-EF0123456789}", row["guid"]);
                Assert.Equal(new byte[] { 9, 8, 7, 6, 5 }, (byte[])row["bin"]!);
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void UpdateRow_in_place_and_relocation()
    {
        string tmp = TempCopy("generated/genEmpty.mdb");
        try
        {
            // seed three rows
            using (var db = Database.Open(tmp, readOnly: false))
            {
                var t = db.GetTable("t_empty")!;
                t.AddRow(new object?[] { null, "row one" });
                t.AddRow(new object?[] { null, "row two" });
                t.AddRow(new object?[] { null, "row three" });
            }

            // update row 1 in place (shorter text) and row 2 with relocation (longer text)
            using (var db = Database.Open(tmp, readOnly: false))
            {
                var t = db.GetTable("t_empty")!;

                var (page, rowNum) = FindRowId(t, "row two");
                t.UpdateRow(page, rowNum, new object?[] { null, "two" });

                (page, rowNum) = FindRowId(t, "row three");
                t.UpdateRow(page, rowNum, new object?[] { null, "row three with a longer name causing relocation" });
            }

            using (var db = Database.Open(tmp))
            {
                var t = db.GetTable("t_empty")!;
                var names = t.Rows().Select(r => (string)r["name"]!).ToList();
                Assert.Contains("two", names);
                Assert.Contains("row three with a longer name causing relocation", names);
                Assert.Equal(3, t.RowCount);
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void UpdateRow_after_repeated_relocation_preserves_the_row()
    {
        string tmp = NewOverflowDatabase();
        try
        {
            using (var db = Database.Open(tmp, readOnly: false))
            {
                var t = db.GetTable("t_overflow")!;
                var location = Assert.Single(t.RowLocations());
                string first = new('a', 160);
                string second = new('b', 300);
                t.UpdateRow(location.PageNumber, location.RowNumber, new object?[] { 1, first });
                t.UpdateRow(location.PageNumber, location.RowNumber, new object?[] { 1, second });
            }

            using (var db = Database.Open(tmp))
            {
                var row = Assert.Single(db.GetTable("t_overflow")!.Rows());
                Assert.Equal(new string('b', 300), row["payload"]);
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void UpdateRow_reusing_an_overflow_page_preserves_both_writes()
    {
        string tmp = NewOverflowDatabase();
        try
        {
            using (var db = Database.Open(tmp, readOnly: false))
            {
                var t = db.GetTable("t_overflow")!;
                for (int id = 2; id <= 400; id++)
                {
                    t.AddRow(new object?[] { id, new string('s', 100) });
                }

                // Fill the last data page first.  The first update therefore
                // relocates to a new page, and the second update reuses that
                // overflow page instead of allocating another one.
                var location = t.RowLocations().Last();
                t.UpdateRow(location.PageNumber, location.RowNumber,
                    new object?[] { 400, new string('a', 450) });
                t.UpdateRow(location.PageNumber, location.RowNumber,
                    new object?[] { 400, new string('b', 480) });
            }

            using (var db = Database.Open(tmp))
            {
                var rows = db.GetTable("t_overflow")!.Rows().ToList();
                Assert.Equal(400, rows.Count);
                Assert.Equal(new string('b', 480), rows.Single(r => Equals(r["id"], 400))["payload"]);
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void DeleteRow_after_overflow_relocation_succeeds()
    {
        string tmp = NewOverflowDatabase();
        try
        {
            using (var db = Database.Open(tmp, readOnly: false))
            {
                var t = db.GetTable("t_overflow")!;
                var location = Assert.Single(t.RowLocations());
                t.UpdateRow(location.PageNumber, location.RowNumber, new object?[] { 1, new string('x', 300) });
                t.DeleteRow(location.PageNumber, location.RowNumber);
            }

            using var check = Database.Open(tmp);
            Assert.Empty(check.GetTable("t_overflow")!.Rows());
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void DeleteRow_marks_row_deleted()
    {
        string tmp = TempCopy("generated/genEmpty.mdb");
        try
        {
            using (var db = Database.Open(tmp, readOnly: false))
            {
                var t = db.GetTable("t_empty")!;
                t.AddRow(new object?[] { null, "keep me" });
                t.AddRow(new object?[] { null, "delete me" });
            }

            using (var db = Database.Open(tmp, readOnly: false))
            {
                var t = db.GetTable("t_empty")!;
                var (page, rowNum) = FindRowId(t, "delete me");
                t.DeleteRow(page, rowNum);
            }

            using (var db = Database.Open(tmp))
            {
                var t = db.GetTable("t_empty")!;
                Assert.Equal(1, t.RowCount);
                var rows = t.Rows().ToList();
                var single = Assert.Single(rows);
                Assert.Equal("keep me", single["name"]);
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void AddRow_to_readonly_database_throws()
    {
        string tmp = TempCopy("generated/genEmpty.mdb");
        try
        {
            using var db = Database.Open(tmp);
            var t = db.GetTable("t_empty")!;
            Assert.Throws<DatabaseException>(() => t.AddRow(new object?[] { null, "x" }));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    /// <summary>finds the physical (page, row number) of the first row with the given name</summary>
    private static (int Page, int RowNumber) FindRowId(Table table, string name)
    {
        var pageCursor = table.OwnedPages.Cursor();
        while (true)
        {
            int page = pageCursor.GetNextPage();
            if (page < 0)
            {
                break;
            }
            byte[] buffer = new byte[table.Database.Format.PageSize];
            table.Database.PageChannel.ReadPage(buffer, page);
            int rowsOnPage = Table.GetRowsOnDataPage(buffer, table.Database.Format);
            for (int r = 0; r < rowsOnPage; r++)
            {
                Row? row = table.GetRow(page, r);
                if (row != null && Equals(row["name"], name))
                {
                    return (page, r);
                }
            }
        }
        throw new InvalidOperationException($"row '{name}' not found");
    }

    private static string NewOverflowDatabase()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_overflow_{Guid.NewGuid():N}.mdb");
        using var db = Database.Create(tmp);
        var table = db.CreateTable("t_overflow", new[]
        {
            new ColumnBuilder("id", DataType.Long),
            new ColumnBuilder("payload", DataType.Text).WithLength(1000),
        });
        table.AddRow(new object?[] { 1, "seed" });
        return tmp;
    }
}
