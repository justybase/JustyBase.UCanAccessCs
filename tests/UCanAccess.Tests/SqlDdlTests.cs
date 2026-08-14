using System.Data;
using System.Data.Common;
using Xunit;

namespace UCanAccess.Tests;

/// <summary>
/// SQL data-definition statements (CREATE / DROP / ALTER) through the ADO.NET provider.
/// Writes go to the MDB file; the Java cross-check verifies the file stays readable
/// by the original Jackcess after the DDL.
/// </summary>
public class SqlDdlTests
{
    private readonly ITestOutputHelper _output;

    public SqlDdlTests(ITestOutputHelper output) => _output = output;

    static SqlDdlTests()
    {
        DbProviderFactories.RegisterFactory("UCanAccess", UCanAccessFactory.Instance);
        DbProviderFactories.RegisterFactory("UCanAccess.UCanAccessFactory", UCanAccessFactory.Instance);
    }

    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static DbConnection OpenWritable(string tmp)
    {
        var conn = DbProviderFactories.GetFactory("UCanAccess")?.CreateConnection()
            ?? throw new InvalidOperationException("provider not registered");
        conn.ConnectionString = $"Data Source={tmp};Read Only=false";
        conn.Open();
        return conn;
    }

    private static string TempCopy(string fixture)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_ddl_{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(fixture, tmp, true);
        return tmp;
    }

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    [Fact]
    public void Create_table_then_insert_select()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE t_new (id LONG INTEGER PRIMARY KEY, name TEXT(20), amount MONEY, active BIT, created DATETIME)");
            Exec(conn, "INSERT INTO t_new (id, name, amount, active, created) VALUES (1, 'alpha', 12.50, true, #1/2/2023#)");
            Exec(conn, "INSERT INTO t_new (id, name, amount, active, created) VALUES (2, 'beta', -3.25, false, #2/3/2024#)");

