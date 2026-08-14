using System.Data.Common;
using System.Text.Json;
using Xunit;

namespace UCanAccess.Tests;

/// <summary>
/// DDL parity: the same DDL script is applied by this port and by the ORIGINAL
/// UCanAccess (via the DdlRunner Java tool), and the resulting schemas are compared.
/// </summary>
public class DdlParityTests
{
    private readonly ITestOutputHelper _output;

    public DdlParityTests(ITestOutputHelper output) => _output = output;

    static DdlParityTests()
    {
        DbProviderFactories.RegisterFactory("UCanAccess", UCanAccessFactory.Instance);
        DbProviderFactories.RegisterFactory("UCanAccess.UCanAccessFactory", UCanAccessFactory.Instance);
    }

    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static string TempCopy(string fixture)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_ddlp_{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(fixture, tmp, true);
        return tmp;
    }

    private static string RepoRoot()
    {
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

    private static string? FindJar(string name)
    {
        string c = Path.Combine(Path.GetTempPath(), "ucanaccess-csharp-oracle", name);
        return System.IO.File.Exists(c) ? c : null;
    }

    private static void ApplyViaPort(string mdbPath, string[] statements)
    {
        var conn = UCanAccessFactory.Instance.CreateConnection()!;
        conn.ConnectionString = $"Data Source={mdbPath};Read Only=false";
        conn.Open();
        try
        {
            foreach (string sql in statements)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }
        finally
        {
            conn.Dispose();
        }
    }

    private static void RunDdlRunner(string jackJar, string hsqldbJar, string ucaJar, string classesDir, string mdbPath, string scriptPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("java")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = classesDir,
        };
        psi.ArgumentList.Add("-Duser.timezone=UTC");
        psi.ArgumentList.Add("-cp");
        psi.ArgumentList.Add($"{jackJar}{Path.PathSeparator}{hsqldbJar}{Path.PathSeparator}{ucaJar}{Path.PathSeparator}{classesDir}");
        psi.ArgumentList.Add("DdlRunner");
        psi.ArgumentList.Add(mdbPath);
        psi.ArgumentList.Add(scriptPath);
        using var p = System.Diagnostics.Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit(60000);
        Assert.True(p.ExitCode == 0, $"DdlRunner failed: {err}");
    }

    private static string RunDbDump(string jackJar, string classesDir, string mdbPath)
    {
        string outJson = Path.Combine(Path.GetTempPath(), $"ucanaccess_ddlp_read_{Guid.NewGuid():N}.json");
        var psi = new System.Diagnostics.ProcessStartInfo("java")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = classesDir,
        };
        psi.ArgumentList.Add("-Duser.timezone=UTC");
        psi.ArgumentList.Add("-Duser.language=en");
        psi.ArgumentList.Add("-Duser.country=US");
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

