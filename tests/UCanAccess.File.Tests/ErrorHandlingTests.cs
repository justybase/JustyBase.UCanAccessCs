using UCanAccess.File;
using Xunit;

namespace UCanAccess.File.Tests;

public class ErrorHandlingTests
{
    private static string Temp(string name) => Path.Combine(Path.GetTempPath(), $"ucanaccess_tests_{Guid.NewGuid():N}_{name}");

    [Fact]
    public void Missing_file_throws()
    {
        Assert.Throws<System.IO.FileNotFoundException>(() => Database.Open(Temp("missing.mdb")));
    }

    [Fact]
    public void Empty_file_throws()
    {
        string path = Temp("empty.mdb");
        System.IO.File.WriteAllBytes(path, Array.Empty<byte>());
        try
        {
            Assert.Throws<DatabaseException>(() => Database.Open(path));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Not_a_database_throws()
    {
        string path = Temp("junk.mdb");
        System.IO.File.WriteAllBytes(path, new byte[100]);
        try
        {
            Assert.Throws<DatabaseException>(() => Database.Open(path));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Unsupported_version_throws()
    {
        // version byte for Jet 17 (newest, unsupported) at offset 20
        var header = new byte[21];
        header[20] = 0x06;
        string path = Temp("fake.accdb");
        System.IO.File.WriteAllBytes(path, header);
        try
        {
            var ex = Assert.Throws<DatabaseException>(() => Database.Open(path));
            Assert.Contains("Unsupported", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Writable_open_creates_and_releases_lock()
    {
        string path = Temp("lock.mdb");
        string lockPath = Path.ChangeExtension(path, ".ldb");
        try
        {
            using (var db = Database.Create(path))
            {
                Assert.True(System.IO.File.Exists(lockPath), ".ldb lock file should exist while the database is open");
                // a second writable open of the same file must fail
                Assert.Throws<DatabaseException>(() => Database.Open(path, readOnly: false));
                // read-only opens are allowed while locked
                using var ro = Database.Open(path, readOnly: true);
                Assert.True(ro.IsReadOnly);
            }
            Assert.False(System.IO.File.Exists(lockPath), ".ldb lock file should be removed on dispose");
            // after dispose the file can be opened writable again
            using var again = Database.Open(path, readOnly: false);
            Assert.True(System.IO.File.Exists(lockPath));
        }
        finally
        {
            System.IO.File.Delete(Path.ChangeExtension(path, ".ldb"));
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Unknown_table_returns_null()
    {
        using var db = Database.Open(Path.Combine(AppContext.BaseDirectory, "fixtures", "accessLike.mdb"));
        Assert.Null(db.GetTable("does_not_exist"));
    }
}
