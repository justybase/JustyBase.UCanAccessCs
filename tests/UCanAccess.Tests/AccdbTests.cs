using System.Data.Common;
using System.Text.Json;
using Xunit;

namespace UCanAccess.Tests;

/// <summary>
/// ACCDB (.accdb, Jet 12/14/16) support: reads tables and executes SQL through the
/// same stack as .mdb, matching the Jackcess oracle.
/// </summary>
public class AccdbTests
{
    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static DbConnection Open(string fixture)
    {
        var conn = UCanAccessFactory.Instance.CreateConnection()!;
        conn.ConnectionString = $"Data Source={Fixture(fixture)};Read Only=true";
        conn.Open();
        return conn;
    }

    public static IEnumerable<object[]> AccdbFixtures()
    {
        yield return new object[] { "accdb2007.accdb", "VERSION_12" };
        yield return new object[] { "accdb2010.accdb", "VERSION_14" };
        yield return new object[] { "accdb2016.accdb", "VERSION_16" };
    }

    [Theory]
    [MemberData(nameof(AccdbFixtures))]
    public void Reads_tables_and_data(string fixture, string formatName)
    {
        using var conn = Open(fixture);
        var db = ((UCanAccess.UCanAccessConnection)conn).AccessDatabase;
        Assert.Equal(formatName, db.Format.Name);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM t_data";
            Assert.Equal(4L, cmd.ExecuteScalar());
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM t_grp";
            Assert.Equal(5L, cmd.ExecuteScalar());
        }
    }

    [Theory]
    [MemberData(nameof(AccdbFixtures))]
    public void Group_by_and_filters_work(string fixture, string _)
    {
        using var conn = Open(fixture);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT grp, Count(*) AS n FROM t_grp GROUP BY grp ORDER BY grp";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("A", reader.GetString(0));
            Assert.Equal(2L, reader.GetInt64(1));
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM t_data WHERE active = true ORDER BY id";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("Alpha", reader.GetString(0));
            Assert.True(reader.Read());
            Assert.Equal("Gamma", reader.GetString(0));
        }
    }

    [Fact]
    public void Data_matches_jackcess_oracle()
    {
        using var conn = Open("accdb2007.accdb");
        var db = ((UCanAccess.UCanAccessConnection)conn).AccessDatabase;
        var t = db.GetTable("t_data")!;
        var rows = t.Rows().Select(r => r.ToArray()).ToList();
        Assert.Equal(4, rows.Count);
        Assert.Equal(1, Convert.ToInt32(rows[0][0]));
        Assert.Equal("Alpha", rows[0][1]);
        Assert.Equal(true, rows[0][5]);
        Assert.Equal(false, rows[3][5]); // ACCDB: null boolean reads as false
    }

    [Fact]
    public void Create_and_write_accdb_readable_by_jackcess()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_accdb_{Guid.NewGuid():N}.accdb");
        try
        {
            using (var db = UCanAccess.File.Database.Create(tmp, version: "2007"))
            {
                var t = db.CreateTable("t_new", new[]
                {
                    new UCanAccess.File.ColumnBuilder("id", UCanAccess.File.DataType.Long).WithAutoNumber(),
                    new UCanAccess.File.ColumnBuilder("name", UCanAccess.File.DataType.Text).WithLength(40),
                });
                t.AddRow(new object?[] { null, "alpha" });
                t.AddRow(new object?[] { null, "beta" });
            }

            using (var db = UCanAccess.File.Database.Open(tmp))
            {
                Assert.Equal("VERSION_12", db.Format.Name);
                var t = db.GetTable("t_new")!;
                Assert.Equal(2, t.RowCount);
            }

            if (!JavaAvailable())
            {
                throw Xunit.Sdk.SkipException.ForSkip("round-trip check requires java");
            }
            string json = RunDbDump(tmp);
            Assert.Contains("t_new", json);
            Assert.Contains("alpha", json);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Connection_creates_new_accdb()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_accdb_new_{Guid.NewGuid():N}.accdb");
        try
        {
            var conn = UCanAccessFactory.Instance.CreateConnection()!;
            conn.ConnectionString = $"Data Source={tmp};Read Only=false;New Database Version=2007";
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "CREATE TABLE t_x (id INTEGER PRIMARY KEY, name TEXT(10))";
                cmd.ExecuteNonQuery();
            }
            conn.Dispose();

            using (var db = UCanAccess.File.Database.Open(tmp))
            {
                Assert.Equal("VERSION_12", db.Format.Name);
                Assert.Contains("t_x", db.GetTableNames());
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Calculated_column_reads_stored_value()
    {
        using var conn = Open("accdb2016calc.accdb");
        var db = ((UCanAccess.UCanAccessConnection)conn).AccessDatabase;
        var t = db.GetTable("t_people")!;
        UCanAccess.File.Column calc = t.Columns.First(c => c.Name.Equals("AGE_TIMES_2", StringComparison.OrdinalIgnoreCase));
        Assert.True(calc.Calculated);
        Assert.Equal(UCanAccess.File.DataType.Long, calc.Type);

        var values = t.Rows().Select(r => r[calc.ColumnIndex]).ToList();
        Assert.Equal(3, values.Count);
        Assert.Equal(68, Convert.ToInt32(values[0])); // 34 * 2
        Assert.Equal(82, Convert.ToInt32(values[1])); // 41 * 2
        Assert.Equal(58, Convert.ToInt32(values[2])); // 29 * 2

        // the SQL layer sees the same values
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT AGE_TIMES_2 FROM t_people ORDER BY id";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(68L, reader.GetInt64(0));
            Assert.True(reader.Read());
            Assert.Equal(82L, reader.GetInt64(0));
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM t_people WHERE AGE_TIMES_2 > 60";
            Assert.Equal(2L, cmd.ExecuteScalar());
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

    private static string RunDbDump(string mdbPath)
    {
        string jackJar = Path.Combine(Path.GetTempPath(), "ucanaccess-csharp-oracle", "jackcess-5.1.5.jar");
        string classesDir = Path.Combine(RepoRoot(), "tools", "JavaOracle", "classes");
        string outJson = Path.Combine(Path.GetTempPath(), $"ucanaccess_accdb_read_{Guid.NewGuid():N}.json");
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
}
