using System.Diagnostics;
using System.Globalization;
using UCanAccess.File;
using Xunit;
using Xunit.Abstractions;

namespace UCanAccess.Tests;

/// <summary>
/// Opt-in low-level performance comparison with the original Java Jackcess
/// implementation.  This is intentionally not part of the normal test run.
/// </summary>
public sealed class InsertBenchmarkTests
{
    private static readonly DateTime BaseDate = new(2000, 1, 1);
    private readonly ITestOutputHelper _output;

    public InsertBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Insert_and_read_rows_compare_with_java_jackcess()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("UCANACCESS_PERF"), "1",
                StringComparison.OrdinalIgnoreCase))
        {
            _output.WriteLine("SKIPPED: set UCANACCESS_PERF=1 to run the benchmark");
            return;
        }

        int rows = ReadRows();
        string? jackJar = FindJar("jackcess-5.1.5.jar");
        if (jackJar == null)
        {
            _output.WriteLine("SKIPPED: jackcess-5.1.5.jar is missing");
            return;
        }
        string? classesDir = FindJavaClasses();
        if (classesDir == null)
        {
            _output.WriteLine("SKIPPED: InsertBench.class is missing; run tools/JavaOracle/run.ps1 first");
            return;
        }

        string baselineFile = Path.Combine(Path.GetTempPath(),
            $"ucanaccess-csharp-bench-baseline-{Guid.NewGuid():N}.mdb");
        string batchFile = Path.Combine(Path.GetTempPath(),
            $"ucanaccess-csharp-bench-batch-{Guid.NewGuid():N}.mdb");
        try
        {
            (double baselineInsertMs, long baselineChecksum) = RunCsharpBenchmark(baselineFile, rows, false);
            (double batchInsertMs, long batchChecksum) = RunCsharpBenchmark(batchFile, rows, true);
            (double javaInsertMs, double javaReadMs, long javaReadRows, long javaChecksum) =
                RunJavaBenchmark(jackJar, classesDir, rows);
            double baselineReadMs = ReadCsharpBenchmark(baselineFile, rows, baselineChecksum);
            double batchReadMs = ReadCsharpBenchmark(batchFile, rows, batchChecksum);

            Assert.Equal(rows, javaReadRows);
            Assert.Equal(baselineChecksum, javaChecksum);
            Assert.Equal(batchChecksum, javaChecksum);

            _output.WriteLine($"Rows: {rows.ToString(CultureInfo.InvariantCulture)}");
            _output.WriteLine("Operation       C# baseline   C# batch       Java");
            _output.WriteLine($"Insert       {baselineInsertMs,11:F3} {batchInsertMs,11:F3} {javaInsertMs,11:F3}");
            _output.WriteLine($"Read         {baselineReadMs,11:F3} {batchReadMs,11:F3} {javaReadMs,11:F3}");
            _output.WriteLine($"Insert C# batch speedup: {Ratio(baselineInsertMs, batchInsertMs):F2}x");
            _output.WriteLine($"Insert C# baseline/Java: {Ratio(baselineInsertMs, javaInsertMs):F2}x");
            _output.WriteLine($"Insert C# batch/Java:    {Ratio(batchInsertMs, javaInsertMs):F2}x");
            _output.WriteLine($"Read C# baseline/Java:   {Ratio(baselineReadMs, javaReadMs):F2}x");
            _output.WriteLine($"Read C# batch/Java:      {Ratio(batchReadMs, javaReadMs):F2}x");
            _output.WriteLine($"C# baseline insert rows/s: {RowsPerSecond(rows, baselineInsertMs):F0}");
            _output.WriteLine($"C# batch insert rows/s:    {RowsPerSecond(rows, batchInsertMs):F0}");
            _output.WriteLine($"Java insert rows/s: {RowsPerSecond(rows, javaInsertMs):F0}");
            _output.WriteLine($"C# baseline read rows/s: {RowsPerSecond(rows, baselineReadMs):F0}");
            _output.WriteLine($"C# batch read rows/s:    {RowsPerSecond(rows, batchReadMs):F0}");
            _output.WriteLine($"Java read rows/s:   {RowsPerSecond(rows, javaReadMs):F0}");
        }
        finally
        {
            System.IO.File.Delete(baselineFile);
            System.IO.File.Delete(batchFile);
        }
    }

    private static (double InsertMs, long Checksum) RunCsharpBenchmark(string path, int rows, bool batched)
    {
        double insertMs;
        using (Database db = Database.Create(path))
        {
            Table table = db.CreateTable("t_perf", new[]
            {
                new ColumnBuilder("id", DataType.Long).WithAutoNumber(),
                new ColumnBuilder("name", DataType.Text).WithLength(60),
                new ColumnBuilder("amount", DataType.Double),
                new ColumnBuilder("active", DataType.Boolean),
                new ColumnBuilder("created", DataType.ShortDateTime),
            });

            using WriteBatch? batch = batched ? db.BeginWriteBatch() : null;
            Stopwatch stopwatch = Stopwatch.StartNew();
            for (int i = 0; i < rows; i++)
            {
                table.AddRow(new object?[]
                {
                    null,
                    "row" + i.ToString(CultureInfo.InvariantCulture),
                    i * 0.5d,
                    i % 2 == 0,
                    BaseDate.AddDays(i % 3650),
                });
            }
            batch?.Commit();
            stopwatch.Stop();
            insertMs = stopwatch.Elapsed.TotalMilliseconds;
            Assert.Equal(rows, table.RowCount);
        }

        return (insertMs, ExpectedChecksum(rows));
    }

    private static double ReadCsharpBenchmark(string path, int rows, long? expectedChecksum)
    {
        using Database db = Database.Open(path);
        Table table = db.GetTable("t_perf") ?? throw new InvalidOperationException("Benchmark table is missing.");
        Stopwatch stopwatch = Stopwatch.StartNew();
        int readRows = 0;
        long checksum = 0;
        foreach (Row row in table.Rows())
        {
            checksum += Convert.ToInt64(row["id"], CultureInfo.InvariantCulture);
            checksum += (long)(double)row["amount"]!;
            if (Convert.ToBoolean(row["active"], CultureInfo.InvariantCulture))
            {
                checksum++;
            }
            readRows++;
        }
        stopwatch.Stop();

        Assert.Equal(rows, readRows);
        if (expectedChecksum.HasValue)
        {
            Assert.Equal(expectedChecksum.Value, checksum);
        }
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static (double InsertMs, double ReadMs, long ReadRows, long Checksum) RunJavaBenchmark(
        string jackJar, string classesDir, int rows)
    {
        var processInfo = new ProcessStartInfo("java")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = classesDir,
        };
        processInfo.ArgumentList.Add("-cp");
        processInfo.ArgumentList.Add($"{jackJar}{Path.PathSeparator}{classesDir}");
        processInfo.ArgumentList.Add("InsertBench");
        processInfo.ArgumentList.Add(rows.ToString(CultureInfo.InvariantCulture));

        using Process process = Process.Start(processInfo)
            ?? throw new InvalidOperationException("Could not start the Java benchmark.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);
        Assert.True(process.HasExited, "Java benchmark timed out.");
        Assert.True(process.ExitCode == 0, $"Java benchmark failed: {error}");

        return (
            ReadMetric<double>(output, "JAVA_INSERT_MS"),
            ReadMetric<double>(output, "JAVA_READ_MS"),
            ReadMetric<long>(output, "JAVA_READ_ROWS"),
            ReadMetric<long>(output, "JAVA_CHECKSUM"));
    }

    private static int ReadRows()
    {
        string? value = Environment.GetEnvironmentVariable("UCANACCESS_PERF_ROWS");
        return string.IsNullOrWhiteSpace(value)
            ? 100_000
            : int.Parse(value, CultureInfo.InvariantCulture);
    }

    private static string? FindJar(string name)
    {
        string path = Path.Combine(Path.GetTempPath(), "ucanaccess-csharp-oracle", name);
        return System.IO.File.Exists(path) ? path : null;
    }

    private static string? FindJavaClasses()
    {
        string? repo = Environment.GetEnvironmentVariable("UCANACCESS_CSHARP_REPO");
        repo = string.IsNullOrWhiteSpace(repo) ? FindRepoRoot() : repo;
        string classes = Path.Combine(repo, "tools", "JavaOracle", "classes");
        return System.IO.File.Exists(Path.Combine(classes, "InsertBench.class")) ? classes : null;
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tools", "JavaOracle")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private static T ReadMetric<T>(string output, string name)
    {
        string? line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .FirstOrDefault(value => value.StartsWith(name + "=", StringComparison.Ordinal));
        if (line == null)
        {
            throw new InvalidOperationException($"Java benchmark did not emit {name}. Output: {output}");
        }
        return (T)Convert.ChangeType(line[(name.Length + 1)..], typeof(T), CultureInfo.InvariantCulture);
    }

    private static double Ratio(double left, double right) => right == 0 ? double.NaN : left / right;

    private static double RowsPerSecond(int rows, double milliseconds)
        => milliseconds <= 0 ? double.PositiveInfinity : rows * 1000d / milliseconds;

    private static long ExpectedChecksum(int rows)
    {
        long checksum = 0;
        for (int i = 0; i < rows; i++)
        {
            checksum += i + 1;
            checksum += (long)(i * 0.5d);
            if (i % 2 == 0)
            {
                checksum++;
            }
        }
        return checksum;
    }
}
