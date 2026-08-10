using UCanAccess.File;
using Xunit;
using Xunit.Abstractions;

namespace UCanAccess.File.Tests;

/// <summary>
/// Cross-checks the index entry encoding for EVERY supported data type against the
/// actual index pages written by the original Jackcess into
/// <c>genIndexedAllTypes.mdb</c> (B4 extension): for each of the 12 indexes the
/// port-encoded entry bytes must exactly match the bytes stored on the Java-written
/// leaf pages. Also verifies the descending index encoding.
/// </summary>
public class IndexAllTypesTests
{
    private readonly ITestOutputHelper _output;

    public IndexAllTypesTests(ITestOutputHelper output) => _output = output;

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static readonly string[] IndexNames =
    {
        "idx_b", "idx_i", "idx_l", "idx_l_desc", "idx_m", "idx_f",
        "idx_d", "idx_dt", "idx_bool", "idx_num", "idx_guid", "idx_txt",
    };

    [Fact]
    public void All_type_index_entries_match_java_written_pages()
    {
        using var db = Database.Open(Fixture("generated/genIndexedAllTypes.mdb"));
        var t = db.GetTable("t_idx_alltypes")!;

        var rows = ReadAllRows(t, db);
        Assert.Equal(5, rows.Count);

        foreach (string name in IndexNames)
        {
            IndexData idx = t.Indexes.First(i => i.Name == name).IndexData;
            var buf = new byte[db.Format.PageSize];
            db.PageChannel.ReadPage(buf, idx.RootPageNumber);
            var entries = IndexData.ReadEntries(buf, db.Format);

            // one entry per row (nulls are indexed for non-ignore-null indexes)
            Assert.Equal(rows.Count, entries.Count);

            IndexData.ColumnDescriptor col = Assert.Single(idx.ColumnDescriptors);
            int colIdx = col.Column.ColumnIndex;

            foreach (object?[] row in rows)
            {
                byte[] encoded = idx.CreateEntryBytes(row);
                bool found = entries.Any(e => e.EntryBytes!.SequenceEqual(encoded));
                Assert.True(found,
                    $"{name}: no parsed entry matches the encoding of {col.Column.Name}={row[colIdx]}");
            }
        }
    }

    [Fact]
    public void Descending_index_entries_are_reversed()
    {
        using var db = Database.Open(Fixture("generated/genIndexedAllTypes.mdb"));
        var t = db.GetTable("t_idx_alltypes")!;
        IndexData idx = t.Indexes.First(i => i.Name == "idx_l_desc").IndexData;
        var buf = new byte[db.Format.PageSize];
        db.PageChannel.ReadPage(buf, idx.RootPageNumber);
        var entries = IndexData.ReadEntries(buf, db.Format);

        // values 100..500 descending: the largest value must be the first entry
        byte[] enc500 = idx.CreateEntryBytes(new object?[] { null, null, 500, null, null, null, null, null, null, null, null });
        Assert.Equal(enc500, entries[0].EntryBytes);

        // entries must be strictly ascending in byte order (descending value order)
        for (int i = 1; i < entries.Count; i++)
        {
            Assert.True(IndexData.ByteCodeCompare(entries[i - 1].EntryBytes, entries[i].EntryBytes) < 0,
                $"desc entry {i} out of order");
        }
    }

    [Fact]
    public void Port_added_row_is_readable_by_original_jackcess()
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

        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_idx_all_{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Fixture("generated/genIndexedAllTypes.mdb"), tmp, true);
        try
        {
            using (var db = Database.Open(tmp, readOnly: false))
            {
                var t = db.GetTable("t_idx_alltypes")!;
                t.AddRow(new object?[] { (byte)6, (short)60, 600, 6.6m, 6.5f, 6.5, new DateTime(2024, 1, 1), false, 66.66m, "{66666666-6666-6666-6666-666666666666}", "delta" });
            }

            // the original Jackcess must read the new row back (all index types)
            string json = RunDbDump(jackJar, classesDir, tmp);
            _output.WriteLine(json);
            Assert.Contains("delta", json);
            Assert.Contains("66666666", json);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    private static List<object?[]> ReadAllRows(Table t, Database db)
    {
        var rows = new List<object?[]>();
        var pageCursor = t.OwnedPages.Cursor();
        while (true)
        {
            int page = pageCursor.GetNextPage();
            if (page < 0)
            {
                break;
            }
            var buffer = new byte[db.Format.PageSize];
            db.PageChannel.ReadPage(buffer, page);
            int n = Table.GetRowsOnDataPage(buffer, db.Format);
            for (int r = 0; r < n; r++)
            {
                Row? row = t.GetRow(page, r);
                if (row != null)
                {
                    rows.Add(row.ToArray());
                }
            }
        }
        return rows;
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
        string outJson = Path.Combine(Path.GetTempPath(), $"ucanaccess_all_read_{Guid.NewGuid():N}.json");
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
