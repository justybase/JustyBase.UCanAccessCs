using System.Data;
using System.Data.Common;
using Xunit;

namespace UCanAccess.Tests;

/// <summary>
/// Metadata/API parity: GetSchema collections (Tables/Columns/Indexes/PrimaryKeys/
/// ForeignKeys/Views) expose the Access schema through the ADO.NET connection.
/// </summary>
public class MetadataTests
{
    private static DbConnection Open(string fixture)
    {
        var conn = UCanAccessFactory.Instance.CreateConnection()!;
        conn.ConnectionString = $"Data Source={Path.Combine(AppContext.BaseDirectory, "fixtures", fixture)};Read Only=true";
        conn.Open();
        return conn;
    }

    [Fact]
    public void GetSchema_tables_lists_user_and_system()
    {
        using var conn = Open("sqljoin.mdb");
        DataTable tables = conn.GetSchema("Tables");
        var names = tables.AsEnumerable().Select(r => r.Field<string>("TABLE_NAME")!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("t_master", names);
        Assert.Contains("t_detail", names);
        // system tables are hidden by default (show schema = false)
        Assert.DoesNotContain("MSysObjects", names);
    }

    [Fact]
    public void GetSchema_tables_show_schema_exposes_system()
    {
        var conn = UCanAccessFactory.Instance.CreateConnection()!;
        conn.ConnectionString = $"Data Source={Path.Combine(AppContext.BaseDirectory, "fixtures", "sqljoin.mdb")};Read Only=true;Show Schema=true";
        conn.Open();
        DataTable tables = conn.GetSchema("Tables");
        var names = tables.AsEnumerable().Select(r => r.Field<string>("TABLE_NAME")!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("t_master", names);
        Assert.Contains("MSysObjects", names);
    }

    [Fact]
    public void GetSchema_columns_reports_types()
    {
        using var conn = Open("sqljoin.mdb");
        DataTable columns = conn.GetSchema("Columns");
        var master = columns.AsEnumerable()
            .Where(r => r.Field<string>("TABLE_NAME")!.Equals("t_master", StringComparison.OrdinalIgnoreCase))
            .Select(r => (Name: r.Field<string>("COLUMN_NAME")!, Type: r.Field<string>("DATA_TYPE")!))
            .ToList();
        Assert.Contains(("id", "System.Int32"), master);
        Assert.Contains(("name", "System.String"), master);
        Assert.Contains(("budget", "System.Decimal"), master);
        Assert.Contains(("active", "System.Boolean"), master);
        Assert.Contains(("created", "System.DateTime"), master);
    }

    [Fact]
    public void GetSchema_indexes_and_primary_keys()
    {
        using var conn = Open("generated/genIndexed.mdb");
        DataTable indexes = conn.GetSchema("Indexes");
        var idxRows = indexes.AsEnumerable()
            .Where(r => r.Field<string>("TABLE_NAME")!.Equals("t_indexed", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Contains(idxRows, r => r.Field<string>("INDEX_NAME") == "PrimaryKey" && r.Field<bool>("PRIMARY_KEY"));
        Assert.Contains(idxRows, r => r.Field<string>("INDEX_NAME") == "idx_code" && r.Field<bool>("UNIQUE"));

        DataTable pk = conn.GetSchema("PrimaryKeys");
        var pkCol = pk.AsEnumerable()
            .First(r => r.Field<string>("TABLE_NAME")!.Equals("t_indexed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("id", pkCol.Field<string>("COLUMN_NAME"));
    }

    [Fact]
    public void GetSchema_foreign_keys_lists_relationship()
    {
        using var conn = Open("sqljoin.mdb");
        DataTable fks = conn.GetSchema("ForeignKeys");
        var rows = fks.AsEnumerable().ToList();
        Assert.Contains(rows, r => r.Field<string>("TABLE_NAME") == "t_detail");
    }

    [Fact]
    public void GetSchema_views_lists_saved_queries()
    {
        using var conn = Open("accessLike.mdb");
        DataTable views = conn.GetSchema("Views");
        var names = views.AsEnumerable().Select(r => r.Field<string>("TABLE_NAME")!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("q_like2", names);
    }

    [Fact]
    public void GetSchema_meta_data_collections()
    {
        using var conn = Open("sqljoin.mdb");
        DataTable collections = conn.GetSchema("MetaDataCollections");
        Assert.Contains(collections.AsEnumerable(), r => r.Field<string>("CollectionName") == "Tables");
        Assert.Contains(collections.AsEnumerable(), r => r.Field<string>("CollectionName") == "Columns");
    }

    [Fact]
    public void GetSchema_restrictions_filter_tables()
    {
        using var conn = Open("sqljoin.mdb");
        DataTable tables = conn.GetSchema("Tables", new string?[] { null, null, "t_master" });
        Assert.Equal(1, tables.Rows.Count);
        Assert.Equal("t_master", tables.Rows[0]["TABLE_NAME"]);
    }

    [Fact]
    public void Result_set_meta_data_reports_boolean_type()
    {
        using var conn = Open("sqljoin.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, active, name FROM t_master WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());

        Assert.Equal(typeof(bool), reader.GetFieldType(1));
        Assert.Equal("BOOLEAN", reader.GetDataTypeName(1));
        Assert.Equal(typeof(long), reader.GetFieldType(0));
        Assert.Equal(typeof(string), reader.GetFieldType(2));
        Assert.True(reader.GetBoolean(1));
    }

    [Fact]
    public void Reader_schema_table_reports_columns_and_types()
    {
        using var conn = Open("sqljoin.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, active, name FROM t_master";
        using var reader = cmd.ExecuteReader();
        DataTable schema = reader.GetSchemaTable()!;
        Assert.NotNull(schema);
        var byName = schema.AsEnumerable()
            .ToDictionary(r => r.Field<string>("ColumnName")!, r => r.Field<Type>("DataType"));
        Assert.Equal(typeof(long), byName["id"]);
        Assert.Equal(typeof(bool), byName["active"]);
        Assert.Equal(typeof(string), byName["name"]);
    }

    [Fact]
    public void Column_order_display_orders_by_display_index()
    {
        var conn = UCanAccessFactory.Instance.CreateConnection()!;
        conn.ConnectionString = $"Data Source={Path.Combine(AppContext.BaseDirectory, "fixtures", "sqljoin.mdb")};Read Only=true;Column Order=display";
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM t_master";
        using var reader = cmd.ExecuteReader();
        var cols = new List<string>();
        for (int i = 0; i < reader.FieldCount; i++)
        {
            cols.Add(reader.GetName(i));
        }
        var expected = ((UCanAccess.UCanAccessConnection)conn).AccessDatabase
            .GetTable("t_master")!
            .Columns.OrderBy(c => c.DisplayIndex).Select(c => c.Name).ToList();
        Assert.Equal(expected, cols);
    }
}
