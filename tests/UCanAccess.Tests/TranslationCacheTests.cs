using System.Data.Common;
using Xunit;

namespace UCanAccess.Tests;

/// <summary>
/// Translation cache: repeated commands reuse the translated statement, and
/// schema changes invalidate cached translations.
/// </summary>
public sealed class TranslationCacheTests
{
    private static DbConnection OpenWritable(string path)
    {
        var conn = UCanAccessFactory.Instance.CreateConnection()!;
        conn.ConnectionString = $"Data Source={path};Read Only=false";
        conn.Open();
        return conn;
    }

    private static object? Scalar(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Repeated_execution_returns_stable_results()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uca-trcache-{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "genEmpty.mdb"), path, true);
        try
        {
            using var conn = OpenWritable(path);
            Exec(conn, "CREATE TABLE t_cache (id LONG PRIMARY KEY, amount MONEY)");
            Exec(conn, "INSERT INTO t_cache (id, amount) VALUES (1, 10.50)");
            object? first = Scalar(conn, "SELECT amount + 1 FROM t_cache WHERE id = 1");
            Assert.IsType<decimal>(first);
            Assert.Equal(11.50m, (decimal)first!);
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(first, Scalar(conn, "SELECT amount + 1 FROM t_cache WHERE id = 1"));
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Repeated_commands_hit_the_cache()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uca-trcache-hit-{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "genEmpty.mdb"), path, true);
        try
        {
            using var conn = (UCanAccessConnection)OpenWritable(path);
            Exec(conn, "CREATE TABLE t_hit (id LONG PRIMARY KEY, amount MONEY)");
            Exec(conn, "INSERT INTO t_hit (id, amount) VALUES (1, 10.50)");
            var mirror = conn.Mirror;
            int before = mirror.TranslationCacheCount;
            Assert.Equal(11.50m, Scalar(conn, "SELECT amount + 1 FROM t_hit WHERE id = 1"));
            Assert.Equal(before + 1, mirror.TranslationCacheCount);
            // Same text (even with different parameter values) reuses the entry.
            Assert.Equal(11.50m, Scalar(conn, "SELECT amount + 1 FROM t_hit WHERE id = 1"));
            Assert.Equal(before + 1, mirror.TranslationCacheCount);
            Assert.Equal(12.50m, Scalar(conn, "SELECT amount + 2 FROM t_hit WHERE id = 1"));
            Assert.Equal(before + 2, mirror.TranslationCacheCount);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Cached_translation_is_invalidated_by_schema_change()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uca-trcache-inv-{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "genEmpty.mdb"), path, true);
        try
        {
            using var conn = OpenWritable(path);
            Exec(conn, "CREATE TABLE t_retype (id LONG PRIMARY KEY, price MONEY)");
            Exec(conn, "INSERT INTO t_retype (id, price) VALUES (1, 10.50)");
            Assert.IsType<decimal>(Scalar(conn, "SELECT price + 0 FROM t_retype WHERE id = 1"));

            // Same statement text, different column type: a stale cached
            // translation (exact-decimal path) would throw on 'abc'.
            Exec(conn, "DROP TABLE t_retype");
            Exec(conn, "CREATE TABLE t_retype (id LONG PRIMARY KEY, price TEXT(20))");
            Exec(conn, "INSERT INTO t_retype (id, price) VALUES (1, 'abc')");
            object? value = Scalar(conn, "SELECT price + 0 FROM t_retype WHERE id = 1");
            Assert.NotNull(value);
            Assert.Equal(0L, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
