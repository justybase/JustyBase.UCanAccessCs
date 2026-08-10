using System.Data;
using System.Data.Common;
using Xunit;

namespace UCanAccess.Tests;

/// <summary>
/// SQL data-definition statements (CREATE / DROP / ALTER) through the ADO.NET provider.
/// Writes go to the MDB file; the Java cross-check verifies the file stays readable
/// by the original Jackcess after the DDL.
/// </summary>
public class SqlDdlTests
{
    private readonly ITestOutputHelper _output;

    public SqlDdlTests(ITestOutputHelper output) => _output = output;

    static SqlDdlTests()
    {
        DbProviderFactories.RegisterFactory("UCanAccess", UCanAccessFactory.Instance);
        DbProviderFactories.RegisterFactory("UCanAccess.UCanAccessFactory", UCanAccessFactory.Instance);
    }

    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static DbConnection OpenWritable(string tmp)
    {
        var conn = DbProviderFactories.GetFactory("UCanAccess")?.CreateConnection()
            ?? throw new InvalidOperationException("provider not registered");
        conn.ConnectionString = $"Data Source={tmp};Read Only=false";
        conn.Open();
        return conn;
    }

    private static string TempCopy(string fixture)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_ddl_{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(fixture, tmp, true);
        return tmp;
    }

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    [Fact]
    public void Create_table_then_insert_select()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE t_new (id LONG INTEGER PRIMARY KEY, name TEXT(20), amount MONEY, active BIT, created DATETIME)");
            Exec(conn, "INSERT INTO t_new (id, name, amount, active, created) VALUES (1, 'alpha', 12.50, true, #1/2/2023#)");
            Exec(conn, "INSERT INTO t_new (id, name, amount, active, created) VALUES (2, 'beta', -3.25, false, #2/3/2024#)");

