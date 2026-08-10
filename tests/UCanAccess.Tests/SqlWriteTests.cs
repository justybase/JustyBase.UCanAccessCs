using System.Data;
using System.Data.Common;
using Xunit;

namespace UCanAccess.Tests;

/// <summary>
/// P0.1: SQL data-modification statements (INSERT / UPDATE / DELETE) through the
/// ADO.NET provider. Writes go to the MDB file; the SQLite mirror is refreshed so
/// subsequent SELECTs see the changes. The Java cross-check verifies the file is
/// still readable by the original Jackcess.
/// </summary>
public class SqlWriteTests
{
    private readonly ITestOutputHelper _output;

    public SqlWriteTests(ITestOutputHelper output) => _output = output;

    static SqlWriteTests()
    {
        DbProviderFactories.RegisterFactory("UCanAccess", UCanAccessFactory.Instance);
        DbProviderFactories.RegisterFactory("UCanAccess.UCanAccessFactory", UCanAccessFactory.Instance);
    }

    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static DbConnection OpenWritable(string tmp)
    {
        string cs = $"Data Source={tmp};Read Only=false";
        var conn = DbProviderFactories.GetFactory("UCanAccess")?.CreateConnection()
            ?? throw new InvalidOperationException("provider not registered");
        conn.ConnectionString = cs;
        conn.Open();
        return conn;
    }

    private static string TempCopy(string fixture)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_sql_{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(fixture, tmp, true);
        return tmp;
    }

