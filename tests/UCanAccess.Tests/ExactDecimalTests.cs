using System.Globalization;
using Xunit;

namespace UCanAccess.Tests;

public class ExactDecimalCollisionTests
{
    [Fact]
    public void Same_column_name_with_mixed_types_is_not_exact_decimal()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uca-dec-collide-{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "genEmpty.mdb"), path, true);
        try
        {
            var conn = (UCanAccessConnection)UCanAccessFactory.Instance.CreateConnection()!;
            conn.ConnectionString = $"Data Source={path};Read Only=false";
            conn.Open();
            using (conn)
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "CREATE TABLE t_dec (id LONG PRIMARY KEY, amount MONEY)";
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "CREATE TABLE t_txt (id LONG PRIMARY KEY, amount TEXT(20))";
                    cmd.ExecuteNonQuery();
                }
                var mirror = conn.Mirror;
                Assert.True(mirror.IsExactDecimalColumn("t_dec.amount"));
                Assert.False(mirror.IsExactDecimalColumn("t_txt.amount"));
                // Unqualified: not every same-named column is decimal.
                Assert.False(mirror.IsExactDecimalColumn("amount"));
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}

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

    [Theory]
    [InlineData("1", "1", 0)]
    [InlineData("1.5", "1.50", 0)]
    [InlineData("0.5", "0.5000", 0)]
    [InlineData("001", "1", 0)]
    [InlineData("0", "-0.0", 0)]
    [InlineData("0.0001", "0", 1)]
    [InlineData("-0.0001", "0", -1)]
    [InlineData("-1.5", "-1.05", -1)]
    [InlineData("-10", "-9.9999", -1)]
    [InlineData("9.99", "10", -1)]
    [InlineData("100.0000", "99.9999", 1)]
    [InlineData("0.5", "1", -1)]
    [InlineData("+2.50", "2.5", 0)]
    public void Decimal_collation_orders_like_numeric_comparison(string left, string right, int expected)
    {
        // The mirror keeps MONEY/NUMERIC values as text and orders/filters them
        // through this collation; it must agree with numeric ordering for every
        // plain decimal shape (and stay antisymmetric).
        Assert.Equal(expected, Math.Sign(ExactDecimalSql.CompareTextForCollation(left, right)));
        Assert.Equal(-expected, Math.Sign(ExactDecimalSql.CompareTextForCollation(right, left)));

        decimal numericLeft = decimal.Parse(left, CultureInfo.InvariantCulture);
        decimal numericRight = decimal.Parse(right, CultureInfo.InvariantCulture);
        Assert.Equal(Math.Sign(numericLeft.CompareTo(numericRight)), expected);
    }

    [Theory]
    [InlineData("1e2", "100", 0)]
    [InlineData("123456789012345678901234567890", "123456789012345678901234567891", -1)]
    [InlineData("123456789012345678901234567891", "123456789012345678901234567890", 1)]
    public void Decimal_collation_handles_exponents_and_values_beyond_decimal_range(
        string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(ExactDecimalSql.CompareTextForCollation(left, right)));
        Assert.Equal(-expected, Math.Sign(ExactDecimalSql.CompareTextForCollation(right, left)));
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
