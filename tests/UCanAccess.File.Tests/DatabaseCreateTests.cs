using UCanAccess.File;
using Xunit;
using Xunit.Abstractions;

namespace UCanAccess.File.Tests;

/// <summary>
/// P0.2: creating a new (empty) Jet 4 database file with <see cref="Database.Create"/>.
/// The result must be openable by the port and readable by the ORIGINAL Jackcess.
/// </summary>
public class DatabaseCreateTests
{
    private readonly ITestOutputHelper _output;

    public DatabaseCreateTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Create_opens_empty_database()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_created_{Guid.NewGuid():N}.mdb");
        try
        {
            using (var db = Database.Create(tmp))
            {
                Assert.False(db.IsReadOnly);
                Assert.Empty(db.GetTableNames());
            }

            // a fresh open must work (the file is a valid database)
            using (var db = Database.Open(tmp))
            {
                Assert.Empty(db.GetTableNames());
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Created_database_is_readable_by_original_jackcess()
    {
        if (!JavaAvailable())
        {
            _output.WriteLine("SKIPPED: java not available");
            throw Xunit.Sdk.SkipException.ForSkip("java not available");
        }
        string? jackJar = FindJar("jackcess-5.1.5.jar");
        if (jackJar == null)
        {
            _output.WriteLine("SKIPPED: jackcess jar not found");
            throw Xunit.Sdk.SkipException.ForSkip("jackcess jar not found");
        }
        string classesDir = Path.Combine(FindRepoRoot(), "tools", "JavaOracle", "classes");
        if (!Directory.Exists(classesDir))
        {
            _output.WriteLine("SKIPPED: oracle classes not compiled");
            throw Xunit.Sdk.SkipException.ForSkip("oracle classes not compiled");
        }

        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_created_java_{Guid.NewGuid():N}.mdb");
        try
        {
            using (var db = Database.Create(tmp))
            {
                Assert.Empty(db.GetTableNames());
            }

            // DbDump exited 0 => the original Jackcess read the created file successfully
            string json = RunDbDump(jackJar, classesDir, tmp);
            _output.WriteLine(json);
            Assert.StartsWith("{", json);
            Assert.Contains("tables", json);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void CreateTable_then_add_rows_roundtrip()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_created_tbl_{Guid.NewGuid():N}.mdb");
        try
        {
            using (var db = Database.Create(tmp))
            {
                db.CreateTable("t_new",
                    new[]
                    {
                        new ColumnBuilder("id", DataType.Long).WithAutoNumber(true),
                        new ColumnBuilder("name", DataType.Text).WithLength(50),
                        new ColumnBuilder("value", DataType.Double),
                    },
                    new[]
                    {
                        new IndexBuilder("PrimaryKey").WithColumns("id").WithPrimaryKey(),
                    });

                var t = db.GetTable("t_new")!;
                Assert.Equal(3, t.Columns.Count);
                Assert.Equal(1, t.IndexCount);

                t.AddRow(new object?[] { null, "alpha", 1.5 });
                t.AddRow(new object?[] { null, "beta", 2.5 });
            }

            // a fresh open must read the table and rows back
            using (var db = Database.Open(tmp))
            {
                var t = db.GetTable("t_new")!;
                Assert.Equal(2, t.RowCount);
                var names = t.Rows().Select(r => (string)r["name"]!).ToList();
                Assert.Equal(new[] { "alpha", "beta" }, names);
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Required_columns_are_enforced_and_survive_reopen()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_required_{Guid.NewGuid():N}.mdb");
        try
        {
            using (var db = Database.Create(tmp))
            {
                var table = db.CreateTable("t_required", new[]
                {
                    new ColumnBuilder("id", DataType.Long).WithRequired(),
                    new ColumnBuilder("name", DataType.Text).WithLength(40),
                });

                Assert.True(table.Columns[0].Required);
                Assert.Throws<DatabaseException>(() => table.AddRow(new object?[] { null, "x" }));
                Assert.Throws<DatabaseException>(() => table.AddRow(new object?[] { DBNull.Value, "x" }));
                table.AddRow(new object?[] { 1, "x" });
                Assert.Throws<DatabaseException>(() => table.UpdateRow(
                    table.RowLocations().Single().PageNumber,
                    table.RowLocations().Single().RowNumber,
                    new object?[] { DBNull.Value, "changed" }));
            }

            using (var db = Database.Open(tmp))
            {
                var table = db.GetTable("t_required")!;
                Assert.True(table.Columns.Single(column => column.Name == "id").Required);
                Assert.Equal(1, table.RowCount);
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Created_table_with_indexes_is_readable_by_original_jackcess()
    {
        if (!JavaAvailable())
        {
            _output.WriteLine("SKIPPED: java not available");
            throw Xunit.Sdk.SkipException.ForSkip("java not available");
        }
        string? jackJar = FindJar("jackcess-5.1.5.jar");
        if (jackJar == null)
        {
            _output.WriteLine("SKIPPED: jackcess jar not found");
            throw Xunit.Sdk.SkipException.ForSkip("jackcess jar not found");
        }
        string classesDir = Path.Combine(FindRepoRoot(), "tools", "JavaOracle", "classes");
        if (!Directory.Exists(classesDir))
        {
            _output.WriteLine("SKIPPED: oracle classes not compiled");
            throw Xunit.Sdk.SkipException.ForSkip("oracle classes not compiled");
        }

        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_created_tbl_java_{Guid.NewGuid():N}.mdb");
        try
        {
            using (var db = Database.Create(tmp))
            {
                db.CreateTable("t_new",
                    new[]
                    {
                        new ColumnBuilder("id", DataType.Long).WithAutoNumber(true),
                        new ColumnBuilder("code", DataType.Text).WithLength(30),
                    },
                    new[]
                    {
                        new IndexBuilder("PrimaryKey").WithColumns("id").WithPrimaryKey(),
                        new IndexBuilder("idx_code").WithColumns("code").WithUnique(),
                    });

                var t = db.GetTable("t_new")!;
                t.AddRow(new object?[] { null, "AAA" });
                t.AddRow(new object?[] { null, "BBB" });
            }

            string json = RunDbDump(jackJar, classesDir, tmp);
            _output.WriteLine(json);
            Assert.Contains("t_new", json);
            Assert.Contains("AAA", json);
            Assert.Contains("BBB", json);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void CreateTable_with_memo_column_roundtrips()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_created_memo_{Guid.NewGuid():N}.mdb");
        try
        {
            using (var db = Database.Create(tmp))
            {
                db.CreateTable("t_notes",
                    new[]
                    {
                        new ColumnBuilder("id", DataType.Long).WithAutoNumber(true),
                        new ColumnBuilder("note", DataType.Memo),
                    });
                var t = db.GetTable("t_notes")!;
                t.AddRow(new object?[] { null, "some longer memo text that exceeds inline storage to force long value pages" });
                t.AddRow(new object?[] { null, "short" });
            }

            using (var db = Database.Open(tmp))
            {
                var t = db.GetTable("t_notes")!;
                Assert.Equal(2, t.RowCount);
                var notes = t.Rows().Select(r => (string)r["note"]!).ToList();
                Assert.Equal("some longer memo text that exceeds inline storage to force long value pages", notes[0]);
                Assert.Equal("short", notes[1]);
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void CreateTable_with_many_columns_spans_multiple_tdef_pages()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_created_wide_{Guid.NewGuid():N}.mdb");
        try
        {
            using (var db = Database.Create(tmp))
            {
                // ~200 columns pushes the table definition beyond one page
                var columns = new List<ColumnBuilder> { new ColumnBuilder("id", DataType.Long).WithAutoNumber(true) };
                for (int i = 0; i < 200; i++)
                {
                    columns.Add(new ColumnBuilder($"c{i:000}", DataType.Long));
                }
                db.CreateTable("t_wide", columns);

                var t = db.GetTable("t_wide")!;
                var row = new object?[201];
                row[0] = null;
                for (int i = 1; i < 201; i++)
                {
                    row[i] = i;
                }
                t.AddRow(row);
            }

            using (var db = Database.Open(tmp))
            {
                var t = db.GetTable("t_wide")!;
                Assert.Equal(201, t.Columns.Count);
                Assert.Equal(1, t.RowCount);
                var row = t.Rows().First();
                Assert.Equal(1, row["c000"]);
                Assert.Equal(43, row["c042"]);
                Assert.Equal(200, row["c199"]);
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    private static bool JavaAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("java", "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(15000);
            return p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string FindRepoRoot()
    {
        string? env = Environment.GetEnvironmentVariable("UCANACCESS_CSHARP_REPO");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
        {
            return env;
        }
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "tools", "JavaOracle")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private static string? FindJar(string name)
    {
        string[] candidates =
        {
            Path.Combine(Path.GetTempPath(), "ucanaccess-csharp-oracle", name),
        };
        foreach (string c in candidates)
        {
            if (System.IO.File.Exists(c))
            {
                return c;
            }
        }
        return null;
    }

    private static string RunDbDump(string jackJar, string classesDir, string mdbPath)
    {
        string outJson = Path.Combine(Path.GetTempPath(), $"ucanaccess_created_read_{Guid.NewGuid():N}.json");
        var psi = new System.Diagnostics.ProcessStartInfo("java")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = classesDir,
        };
        psi.ArgumentList.Add("-Duser.timezone=UTC");
        psi.ArgumentList.Add("-cp");
        psi.ArgumentList.Add($"{jackJar}{Path.PathSeparator}{classesDir}");
        psi.ArgumentList.Add("DbDump");
        psi.ArgumentList.Add(mdbPath);
        psi.ArgumentList.Add(outJson);
        using var p = System.Diagnostics.Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit(60000);
        Assert.True(p.ExitCode == 0, $"DbDump failed: {err}");
        string json = System.IO.File.ReadAllText(outJson);
        System.IO.File.Delete(outJson);
        return json;
    }
}
