using System.Data.Common;
using Xunit;

namespace UCanAccess.Tests;

public sealed class MirrorStorageTests
{
    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void File_mirror_can_be_reopened_without_using_process_memory_for_storage()
    {
        string mirrorPath = Path.Combine(Path.GetTempPath(), $"ucanaccess_mirror_{Guid.NewGuid():N}.sqlite");
        try
        {
            using (var connection = UCanAccessFactory.Instance.CreateConnection()!)
            {
                connection.ConnectionString =
                    $"Data Source={Fixture("sqljoin.mdb")};Read Only=true;Mirror Mode=file;Mirror Path={mirrorPath}";
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM t_detail";
                Assert.Equal(12L, command.ExecuteScalar());
            }

            Assert.True(System.IO.File.Exists(mirrorPath));

            using (var connection = UCanAccessFactory.Instance.CreateConnection()!)
            {
                connection.ConnectionString =
                    $"Data Source={Fixture("sqljoin.mdb")};Read Only=true;Mirror Mode=file;Mirror Path={mirrorPath}";
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM t_master";
                Assert.Equal(7L, command.ExecuteScalar());
            }
        }
        finally
        {
            System.IO.File.Delete(mirrorPath);
        }
    }

    [Fact]
    public void Mirror_path_cannot_be_the_access_source_file()
    {
        string source = Fixture("sqljoin.mdb");
        using var connection = UCanAccessFactory.Instance.CreateConnection()!;
        connection.ConnectionString =
            $"Data Source={source};Read Only=true;Mirror Mode=file;Mirror Path={source}";
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM t_master";
        Assert.Throws<ArgumentException>(() => command.ExecuteScalar());
    }

    [Fact]
    public void Query_reloads_when_another_database_handle_changes_the_source_file()
    {
        string source = Path.Combine(Path.GetTempPath(), $"ucanaccess_external_{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Fixture("generated/genEmpty.mdb"), source, true);
        try
        {
            using var connection = UCanAccessFactory.Instance.CreateConnection()!;
            connection.ConnectionString = $"Data Source={source};Read Only=true";
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT count(*) FROM t_empty";
                Assert.Equal(0L, command.ExecuteScalar());
            }

            using (var database = UCanAccess.File.Database.Open(source, readOnly: false))
            {
                database.GetTable("t_empty")!.AddRow(new object?[] { null, "external" });
            }

            using var refreshed = connection.CreateCommand();
            refreshed.CommandText = "SELECT count(*) FROM t_empty";
            Assert.Equal(1L, refreshed.ExecuteScalar());
        }
        finally
        {
            System.IO.File.Delete(source);
        }
    }

    [Fact]
    public void Prevent_reloading_keeps_the_current_file_snapshot()
    {
        string source = Path.Combine(Path.GetTempPath(), $"ucanaccess_no_reload_{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Fixture("generated/genEmpty.mdb"), source, true);
        try
        {
            using var connection = UCanAccessFactory.Instance.CreateConnection()!;
            connection.ConnectionString = $"Data Source={source};Read Only=true;preventReloading=true";
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT count(*) FROM t_empty";
                Assert.Equal(0L, command.ExecuteScalar());
            }

            using (var database = UCanAccess.File.Database.Open(source, readOnly: false))
            {
                database.GetTable("t_empty")!.AddRow(new object?[] { null, "external" });
            }

            using var unchanged = connection.CreateCommand();
            unchanged.CommandText = "SELECT count(*) FROM t_empty";
            Assert.Equal(0L, unchanged.ExecuteScalar());
        }
        finally
        {
            System.IO.File.Delete(source);
        }
    }
}