    private static Dictionary<string, List<string>> ExtractColumns(string json)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        foreach (JsonElement table in doc.RootElement.GetProperty("tables").EnumerateArray())
        {
            string name = table.GetProperty("name").GetString()!;
            var cols = new List<string>();
            foreach (JsonElement col in table.GetProperty("columns").EnumerateArray())
            {
                cols.Add(string.Join("|",
                    col.GetProperty("name").GetString(),
                    col.GetProperty("type").GetString(),
                    col.GetProperty("length").GetInt32(),
                    col.GetProperty("autoNumber").GetBoolean(),
                    col.GetProperty("calculated").GetBoolean(),
                    col.GetProperty("precision").GetInt32(),
                    col.GetProperty("scale").GetInt32()));
            }
            result[name] = cols;
        }
        return result;
    }

    [Fact]
    public void Same_ddl_produces_same_schema_as_java()
    {
        if (!JavaAvailable() || FindJar("jackcess-5.1.5.jar") == null
            || FindJar("hsqldb-2.7.4.jar") == null || FindJar("ucanaccess-5.1.6.jar") == null
            || !Directory.Exists(Path.Combine(RepoRoot(), "tools", "JavaOracle", "classes")))
        {
            _output.WriteLine("SKIPPED: java/jars/classes not available");
            throw Xunit.Sdk.SkipException.ForSkip("java/jars/classes not available");
        }

        string[] statements =
        {
            "CREATE TABLE t_new (id INTEGER PRIMARY KEY, name VARCHAR(20), amount DECIMAL(18,2))",
            "INSERT INTO t_new (id, name, amount) VALUES (1, 'alpha', 10.50)",
            "CREATE INDEX idx_name ON t_new (name)",
        };

        string scriptPath = Path.Combine(Path.GetTempPath(), $"ucanaccess_ddlp_{Guid.NewGuid():N}.sql");
        System.IO.File.WriteAllLines(scriptPath, statements);

        string portCopy = TempCopy(Fixture("sqljoin.mdb"));
        string javaCopy = TempCopy(Fixture("sqljoin.mdb"));
        try
        {
            ApplyViaPort(portCopy, statements);

            string jackJar = FindJar("jackcess-5.1.5.jar")!;
            string hsqldbJar = FindJar("hsqldb-2.7.4.jar")!;
            string ucaJar = FindJar("ucanaccess-5.1.6.jar")!;
            string classesDir = Path.Combine(RepoRoot(), "tools", "JavaOracle", "classes");
            RunDdlRunner(jackJar, hsqldbJar, ucaJar, classesDir, javaCopy, scriptPath);

            string portJson = RunDbDump(jackJar, classesDir, portCopy);
            string javaJson = RunDbDump(jackJar, classesDir, javaCopy);
            _output.WriteLine(portJson);

            var portCols = ExtractColumns(portJson);
            var javaCols = ExtractColumns(javaJson);

            Assert.Contains("t_master", portCols.Keys);
            Assert.True(portCols.ContainsKey("t_new"), "port file is missing t_new");
            Assert.True(javaCols.ContainsKey("t_new"), "java file is missing t_new");
            Assert.Equal(javaCols["t_new"], portCols["t_new"]);
        }
        finally
        {
            System.IO.File.Delete(portCopy);
            System.IO.File.Delete(javaCopy);
            System.IO.File.Delete(scriptPath);
        }
    }

    private static List<string> ExtractRows(string json, string tableName)
    {
        var rows = new List<string>();
        using var doc = JsonDocument.Parse(json);
        foreach (JsonElement table in doc.RootElement.GetProperty("tables").EnumerateArray())
        {
            if (!table.GetProperty("name").GetString()!.Equals(tableName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (JsonElement row in table.GetProperty("rows").EnumerateArray())
            {
                rows.Add(JsonSerializer.Serialize(row));
            }
        }
        return rows;
    }

    [Fact]
    public void Disable_enable_autoincrement_produces_same_file_state_as_java()
    {
        if (!JavaAvailable() || FindJar("jackcess-5.1.5.jar") == null
            || FindJar("hsqldb-2.7.4.jar") == null || FindJar("ucanaccess-5.1.6.jar") == null
            || !Directory.Exists(Path.Combine(RepoRoot(), "tools", "JavaOracle", "classes")))
        {
            _output.WriteLine("SKIPPED: java/jars/classes not available");
            throw Xunit.Sdk.SkipException.ForSkip("java/jars/classes not available");
        }

        string[] statements =
        {
            // explicit value while AUTOINCREMENT is enabled: silently ignored by both
            "INSERT INTO t_detail (id, master_id, qty, price, dt, note, code) VALUES (500, 1, 7, 1.50, #1/1/2024#, 'explicit enabled', 'x01')",
            "DISABLE AUTOINCREMENT ON t_detail",
            "INSERT INTO t_detail (id, master_id, qty, price, dt, note, code) VALUES (501, 1, 8, 2.00, #1/2/2024#, 'explicit disabled', 'x02')",
            "ENABLE AUTOINCREMENT ON t_detail",
            "INSERT INTO t_detail (master_id, qty, price, dt, note, code) VALUES (1, 9, 3.00, #1/3/2024#, 'auto after enable', 'x03')",
        };

        string scriptPath = Path.Combine(Path.GetTempPath(), $"ucanaccess_ddlp_{Guid.NewGuid():N}.sql");
        System.IO.File.WriteAllLines(scriptPath, statements);

        string portCopy = TempCopy(Fixture("sqljoin.mdb"));
        string javaCopy = TempCopy(Fixture("sqljoin.mdb"));
        try
        {
            ApplyViaPort(portCopy, statements);

            string jackJar = FindJar("jackcess-5.1.5.jar")!;
            string hsqldbJar = FindJar("hsqldb-2.7.4.jar")!;
            string ucaJar = FindJar("ucanaccess-5.1.6.jar")!;
            string classesDir = Path.Combine(RepoRoot(), "tools", "JavaOracle", "classes");
            RunDdlRunner(jackJar, hsqldbJar, ucaJar, classesDir, javaCopy, scriptPath);

            string portJson = RunDbDump(jackJar, classesDir, portCopy);
            string javaJson = RunDbDump(jackJar, classesDir, javaCopy);

            var portRows = ExtractRows(portJson, "t_detail");
            var javaRows = ExtractRows(javaJson, "t_detail");
            Assert.Equal(javaRows.Count, portRows.Count);
            Assert.Equal(javaRows, portRows);
            Assert.Contains("\"explicit disabled\"", portJson);
            Assert.Contains("501", portJson);
            Assert.Contains("\"auto after enable\"", portJson);
        }
        finally
        {
            System.IO.File.Delete(portCopy);
            System.IO.File.Delete(javaCopy);
            System.IO.File.Delete(scriptPath);
        }
    }
}
