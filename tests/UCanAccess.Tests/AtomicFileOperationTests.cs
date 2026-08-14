using System.Data;
using UCanAccess.File;
using Xunit;

namespace UCanAccess.Tests;

/// <summary>
/// Verifies that failures at the staging boundary do not expose a partially
/// written Access file and that the connection remains usable afterwards.
/// </summary>
public sealed class AtomicFileOperationTests
{
    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Replace_failure_keeps_source_unchanged_and_reopens_connection()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uca-atomic-replace-{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Fixture("generated/genEmpty.mdb"), path);
        try
        {
            using (Database db = Database.Open(path, readOnly: false))
            {
                Table table = db.CreateTable("atomic_rows", new[]
                {
                    new ColumnBuilder("id", DataType.Long).WithAutoNumber(),
                    new ColumnBuilder("value", DataType.Text).WithLength(40),
                });
                table.AddRow(new object?[] { null, "before" });
            }

            byte[] before = System.IO.File.ReadAllBytes(path);
            var fileSystem = new FailingAtomicFileSystem(failCopy: false, failReplace: true);
            using (var connection = new UCanAccessConnection
                   {
                       ConnectionString = $"Data Source={path};Read Only=false",
                       AtomicFileSystem = fileSystem,
                   })
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO atomic_rows (value) VALUES ('after')";
                Assert.Throws<IOException>(() => command.ExecuteNonQuery());

                Assert.Equal(ConnectionState.Open, connection.State);
                using var verify = connection.CreateCommand();
                verify.CommandText = "SELECT COUNT(*) FROM atomic_rows";
                Assert.Equal(1L, verify.ExecuteScalar());
            }
            Assert.Equal(before, System.IO.File.ReadAllBytes(path));
            Assert.Contains(fileSystem.Calls, call => call == "replace");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Copy_failure_does_not_change_source_or_leave_connection_closed()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uca-atomic-copy-{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Fixture("generated/genEmpty.mdb"), path);
        try
        {
            using (Database db = Database.Open(path, readOnly: false))
            {
                Table table = db.CreateTable("atomic_copy_rows", new[]
                {
                    new ColumnBuilder("id", DataType.Long),
                });
                table.AddRow(new object?[] { 1 });
            }

            byte[] before = System.IO.File.ReadAllBytes(path);
            var fileSystem = new FailingAtomicFileSystem(failCopy: true, failReplace: false);
            using (var connection = new UCanAccessConnection
                   {
                       ConnectionString = $"Data Source={path};Read Only=false",
                       AtomicFileSystem = fileSystem,
                   })
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO atomic_copy_rows (id) VALUES (2)";
                Assert.Throws<IOException>(() => command.ExecuteNonQuery());

                Assert.Equal(ConnectionState.Open, connection.State);
            }
            Assert.Equal(before, System.IO.File.ReadAllBytes(path));
            Assert.DoesNotContain(fileSystem.Calls, call => call == "replace");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Ddl_copy_failure_does_not_leave_a_staging_artifact()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uca-atomic-ddl-{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Fixture("generated/genEmpty.mdb"), path);
        string directory = Path.GetDirectoryName(path)!;
        string prefix = "." + Path.GetFileNameWithoutExtension(path) + ".ucanaccess-ddl-";
        try
        {
            byte[] before = System.IO.File.ReadAllBytes(path);
            var fileSystem = new FailingAtomicFileSystem(failCopy: true, failReplace: false);
            using (var connection = new UCanAccessConnection
                   {
                       ConnectionString = $"Data Source={path};Read Only=false",
                       AtomicFileSystem = fileSystem,
                   })
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE should_not_exist (id LONG)";
                Assert.Throws<IOException>(() => command.ExecuteNonQuery());
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            Assert.Equal(before, System.IO.File.ReadAllBytes(path));
            Assert.Empty(Directory.EnumerateFiles(directory, prefix + "*"));
        }
        finally
        {
            System.IO.File.Delete(path);
            foreach (string file in Directory.EnumerateFiles(directory, prefix + "*"))
            {
                System.IO.File.Delete(file);
            }
        }
    }

    private sealed class FailingAtomicFileSystem : IAtomicFileSystem
    {
        private readonly bool _failCopy;
        private readonly bool _failReplace;

        internal FailingAtomicFileSystem(bool failCopy, bool failReplace)
        {
            _failCopy = failCopy;
            _failReplace = failReplace;
        }

        internal List<string> Calls { get; } = new();

        public void Copy(string sourceFileName, string destFileName, bool overwrite)
        {
            Calls.Add("copy");
            if (_failCopy)
            {
                throw new IOException("Injected staging-copy failure.");
            }
            System.IO.File.Copy(sourceFileName, destFileName, overwrite);
        }

        public void Replace(string sourceFileName, string destinationFileName,
            string? destinationBackupFileName, bool ignoreMetadataErrors)
        {
            Calls.Add("replace");
            if (_failReplace)
            {
                throw new IOException("Injected replacement failure.");
            }
            System.IO.File.Replace(sourceFileName, destinationFileName, destinationBackupFileName,
                ignoreMetadataErrors);
        }

        public void Delete(string path)
        {
            Calls.Add("delete");
            System.IO.File.Delete(path);
        }
    }
}
