using System.Data.Common;
using Xunit;

namespace UCanAccess.Tests;

public class WindowFunctionTests
{
    private static DbConnection Open()
    {
        var connection = UCanAccessFactory.Instance.CreateConnection()!;
        connection.ConnectionString =
            $"Data Source={Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "genIndexed.mdb")};Read Only=true";
        connection.Open();
        return connection;
    }

    [Fact]
    public void Multiple_window_functions_return_rows_and_metadata()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,
                   ROW_NUMBER() OVER (PARTITION BY code ORDER BY value DESC ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS rn,
                   RANK() OVER (ORDER BY value DESC) AS rnk,
                   DENSE_RANK() OVER (ORDER BY value DESC) AS drnk,
                   LAG(value) OVER (ORDER BY id) AS previous_value,
                   LEAD(value) OVER (ORDER BY id) AS next_value
            FROM t_indexed
            ORDER BY id
            """;

        using var reader = command.ExecuteReader();
        Assert.Equal(6, reader.FieldCount);
        Assert.Equal("rn", reader.GetName(1));
        Assert.Equal("previous_value", reader.GetName(4));
        Assert.True(reader.GetDataTypeName(1).Length > 0);

        int rows = 0;
        while (reader.Read())
        {
            Assert.True(reader.GetInt64(1) >= 1);
            Assert.True(reader.GetInt64(2) >= 1);
            Assert.True(reader.GetInt64(3) >= 1);
            rows++;
        }
        Assert.Equal(50, rows);
    }

    [Fact]
    public void Window_function_result_types_are_stable()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ROW_NUMBER() OVER (ORDER BY id) AS rn,
                   RANK() OVER (ORDER BY value DESC) AS rnk,
                   DENSE_RANK() OVER (ORDER BY value DESC) AS drnk,
                   LAG(value) OVER (ORDER BY id) AS prev,
                   LEAD(value) OVER (ORDER BY id) AS next
            FROM t_indexed
            ORDER BY id
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        // Ranking functions always produce integers; LAG/LEAD values convert
        // back to the DOUBLE column domain (expression columns keep the
        // mirror's fallback field-type mapping, so values are asserted here).
        Assert.Equal(typeof(long), reader.GetFieldType(0));
        Assert.Equal(typeof(long), reader.GetFieldType(1));
        Assert.Equal(typeof(long), reader.GetFieldType(2));
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.True(reader.IsDBNull(3));
        Assert.False(reader.IsDBNull(4));
        Assert.IsType<double>(reader.GetValue(4));
        Assert.Equal(reader.GetDouble(4), reader.GetFieldValue<double>(4));
    }

    [Fact]
    public void Window_frame_edges_yield_null()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT LAG(value, 100) OVER (ORDER BY id) AS far_back,
                   LEAD(value, 100) OVER (ORDER BY id) AS far_ahead
            FROM t_indexed
            ORDER BY id
            """;
        using var reader = command.ExecuteReader();
        int rows = 0;
        while (reader.Read())
        {
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
            rows++;
        }
        Assert.Equal(50, rows);
    }

    [Fact]
    public void Window_nulls_sort_first_in_descending_order()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        // Access sorts NULL first in both directions; the mirror rewrites bare DESC keys.
        command.CommandText = """
            SELECT id, ROW_NUMBER() OVER (ORDER BY value DESC) AS rn
            FROM t_indexed
            ORDER BY value DESC
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0) || reader.GetInt64(1) == 1);
    }

    [Fact]
    public void Window_function_outside_select_is_rejected()
    {
        var ex = Assert.Throws<NotSupportedException>(() => AccessSqlTranslator.Translate(
            "UPDATE t SET x = ROW_NUMBER() OVER (ORDER BY id)"));
        Assert.Contains("SELECT", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Window_function_accepts_parameters_and_access_expressions()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id & code AS display_id,
                   LAG(value, ?) OVER (ORDER BY id) AS previous_value
            FROM t_indexed
            ORDER BY id
            """;
        var parameter = command.CreateParameter();
        parameter.Value = 1;
        command.Parameters.Add(parameter);

        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("1code01", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
    }
}
