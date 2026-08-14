using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UCanAccess;
using Xunit;

namespace UCanAccess.AccessCrypto.Tests;

[SupportedOSPlatform("windows")]
public sealed class AccessComRoundTripTests
{
    private const string DefaultPassword = "Uca!fixture-2026";

    [Fact]
    [Trait("Category", "AccessCom")]
    public void Com_generated_encrypted_accdb_round_trips_through_managed_provider()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("UCANACCESS_ACCESS_COM"), "true",
            StringComparison.OrdinalIgnoreCase))
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "Set UCANACCESS_ACCESS_COM=true to run the Microsoft Access COM integration test.");
        }
        if (!OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("Microsoft Access COM is not installed on this runner.");
        }
        Assert.True(CanCreateAccessCom(),
            "UCANACCESS_ACCESS_COM=true requires Microsoft Access COM to be installed and creatable.");

        string root = FindRepositoryRoot();
        string script = Path.Combine(root, "tools", "AccessFixtures", "Generate-EncryptedFixture.ps1");
        string fixture = Path.Combine(Path.GetTempPath(), $"uca-encrypted-{Guid.NewGuid():N}.accdb");
        string password = FixturePassword();
        try
        {
            RunGenerator(script, fixture, password);

            // Establish that the file produced by Microsoft Access is readable
            // by Access itself before the managed provider touches it.
            using (var com = new AccessComScope(fixture, password))
            {
                Assert.Equal(2, Convert.ToInt32(com.Scalar(
                    "SELECT COUNT(*) AS Cnt FROM CryptoFixture")));
                Assert.Equal("sentinel-Access-crypto", Convert.ToString(com.Scalar(
                    "SELECT Description FROM CryptoFixture WHERE Code='COM-ROW'")));
            }

            using (var connection = new UCanAccessConnection
            {
                ConnectionString = $"Data Source={fixture};Password={password};Read Only=false",
                DatabaseOpener = new UCanAccess.AccessCrypto.AccessCryptoOpener(),
            })
            {
                connection.Open();
                Assert.Equal(2, ScalarInt(connection, "SELECT COUNT(*) FROM CryptoFixture"));
                Execute(connection, "ALTER TABLE CryptoFixture ADD COLUMN ManagedText TEXT(80)");
                Execute(connection, "UPDATE CryptoFixture SET ManagedText='from-dotnet' WHERE Code='COM-ROW'");
                Execute(connection, "INSERT INTO CryptoFixture (Code, Description, ManagedText) VALUES ('DOTNET-ROW', 'managed insert', 'from-dotnet')");
            }

            using (var com = new AccessComScope(fixture, password))
            {
                Assert.Equal(3, Convert.ToInt32(com.Scalar("SELECT COUNT(*) AS Cnt FROM CryptoFixture")));
                Assert.Equal("from-dotnet", Convert.ToString(com.Scalar("SELECT ManagedText FROM CryptoFixture WHERE Code='COM-ROW'")));
                com.Execute("UPDATE CryptoFixture SET ManagedText='from-com' WHERE Code='COM-ROW'");
            }

            using (var connection = new UCanAccessConnection
            {
                ConnectionString = $"Data Source={fixture};Password={password};Read Only=true",
                DatabaseOpener = new UCanAccess.AccessCrypto.AccessCryptoOpener(),
            })
            {
                connection.Open();
                Assert.Equal("from-com", ScalarString(connection,
                    "SELECT ManagedText FROM CryptoFixture WHERE Code='COM-ROW'"));
            }
        }
        finally
        {
            if (System.IO.File.Exists(fixture)) System.IO.File.Delete(fixture);
            foreach (string lockFile in new[] { Path.ChangeExtension(fixture, ".laccdb") })
            {
                if (System.IO.File.Exists(lockFile)) System.IO.File.Delete(lockFile);
            }
        }
    }

    private static bool CanCreateAccessCom()
    {
        try
        {
            Type? type = Type.GetTypeFromProgID("Access.Application");
            if (type == null) return false;
            object instance = Activator.CreateInstance(type)!;
            dynamic access = instance;
            try { access.Quit(2); } catch { }
            Marshal.FinalReleaseComObject(instance);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FixturePassword()
    {
        string? configured = Environment.GetEnvironmentVariable("UCANACCESS_ACCESS_FIXTURE_PASSWORD");
        return string.IsNullOrWhiteSpace(configured) ? DefaultPassword : configured;
    }

    private static void RunGenerator(string script, string output, string password)
    {
        string shell = FindPowerShell();
        var info = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(script);
        info.ArgumentList.Add("-OutputPath");
        info.ArgumentList.Add(output);
        info.Environment["UCANACCESS_ACCESS_FIXTURE_PASSWORD"] = password;
        using Process process = Process.Start(info)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"Access fixture generator failed: {stderr}\n{stdout}");
        Assert.True(System.IO.File.Exists(output), "The Access fixture generator did not create an output file.");
    }

    private static string FindPowerShell()
    {
        string? pwsh = Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator)
            .Select(p => Path.Combine(p, OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh"))
            .FirstOrDefault(System.IO.File.Exists);
        return pwsh ?? (OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null && !System.IO.File.Exists(Path.Combine(current.FullName, "UCanAccess.slnx")))
        {
            current = current.Parent;
        }
        return current?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private static int ScalarInt(UCanAccessConnection connection, string sql)
        => Convert.ToInt32(Scalar(connection, sql));

    private static string? ScalarString(UCanAccessConnection connection, string sql)
        => Scalar(connection, sql) as string;

    private static object? Scalar(UCanAccessConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(UCanAccessConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class AccessComScope : IDisposable
    {
        private readonly object _access;
        private readonly dynamic _database;
        private readonly dynamic _engine;

        public AccessComScope(string path, string password)
        {
            Type type = Type.GetTypeFromProgID("Access.Application")
                ?? throw new InvalidOperationException("Access.Application is not registered.");
            _access = Activator.CreateInstance(type)!;
            dynamic access = _access;
            _engine = access.DBEngine;
            _database = _engine.OpenDatabase(path, false, false, $";PWD={password}");
        }

        public object? Scalar(string sql)
        {
            dynamic recordset = _database.OpenRecordset(sql);
            try { return recordset.Fields(0).Value; }
            finally { try { recordset.Close(); } catch { } Marshal.FinalReleaseComObject(recordset); }
        }

        public void Execute(string sql) => _database.Execute(sql, 128);

        public void Dispose()
        {
            try { _database.Close(); } catch { }
            try { Marshal.FinalReleaseComObject(_database); } catch { }
            try { Marshal.FinalReleaseComObject(_engine); } catch { }
            try { ((dynamic)_access).Quit(2); } catch { }
            try { Marshal.FinalReleaseComObject(_access); } catch { }
        }
    }
}