    private static void Exec(DbConnection conn, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    [Fact]
    public void Insert_then_select_roundtrip()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "INSERT INTO t_empty (name) VALUES ('row one')");
            Exec(conn, "INSERT INTO t_empty (name) VALUES ('row two')");

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM t_empty";
                Assert.Equal(2L, cmd.ExecuteScalar());
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM t_empty ORDER BY id";
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal("row one", reader.GetString(0));
                Assert.True(reader.Read());
                Assert.Equal("row two", reader.GetString(0));
                Assert.False(reader.Read());
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Insert_select_roundtrip()
    {
        string tmp = TempCopy(Fixture("sqljoin.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            int affected;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO t_detail (master_id, qty, price, note, code) " +
                    "SELECT master_id, qty, price, note, code FROM t_detail WHERE master_id = 1";
                affected = cmd.ExecuteNonQuery();
            }
            Assert.Equal(4, affected);

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM t_detail";
                Assert.Equal(16L, cmd.ExecuteScalar());
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM t_detail WHERE master_id = 1 AND note = 'first item'";
                Assert.Equal(2L, cmd.ExecuteScalar());
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Insert_select_with_column_subset_and_autonumber()
    {
        string tmp = TempCopy(Fixture("sqljoin.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO t_detail (master_id, qty) SELECT id, qty FROM t_detail WHERE id <= 3";
                Assert.Equal(3, cmd.ExecuteNonQuery());
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM t_detail WHERE master_id IN (1,2,3) AND price IS NULL";
                Assert.Equal(3L, cmd.ExecuteScalar());
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Delete_with_in_subquery()
    {
        string tmp = TempCopy(Fixture("sqljoin.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "DELETE FROM t_detail WHERE master_id IN (SELECT id FROM t_master WHERE cat = 'B')";
                Assert.Equal(4, cmd.ExecuteNonQuery());
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM t_detail";
                Assert.Equal(8L, cmd.ExecuteScalar());
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Update_with_exists_subquery()
    {
        string tmp = TempCopy(Fixture("sqljoin.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE t_detail SET note = 'matched' WHERE EXISTS (SELECT 1 FROM t_master WHERE cat = 'A')";
                Assert.Equal(12, cmd.ExecuteNonQuery());
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM t_detail WHERE note = 'matched'";
                Assert.Equal(12L, cmd.ExecuteScalar());
            }
            // a subquery returning no rows matches nothing
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE t_detail SET note = 'nope' WHERE EXISTS (SELECT 1 FROM t_master WHERE cat = 'zzz')";
                Assert.Equal(0, cmd.ExecuteNonQuery());
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Update_with_set_expressions()
    {
        string tmp = TempCopy(Fixture("sqljoin.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE t_detail SET qty = qty + 1 WHERE master_id = 1";
                Assert.Equal(4, cmd.ExecuteNonQuery());
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT sum(qty) FROM t_detail WHERE master_id = 1";
                Assert.Equal(13L, cmd.ExecuteScalar());
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE t_detail SET price = price * 2 WHERE id = 1";
                Assert.Equal(1, cmd.ExecuteNonQuery());
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT price FROM t_detail WHERE id = 1";
                Assert.Equal(21.0, Convert.ToDouble(cmd.ExecuteScalar()), 4);
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE t_detail SET note = 'id' & id WHERE id = 3";
                Assert.Equal(1, cmd.ExecuteNonQuery());
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT note FROM t_detail WHERE id = 3";
                Assert.Equal("id3", cmd.ExecuteScalar());
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Update_with_where()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "INSERT INTO t_empty (name) VALUES ('alpha')");
            Exec(conn, "INSERT INTO t_empty (name) VALUES ('beta')");
            Exec(conn, "INSERT INTO t_empty (name) VALUES ('gamma')");

            int affected;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE t_empty SET name = 'BETA2' WHERE name = 'beta'";
                affected = cmd.ExecuteNonQuery();
            }
            Assert.Equal(1, affected);

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM t_empty WHERE name = 'BETA2'";
                Assert.Equal(1L, cmd.ExecuteScalar());
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Delete_with_where_and_all()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "INSERT INTO t_empty (name) VALUES ('keep')");
            Exec(conn, "INSERT INTO t_empty (name) VALUES ('remove')");

            int affected;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM t_empty WHERE name = 'remove'";
                affected = cmd.ExecuteNonQuery();
            }
            Assert.Equal(1, affected);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM t_empty";
                Assert.Equal(1L, cmd.ExecuteScalar());
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM t_empty";
                affected = cmd.ExecuteNonQuery();
            }
            Assert.Equal(1, affected);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM t_empty";
                Assert.Equal(0L, cmd.ExecuteScalar());
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Parameterized_insert_and_delete()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "INSERT INTO t_empty (name) VALUES (@name)", ("@name", "param one"));
            Exec(conn, "INSERT INTO t_empty (name) VALUES (@name)", ("@name", "param two"));

            int affected;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM t_empty WHERE name = @name";
                var p = cmd.CreateParameter();
                p.ParameterName = "@name";
                p.Value = "param one";
                cmd.Parameters.Add(p);
                affected = cmd.ExecuteNonQuery();
            }
            Assert.Equal(1, affected);
            Assert.Equal(1L, Scalar(conn, "SELECT count(*) FROM t_empty"));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Where_clause_operators()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "INSERT INTO t_empty (name) VALUES ('abc')");
            Exec(conn, "INSERT INTO t_empty (name) VALUES ('xyz')");
            Exec(conn, "INSERT INTO t_empty (name) VALUES ('abcxyz')");

            // LIKE
            Exec(conn, "DELETE FROM t_empty WHERE name LIKE 'abc%'");
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM t_empty";
                Assert.Equal(1L, cmd.ExecuteScalar()); // only 'xyz' remains
            }

            // IN
            Exec(conn, "INSERT INTO t_empty (name) VALUES ('in1')");
            Exec(conn, "INSERT INTO t_empty (name) VALUES ('in2')");
            Exec(conn, "DELETE FROM t_empty WHERE name IN ('in1', 'in2')");
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM t_empty";
                Assert.Equal(1L, cmd.ExecuteScalar());
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Select_before_write_then_refresh()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            // create the mirror first via SELECT
            Assert.Equal(0L, Scalar(conn, "SELECT count(*) FROM t_empty"));

            Exec(conn, "INSERT INTO t_empty (name) VALUES ('after mirror')");

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM t_empty";
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal("after mirror", reader.GetString(0));
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Jackcess_reads_sql_written_file()
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

        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                Exec(conn, "INSERT INTO t_empty (name) VALUES ('sql written')");
                Exec(conn, "INSERT INTO t_empty (name) VALUES ('second')");
                Exec(conn, "UPDATE t_empty SET name = 'updated' WHERE name = 'second'");
            }

            string json = RunDbDump(jackJar, classesDir, tmp);
            _output.WriteLine(json);
            Assert.Contains("sql written", json);
            Assert.Contains("updated", json);
            Assert.DoesNotContain("second", json);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Jackcess_reads_insert_select_written_file()
    {
        if (!JavaAvailable())
        {
            _output.WriteLine("SKIPPED: java not available");
            throw Xunit.Sdk.SkipException.ForSkip("java not available");
        }
        string? jackJar = FindJar("jackcess-5.1.5.jar");
        if (jackJar == null || !Directory.Exists(Path.Combine(FindRepoRoot(), "tools", "JavaOracle", "classes")))
        {
            _output.WriteLine("SKIPPED: oracle classes/jar not found");
            throw Xunit.Sdk.SkipException.ForSkip("oracle classes/jar not found");
        }

        string tmp = TempCopy(Fixture("sqljoin.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO t_detail (master_id, qty, price, note, code) " +
                    "SELECT master_id, qty, price, note & 'X', code FROM t_detail WHERE id = 1";
                Assert.Equal(1, cmd.ExecuteNonQuery());
            }

            string json = RunDbDump(jackJar, Path.Combine(FindRepoRoot(), "tools", "JavaOracle", "classes"), tmp);
            _output.WriteLine(json);
            Assert.Contains("first itemX", json);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Parameterized_update_with_set_and_where_parameters()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "INSERT INTO t_empty (name) VALUES ('aaa')");
            Exec(conn, "INSERT INTO t_empty (name) VALUES ('bbb')");

            // SET uses parameter 1, WHERE uses parameter 2
            int affected;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE t_empty SET name = ? WHERE name = ?";
                cmd.Parameters.Add(CreateParameter(cmd, "renamed"));
                cmd.Parameters.Add(CreateParameter(cmd, "bbb"));
                affected = cmd.ExecuteNonQuery();
            }
            Assert.Equal(1, affected);
            Assert.Equal(1L, Scalar(conn, "SELECT count(*) FROM t_empty WHERE name = 'renamed'"));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    private static System.Data.Common.DbParameter CreateParameter(System.Data.Common.DbCommand cmd, object? value)
    {
        var p = cmd.CreateParameter();
        p.Value = value ?? DBNull.Value;
        return p;
    }

    [Fact]
    public void Transaction_rollback_discards_writes()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "INSERT INTO t_empty (name) VALUES ('in tx')";
                        Assert.Equal(1, cmd.ExecuteNonQuery());
                    }
                    tx.Rollback();
                }
                // after rollback the row must not exist
                Assert.Equal(0L, Scalar(conn, "SELECT count(*) FROM t_empty"));
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Transaction_commit_applies_writes()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "INSERT INTO t_empty (name) VALUES ('committed')";
                        Assert.Equal(1, cmd.ExecuteNonQuery());
                    }
                    tx.Commit();
                }
                Assert.Equal(1L, Scalar(conn, "SELECT count(*) FROM t_empty"));
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Transaction_reads_its_own_writes_before_commit()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            using (var tx = conn.BeginTransaction())
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO t_empty (name) VALUES ('visible in tx')";
                Assert.Equal(1, cmd.ExecuteNonQuery());

                Assert.Equal(1L, Scalar(conn, "SELECT count(*) FROM t_empty"));
                tx.Rollback();
            }
            Assert.Equal(0L, Scalar(conn, "SELECT count(*) FROM t_empty"));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Transaction_savepoint_rolls_back_only_later_writes()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            using (var tx = conn.BeginTransaction())
            {
                using (var first = conn.CreateCommand())
                {
                    first.Transaction = tx;
                    first.CommandText = "INSERT INTO t_empty (name) VALUES ('before savepoint')";
                    Assert.Equal(1, first.ExecuteNonQuery());
                }

                tx.Save("after-first");

                using (var second = conn.CreateCommand())
                {
                    second.Transaction = tx;
                    second.CommandText = "INSERT INTO t_empty (name) VALUES ('after savepoint')";
                    Assert.Equal(1, second.ExecuteNonQuery());
                }
                Assert.Equal(2L, Scalar(conn, "SELECT count(*) FROM t_empty"));

                tx.Rollback("after-first");
                Assert.Equal(1L, Scalar(conn, "SELECT count(*) FROM t_empty"));
                Assert.Equal("before savepoint", Scalar(conn, "SELECT name FROM t_empty"));
                tx.Commit();
            }

            Assert.Equal(1L, Scalar(conn, "SELECT count(*) FROM t_empty"));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Dml_named_parameter_repeated_occurrences_bind_once()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "INSERT INTO t_empty (name) VALUES ('old')");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE t_empty SET name = @value WHERE name = @value";
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = "value";
            parameter.Value = "new";
            cmd.Parameters.Add(parameter);
            // no row matches, but the repeated named parameter must still be bound
            Assert.Equal(0, cmd.ExecuteNonQuery());
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Transaction_with_invalid_statement_commits_nothing()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "INSERT INTO t_empty (name) VALUES ('should not persist')";
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "INSERT INTO no_such_table (x) VALUES (1)";
                        cmd.ExecuteNonQuery();
                    }
                    Assert.ThrowsAny<Exception>(() => tx.Commit());
                }
                // the valid insert must NOT have been applied (all-or-nothing)
                Assert.Equal(0L, Scalar(conn, "SELECT count(*) FROM t_empty"));
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Transaction_with_ddl_commits_all()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "CREATE TABLE t_tx (id INTEGER PRIMARY KEY, name TEXT(20))";
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "INSERT INTO t_tx (id, name) VALUES (1, 'a')";
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                Assert.Equal(1L, Scalar(conn, "SELECT count(*) FROM t_tx"));
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Batch_multiple_statements_execute_in_order()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO t_empty (name) VALUES ('a'); " +
                    "INSERT INTO t_empty (name) VALUES ('b'); " +
                    "UPDATE t_empty SET name = 'A2' WHERE name = 'a'; " +
                    "DELETE FROM t_empty WHERE name = 'zzz'";
                Assert.Equal(3, cmd.ExecuteNonQuery());
            }
            Assert.Equal(2L, Scalar(conn, "SELECT count(*) FROM t_empty"));
            Assert.Equal(1L, Scalar(conn, "SELECT count(*) FROM t_empty WHERE name = 'A2'"));
            Assert.Equal(1L, Scalar(conn, "SELECT count(*) FROM t_empty WHERE name = 'b'"));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Batch_split_respects_semicolon_inside_strings()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO t_empty (name) VALUES ('a;b'); INSERT INTO t_empty (name) VALUES ('c')";
                Assert.Equal(2, cmd.ExecuteNonQuery());
            }
            Assert.Equal(1L, Scalar(conn, "SELECT count(*) FROM t_empty WHERE name = 'a;b'"));
            Assert.Equal(1L, Scalar(conn, "SELECT count(*) FROM t_empty WHERE name = 'c'"));
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
        string outJson = Path.Combine(Path.GetTempPath(), $"ucanaccess_sql_read_{Guid.NewGuid():N}.json");
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