            Assert.Equal(2L, Scalar(conn, "SELECT count(*) FROM t_new"));
            Assert.Equal("alpha", Scalar(conn, "SELECT name FROM t_new WHERE id = 1"));
            Assert.Equal(true, Scalar(conn, "SELECT active FROM t_new WHERE id = 1"));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Create_table_as_select_copies_schema_and_data()
    {
        string tmp = TempCopy(Fixture("sqljoin.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE t_ctas AS SELECT id, name, budget FROM t_master WHERE id <= 2 WITH DATA");

            Assert.Equal(2L, Scalar(conn, "SELECT count(*) FROM t_ctas"));
            Assert.Equal("Alpha", Scalar(conn, "SELECT name FROM t_ctas WHERE id = 1"));
            Assert.Equal(3, conn.GetSchema("Columns", new string?[] { null, null, "t_ctas", null }).Rows.Count);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Create_table_as_select_with_no_data_copies_only_schema()
    {
        string tmp = TempCopy(Fixture("sqljoin.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE t_ctas_empty AS SELECT id, name FROM t_master WITH NO DATA");
            Assert.Equal(0L, Scalar(conn, "SELECT count(*) FROM t_ctas_empty"));
            Assert.Equal(2, conn.GetSchema("Columns", new string?[] { null, null, "t_ctas_empty", null }).Rows.Count);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Create_table_not_null_is_enforced_and_reported_in_schema()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                Exec(conn, "CREATE TABLE t_required (id INTEGER NOT NULL, name TEXT(20))");
                Assert.ThrowsAny<Exception>(() => Exec(conn,
                    "INSERT INTO t_required (id, name) VALUES (NULL, 'bad')"));
                Exec(conn, "INSERT INTO t_required (id, name) VALUES (1, 'ok')");

                var fileDb = ((UCanAccessConnection)conn).AccessDatabase;
                Assert.True(fileDb.GetTable("t_required")!.Columns.Single(c => c.Name == "id").Required);
                DataTable columns = conn.GetSchema("Columns", new string?[] { null, null, "t_required", "id" });
                Assert.Equal("NO", columns.Rows[0]["IS_NULLABLE"]);
            }

            using (var conn = OpenWritable(tmp))
            {
                Assert.Equal(1L, Scalar(conn, "SELECT count(*) FROM t_required"));
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Add_not_null_column_to_nonempty_table_is_rejected_before_rewrite()
    {
        string tmp = TempCopy(Fixture("generated/genIndexed.mdb"));
        try
        {
            byte[] before = System.IO.File.ReadAllBytes(tmp);
            using (var conn = OpenWritable(tmp))
            {
                Assert.Throws<NotSupportedException>(() => Exec(conn,
                    "ALTER TABLE t_indexed ADD COLUMN required_note TEXT(20) NOT NULL"));
            }
            Assert.Equal(before, System.IO.File.ReadAllBytes(tmp));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Drop_middle_column_preserves_the_other_values()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE t_columns (a INTEGER, b TEXT(20), c INTEGER)");
            Exec(conn, "INSERT INTO t_columns (a, b, c) VALUES (1, 'middle', 3)");
            Exec(conn, "ALTER TABLE t_columns DROP COLUMN b");

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT a, c FROM t_columns";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(3L, reader.GetInt64(1));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Create_table_with_autonumber_and_primary_key()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE t_ids (id COUNTER PRIMARY KEY, val TEXT(10))");
            Exec(conn, "INSERT INTO t_ids (val) VALUES ('a')");
            Exec(conn, "INSERT INTO t_ids (val) VALUES ('b')");

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id FROM t_ids ORDER BY id";
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.True(reader.Read());
                Assert.Equal(2L, reader.GetInt64(0));
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Drop_table_removes_it()
    {
        string tmp = TempCopy(Fixture("sqljoin.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "DROP TABLE t_detail");

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM t_master";
                Assert.Equal(7L, cmd.ExecuteScalar());
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM t_detail";
                Assert.ThrowsAny<Exception>(() => cmd.ExecuteScalar());
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Jackcess_reads_created_table()
    {
        if (!JavaAvailable() || FindJar("jackcess-5.1.5.jar") == null
            || !Directory.Exists(Path.Combine(FindRepoRoot(), "tools", "JavaOracle", "classes")))
        {
            _output.WriteLine("SKIPPED: java/jar/classes not available");
            throw Xunit.Sdk.SkipException.ForSkip("java/jar/classes not available");
        }
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                Exec(conn, "CREATE TABLE t_created (id INTEGER PRIMARY KEY, name TEXT(20) NOT NULL, amount MONEY)");
                Exec(conn, "INSERT INTO t_created (id, name, amount) VALUES (1, 'alpha', 12.50)");
            }

            string json = RunDbDump(tmp);
            _output.WriteLine(json);
            Assert.Contains("t_created", json);
            Assert.Contains("alpha", json);
            Assert.Contains("\"name\"", json);
            Assert.Contains("\"required\": true", json);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Jackcess_reads_after_drop_table()
    {
        if (!JavaAvailable() || FindJar("jackcess-5.1.5.jar") == null
            || !Directory.Exists(Path.Combine(FindRepoRoot(), "tools", "JavaOracle", "classes")))
        {
            _output.WriteLine("SKIPPED: java/jar/classes not available");
            throw Xunit.Sdk.SkipException.ForSkip("java/jar/classes not available");
        }
        string tmp = TempCopy(Fixture("sqljoin.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                Exec(conn, "DROP TABLE t_detail");
            }

            string json = RunDbDump(tmp);
            _output.WriteLine(json);
            Assert.DoesNotContain("t_detail", json);
            Assert.Contains("t_master", json);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Create_index_on_existing_table()
    {
        string tmp = TempCopy(Fixture("generated/genIndexed.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                var beforeLocations = ((UCanAccessConnection)conn).AccessDatabase
                    .GetTable("t_indexed")!.RowLocations()
                    .Select(location => (location.PageNumber, location.RowNumber)).ToList();
                Exec(conn, "CREATE INDEX idx_val ON t_indexed (value)");

                var db = ((UCanAccess.UCanAccessConnection)conn).AccessDatabase;
                var names = db.GetIndexNames("t_indexed");
                Assert.Contains("idx_val", names);
                Assert.Contains("idx_code", names);
                Assert.Contains("PrimaryKey", names);
                var afterLocations = db.GetTable("t_indexed")!.RowLocations()
                    .Select(location => (location.PageNumber, location.RowNumber)).ToList();
                Assert.Equal(beforeLocations, afterLocations);
            }
            using (var conn = Open("tmp", tmp))
            {
                Assert.Equal(50L, Scalar(conn, "SELECT count(*) FROM t_indexed"));
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Drop_index_on_existing_table()
    {
        string tmp = TempCopy(Fixture("generated/genIndexed.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                var beforeLocations = ((UCanAccessConnection)conn).AccessDatabase
                    .GetTable("t_indexed")!.RowLocations()
                    .Select(location => (location.PageNumber, location.RowNumber)).ToList();
                Exec(conn, "DROP INDEX idx_code ON t_indexed");

                var db = ((UCanAccess.UCanAccessConnection)conn).AccessDatabase;
                var names = db.GetIndexNames("t_indexed");
                Assert.DoesNotContain("idx_code", names);
                Assert.Contains("PrimaryKey", names);
                var afterLocations = db.GetTable("t_indexed")!.RowLocations()
                    .Select(location => (location.PageNumber, location.RowNumber)).ToList();
                Assert.Equal(beforeLocations, afterLocations);
            }
            using (var conn = Open("tmp", tmp))
            {
                Assert.Equal(50L, Scalar(conn, "SELECT count(*) FROM t_indexed"));
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Create_unique_index_prefix_is_supported()
    {
        string tmp = TempCopy(Fixture("generated/genIndexed.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE UNIQUE INDEX idx_value_unique ON t_indexed (value)");

            var index = ((UCanAccess.UCanAccessConnection)conn).AccessDatabase
                .GetIndexInfo("t_indexed")
                .Single(info => info.Name.Equals("idx_value_unique", StringComparison.OrdinalIgnoreCase));
            Assert.True(index.Unique);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Failed_index_mutation_leaves_original_bytes_untouched()
    {
        string tmp = TempCopy(Fixture("generated/genIndexed.mdb"));
        try
        {
            byte[] before = System.IO.File.ReadAllBytes(tmp);
            using (var conn = OpenWritable(tmp))
            {
                Assert.ThrowsAny<Exception>(() => Exec(conn,
                    "CREATE INDEX idx_missing ON t_indexed (does_not_exist)"));
            }
            Assert.Equal(before, System.IO.File.ReadAllBytes(tmp));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Jackcess_reads_file_after_create_index()
    {
        if (!JavaAvailable() || FindJar("jackcess-5.1.5.jar") == null
            || !Directory.Exists(Path.Combine(FindRepoRoot(), "tools", "JavaOracle", "classes")))
        {
            _output.WriteLine("SKIPPED: java/jar/classes not available");
            throw Xunit.Sdk.SkipException.ForSkip("java/jar/classes not available");
        }
        string tmp = TempCopy(Fixture("generated/genIndexed.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                Exec(conn, "CREATE INDEX idx_value_desc ON t_indexed (value DESC)");
            }

            string json = RunDbDump(tmp);
            _output.WriteLine(json);
            Assert.Contains("t_indexed", json);
            Assert.Contains("code01", json);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Alter_table_add_and_drop_column()
    {
        string tmp = TempCopy(Fixture("generated/genIndexed.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                Exec(conn, "ALTER TABLE t_indexed ADD COLUMN note TEXT(20)");
                Exec(conn, "UPDATE t_indexed SET note = 'n' WHERE id = 1");

                Assert.Equal("n", Scalar(conn, "SELECT note FROM t_indexed WHERE id = 1"));
                Assert.Equal(49L, Scalar(conn, "SELECT count(*) FROM t_indexed WHERE note IS NULL"));

                Exec(conn, "ALTER TABLE t_indexed DROP COLUMN note");
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT count(*) FROM t_indexed";
                    Assert.Equal(50L, cmd.ExecuteScalar());
                }
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Jackcess_reads_file_after_alter_column()
    {
        if (!JavaAvailable() || FindJar("jackcess-5.1.5.jar") == null
            || !Directory.Exists(Path.Combine(FindRepoRoot(), "tools", "JavaOracle", "classes")))
        {
            _output.WriteLine("SKIPPED: java/jar/classes not available");
            throw Xunit.Sdk.SkipException.ForSkip("java/jar/classes not available");
        }
        string tmp = TempCopy(Fixture("generated/genIndexed.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                Exec(conn, "ALTER TABLE t_indexed ADD COLUMN extra MONEY");
            }

            string json = RunDbDump(tmp);
            _output.WriteLine(json);
            Assert.Contains("t_indexed", json);
            Assert.Contains("extra", json);
            Assert.Contains("code01", json);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    private static DbConnection Open(string name, string tmp)
    {
        var conn = DbProviderFactories.GetFactory("UCanAccess")?.CreateConnection()
            ?? throw new InvalidOperationException("provider not registered");
        conn.ConnectionString = $"Data Source={tmp};Read Only=true";
        conn.Open();
        return conn;
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
        string c = Path.Combine(Path.GetTempPath(), "ucanaccess-csharp-oracle", name);
        return System.IO.File.Exists(c) ? c : null;
    }

    private static string RunDbDump(string mdbPath)
    {
        string jackJar = FindJar("jackcess-5.1.5.jar")!;
        string classesDir = Path.Combine(FindRepoRoot(), "tools", "JavaOracle", "classes");
        string outJson = Path.Combine(Path.GetTempPath(), $"ucanaccess_ddl_read_{Guid.NewGuid():N}.json");
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
