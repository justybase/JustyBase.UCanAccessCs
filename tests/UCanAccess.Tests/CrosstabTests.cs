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
