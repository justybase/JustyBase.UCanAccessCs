using UCanAccess.File;
using Xunit;
using Xunit.Abstractions;

namespace UCanAccess.File.Tests;

/// <summary>
/// Exercises the B-tree page-split path: adding enough rows to overflow an index page
/// must split leaf pages and grow the tree (B5), producing a file the ORIGINAL Jackcess
/// can still read.
/// </summary>
public class IndexSplitTests
{
    private readonly ITestOutputHelper _output;

    public IndexSplitTests(ITestOutputHelper output) => _output = output;

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Overflowing_index_pages_splits_and_stays_sorted()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_split_{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Fixture("generated/genIndexed.mdb"), tmp, true);
        try
        {
            using (var db = Database.Open(tmp, readOnly: false))
            {
                var t = db.GetTable("t_indexed")!;
                // add enough rows to overflow a single index page
                for (int i = 0; i < 300; i++)
                {
                    t.AddRow(new object?[] { 1000 + i, $"lc{i:0000}", i * 0.25 });
                }
            }

            using (var db = Database.Open(tmp))
            {
                var t = db.GetTable("t_indexed")!;
                Assert.Equal(350, t.RowCount);

                IndexData pk = t.Indexes.First(i => i.Name == "PrimaryKey").IndexData;
                var pkEntries = CollectAllEntries(pk, db);
                Assert.Equal(350, pkEntries.Count);

                // entries must be strictly ascending
                for (int i = 1; i < pkEntries.Count; i++)
                {
                    Assert.True(IndexData.ByteCodeCompare(pkEntries[i - 1], pkEntries[i]) < 0, $"pk entry {i} out of order");
                }

                IndexData code = t.Indexes.First(i => i.Name == "idx_code").IndexData;
                var codeEntries = CollectAllEntries(code, db);
                Assert.Equal(350, codeEntries.Count);
                for (int i = 1; i < codeEntries.Count; i++)
                {
                    Assert.True(IndexData.ByteCodeCompare(codeEntries[i - 1], codeEntries[i]) < 0, $"code entry {i} out of order");
                }
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Split_index_is_readable_by_original_jackcess()
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

        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_split_java_{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Fixture("generated/genIndexed.mdb"), tmp, true);
        try
        {
            using (var db = Database.Open(tmp, readOnly: false))
            {
                var t = db.GetTable("t_indexed")!;
                for (int i = 0; i < 300; i++)
                {
                    t.AddRow(new object?[] { 1000 + i, $"lc{i:0000}", i * 0.25 });
                }
            }

            // the original Jackcess must be able to read the split file
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
            psi.ArgumentList.Add(tmp);
            psi.ArgumentList.Add(Path.Combine(Path.GetTempPath(), $"ucanaccess_split_out_{Guid.NewGuid():N}.json"));
            using var p = System.Diagnostics.Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(120000);
            Assert.True(p.ExitCode == 0, $"DbDump failed on split file: {err}");
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    /// <summary>walks the whole B-tree and collects every leaf entry</summary>
    private static List<byte[]> CollectAllEntries(IndexData idx, Database db)
    {
        var root = new byte[db.Format.PageSize];
        db.PageChannel.ReadPage(root, idx.RootPageNumber);
        var result = new List<byte[]>();
        CollectPage(root, db, result);
        return result;
    }

    private static void CollectPage(byte[] page, Database db, List<byte[]> result)
    {
        bool isLeaf = page[0] == PageTypes.IndexLeaf;
        var entries = IndexData.ReadEntries(page, db.Format);
        if (isLeaf)
        {
            foreach (IndexData.Entry e in entries)
            {
                result.Add(e.EntryBytes!);
            }
            return;
        }

        // node page: descend into each child, then into the child tail (if any)
        foreach (IndexData.Entry e in entries)
        {
            int child = e.SubPageNumber!.Value;
            var childPage = new byte[db.Format.PageSize];
            db.PageChannel.ReadPage(childPage, child);
            CollectPage(childPage, db, result);
        }

        int childTail = ByteUtil.GetIntLittleEndian(page, db.Format.OffsetChildTailIndexPage);
        if (childTail != IndexData.InvalidIndexPageNumber)
        {
            var tailPage = new byte[db.Format.PageSize];
            db.PageChannel.ReadPage(tailPage, childTail);
            CollectPage(tailPage, db, result);
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
}
