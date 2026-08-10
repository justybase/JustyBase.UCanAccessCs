using Xunit;

namespace UCanAccess.Tests;

public sealed class ExactDecimalTests
{
    [Fact]
    public void Numeric_values_keep_precision_in_projection_arithmetic_and_aggregates()
    {
        string path = TempCopy();
        try
        {
        using var connection = new UCanAccessConnection
        {
            ConnectionString = $"Data Source={path};Read Only=false"
        };
        connection.Open();
        Execute(connection, "CREATE TABLE exact_decimal (id LONG, amount NUMERIC(28, 10))");
        Execute(connection,
            "INSERT INTO exact_decimal VALUES (1, 123456789012345678.1234567890)");

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT amount, amount + 0.0000000001, SUM(amount), MIN(amount) FROM exact_decimal";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(123456789012345678.1234567890m, reader.GetDecimal(0));
        Assert.Equal(123456789012345678.1234567891m, reader.GetDecimal(1));
        Assert.Equal(123456789012345678.1234567890m, reader.GetDecimal(2));
        Assert.Equal(123456789012345678.1234567890m, reader.GetDecimal(3));
        Assert.Equal("NUMERIC", reader.GetDataTypeName(1), ignoreCase: true);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Money_is_fixed_to_four_decimal_places_without_binary_float_rounding()
    {
        string path = TempCopy();
        try
        {
        using var connection = new UCanAccessConnection
        {
            ConnectionString = $"Data Source={path};Read Only=false"
        };
        connection.Open();
        Execute(connection, "CREATE TABLE exact_money (id LONG, amount MONEY)");
        Execute(connection, "INSERT INTO exact_money VALUES (1, 10.1250)");

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT amount, amount + 0.0001 FROM exact_money";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(10.1250m, reader.GetDecimal(0));
        Assert.Equal(10.1251m, reader.GetDecimal(1));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static void Execute(UCanAccessConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string TempCopy()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uca-exact-{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "genEmpty.mdb"), path);
        return path;
    }
}
