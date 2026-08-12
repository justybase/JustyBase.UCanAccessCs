using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace UCanAccess.Tests;

public class AdoNetTests
{
    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static DbConnection Open(string fixture, string extra = "")
    {
        string cs = $"Data Source={Fixture(fixture)};Read Only=true{extra}";
        var conn = DbProviderFactories.GetFactory("UCanAccess")?.CreateConnection()
            ?? throw new InvalidOperationException("provider not registered");
        conn.ConnectionString = cs;
        conn.Open();
        return conn;
    }

    static AdoNetTests()
    {
        // register our provider so DbProviderFactories can find it
        DbProviderFactories.RegisterFactory("UCanAccess", UCanAccessFactory.Instance);
        DbProviderFactories.RegisterFactory("UCanAccess.UCanAccessFactory", UCanAccessFactory.Instance);
    }

    [Fact]
    public void Select_star_reads_all_rows()
    {
        using var conn = Open("pivot.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM t_pivot";
        using var reader = cmd.ExecuteReader();

        Assert.Equal(3, reader.FieldCount);
        var codes = new List<string>();
        var values = new List<decimal>();
        while (reader.Read())
        {
            codes.Add(reader.GetString(0));
            values.Add(reader.GetDecimal(1));
        }
        Assert.Equal(new[] { "paperino", "piero", "pippo", "pluto" }, codes);
        Assert.Equal(new[] { 4444.0000m, 33.0000m, 122.0000m, 443.0000m }, values);
    }

    [Fact]
    public void Where_clause_filters_rows()
    {
        using var conn = Open("pivot.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT c_cod FROM t_pivot WHERE c_val > 100";
        using var reader = cmd.ExecuteReader();

        var codes = new List<string>();
        while (reader.Read())
        {
            codes.Add(reader.GetString(0));
        }
        Assert.Equal(new[] { "paperino", "pippo", "pluto" }, codes);
    }

    [Fact]
    public void Projection_and_order_by()
    {
        using var conn = Open("pivot.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT c_cod, c_val FROM t_pivot ORDER BY c_val DESC";
        using var reader = cmd.ExecuteReader();

        var codes = new List<string>();
        while (reader.Read())
        {
            codes.Add(reader.GetString(0));
        }
        Assert.Equal(new[] { "paperino", "pluto", "pippo", "piero" }, codes);
    }

    [Fact]
    public void Access_like_wildcards()
    {
        using var conn = Open("pivot.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT c_cod FROM t_pivot WHERE c_cod LIKE 'p?p*'";
        using var reader = cmd.ExecuteReader();

        var codes = new List<string>();
        while (reader.Read())
        {
            codes.Add(reader.GetString(0));
        }
        Assert.Equal(new[] { "paperino", "pippo" }, codes);
    }

    [Fact]
    public void Date_literals_work()
    {
        using var conn = Open("pivot.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT c_cod FROM t_pivot WHERE c_dt = #5/30/2013 1:18:14 PM#";
        using var reader = cmd.ExecuteReader();

        var codes = new List<string>();
        while (reader.Read())
        {
            codes.Add(reader.GetString(0));
        }
        Assert.Equal(new[] { "paperino", "pippo" }, codes);
    }

    [Fact]
    public void Access_functions_work()
    {
        using var conn = Open("pivot.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT c_cod, IIf(c_val > 100, 'big', 'small'), Nz(NULL, 'x') FROM t_pivot";
        using var reader = cmd.ExecuteReader();

        var labels = new List<string>();
        var nzValues = new List<string>();
        while (reader.Read())
        {
            labels.Add(reader.GetString(1));
            nzValues.Add(reader.GetString(2));
        }
        Assert.Equal(new[] { "big", "small", "big", "big" }, labels);
        Assert.All(nzValues, v => Assert.Equal("x", v));
    }

    [Fact]
    public void Concat_with_ampersand()
    {
        using var conn = Open("pivot.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT c_cod & '-' & c_val FROM t_pivot WHERE c_cod = 'pluto'";
        using var reader = cmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal("pluto-443.0000", reader.GetString(0));
    }

    [Fact]
    public void Top_n_limits_rows()
    {
        using var conn = Open("pivot.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TOP 2 c_cod FROM t_pivot ORDER BY c_cod";
        using var reader = cmd.ExecuteReader();

        var codes = new List<string>();
        while (reader.Read())
        {
            codes.Add(reader.GetString(0));
        }
        Assert.Equal(new[] { "paperino", "piero" }, codes);
    }

    [Fact]
    public void Select_identity_returns_the_last_generated_autonumber()
    {
        string source = Fixture("generated/genEmpty.mdb");
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_identity_{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(source, tmp, true);
        try
        {
            using var conn = new UCanAccessConnection($"Data Source={tmp};Read Only=false");
            conn.Open();
            using (var create = conn.CreateCommand())
            {
                create.CommandText = "CREATE TABLE t_identity (id COUNTER PRIMARY KEY, value TEXT(20))";
                create.ExecuteNonQuery();
            }
            using (var insert = conn.CreateCommand())
            {
                insert.CommandText = "INSERT INTO t_identity (value) VALUES ('created')";
                insert.ExecuteNonQuery();
            }
            using var identity = conn.CreateCommand();
            identity.CommandText = "SELECT @@IDENTITY";
            Assert.Equal(1L, identity.ExecuteScalar());

            identity.CommandText = "SELECT\t@@IDENTITY AS id;";
            Assert.Equal(1L, identity.ExecuteScalar());
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Select_identity_resets_after_close_and_reopen()
    {
        string source = Fixture("generated/genEmpty.mdb");
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_identity_reopen_{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(source, tmp, true);
        try
        {
            using var conn = new UCanAccessConnection($"Data Source={tmp};Read Only=false");
            conn.Open();
            using (var create = conn.CreateCommand())
            {
                create.CommandText = "CREATE TABLE t_identity (id COUNTER PRIMARY KEY, value TEXT(20))";
                create.ExecuteNonQuery();
            }
            using (var insert = conn.CreateCommand())
            {
                insert.CommandText = "INSERT INTO t_identity (value) VALUES ('created')";
                insert.ExecuteNonQuery();
            }
            Assert.Equal(1L, conn.LastInsertedId);

            conn.Close();
            conn.Open();

            using var identity = conn.CreateCommand();
            identity.CommandText = "SELECT @@IDENTITY";
            Assert.Null(identity.ExecuteScalar());
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Select_identity_resets_when_the_source_file_is_reloaded()
    {
        string source = Fixture("generated/genEmpty.mdb");
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_identity_reload_{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(source, tmp, true);
        try
        {
            using var conn = new UCanAccessConnection($"Data Source={tmp};Read Only=false");
            conn.Open();
            using (var create = conn.CreateCommand())
            {
                create.CommandText = "CREATE TABLE t_identity (id COUNTER PRIMARY KEY, value TEXT(20))";
                create.ExecuteNonQuery();
            }
            using (var insert = conn.CreateCommand())
            {
                insert.CommandText = "INSERT INTO t_identity (value) VALUES ('created')";
                insert.ExecuteNonQuery();
            }
            Assert.Equal(1L, conn.LastInsertedId);

            DateTime changed = System.IO.File.GetLastWriteTimeUtc(tmp).AddMinutes(1);
            System.IO.File.SetLastWriteTimeUtc(tmp, changed);

            using var identity = conn.CreateCommand();
            identity.CommandText = "SELECT @@IDENTITY";
            Assert.Null(identity.ExecuteScalar());
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Named_parameters_bind_by_name()
    {
        using var conn = Open("sqljoin.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, qty FROM t_detail WHERE qty = @target AND master_id = :mid ORDER BY id";
        var target = cmd.CreateParameter();
        target.ParameterName = "@target";
        target.Value = 100;
        cmd.Parameters.Add(target);
        var mid = cmd.CreateParameter();
        mid.ParameterName = ":mid";
        mid.Value = 5;
        cmd.Parameters.Add(mid);

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(9L, reader.GetInt64(0));
        Assert.Equal(100L, reader.GetInt64(1));
        Assert.False(reader.Read());
    }

    [Fact]
    public void Named_parameters_out_of_order_still_bind()
    {
        using var conn = Open("sqljoin.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM t_detail WHERE qty = @a AND master_id = @b ORDER BY id";
        var b = cmd.CreateParameter();
        b.ParameterName = "@b";
        b.Value = 2;
        cmd.Parameters.Add(b);
        var a = cmd.CreateParameter();
        a.ParameterName = "@a";
        a.Value = 10;
        cmd.Parameters.Add(a);

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(4L, reader.GetInt64(0));
    }

    [Fact]
    public void Parameters_clause_sql_works()
    {
        using var conn = Open("sqljoin.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PARAMETERS [which] Long; SELECT id, name FROM t_master WHERE id = [which]";
        var which = cmd.CreateParameter();
        which.ParameterName = "which";
        which.Value = 3;
        cmd.Parameters.Add(which);

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(3L, reader.GetInt64(0));
        Assert.Equal("Gamma", reader.GetString(1));
    }

    [Fact]
    public void Group_by_and_aggregate()
    {
        using var conn = Open("pivot.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Month(c_dt) AS m, Count(*) AS n FROM t_pivot GROUP BY Month(c_dt) ORDER BY m";
        using var reader = cmd.ExecuteReader();

        var counts = new List<long>();
        while (reader.Read())
        {
            counts.Add(reader.GetInt64(1));
        }
        Assert.Equal(new[] { 2L, 2L }, counts);
    }

    [Fact]
    public void Works_with_dapper()
    {
        using var conn = Open("pivot.mdb");
        var rows = conn.Query("SELECT c_cod AS Code, c_val AS Value FROM t_pivot WHERE c_val > 100").ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal("paperino", (string)rows[0].Code);
        Assert.Equal(4444.0000m, (decimal)rows[0].Value);
    }

    [Fact]
    public void Saved_query_executes_as_view()
    {
        using var conn = Open("accessLike.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM q_like2";
        using var reader = cmd.ExecuteReader();

        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }
        Assert.Equal(new[] { "dd1" }, values);
    }

    [Fact]
    public void Saved_query_with_reserved_word_column_executes()
    {
        using var conn = Open("reservedWordLeave.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM [Without Leave]";
        using var reader = cmd.ExecuteReader();
        Assert.False(reader.Read()); // t_leave is empty, but the query must parse
    }

    [Fact]
    public void Positional_parameters_work()
    {
        using var conn = Open("pivot.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT c_cod, c_val FROM t_pivot WHERE c_cod = ?";
        var p = cmd.CreateParameter();
        p.Value = "paperino";
        cmd.Parameters.Add(p);

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("paperino", reader.GetString(0));
        Assert.Equal(4444.0000m, reader.GetDecimal(1));
        Assert.False(reader.Read());
    }

    [Fact]
    public void Extra_parameters_without_placeholders_are_rejected()
    {
        using var conn = Open("pivot.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT c_cod FROM t_pivot";
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "unused";
        parameter.Value = "paperino";
        cmd.Parameters.Add(parameter);

        Assert.Throws<InvalidOperationException>(() => cmd.ExecuteReader());
    }

    [Fact]
    public void Cancel_keeps_the_active_reader_usable()
    {
        using var conn = Open("pivot.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT c_cod FROM t_pivot";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());

        cmd.Cancel();

        Assert.True(reader.Read());
    }

    [Fact]
    public void Execute_non_query_on_readonly_connection_throws()
    {
        // "Read Only=true" (the default) must reject data modifications
        using var conn = Open("pivot.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE t_pivot SET c_cod = 'x'";
        Assert.Throws<UCanAccess.File.DatabaseException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public void Unsupported_statement_type_throws()
    {
        using var conn = Open("pivot.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "GRANT SELECT ON t TO someone";
        Assert.Throws<NotSupportedException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public void Missing_table_throws()
    {
        using var conn = Open("pivot.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM nope";
        Assert.Throws<SqliteException>(() => cmd.ExecuteReader());
    }

    [Fact]
    public void Provider_factory_create_connection_string_builder()
    {
        var builder = UCanAccessFactory.Instance.CreateConnectionStringBuilder();
        builder.DataSource = "c:\\x.mdb";
        builder.ReadOnly = true;
        Assert.Contains("c:\\x.mdb", builder.ConnectionString);
    }

    [Fact]
    public void Lazy_load_false_builds_the_mirror_during_open()
    {
        using var conn = Open("pivot.mdb", ";Lazy Load=false");
        Assert.NotNull(((UCanAccessConnection)conn).MirrorIfCreated);
    }

    [Fact]
    public void Keep_mirror_false_releases_the_operation_mirror_after_read()
    {
        using var conn = Open("pivot.mdb", ";Keep Mirror=false");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT c_cod FROM t_pivot ORDER BY c_cod";
        using (var reader = cmd.ExecuteReader())
        {
            var values = new List<string>();
            foreach (IDataRecord row in reader)
            {
                values.Add(row.GetString(0));
            }
            Assert.Equal(new[] { "paperino", "piero", "pippo", "pluto" }, values);
        }
        Assert.Null(((UCanAccessConnection)conn).MirrorIfCreated);
    }

    [Fact]
    public void Java_memory_and_path_keepmirror_options_map_to_the_local_mirror_modes()
    {
        string mirror = Path.Combine(Path.GetTempPath(), $"ucanaccess_keep_{Guid.NewGuid():N}.sqlite");
        try
        {
            var memory = new UCanAccessConnectionString("Data Source=x.mdb;memory=false");
            Assert.Equal("file", memory.MirrorMode);

            var persistent = new UCanAccessConnectionString($"Data Source=x.mdb;keepMirror={mirror}");
            Assert.True(persistent.KeepMirror);
            Assert.Equal("file", persistent.MirrorMode);
            Assert.Equal(mirror, persistent.MirrorPath);

            using (var conn = new UCanAccessConnection(
                       $"Data Source={Fixture("pivot.mdb")};Read Only=true;memory=false;keepMirror={mirror}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM t_pivot";
                Assert.Equal(4L, cmd.ExecuteScalar());
            }
            Assert.True(System.IO.File.Exists(mirror));
        }
        finally
        {
            System.IO.File.Delete(mirror);
        }
    }

    [Fact]
    public void Immediately_release_resources_is_an_alias_for_a_transient_mirror()
    {
        var options = new UCanAccessConnectionString(
            "Data Source=x.mdb;Keep Mirror=true;ImmediatelyReleaseResources=true;preventReloading=true;sysSchema=true");

        Assert.True(options.ImmediatelyReleaseResources);
        Assert.False(options.KeepMirror);
        Assert.True(options.PreventReloading);
        Assert.True(options.ShowSchema);

        var builder = new UCanAccessConnectionStringBuilder
        {
            DataSource = "x.mdb",
            PersistentMirrorPath = "persistent.sqlite",
            SingleConnection = true,
            SysSchema = true,
        };
        Assert.True(builder.SingleConnection);
        Assert.True(builder.ImmediatelyReleaseResources);
        Assert.True(builder.SysSchema);
        Assert.True(builder.ShowSchema);

        var aliases = new UCanAccessConnectionStringBuilder
        {
            ConnectionString = "Data Source=x.mdb;Memory=false;SingleConnection=true;SysSchema=true",
        };
        Assert.False(aliases.Memory);
        Assert.Equal("file", aliases.MirrorMode);
        Assert.True(aliases.ImmediatelyReleaseResources);
        Assert.True(aliases.SingleConnection);
        Assert.True(aliases.SysSchema);
        Assert.True(aliases.ShowSchema);

        var roundTrip = new UCanAccessConnectionString(builder.ConnectionString);
        Assert.False(roundTrip.KeepMirror);
        Assert.Equal("persistent.sqlite", roundTrip.PersistentMirrorPath);
        Assert.True(roundTrip.ImmediatelyReleaseResources);
        Assert.True(roundTrip.ShowSchema);
    }
}
