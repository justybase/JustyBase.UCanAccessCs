using UCanAccess.File;
using Xunit;

namespace UCanAccess.File.Tests;

/// <summary>
/// P0.3: Jet 3 (Access 97) write support. The port writes to a real Access 97 file
/// (<c>size97.mdb</c>, GBK code page) and the ORIGINAL Jackcess reads the result back.
/// </summary>
public class Jet3WriteTests
{
    private readonly ITestOutputHelper _output;

    public Jet3WriteTests(ITestOutputHelper output) => _output = output;

    private static void ConfigureCodePages()
        => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Jet3_add_rows_roundtrip()
    {
        ConfigureCodePages();
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_j3_{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Fixture("size97.mdb"), tmp, true);
        try
        {
            System.Text.Encoding gbk = System.Text.Encoding.GetEncoding(936);
            using (var db = Database.Open(tmp, gbk, readOnly: false))
            {
                var t = db.GetTable("table1")!;
                t.AddRow(new object?[] { null, "hello" });
                t.AddRow(new object?[] { null, "world" });
            }

            using (var db = Database.Open(tmp, gbk))
            {
                var t = db.GetTable("table1")!;
                Assert.Equal(2, t.RowCount);
                var names = t.Rows().Select(r => (string)r["field1"]!).ToList();
                Assert.Equal(new[] { "hello", "world" }, names);
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Jet3_add_update_delete_readable_by_original_jackcess()
    {
        ConfigureCodePages();
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

        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_j3_java_{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Fixture("size97.mdb"), tmp, true);
        try
        {
            System.Text.Encoding gbk = System.Text.Encoding.GetEncoding(936);
            using (var db = Database.Open(tmp, gbk, readOnly: false))
            {
                var t = db.GetTable("table1")!;
                t.AddRow(new object?[] { null, "kasia" });
                t.AddRow(new object?[] { null, "ma kota" });

                var (page, rnum) = FindRowId(t, "kasia");
                t.UpdateRow(page, rnum, new object?[] { null, "kasia2" });

                (page, rnum) = FindRowId(t, "ma kota");
                t.DeleteRow(page, rnum);
            }

            string json = RunDbDump(jackJar, classesDir, tmp);
            _output.WriteLine(json);
            Assert.Contains("kasia2", json);
            Assert.DoesNotContain("ma kota", json);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    private static (int Page, int RowNumber) FindRowId(Table table, string field1)
    {
        foreach (Table.RowLocation location in table.RowLocations())
        {
            if (Equals(location.Row["field1"], field1))
            {
                return (location.PageNumber, location.RowNumber);
            }
        }
        throw new InvalidOperationException($"row with field1='{field1}' not found");
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
        string outJson = Path.Combine(Path.GetTempPath(), $"ucanaccess_j3_read_{Guid.NewGuid():N}.json");
        var psi = new System.Diagnostics.ProcessStartInfo("java")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = classesDir,
        };
        psi.ArgumentList.Add("-Duser.timezone=UTC");
        psi.ArgumentList.Add("-Djackcess.charset.VERSION_3=GBK");
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
