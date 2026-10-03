using Xunit;

namespace UCanAccess.Tests;

/// <summary>
/// S3: upstream SkipIndexes / OpenExclusive parity.
/// </summary>
public class ConnectionOptionsTests
{
    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static string CopyToTemp(string fixture)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ucanaccess_opts_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string dest = Path.Combine(dir, Path.GetFileName(fixture));
        System.IO.File.Copy(Fixture(fixture), dest, true);
        return dest;
    }

    [Fact]
    public void SkipIndexes_is_accepted_and_queries_still_work()
    {
        using var conn = UCanAccessFactory.Instance.CreateConnection()!;
        conn.ConnectionString = $"Data Source={Fixture("generated/genLinked.mdb")};Read Only=true;Skip Indexes=true";
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM t_linked";
        Assert.Equal(2L, cmd.ExecuteScalar());
        var mirror = ((UCanAccessConnection)conn).Mirror;
        Assert.True(mirror.SkipSimpleIndexes);
    }

    [Fact]
    public void SkipIndexes_defaults_to_false()
    {
        var parsed = new UCanAccessConnectionString($"Data Source=x.mdb");
        Assert.False(parsed.SkipIndexes);
        Assert.False(parsed.OpenExclusive);
    }

    [Fact]
    public void OpenExclusive_locks_readonly_file()
    {
        string path = CopyToTemp("generated/genLinkee.mdb");
        string dir = Path.GetDirectoryName(path)!;
        string lockPath = Path.ChangeExtension(path, ".ldb");
        try
        {
            if (System.IO.File.Exists(lockPath))
            {
                System.IO.File.Delete(lockPath);
            }
            var conn = UCanAccessFactory.Instance.CreateConnection()!;
            conn.ConnectionString = $"Data Source={path};Read Only=true;Open Exclusive=true";
            conn.Open();
            try
            {
                Assert.True(System.IO.File.Exists(lockPath));
                using var db = UCanAccess.File.Database.Open(path);
                Assert.True(db.OpenExclusive == false);
                // Second exclusive open must fail while the lock is held.
                Assert.Throws<UCanAccess.File.DatabaseException>(() =>
                {
                    using var db2 = UCanAccess.File.Database.Open(path, openExclusive: true);
                });
            }
            finally
            {
                conn.Dispose();
            }
            Assert.False(System.IO.File.Exists(lockPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LockMdb_alias_maps_to_open_exclusive()
    {
        var parsed = new UCanAccessConnectionString("Data Source=x.mdb;Lock Mdb=true");
        Assert.True(parsed.OpenExclusive);
    }

    [Fact]
    public void ReadOnly_without_exclusive_does_not_lock()
    {
        string path = CopyToTemp("generated/genLinkee.mdb");
        string dir = Path.GetDirectoryName(path)!;
        string lockPath = Path.ChangeExtension(path, ".ldb");
        try
        {
            if (System.IO.File.Exists(lockPath))
            {
                System.IO.File.Delete(lockPath);
            }
            using var conn = UCanAccessFactory.Instance.CreateConnection()!;
            conn.ConnectionString = $"Data Source={path};Read Only=true";
            conn.Open();
            Assert.False(System.IO.File.Exists(lockPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
