using System.Data;
using System.Data.Common;
using Xunit;

namespace UCanAccess.Tests;

/// <summary>
/// Saved queries: exposed as queryable views, listed through GetSchema("Views"),
/// and their SQL can be executed with parameters (PARAMETERS clause).
/// </summary>
public class QueryTests
{
    private static DbConnection Open(string fixture)
    {
        var conn = UCanAccessFactory.Instance.CreateConnection()!;
        conn.ConnectionString = $"Data Source={Path.Combine(AppContext.BaseDirectory, "fixtures", fixture)};Read Only=true";
        conn.Open();
        return conn;
    }

    private static DbConnection OpenWritable(string path)
    {
        var conn = UCanAccessFactory.Instance.CreateConnection()!;
        conn.ConnectionString = $"Data Source={path};Read Only=false";
        conn.Open();
        return conn;
    }

    private static object? Scalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Fact]
    public void Select_from_saved_query_view()
    {
        using var conn = Open("accessLike.mdb");
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM [q_like2]";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.False(reader.Read()); // one row
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM q_like2";
            Assert.Equal(1L, cmd.ExecuteScalar());
        }
    }

    [Fact]
    public void Saved_query_sql_exposed_via_get_schema()
    {
        using var conn = Open("accessLike.mdb");
        DataTable views = conn.GetSchema("Views");
        DataRow? row = views.AsEnumerable()
            .FirstOrDefault(r => r.Field<string>("TABLE_NAME")!.Equals("q_like2", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(row);
        Assert.Contains("LIKE", row!.Field<string>("VIEW_DEFINITION")!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Saved_query_filterable_in_view()
    {
        using var conn = Open("accessLike.mdb");
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Campo2 FROM q_like2 WHERE Campo2 LIKE 'd*'";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
        }
    }

    [Fact]
    public void Parameterized_saved_query_is_expanded_and_bound_at_execution()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uca-query-param-{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "accessLike.mdb"), path, true);
        try
        {
            using (var writable = OpenWritable(path))
            {
                using var create = writable.CreateCommand();
                create.CommandText =
                    "CREATE VIEW q_managed_param AS PARAMETERS p TEXT(20); "
                    + "SELECT Campo2 FROM t_like2 WHERE Campo2 LIKE p";
                create.ExecuteNonQuery();
            }

            using (var readOnly = OpenWritable(path))
            {
                using var command = readOnly.CreateCommand();
                command.CommandText = "SELECT Campo2 FROM q_managed_param";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "p";
                parameter.Value = "d*";
                command.Parameters.Add(parameter);
                using var reader = command.ExecuteReader();
                Assert.True(reader.Read());
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Parameterized_saved_query_preserves_qualified_columns_and_bracket_parameters()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uca-query-param-qualified-{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "accessLike.mdb"), path, true);
        try
        {
            using var conn = OpenWritable(path);
            using (var create = conn.CreateCommand())
            {
                create.CommandText =
                    "CREATE VIEW q_qualified AS PARAMETERS Campo2 TEXT(20); "
                    + "SELECT t.Campo2 FROM [t_like2] AS t WHERE t.Campo2 LIKE [Campo2]";
                create.ExecuteNonQuery();
            }
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT Campo2 FROM q_qualified";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "Campo2";
            parameter.Value = "d*";
            command.Parameters.Add(parameter);
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.StartsWith("d", reader.GetString(0), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Parameterized_saved_query_expands_inside_nested_select_and_dml()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uca-query-param-nested-{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "accessLike.mdb"), path, true);
        try
        {
            using var conn = OpenWritable(path);
            using (var create = conn.CreateCommand())
            {
                create.CommandText =
                    "CREATE VIEW q_nested AS PARAMETERS p TEXT(20); "
                    + "SELECT Campo2 FROM t_like2 WHERE Campo2 LIKE p";
                create.ExecuteNonQuery();
                create.CommandText = "CREATE TABLE q_nested_dest (value TEXT(20))";
                create.ExecuteNonQuery();
            }

            using (var select = conn.CreateCommand())
            {
                select.CommandText =
                    "SELECT Campo2 FROM t_like2 "
                    + "WHERE Campo2 IN (SELECT Campo2 FROM q_nested)";
                var parameter = select.CreateParameter();
                parameter.ParameterName = "p";
                parameter.Value = "d*";
                select.Parameters.Add(parameter);
                using var reader = select.ExecuteReader();
                Assert.True(reader.Read());
            }

            using (var insert = conn.CreateCommand())
            {
                insert.CommandText = "INSERT INTO q_nested_dest (value) SELECT Campo2 FROM q_nested";
                var parameter = insert.CreateParameter();
                parameter.ParameterName = "p";
                parameter.Value = "d*";
                insert.Parameters.Add(parameter);
                Assert.True(insert.ExecuteNonQuery() > 0);
            }
            Assert.True(Convert.ToInt64(Scalar(conn, "SELECT count(*) FROM q_nested_dest")) > 0);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Transaction_querydef_snapshot_is_used_for_parameterized_reads()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uca-query-param-tx-{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "accessLike.mdb"), path, true);
        try
        {
            using var conn = OpenWritable(path);
            using var tx = conn.BeginTransaction();
            using (var create = conn.CreateCommand())
            {
                create.Transaction = tx;
                create.CommandText =
                    "CREATE VIEW q_tx AS PARAMETERS p TEXT(20); "
                    + "SELECT Campo2 FROM t_like2 WHERE Campo2 LIKE p";
                create.ExecuteNonQuery();
            }

            using (var read = conn.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = "SELECT Campo2 FROM q_tx";
                var parameter = read.CreateParameter();
                parameter.ParameterName = "p";
                parameter.Value = "d*";
                read.Parameters.Add(parameter);
                using var reader = read.ExecuteReader();
                Assert.True(reader.Read());
            }
            tx.Rollback();
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
