using UCanAccess.File;
using Xunit;

namespace UCanAccess.File.Tests;

/// <summary>
/// End-to-end index maintenance (B5-B7): rows added/updated/deleted on an indexed
/// table must keep the B-tree index pages consistent. The resulting file is verified by
/// re-reading the index entries with the port, and (when a JDK is available) by having
/// the ORIGINAL Jackcess read the mutated file back.
/// </summary>
public class IndexWriteTests
{
    private readonly ITestOutputHelper _output;

    public IndexWriteTests(ITestOutputHelper output) => _output = output;

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static string Hex(byte[] bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));

    [Fact]
    public void Add_update_delete_keeps_index_consistent()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_idx_{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Fixture("generated/genIndexed.mdb"), tmp, true);
        try
        {
            // ---- port writes ----
            using (var db = Database.Open(tmp, readOnly: false))
            {
                var t = db.GetTable("t_indexed")!;
                t.AddRow(new object?[] { 51, "code51", 25.5 });
                t.AddRow(new object?[] { 52, "code52", 26.0 });

                var (page, rnum) = FindRowId(t, "code01");
                t.UpdateRow(page, rnum, new object?[] { 1, "code001", 0.5 });

                (page, rnum) = FindRowId(t, "code02");
                t.DeleteRow(page, rnum);
            }

            // ---- verify with the port ----
            using (var db = Database.Open(tmp))
            {
                var t = db.GetTable("t_indexed")!;
                Assert.Equal(51, t.RowCount); // 50 + 2 added - 1 deleted

                // primary key index
                IndexData pk = t.Indexes.First(i => i.Name == "PrimaryKey").IndexData;
                var pkBuf = new byte[db.Format.PageSize];
                db.PageChannel.ReadPage(pkBuf, pk.RootPageNumber);
                var pkEntries = IndexData.ReadEntries(pkBuf, db.Format);
                Assert.Equal(51, pkEntries.Count);
                // first entry id=1 (unchanged by update), last entry id=52
                Assert.Equal(pk.CreateEntryBytes(new object?[] { 1 }), pkEntries[0].EntryBytes);
                Assert.Equal(pk.CreateEntryBytes(new object?[] { 52 }), pkEntries[^1].EntryBytes);
                // id=2 must be gone
                Assert.DoesNotContain(pkEntries, e => e.EntryBytes!.SequenceEqual(pk.CreateEntryBytes(new object?[] { 2 })));
                Assert.Equal(52, pk.UniqueEntryCount);

                // code index
                IndexData code = t.Indexes.First(i => i.Name == "idx_code").IndexData;
                var codeBuf = new byte[db.Format.PageSize];
                db.PageChannel.ReadPage(codeBuf, code.RootPageNumber);
                var codeEntries = IndexData.ReadEntries(codeBuf, db.Format);
                Assert.Equal(51, codeEntries.Count);
                Assert.Equal(code.CreateEntryBytes(new object?[] { null, "code001", null }), codeEntries[0].EntryBytes);
                Assert.Equal(code.CreateEntryBytes(new object?[] { null, "code52", null }), codeEntries[^1].EntryBytes);
                Assert.DoesNotContain(codeEntries, e => e.EntryBytes!.SequenceEqual(code.CreateEntryBytes(new object?[] { null, "code02", null })));
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void AddRow_to_indexed_table_is_readable_by_original_jackcess()
    {
        if (!JavaAvailable())
        {
            _output.WriteLine("SKIPPED: java not available");
            throw Xunit.Sdk.SkipException.ForSkip("java not available");
        }
        string? jackJar = FindJar("jackcess-5.1.5.jar");
        if (jackJar == null)
        {
            _output.WriteLine("SKIPPED: jackcess jar not found (run tools/JavaOracle/run.ps1)");
            throw Xunit.Sdk.SkipException.ForSkip("jackcess jar not found (run tools/JavaOracle/run.ps1)");
        }
        string classesDir = Path.Combine(FindRepoRoot(), "tools", "JavaOracle", "classes");
        if (!Directory.Exists(classesDir))
        {
            _output.WriteLine("SKIPPED: oracle classes not compiled (run tools/JavaOracle/run.ps1)");
            throw Xunit.Sdk.SkipException.ForSkip("oracle classes not compiled (run tools/JavaOracle/run.ps1)");
        }

        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_idx_java_{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Fixture("generated/genIndexed.mdb"), tmp, true);
        try
        {
            using (var db = Database.Open(tmp, readOnly: false))
            {
                var t = db.GetTable("t_indexed")!;
                t.AddRow(new object?[] { 51, "code51", 25.5 });
                t.AddRow(new object?[] { 53, "code53", 26.5 });
                var (page, rnum) = FindRowId(t, "code03");
                t.DeleteRow(page, rnum);
            }

            string json = RunDbDump(jackJar, classesDir, tmp);
            _output.WriteLine(json);
            Assert.Contains("code51", json);
            Assert.Contains("code53", json);
            Assert.DoesNotContain("code03", json);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

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
                if (row != null && Equals(row["code"], name))
                {
                    return (page, r);
                }
            }
        }
        throw new InvalidOperationException($"row with code '{name}' not found");
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
        string outJson = Path.Combine(Path.GetTempPath(), $"ucanaccess_idx_read_{Guid.NewGuid():N}.json");
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
