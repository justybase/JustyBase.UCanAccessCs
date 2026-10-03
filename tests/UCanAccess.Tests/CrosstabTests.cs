using System.Data.Common;
using Xunit;

namespace UCanAccess.Tests;

public sealed class CrosstabTests
{
    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Transform_pivot_reads_real_access_rows()
    {
        using var connection = new UCanAccessConnection
        {
            ConnectionString = $"Data Source={Fixture("pivot.mdb")};Read Only=true",
        };
        connection.Open();
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "TRANSFORM Sum(c_val) "
            + "SELECT 1 AS m FROM t_pivot "
            + "GROUP BY 1 "
            + "PIVOT c_cod IN ('paperino', 'piero', 'pippo', 'pluto')";

        using DbDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(4444m, reader.GetDecimal(1));
        Assert.Equal(33m, reader.GetDecimal(2));
        Assert.Equal(122m, reader.GetDecimal(3));
        Assert.Equal(443m, reader.GetDecimal(4));
        Assert.False(reader.Read());
    }

    [Fact]
    public void Transform_rejects_unsupported_aggregate()
    {
        var ex = Assert.Throws<NotSupportedException>(() => AccessSqlTranslator.Translate(
            "TRANSFORM First(c_val) SELECT 1 AS m FROM t_pivot GROUP BY 1 PIVOT c_cod IN ('a')"));
        Assert.Contains("not supported", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Transform_rejects_missing_pivot_clause()
    {
        Assert.Throws<NotSupportedException>(() => AccessSqlTranslator.Translate(
            "TRANSFORM Sum(c_val) SELECT 1 AS m FROM t_pivot GROUP BY 1"));
    }

    [Fact]
    public void Transform_rejects_empty_in_list()
    {
        Assert.Throws<NotSupportedException>(() => AccessSqlTranslator.Translate(
            "TRANSFORM Sum(c_val) SELECT 1 AS m FROM t_pivot GROUP BY 1 PIVOT c_cod IN ()"));
    }

    [Fact]
    public void Transform_rejects_trailing_tokens_after_in_list()
    {
        Assert.Throws<NotSupportedException>(() => AccessSqlTranslator.Translate(
            "TRANSFORM Sum(c_val) SELECT 1 AS m FROM t_pivot GROUP BY 1 PIVOT c_cod IN ('a') ORDER BY 1"));
    }

    [Fact]
    public void Dynamic_transform_pivot_with_where_parameter_resolves_per_command()
    {
        using var connection = new UCanAccessConnection
        {
            ConnectionString = $"Data Source={Fixture("pivot.mdb")}; Read Only=true",
        };
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "TRANSFORM Sum(c_val) SELECT 1 AS m FROM t_pivot WHERE c_val > ? GROUP BY 1 PIVOT c_cod";
        var parameter = command.CreateParameter();
        parameter.Value = 100;
        command.Parameters.Add(parameter);

        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        // Only pivot values with c_val > 100 survive ('paperino' 4444, 'pippo' 122, 'pluto' 443).
        Assert.Equal(4, reader.FieldCount);
        Assert.Equal(4444m, reader.GetDecimal(1));
        Assert.Equal(122m, reader.GetDecimal(2));
        Assert.Equal(443m, reader.GetDecimal(3));
        Assert.False(reader.Read());
    }

    [Fact]
    public void Dynamic_transform_pivot_discovers_columns_from_mirror()
    {
        using var connection = new UCanAccessConnection
        {
            ConnectionString = $"Data Source={Fixture("pivot.mdb")}; Read Only=true",
        };
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "TRANSFORM Sum(c_val) SELECT 1 AS m FROM t_pivot GROUP BY 1 PIVOT c_cod";

        using var reader = command.ExecuteReader();
        Assert.Equal(5, reader.FieldCount);
        Assert.True(reader.Read());
        Assert.Equal(4444m, reader.GetDecimal(1));
        Assert.Equal(33m, reader.GetDecimal(2));
        Assert.Equal(122m, reader.GetDecimal(3));
        Assert.Equal(443m, reader.GetDecimal(4));
    }
}
