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
