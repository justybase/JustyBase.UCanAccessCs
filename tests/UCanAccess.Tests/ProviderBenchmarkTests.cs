using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using UCanAccess.File;
using Xunit;

namespace UCanAccess.Tests;

/// <summary>
/// Opt-in provider benchmarks (mirror materialization, query shapes,
/// translator throughput) with GC allocation accounting. Not part of the
/// normal test run: timings depend on the host. No timing assertions —
/// numbers are reported for before/after comparison (see docs/PERFORMANCE.md).
/// </summary>
public sealed class ProviderBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public ProviderBenchmarkTests(ITestOutputHelper output) => _output = output;

    private static bool Enabled(ITestOutputHelper output)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("UCANACCESS_PERF"), "1",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        output.WriteLine("SKIPPED: set UCANACCESS_PERF=1 to run the benchmark");
        return false;
    }

    private static int ReadRows(int fallback)
    {
        string? value = Environment.GetEnvironmentVariable("UCANACCESS_PERF_ROWS");
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : int.Parse(value, CultureInfo.InvariantCulture);
    }

    private static (TimeSpan Elapsed, long AllocatedBytes) Measure(Func<object?> action)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch stopwatch = Stopwatch.StartNew();
        object? result = action();
        stopwatch.Stop();
        (TimeSpan elapsed, long allocated) = (stopwatch.Elapsed, GC.GetAllocatedBytesForCurrentThread() - before);
        if (result is IDisposable disposable)
        {
            disposable.Dispose();
        }
        else
        {
            GC.KeepAlive(result);
        }
        return (elapsed, allocated);
    }

    private static string PerfDb(int rows)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ucanaccess_perf_{Guid.NewGuid():N}.mdb");
        using (Database db = Database.Create(path))
        {
            Table table = db.CreateTable("t_perf", new[]
            {
                new ColumnBuilder("id", DataType.Long).WithAutoNumber(),
                new ColumnBuilder("name", DataType.Text).WithLength(60),
                new ColumnBuilder("amount", DataType.Money),
                new ColumnBuilder("active", DataType.Boolean),
                new ColumnBuilder("created", DataType.ShortDateTime),
            });
            using var batch = db.BeginWriteBatch();
            for (int i = 0; i < rows; i++)
            {
                table.AddRow(new object?[]
                {
                    null,
                    "row" + i.ToString(CultureInfo.InvariantCulture),
                    (i % 1000) + 0.25m,
                    i % 2 == 0,
                    new DateTime(2020, 1, 1).AddDays(i % 3650),
                });
            }
            batch.Commit();
        }
        return path;
    }

    private static int Drain(DbConnection conn, string sql, Action<DbParameter>? bind = null)
    {
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (bind != null)
        {
            var parameter = cmd.CreateParameter();
            bind(parameter);
            cmd.Parameters.Add(parameter);
        }
        using DbDataReader reader = cmd.ExecuteReader();
        int rows = 0;
        while (reader.Read())
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                GC.KeepAlive(reader.GetValue(i));
            }
            rows++;
        }
        return rows;
    }

    [Fact]
    public void Mirror_materialization_reports_time_and_allocations()
    {
        if (!Enabled(_output))
        {
            return;
        }
        int rows = ReadRows(20_000);
        string path = PerfDb(rows);
        try
        {
            (TimeSpan elapsed, long allocated) = Measure(() =>
            {
                var conn = UCanAccessFactory.Instance.CreateConnection()!;
                conn.ConnectionString = $"Data Source={path};Read Only=true;Lazy Load=false";
                conn.Open();
                return conn;
            });
            _output.WriteLine($"Mirror.Open rows={rows} ms={elapsed.TotalMilliseconds:F1} " +
                $"rows/s={rows * 1000d / elapsed.TotalMilliseconds:F0} " +
                $"alloc_bytes={allocated} alloc_per_row={allocated / rows}");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Query_shapes_report_time_and_allocations()
    {
        if (!Enabled(_output))
        {
            return;
        }
        int rows = ReadRows(20_000);
        string path = PerfDb(rows);
        try
        {
            var conn = UCanAccessFactory.Instance.CreateConnection()!;
            conn.ConnectionString = $"Data Source={path};Read Only=true";
            conn.Open();
            using (conn)
            {
                // Warm up the mirror once; measure steady-state query execution.
                Assert.True(Drain(conn, "SELECT count(*) FROM t_perf") >= 0);
                ReportQuery(conn, "filter", "SELECT id, name FROM t_perf WHERE amount > ? ORDER BY id",
                    p => p.Value = 500m);
                ReportQuery(conn, "like", "SELECT id, name FROM t_perf WHERE name LIKE 'row1*'");
                ReportQuery(conn, "decimal-agg", "SELECT SUM(amount), AVG(amount), MIN(amount), MAX(amount) FROM t_perf");
                ReportQuery(conn, "group", "SELECT active, COUNT(*), SUM(amount) FROM t_perf GROUP BY active");
                ReportQuery(conn, "window", "SELECT id, ROW_NUMBER() OVER (ORDER BY id), RANK() OVER (ORDER BY amount DESC) FROM t_perf");
                ReportQuery(conn, "concat", "SELECT id, name & '!' & amount FROM t_perf WHERE id < 5000");
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private void ReportQuery(DbConnection conn, string name, string sql, Action<DbParameter>? bind = null)
    {
        int total = 0;
        (TimeSpan elapsed, long allocated) = Measure(() =>
        {
            int rows = Drain(conn, sql, bind);
            total = rows;
            return rows;
        });
        _output.WriteLine($"Query.{name} rows={total} ms={elapsed.TotalMilliseconds:F1} " +
            $"rows/s={(total <= 0 ? 0 : total * 1000d / elapsed.TotalMilliseconds):F0} " +
            $"alloc_bytes={allocated} alloc_per_row={(total <= 0 ? 0 : allocated / total)}");
    }

    [Fact]
    public void Repeated_command_reports_time_and_allocations()
    {
        if (!Enabled(_output))
        {
            return;
        }
        int rows = ReadRows(20_000);
        string path = PerfDb(rows);
        try
        {
            var conn = UCanAccessFactory.Instance.CreateConnection()!;
            conn.ConnectionString = $"Data Source={path};Read Only=true";
            conn.Open();
            using (conn)
            {
                Assert.True(Drain(conn, "SELECT count(*) FROM t_perf") >= 0);
                const int iterations = 2_000;
                // SELECT without table access: isolates per-command overhead
                // (translation cache, parameter binding, SQLite prepare/step)
                // from table-scan costs. The mirror carries no SQLite indexes,
                // so any table query would be dominated by the scan instead.
                (TimeSpan elapsed, long allocated) = Measure(() =>
                {
                    object? last = null;
                    for (int i = 0; i < iterations; i++)
                    {
                        using DbCommand cmd = conn.CreateCommand();
                        cmd.CommandText = "SELECT ? + ?";
                        var first = cmd.CreateParameter();
                        first.Value = i;
                        cmd.Parameters.Add(first);
                        var second = cmd.CreateParameter();
                        second.Value = 1;
                        cmd.Parameters.Add(second);
                        last = cmd.ExecuteScalar();
                    }
                    return last;
                });
                _output.WriteLine($"Command.repeat iterations={iterations} ms={elapsed.TotalMilliseconds:F1} " +
                    $"per_command_us={elapsed.TotalMilliseconds * 1000d / iterations:F2} " +
                    $"alloc_bytes={allocated} alloc_per_command={allocated / iterations}");
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Translator_throughput_reports_time_and_allocations()
    {
        if (!Enabled(_output))
        {
            return;
        }
        string[] corpus =
        {
            "SELECT m.id, m.name, d.qty FROM t_master m INNER JOIN t_detail d ON m.id = d.master_id WHERE d.qty > ? ORDER BY d.id",
            "SELECT id, name & '!' AS ex, price * qty AS total FROM t_detail WHERE note LIKE 'A*' AND dt > #1/1/2024#",
            "TRANSFORM Sum(qty) SELECT master_id FROM t_detail GROUP BY master_id PIVOT code IN ('a01', 'b02')",
            "SELECT id, ROW_NUMBER() OVER (PARTITION BY master_id ORDER BY qty DESC), RANK() OVER (ORDER BY price) FROM t_detail",
            "SELECT DISTINCTROW master_id, Sum(price) FROM t_detail GROUP BY master_id HAVING Sum(price) > 10 ORDER BY master_id",
        };
        const int iterations = 2_000;
        (TimeSpan elapsed, long allocated) = Measure(() =>
        {
            int count = 0;
            for (int i = 0; i < iterations; i++)
            {
                foreach (string sql in corpus)
                {
                    count += AccessSqlTranslator.Translate(sql, out _, out _).Length;
                }
            }
            return count;
        });
        long total = (long)iterations * corpus.Length;
        _output.WriteLine($"Translate statements={total} ms={elapsed.TotalMilliseconds:F1} " +
            $"per_statement_us={elapsed.TotalMilliseconds * 1000d / total:F2} " +
            $"alloc_bytes={allocated} alloc_per_statement={allocated / total}");
    }
}