            Assert.Equal(2L, Scalar(conn, "SELECT count(*) FROM t_new"));
            Assert.Equal("alpha", Scalar(conn, "SELECT name FROM t_new WHERE id = 1"));
            Assert.Equal(true, Scalar(conn, "SELECT active FROM t_new WHERE id = 1"));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Create_table_as_select_copies_schema_and_data()
    {
        string tmp = TempCopy(Fixture("sqljoin.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE t_ctas AS SELECT id, name, budget FROM t_master WHERE id <= 2 WITH DATA");

            Assert.Equal(2L, Scalar(conn, "SELECT count(*) FROM t_ctas"));
            Assert.Equal("Alpha", Scalar(conn, "SELECT name FROM t_ctas WHERE id = 1"));
            Assert.Equal(3, conn.GetSchema("Columns", new string?[] { null, null, "t_ctas", null }).Rows.Count);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Create_table_as_select_with_no_data_copies_only_schema()
    {
        string tmp = TempCopy(Fixture("sqljoin.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE t_ctas_empty AS SELECT id, name FROM t_master WITH NO DATA");
            Assert.Equal(0L, Scalar(conn, "SELECT count(*) FROM t_ctas_empty"));
            Assert.Equal(2, conn.GetSchema("Columns", new string?[] { null, null, "t_ctas_empty", null }).Rows.Count);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Select_into_creates_table_with_schema_and_data()
    {
        string tmp = TempCopy(Fixture("sqljoin.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                Exec(conn, "SELECT id, name, budget INTO t_si FROM t_master WHERE id <= 2");

                Assert.Equal(2L, Scalar(conn, "SELECT count(*) FROM t_si"));
                Assert.Equal("Alpha", Scalar(conn, "SELECT name FROM t_si WHERE id = 1"));
                Assert.Equal(3, conn.GetSchema("Columns", new string?[] { null, null, "t_si", null }).Rows.Count);
                Assert.NotNull(((UCanAccessConnection)conn).AccessDatabase.GetTable("t_si"));
            }

            using var conn2 = OpenWritable(tmp);
            Assert.Equal(2L, Scalar(conn2, "SELECT count(*) FROM t_si"));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Select_star_into_creates_table_with_all_columns()
    {
        string tmp = TempCopy(Fixture("sqljoin.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "SELECT * INTO t_si_star FROM t_master WHERE id = 1");
            Assert.Equal(1L, Scalar(conn, "SELECT count(*) FROM t_si_star"));
            Assert.Equal("Alpha", Scalar(conn, "SELECT name FROM t_si_star WHERE id = 1"));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Select_into_rejects_existing_table_and_missing_from()
    {
        string tmp = TempCopy(Fixture("sqljoin.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Assert.ThrowsAny<Exception>(() => Exec(conn, "SELECT id INTO t_master FROM t_master WHERE 1=0"));
            Assert.ThrowsAny<Exception>(() => Exec(conn, "SELECT id INTO t_no_from"));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Select_into_via_ExecuteScalar_or_Reader_is_rejected()
    {
        string tmp = TempCopy(Fixture("sqljoin.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id INTO t_si_reader FROM t_master";
                Assert.Throws<InvalidOperationException>(() => cmd.ExecuteReader());
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id INTO t_si_scalar FROM t_master";
                Assert.Throws<InvalidOperationException>(() => cmd.ExecuteScalar());
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Select_into_within_transaction_commits_and_rolls_back()
    {
        string tmp = TempCopy(Fixture("sqljoin.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            using (var tx = conn.BeginTransaction())
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT id, name INTO t_si_tx FROM t_master WHERE id = 1";
                cmd.ExecuteNonQuery();
                Assert.Equal(1L, Scalar(conn, "SELECT count(*) FROM t_si_tx"));
                tx.Rollback();
            }
            Assert.ThrowsAny<Exception>(() => Scalar(conn, "SELECT count(*) FROM t_si_tx"));

            using (var tx = conn.BeginTransaction())
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT id INTO t_si_tx2 FROM t_master WHERE id = 2";
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
            Assert.Equal(1L, Scalar(conn, "SELECT count(*) FROM t_si_tx2"));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Create_table_not_null_is_enforced_and_reported_in_schema()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                Exec(conn, "CREATE TABLE t_required (id INTEGER NOT NULL, name TEXT(20))");
                Assert.ThrowsAny<Exception>(() => Exec(conn,
                    "INSERT INTO t_required (id, name) VALUES (NULL, 'bad')"));
                Exec(conn, "INSERT INTO t_required (id, name) VALUES (1, 'ok')");

                var fileDb = ((UCanAccessConnection)conn).AccessDatabase;
                Assert.True(fileDb.GetTable("t_required")!.Columns.Single(c => c.Name == "id").Required);
                DataTable columns = conn.GetSchema("Columns", new string?[] { null, null, "t_required", "id" });
                Assert.Equal("NO", columns.Rows[0]["IS_NULLABLE"]);
            }

            using (var conn = OpenWritable(tmp))
            {
                Assert.Equal(1L, Scalar(conn, "SELECT count(*) FROM t_required"));
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Add_not_null_column_to_nonempty_table_is_rejected_before_rewrite()
    {
        string tmp = TempCopy(Fixture("generated/genIndexed.mdb"));
        try
        {
            byte[] before = System.IO.File.ReadAllBytes(tmp);
            using (var conn = OpenWritable(tmp))
            {
                Assert.Throws<NotSupportedException>(() => Exec(conn,
                    "ALTER TABLE t_indexed ADD COLUMN required_note TEXT(20) NOT NULL"));
            }
            Assert.Equal(before, System.IO.File.ReadAllBytes(tmp));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Drop_middle_column_preserves_the_other_values()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE t_columns (a INTEGER, b TEXT(20), c INTEGER)");
            Exec(conn, "INSERT INTO t_columns (a, b, c) VALUES (1, 'middle', 3)");
            Exec(conn, "ALTER TABLE t_columns DROP COLUMN b");

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT a, c FROM t_columns";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(3L, reader.GetInt64(1));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Create_table_with_autonumber_and_primary_key()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE t_ids (id COUNTER PRIMARY KEY, val TEXT(10))");
            Exec(conn, "INSERT INTO t_ids (val) VALUES ('a')");
            Exec(conn, "INSERT INTO t_ids (val) VALUES ('b')");

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id FROM t_ids ORDER BY id";
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.True(reader.Read());
                Assert.Equal(2L, reader.GetInt64(0));
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Drop_table_removes_it()
    {
        string tmp = TempCopy(Fixture("sqljoin.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "DROP TABLE t_detail");

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM t_master";
                Assert.Equal(7L, cmd.ExecuteScalar());
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM t_detail";
                Assert.ThrowsAny<Exception>(() => cmd.ExecuteScalar());
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Jackcess_reads_created_table()
    {
        if (!JavaAvailable() || FindJar("jackcess-5.1.5.jar") == null
            || !Directory.Exists(Path.Combine(FindRepoRoot(), "tools", "JavaOracle", "classes")))
        {
            _output.WriteLine("SKIPPED: java/jar/classes not available");
            throw Xunit.Sdk.SkipException.ForSkip("java/jar/classes not available");
        }
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                Exec(conn, "CREATE TABLE t_created (id INTEGER PRIMARY KEY, name TEXT(20) NOT NULL, amount MONEY)");
                Exec(conn, "INSERT INTO t_created (id, name, amount) VALUES (1, 'alpha', 12.50)");
            }

            string json = RunDbDump(tmp);
            _output.WriteLine(json);
            Assert.Contains("t_created", json);
            Assert.Contains("alpha", json);
            Assert.Contains("\"name\"", json);
            Assert.Contains("\"required\": true", json);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Jackcess_reads_after_drop_table()
    {
        if (!JavaAvailable() || FindJar("jackcess-5.1.5.jar") == null
            || !Directory.Exists(Path.Combine(FindRepoRoot(), "tools", "JavaOracle", "classes")))
        {
            _output.WriteLine("SKIPPED: java/jar/classes not available");
            throw Xunit.Sdk.SkipException.ForSkip("java/jar/classes not available");
        }
        string tmp = TempCopy(Fixture("sqljoin.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                Exec(conn, "DROP TABLE t_detail");
            }

            string json = RunDbDump(tmp);
            _output.WriteLine(json);
            Assert.DoesNotContain("t_detail", json);
            Assert.Contains("t_master", json);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Create_index_on_existing_table()
    {
        string tmp = TempCopy(Fixture("generated/genIndexed.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                var beforeLocations = ((UCanAccessConnection)conn).AccessDatabase
                    .GetTable("t_indexed")!.RowLocations()
                    .Select(location => (location.PageNumber, location.RowNumber)).ToList();
                Exec(conn, "CREATE INDEX idx_val ON t_indexed (value)");

                var db = ((UCanAccess.UCanAccessConnection)conn).AccessDatabase;
                var names = db.GetIndexNames("t_indexed");
                Assert.Contains("idx_val", names);
                Assert.Contains("idx_code", names);
                Assert.Contains("PrimaryKey", names);
                var afterLocations = db.GetTable("t_indexed")!.RowLocations()
                    .Select(location => (location.PageNumber, location.RowNumber)).ToList();
                Assert.Equal(beforeLocations, afterLocations);
            }
            using (var conn = Open("tmp", tmp))
            {
                Assert.Equal(50L, Scalar(conn, "SELECT count(*) FROM t_indexed"));
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Drop_index_on_existing_table()
    {
        string tmp = TempCopy(Fixture("generated/genIndexed.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                var beforeLocations = ((UCanAccessConnection)conn).AccessDatabase
                    .GetTable("t_indexed")!.RowLocations()
                    .Select(location => (location.PageNumber, location.RowNumber)).ToList();
                Exec(conn, "DROP INDEX idx_code ON t_indexed");

                var db = ((UCanAccess.UCanAccessConnection)conn).AccessDatabase;
                var names = db.GetIndexNames("t_indexed");
                Assert.DoesNotContain("idx_code", names);
                Assert.Contains("PrimaryKey", names);
                var afterLocations = db.GetTable("t_indexed")!.RowLocations()
                    .Select(location => (location.PageNumber, location.RowNumber)).ToList();
                Assert.Equal(beforeLocations, afterLocations);
            }
            using (var conn = Open("tmp", tmp))
            {
                Assert.Equal(50L, Scalar(conn, "SELECT count(*) FROM t_indexed"));
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Create_unique_index_prefix_is_supported()
    {
        string tmp = TempCopy(Fixture("generated/genIndexed.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE UNIQUE INDEX idx_value_unique ON t_indexed (value)");

            var index = ((UCanAccess.UCanAccessConnection)conn).AccessDatabase
                .GetIndexInfo("t_indexed")
                .Single(info => info.Name.Equals("idx_value_unique", StringComparison.OrdinalIgnoreCase));
            Assert.True(index.Unique);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Failed_index_mutation_leaves_original_bytes_untouched()
    {
        string tmp = TempCopy(Fixture("generated/genIndexed.mdb"));
        try
        {
            byte[] before = System.IO.File.ReadAllBytes(tmp);
            using (var conn = OpenWritable(tmp))
            {
                Assert.ThrowsAny<Exception>(() => Exec(conn,
                    "CREATE INDEX idx_missing ON t_indexed (does_not_exist)"));
            }
            Assert.Equal(before, System.IO.File.ReadAllBytes(tmp));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Jackcess_reads_file_after_create_index()
    {
        if (!JavaAvailable() || FindJar("jackcess-5.1.5.jar") == null
            || !Directory.Exists(Path.Combine(FindRepoRoot(), "tools", "JavaOracle", "classes")))
        {
            _output.WriteLine("SKIPPED: java/jar/classes not available");
            throw Xunit.Sdk.SkipException.ForSkip("java/jar/classes not available");
        }
        string tmp = TempCopy(Fixture("generated/genIndexed.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                Exec(conn, "CREATE INDEX idx_value_desc ON t_indexed (value DESC)");
            }

            string json = RunDbDump(tmp);
            _output.WriteLine(json);
            Assert.Contains("t_indexed", json);
            Assert.Contains("code01", json);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Alter_table_add_and_drop_column()
    {
        string tmp = TempCopy(Fixture("generated/genIndexed.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                Exec(conn, "ALTER TABLE t_indexed ADD COLUMN note TEXT(20)");
                Exec(conn, "UPDATE t_indexed SET note = 'n' WHERE id = 1");

                Assert.Equal("n", Scalar(conn, "SELECT note FROM t_indexed WHERE id = 1"));
                Assert.Equal(49L, Scalar(conn, "SELECT count(*) FROM t_indexed WHERE note IS NULL"));

                Exec(conn, "ALTER TABLE t_indexed DROP COLUMN note");
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT count(*) FROM t_indexed";
                    Assert.Equal(50L, cmd.ExecuteScalar());
                }
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Create_table_accepts_named_primary_and_unique_constraints()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE t_named (id LONG, code TEXT(20), CONSTRAINT pk_named PRIMARY KEY (id), CONSTRAINT uq_named UNIQUE (code))");
            var indexes = ((UCanAccessConnection)conn).AccessDatabase.GetIndexInfo("t_named");
            Assert.Contains(indexes, index => index.Name == "pk_named" && index.PrimaryKey);
            Assert.Contains(indexes, index => index.Name == "uq_named" && index.Unique);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Defaults_are_persisted_and_used_for_omitted_insert_columns()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                Exec(conn, "CREATE TABLE t_defaults (id LONG, label TEXT(20) DEFAULT 'fallback', keyword TEXT(20) DEFAULT 'not', amount LONG DEFAULT (3))");
                Exec(conn, "INSERT INTO t_defaults (id) VALUES (1)");
                Exec(conn, "INSERT INTO t_defaults (id, label, amount) VALUES (2, NULL, NULL)");

                Assert.Equal("fallback", Scalar(conn, "SELECT label FROM t_defaults WHERE id = 1"));
                Assert.Equal("not", Scalar(conn, "SELECT keyword FROM t_defaults WHERE id = 1"));
                Assert.Equal(3L, Scalar(conn, "SELECT amount FROM t_defaults WHERE id = 1"));
                Assert.Null(Scalar(conn, "SELECT label FROM t_defaults WHERE id = 2"));
                Assert.Equal("'fallback'", ((UCanAccessConnection)conn).AccessDatabase
                    .GetTable("t_defaults")!.Columns.Single(c => c.Name == "label").DefaultValue);
                DataTable schema = conn.GetSchema("Columns",
                    new[] { null, null, "t_defaults", "label" });
                Assert.Equal("'fallback'", schema.Rows[0]["COLUMN_DEFAULT"]);
            }

            using (var reopened = OpenWritable(tmp))
            {
                Assert.Equal("fallback", Scalar(reopened, "SELECT label FROM t_defaults WHERE id = 1"));
            }
            if (JavaAvailable() && FindJar("jackcess-5.1.5.jar") != null
                && Directory.Exists(Path.Combine(FindRepoRoot(), "tools", "JavaOracle", "classes")))
            {
                Assert.Contains("t_defaults", RunDbDump(tmp));
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Access_default_literal_syntax_is_applied_to_omitted_columns()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE t_access_defaults (id LONG, label TEXT(20) DEFAULT \"rrr\", enabled YESNO DEFAULT No)");
            Exec(conn, "INSERT INTO t_access_defaults (id) VALUES (1)");
            Assert.Equal("rrr", Scalar(conn, "SELECT label FROM t_access_defaults WHERE id = 1"));
            Assert.Equal(false, Scalar(conn, "SELECT enabled FROM t_access_defaults WHERE id = 1"));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Unary_numeric_defaults_roundtrip_without_whitespace_between_sign_and_value()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE t_signed_defaults (id LONG, negative LONG DEFAULT -1, positive LONG DEFAULT +1)");
            Exec(conn, "INSERT INTO t_signed_defaults (id) VALUES (1)");
            Assert.Equal(-1L, Scalar(conn, "SELECT negative FROM t_signed_defaults WHERE id = 1"));
            Assert.Equal(1L, Scalar(conn, "SELECT positive FROM t_signed_defaults WHERE id = 1"));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Add_required_column_with_default_backfills_existing_rows()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE t_add_default (id LONG)");
            Exec(conn, "INSERT INTO t_add_default (id) VALUES (1)");
            Exec(conn, "ALTER TABLE t_add_default ADD COLUMN note TEXT(20) DEFAULT 'added' NOT NULL");
            Assert.Equal("added", Scalar(conn, "SELECT note FROM t_add_default WHERE id = 1"));
            Assert.True(((UCanAccessConnection)conn).AccessDatabase.GetTable("t_add_default")!
                .Columns.Single(c => c.Name == "note").Required);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Alter_table_add_and_drop_foreign_key_enforces_relationship()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE fk_parent (id LONG PRIMARY KEY)");
            Exec(conn, "CREATE TABLE fk_child (id LONG PRIMARY KEY, parent_id LONG)");
            Exec(conn, "INSERT INTO fk_parent (id) VALUES (1)");
            Exec(conn, "INSERT INTO fk_child (id, parent_id) VALUES (10, 1)");
            Exec(conn, "ALTER TABLE fk_child ADD CONSTRAINT fk_child_parent FOREIGN KEY (parent_id) REFERENCES fk_parent (id) ON DELETE CASCADE");
            Exec(conn, "ALTER TABLE fk_child ADD COLUMN note TEXT(20)");

            Assert.ThrowsAny<Exception>(() => Exec(conn,
                "INSERT INTO fk_child (id, parent_id) VALUES (11, 99)"));
            Assert.Contains(((UCanAccessConnection)conn).AccessDatabase.GetRelationships(),
                relationship => relationship.Name == "fk_child_parent" && relationship.CascadeDeletes);
            Assert.Contains(((UCanAccessConnection)conn).AccessDatabase.GetIndexInfo("fk_child"),
                index => index.Name == "fk_child_parent" && index.ForeignKey);

            Exec(conn, "DELETE FROM fk_parent WHERE id = 1");
            Assert.Equal(0L, Scalar(conn, "SELECT count(*) FROM fk_child"));
            if (JavaAvailable() && FindJar("jackcess-5.1.5.jar") != null
                && Directory.Exists(Path.Combine(FindRepoRoot(), "tools", "JavaOracle", "classes")))
            {
                Assert.Contains("fk_child_parent", RunDbDump(tmp));
            }
            Exec(conn, "ALTER TABLE fk_child DROP CONSTRAINT fk_child_parent");
            Assert.Empty(((UCanAccessConnection)conn).AccessDatabase.GetRelationships());
            Exec(conn, "ALTER TABLE fk_child DROP COLUMN parent_id");
            Assert.DoesNotContain(((UCanAccessConnection)conn).AccessDatabase.GetTable("fk_child")!.Columns,
                column => column.Name.Equals("parent_id", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Drop_constraint_must_belong_to_the_alter_table_target()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE fk_target_parent (id LONG PRIMARY KEY)");
            Exec(conn, "CREATE TABLE fk_target_child (id LONG PRIMARY KEY, parent_id LONG)");
            Exec(conn, "ALTER TABLE fk_target_child ADD CONSTRAINT fk_target FOREIGN KEY (parent_id) REFERENCES fk_target_parent (id)");

            Assert.ThrowsAny<Exception>(() => Exec(conn,
                "ALTER TABLE unrelated_table DROP CONSTRAINT fk_target"));
            Assert.Contains(((UCanAccessConnection)conn).AccessDatabase.GetRelationships(),
                relationship => relationship.Name == "fk_target");
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Create_table_accepts_foreign_key_constraint()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE fk_create_parent (id LONG PRIMARY KEY)");
            Exec(conn, "INSERT INTO fk_create_parent (id) VALUES (1)");
            Exec(conn, "CREATE TABLE fk_create_child (id LONG PRIMARY KEY, parent_id LONG, CONSTRAINT fk_create FOREIGN KEY (parent_id) REFERENCES fk_create_parent (id) ON DELETE CASCADE)");

            Assert.Contains(((UCanAccessConnection)conn).AccessDatabase.GetRelationships(),
                relationship => relationship.Name == "fk_create");
            Assert.ThrowsAny<Exception>(() => Exec(conn,
                "INSERT INTO fk_create_child (id, parent_id) VALUES (2, 99)"));
            Exec(conn, "INSERT INTO fk_create_child (id, parent_id) VALUES (3, 1)");
            Exec(conn, "DELETE FROM fk_create_parent WHERE id = 1");
            Assert.Equal(0L, Scalar(conn, "SELECT count(*) FROM fk_create_child"));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Failed_create_table_foreign_key_is_rolled_back()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Assert.ThrowsAny<Exception>(() => Exec(conn,
                "CREATE TABLE fk_failed (id LONG, parent_id LONG, CONSTRAINT fk_missing FOREIGN KEY (parent_id) REFERENCES no_such_parent (id))"));
            Assert.DoesNotContain("fk_failed",
                ((UCanAccessConnection)conn).AccessDatabase.GetTableNames());
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Parent_definition_replacement_retargets_child_foreign_key_index()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                Exec(conn, "CREATE TABLE fk_retarget_parent (id LONG PRIMARY KEY)");
                Exec(conn, "CREATE TABLE fk_retarget_child (id LONG PRIMARY KEY, parent_id LONG)");
                Exec(conn, "INSERT INTO fk_retarget_parent (id) VALUES (1)");
                Exec(conn, "ALTER TABLE fk_retarget_child ADD CONSTRAINT fk_retarget FOREIGN KEY (parent_id) REFERENCES fk_retarget_parent (id)");
                Exec(conn, "ALTER TABLE fk_retarget_parent ADD COLUMN note TEXT(20)");
                Assert.Contains(((UCanAccessConnection)conn).AccessDatabase.GetIndexInfo("fk_retarget_child"),
                    index => index.ForeignKey);
            }

            using (var reopened = OpenWritable(tmp))
            {
                Exec(reopened, "INSERT INTO fk_retarget_child (id, parent_id) VALUES (1, 1)");
                Assert.ThrowsAny<Exception>(() => Exec(reopened,
                    "INSERT INTO fk_retarget_child (id, parent_id) VALUES (2, 99)"));
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Shared_foreign_key_index_survives_until_last_relationship_is_dropped()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE fk_shared_parent1 (id LONG PRIMARY KEY)");
            Exec(conn, "CREATE TABLE fk_shared_child (id LONG PRIMARY KEY, parent_id LONG)");
            Exec(conn, "INSERT INTO fk_shared_parent1 (id) VALUES (1)");
            Exec(conn, "ALTER TABLE fk_shared_child ADD CONSTRAINT fk_shared_one FOREIGN KEY (parent_id) REFERENCES fk_shared_parent1 (id)");
            Exec(conn, "ALTER TABLE fk_shared_child ADD CONSTRAINT fk_shared_two FOREIGN KEY (parent_id) REFERENCES fk_shared_parent1 (id)");

            Exec(conn, "ALTER TABLE fk_shared_child DROP CONSTRAINT fk_shared_one");
            Assert.Contains(((UCanAccessConnection)conn).AccessDatabase.GetRelationships(),
                relationship => relationship.Name == "fk_shared_two");
            Assert.Contains(((UCanAccessConnection)conn).AccessDatabase.GetIndexInfo("fk_shared_child"),
                index => index.ForeignKey);

            Exec(conn, "ALTER TABLE fk_shared_child DROP CONSTRAINT fk_shared_two");
            Assert.DoesNotContain(((UCanAccessConnection)conn).AccessDatabase.GetIndexInfo("fk_shared_child"),
                index => index.ForeignKey);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Dropping_parent_removes_generated_child_foreign_key_index()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE fk_drop_parent (id LONG PRIMARY KEY)");
            Exec(conn, "CREATE TABLE fk_drop_child (id LONG PRIMARY KEY, parent_id LONG)");
            Exec(conn, "ALTER TABLE fk_drop_child ADD CONSTRAINT fk_drop FOREIGN KEY (parent_id) REFERENCES fk_drop_parent (id)");
            Exec(conn, "DROP TABLE fk_drop_parent");
            Assert.DoesNotContain("fk_drop_parent",
                ((UCanAccessConnection)conn).AccessDatabase.GetTableNames());
            Assert.Empty(((UCanAccessConnection)conn).AccessDatabase.GetRelationships());
            Assert.DoesNotContain(((UCanAccessConnection)conn).AccessDatabase.GetIndexInfo("fk_drop_child"),
                index => index.ForeignKey);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Dropping_one_of_two_parent_keys_removes_only_its_foreign_index()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE fk_multi_parent1 (id LONG PRIMARY KEY)");
            Exec(conn, "CREATE TABLE fk_multi_parent2 (id LONG PRIMARY KEY)");
            Exec(conn, "CREATE TABLE fk_multi_child (id LONG PRIMARY KEY, parent_id LONG)");
            Exec(conn, "ALTER TABLE fk_multi_child ADD CONSTRAINT fk_multi_one FOREIGN KEY (parent_id) REFERENCES fk_multi_parent1 (id)");
            Exec(conn, "ALTER TABLE fk_multi_child ADD CONSTRAINT fk_multi_two FOREIGN KEY (parent_id) REFERENCES fk_multi_parent2 (id)");

            Exec(conn, "DROP TABLE fk_multi_parent1");
            Assert.Contains(((UCanAccessConnection)conn).AccessDatabase.GetRelationships(),
                relationship => relationship.Name == "fk_multi_two");
            Assert.DoesNotContain(((UCanAccessConnection)conn).AccessDatabase.GetRelationships(),
                relationship => relationship.Name == "fk_multi_one");
            Assert.Single(((UCanAccessConnection)conn).AccessDatabase.GetIndexInfo("fk_multi_child"),
                index => index.ForeignKey);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Self_referencing_relationship_can_drop_its_retargeted_supporting_index()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                Exec(conn, "CREATE TABLE fk_self (id LONG PRIMARY KEY, parent_id LONG)");
                Exec(conn, "ALTER TABLE fk_self ADD CONSTRAINT fk_self_parent FOREIGN KEY (parent_id) REFERENCES fk_self (id)");
            }

            using (var reopened = OpenWritable(tmp))
            {
                Exec(reopened, "ALTER TABLE fk_self DROP CONSTRAINT fk_self_parent");
                Assert.DoesNotContain(((UCanAccessConnection)reopened).AccessDatabase.GetIndexInfo("fk_self"),
                    index => index.ForeignKey);
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Parent_index_number_shift_retargets_child_foreign_key_metadata()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                Exec(conn, "CREATE TABLE fk_index_parent (id LONG, code LONG)");
                Exec(conn, "CREATE INDEX fk_index_extra ON fk_index_parent (id)");
                Exec(conn, "CREATE UNIQUE INDEX fk_index_key ON fk_index_parent (code)");
                Exec(conn, "CREATE TABLE fk_index_child (id LONG PRIMARY KEY, code LONG)");
                Exec(conn, "ALTER TABLE fk_index_child ADD CONSTRAINT fk_index_rel FOREIGN KEY (code) REFERENCES fk_index_parent (code)");
                Exec(conn, "DROP INDEX fk_index_extra ON fk_index_parent");
            }

            using (var reopened = OpenWritable(tmp))
            {
                Exec(reopened, "INSERT INTO fk_index_parent (id, code) VALUES (1, 7)");
                Exec(reopened, "INSERT INTO fk_index_child (id, code) VALUES (1, 7)");
                Exec(reopened, "ALTER TABLE fk_index_child DROP CONSTRAINT fk_index_rel");
                Assert.DoesNotContain(((UCanAccessConnection)reopened).AccessDatabase.GetIndexInfo("fk_index_child"),
                    index => index.ForeignKey);
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Alter_columns_preserves_autonumber_values_and_counter()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                Exec(conn, "CREATE TABLE t_auto_ddl (id COUNTER PRIMARY KEY, val TEXT(20))");
                Exec(conn, "INSERT INTO t_auto_ddl (val) VALUES ('kept')");
                Exec(conn, "INSERT INTO t_auto_ddl (val) VALUES ('deleted')");
                Exec(conn, "DELETE FROM t_auto_ddl WHERE id = 2");

                Exec(conn, "ALTER TABLE t_auto_ddl ADD COLUMN note TEXT(20)");
                Exec(conn, "UPDATE t_auto_ddl SET note = 'after-add' WHERE id = 1");
                Exec(conn, "ALTER TABLE t_auto_ddl DROP COLUMN note");
                Exec(conn, "INSERT INTO t_auto_ddl (val) VALUES ('next')");

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT id, val FROM t_auto_ddl ORDER BY id";
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.Equal("kept", reader.GetString(1));
                Assert.True(reader.Read());
                Assert.Equal(3L, reader.GetInt64(0));
                Assert.Equal("next", reader.GetString(1));
                Assert.False(reader.Read());
            }

            using (var reopened = OpenWritable(tmp))
            {
                Exec(reopened, "INSERT INTO t_auto_ddl (val) VALUES ('reopened')");
                Assert.Equal(4L, Scalar(reopened, "SELECT id FROM t_auto_ddl WHERE val = 'reopened'"));
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Alter_table_rename_updates_the_catalog_and_preserves_rows()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE before_rename (id INTEGER PRIMARY KEY, value TEXT(20))");
            Exec(conn, "INSERT INTO before_rename (id, value) VALUES (1, 'kept')");

            Exec(conn, "ALTER TABLE before_rename RENAME TO after_rename");

            Assert.Equal("kept", Scalar(conn, "SELECT value FROM after_rename WHERE id = 1"));
            Assert.ThrowsAny<Exception>(() => Scalar(conn, "SELECT value FROM before_rename"));
            Assert.Contains("after_rename", ((UCanAccessConnection)conn).AccessDatabase.GetTableNames());
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Alter_table_add_primary_key_matches_the_upstream_ddl_boundary()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using var conn = OpenWritable(tmp);
            Exec(conn, "CREATE TABLE t_add_pk (id LONG, value TEXT(20))");
            Exec(conn, "INSERT INTO t_add_pk (id, value) VALUES (1, 'one')");
            Exec(conn, "ALTER TABLE t_add_pk ADD CONSTRAINT pk_t_add_pk PRIMARY KEY (id)");

            Assert.ThrowsAny<Exception>(() => Exec(conn,
                "INSERT INTO t_add_pk (id, value) VALUES (1, 'duplicate')"));
            Assert.Contains(((UCanAccessConnection)conn).AccessDatabase.GetIndexInfo("t_add_pk"),
                index => index.Name == "pk_t_add_pk" && index.PrimaryKey);

            Assert.Throws<NotSupportedException>(() => Exec(conn,
                "ALTER TABLE t_add_pk DROP CONSTRAINT pk_t_add_pk"));
            Assert.Throws<NotSupportedException>(() => Exec(conn,
                "ALTER TABLE t_add_pk DROP PRIMARY KEY"));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Alter_table_add_primary_key_rejects_a_readonly_connection_before_staging()
    {
        string tmp = TempCopy(Fixture("generated/genEmpty.mdb"));
        try
        {
            using (var writable = OpenWritable(tmp))
            {
                Exec(writable, "CREATE TABLE t_readonly_pk (id LONG, value TEXT(20))");
            }
            byte[] before = System.IO.File.ReadAllBytes(tmp);

            using (var readonlyConnection = new UCanAccessConnection($"Data Source={tmp};Read Only=true"))
            {
                readonlyConnection.Open();
                using var command = readonlyConnection.CreateCommand();
                command.CommandText =
                    "ALTER TABLE t_readonly_pk ADD CONSTRAINT pk_t_readonly_pk PRIMARY KEY (id)";
                Assert.Throws<UCanAccess.File.DatabaseException>(() => command.ExecuteNonQuery());
            }

            Assert.Equal(before, System.IO.File.ReadAllBytes(tmp));
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Create_and_drop_view_roundtrip_through_access_catalog()
    {
        string tmp = TempCopy(Fixture("accessLike.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                Exec(conn, "CREATE VIEW q_managed AS SELECT Campo2 FROM t_like2 WHERE Campo2 LIKE 'd*'");
                Assert.Equal(2L, Scalar(conn, "SELECT COUNT(*) FROM q_managed"));
                DataTable views = conn.GetSchema("Views");
                Assert.Contains(views.AsEnumerable(), row =>
                    string.Equals(row.Field<string>("TABLE_NAME"), "q_managed",
                        StringComparison.OrdinalIgnoreCase));
            }

            using (var reopened = OpenWritable(tmp))
            {
                Assert.Equal(2L, Scalar(reopened, "SELECT COUNT(*) FROM q_managed"));
                Exec(reopened, "DROP VIEW q_managed");
                Assert.ThrowsAny<Exception>(() => Scalar(reopened, "SELECT COUNT(*) FROM q_managed"));
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Jackcess_reads_file_after_alter_column()
    {
        if (!JavaAvailable() || FindJar("jackcess-5.1.5.jar") == null
            || !Directory.Exists(Path.Combine(FindRepoRoot(), "tools", "JavaOracle", "classes")))
        {
            _output.WriteLine("SKIPPED: java/jar/classes not available");
            throw Xunit.Sdk.SkipException.ForSkip("java/jar/classes not available");
        }
        string tmp = TempCopy(Fixture("generated/genIndexed.mdb"));
        try
        {
            using (var conn = OpenWritable(tmp))
            {
                Exec(conn, "ALTER TABLE t_indexed ADD COLUMN extra MONEY");
            }

            string json = RunDbDump(tmp);
            _output.WriteLine(json);
            Assert.Contains("t_indexed", json);
            Assert.Contains("extra", json);
            Assert.Contains("code01", json);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    private static DbConnection Open(string name, string tmp)
    {
        var conn = DbProviderFactories.GetFactory("UCanAccess")?.CreateConnection()
            ?? throw new InvalidOperationException("provider not registered");
        conn.ConnectionString = $"Data Source={tmp};Read Only=true";
        conn.Open();
        return conn;
    }

    private static bool JavaAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("java", "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(15000);
            return p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "tools", "JavaOracle")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private static string? FindJar(string name)
    {
        string c = Path.Combine(Path.GetTempPath(), "ucanaccess-csharp-oracle", name);
        return System.IO.File.Exists(c) ? c : null;
    }

    private static string RunDbDump(string mdbPath)
    {
        string jackJar = FindJar("jackcess-5.1.5.jar")!;
        string classesDir = Path.Combine(FindRepoRoot(), "tools", "JavaOracle", "classes");
        string outJson = Path.Combine(Path.GetTempPath(), $"ucanaccess_ddl_read_{Guid.NewGuid():N}.json");
        var psi = new System.Diagnostics.ProcessStartInfo("java")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = classesDir,
        };
        psi.ArgumentList.Add("-Duser.timezone=UTC");
        psi.ArgumentList.Add("-cp");
        psi.ArgumentList.Add($"{jackJar}{Path.PathSeparator}{classesDir}");
        psi.ArgumentList.Add("DbDump");
        psi.ArgumentList.Add(mdbPath);
        psi.ArgumentList.Add(outJson);
        using var p = System.Diagnostics.Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit(60000);
        Assert.True(p.ExitCode == 0, $"DbDump failed: {err}");
        string json = System.IO.File.ReadAllText(outJson);
        System.IO.File.Delete(outJson);
        return json;
    }
}
