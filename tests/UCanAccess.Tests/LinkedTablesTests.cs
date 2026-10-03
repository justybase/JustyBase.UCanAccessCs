using System.Data.Common;
using Xunit;

namespace UCanAccess.Tests;

/// <summary>
/// Linked tables: resolved through their link and queryable via SQL (mirror).
/// </summary>
public class LinkedTablesTests
{
    private static DbConnection Open(string fixture)
    {
        var conn = UCanAccessFactory.Instance.CreateConnection()!;
        conn.ConnectionString = $"Data Source={Path.Combine(AppContext.BaseDirectory, "fixtures", fixture)};Read Only=true";
        conn.Open();
        return conn;
    }

    [Fact]
    public void Sql_queries_linked_table()
    {
        // genLinked.mdb links t_linked -> genLinkee.mdb::t_linkee
        using var conn = Open("generated/genLinked.mdb");

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM t_linked";
            Assert.Equal(2L, cmd.ExecuteScalar());
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM t_linked ORDER BY id";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("linkee one", reader.GetString(0));
            Assert.True(reader.Read());
            Assert.Equal("linkee two", reader.GetString(0));
            Assert.False(reader.Read());
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM t_linked WHERE id = 2";
            Assert.Equal("linkee two", cmd.ExecuteScalar());
        }
    }

    [Fact]
    public void Writes_through_link_go_to_linkee()
    {
        // copy both files so the relative link resolves in the temp dir
        string dir = Path.Combine(Path.GetTempPath(), $"ucanaccess_link_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string linked = Path.Combine(dir, "genLinked.mdb");
        string linkee = Path.Combine(dir, "genLinkee.mdb");
        string fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "generated");
        System.IO.File.Copy(Path.Combine(fixtures, "genLinked.mdb"), linked, true);
        System.IO.File.Copy(Path.Combine(fixtures, "genLinkee.mdb"), linkee, true);
        try
        {
            var conn = UCanAccessFactory.Instance.CreateConnection()!;
            conn.ConnectionString = $"Data Source={linked};Read Only=false";
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO t_linked (name) VALUES ('via link')";
                Assert.Equal(1, cmd.ExecuteNonQuery());
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM t_linked";
                Assert.Equal(3L, cmd.ExecuteScalar());
            }
            conn.Dispose();

            // the row landed in the LINKEE file
            using var db = UCanAccess.File.Database.Open(linkee);
            var t = db.GetTable("t_linkee")!;
            Assert.Equal(3, t.RowCount);
            var names = t.Rows().Select(r => Convert.ToString(r["name"])).ToList();
            Assert.Contains("via link", names);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Remap_resolves_renamed_linkee()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ucanaccess_remap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string linked = Path.Combine(dir, "genLinked.mdb");
        string renamed = Path.Combine(dir, "renamed.mdb");
        string fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "generated");
        System.IO.File.Copy(Path.Combine(fixtures, "genLinked.mdb"), linked, true);
        System.IO.File.Copy(Path.Combine(fixtures, "genLinkee.mdb"), renamed, true);
        try
        {
            var conn = UCanAccessFactory.Instance.CreateConnection()!;
            conn.ConnectionString = $"Data Source={linked};Read Only=true;Remap=genLinkee.mdb|{renamed}";
            conn.Open();
            using var _ = conn;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM t_linked";
            Assert.Equal(2L, cmd.ExecuteScalar());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Remap_allows_external_path_without_allow_external_links()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ucanaccess_remap_{Guid.NewGuid():N}");
        string outer = Path.Combine(Path.GetTempPath(), $"ucanaccess_remap_outer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(outer);
        string linked = Path.Combine(dir, "genLinked.mdb");
        string outside = Path.Combine(outer, "outside.mdb");
        string fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "generated");
        System.IO.File.Copy(Path.Combine(fixtures, "genLinked.mdb"), linked, true);
        System.IO.File.Copy(Path.Combine(fixtures, "genLinkee.mdb"), outside, true);
        try
        {
            // Remap is trusted explicit config: it bypasses the AllowExternalLinks guard.
            var conn = UCanAccessFactory.Instance.CreateConnection()!;
            conn.ConnectionString = $"Data Source={linked};Read Only=true;Remap=genLinkee.mdb|{outside}";
            conn.Open();
            using var _ = conn;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM t_linked";
            Assert.Equal(2L, cmd.ExecuteScalar());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            Directory.Delete(outer, recursive: true);
        }
    }

    [Fact]
    public void Remap_invalid_entry_is_rejected()
    {
        var conn = UCanAccessFactory.Instance.CreateConnection()!;
        conn.ConnectionString =
            $"Data Source={Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "genLinked.mdb")};Read Only=true;Remap=no-separator";
        Assert.Throws<ArgumentException>(() => conn.Open());
    }
}
