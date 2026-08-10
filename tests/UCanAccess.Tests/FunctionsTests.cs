using System.Data.Common;
using Xunit;

namespace UCanAccess.Tests;

public class FunctionsTests
{
    private static DbConnection Open(string fixture)
    {
        var conn = UCanAccessFactory.Instance.CreateConnection()!;
        conn.ConnectionString = $"Data Source={Path.Combine(AppContext.BaseDirectory, "fixtures", fixture)};Read Only=true";
        conn.Open();
        return conn;
    }

    private static object? Scalar(string sql, DbConnection? conn = null)
    {
        bool own = conn == null;
        conn ??= Open("pivot.mdb");
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar();
        }
        finally
        {
            if (own)
            {
                conn.Dispose();
            }
        }
    }

    private static T ScalarAs<T>(string sql, DbConnection? conn = null)
        => (T)Scalar(sql, conn)!;

    [Fact]
    public void Nz_returns_fallback_for_null()
    {
        Assert.Equal("x", ScalarAs<string>("SELECT Nz(NULL, 'x')"));
        Assert.Equal("v", ScalarAs<string>("SELECT Nz('v', 'x')"));
    }

    [Fact]
    public void Iif_selects_branch()
    {
        Assert.Equal("yes", ScalarAs<string>("SELECT IIf(2 > 1, 'yes', 'no')"));
        Assert.Equal("no", ScalarAs<string>("SELECT IIf(2 < 1, 'yes', 'no')"));
    }

    [Fact]
    public void String_functions()
    {
        Assert.Equal(2L, ScalarAs<long>("SELECT InStr('abc', 'b')"));
        Assert.Equal("bcd", ScalarAs<string>("SELECT Mid('abcdef', 2, 3)"));
        Assert.Equal("ab", ScalarAs<string>("SELECT Left('abc', 2)"));
        Assert.Equal("bc", ScalarAs<string>("SELECT Right('abc', 2)"));
        Assert.Equal("cba", ScalarAs<string>("SELECT StrReverse('abc')"));
        Assert.Equal(97L, ScalarAs<long>("SELECT Asc('a')"));
        Assert.Equal("B", ScalarAs<string>("SELECT Chr(66)"));
        Assert.Equal("ABC", ScalarAs<string>("SELECT StrConv('abc', 1)"));
    }

    [Fact]
    public void Numeric_functions()
    {
        Assert.Equal(4L, ScalarAs<long>("SELECT CLng(3.7)"));
        Assert.Equal(4L, ScalarAs<long>("SELECT CInt(3.7)"));
        Assert.Equal(-4L, ScalarAs<long>("SELECT Int(-3.7)"));
        Assert.Equal(-3L, ScalarAs<long>("SELECT Fix(-3.7)"));
        Assert.Equal(-1L, ScalarAs<long>("SELECT Sgn(-5)"));
        Assert.Equal(2.0, ScalarAs<double>("SELECT Sqr(4)"));
        Assert.Equal(1L, ScalarAs<long>("SELECT IsNull(NULL)"));
        Assert.Equal(0L, ScalarAs<long>("SELECT IsNull('x')"));
    }

    [Fact]
    public void Additional_access_scalar_functions()
    {
        Assert.Equal("value", ScalarAs<string>("SELECT Trim('  value  ')") );
        Assert.Equal("value  ", ScalarAs<string>("SELECT LTrim('  value  ')") );
        Assert.Equal("  value", ScalarAs<string>("SELECT RTrim('  value  ')") );
        Assert.Equal(1.0, ScalarAs<double>("SELECT Sin(3.141592653589793 / 2)"), 12);
        Assert.Equal(1.0, ScalarAs<double>("SELECT Cos(0)"), 12);
        Assert.Equal(Math.Log(10), ScalarAs<double>("SELECT Log(10)"), 12);
        Assert.Equal(100.0, ScalarAs<double>("SELECT Exp(Log(100))"), 10);
    }

    [Fact]
    public void Date_functions()
    {
        Assert.Equal("2020-01-06 00:00:00.000", ScalarAs<string>("SELECT DateAdd('d', 5, #1/1/2020#)"));
        Assert.Equal(9L, ScalarAs<long>("SELECT DateDiff('d', #1/1/2020#, #1/10/2020#)"));
        Assert.Equal(2020L, ScalarAs<long>("SELECT DatePart('yyyy', #6/15/2020#)"));
        Assert.Equal(6L, ScalarAs<long>("SELECT Month(#6/15/2020#)"));
        Assert.Equal(15L, ScalarAs<long>("SELECT Day(#6/15/2020#)"));
        Assert.Equal(2013L, ScalarAs<long>("SELECT Year(DateValue('5/30/2013'))"));
        Assert.Equal(1L, ScalarAs<long>("SELECT Weekday(#1/1/2023#)")); // Sunday = 1
        Assert.Equal("2013", ScalarAs<string>("SELECT Format(DateValue('5/30/2013'), 'yyyy')"));
    }

    [Fact]
    public void Format_masks_match_ucanaccess()
    {
        using var conn = Open("sqljoin.mdb");
        // date masks
        Assert.Equal("January 05, 2021", ScalarAs<string>("SELECT Format(dt, 'mmmm dd, yyyy') FROM t_detail WHERE id = 1", conn));
        Assert.Equal("Jan 2021", ScalarAs<string>("SELECT Format(dt, 'mmm yyyy') FROM t_detail WHERE id = 1", conn));
        Assert.Equal("005", ScalarAs<string>("SELECT Format(dt, 'ddd') FROM t_detail WHERE id = 1", conn)); // day of year
        Assert.Equal("Tuesday", ScalarAs<string>("SELECT Format(dt, 'dddd') FROM t_detail WHERE id = 1", conn));
        Assert.Equal("09:01:00", ScalarAs<string>("SELECT Format(dt, 'hh:mm:ss') FROM t_detail WHERE id = 1", conn));
        // number masks (0 keeps trailing zeros; named formats)
        Assert.Equal("1,234.50", ScalarAs<string>("SELECT Format(1234.5, '#,##0.00')", conn));
        Assert.Equal("1234.50", ScalarAs<string>("SELECT Format(1234.5, '0.00')", conn));
        Assert.Equal("50%", ScalarAs<string>("SELECT Format(0.5, '0%')", conn));
        Assert.Equal("$1,234.50", ScalarAs<string>("SELECT Format(1234.5, '$#,##0.00')", conn));
        Assert.Equal("1234.5", ScalarAs<string>("SELECT Format(1234.5, 'general number')", conn));
        Assert.Equal("1234.50", ScalarAs<string>("SELECT Format(1234.5, 'fixed')", conn));
        Assert.Equal("1,234.5", ScalarAs<string>("SELECT Format(1234.5, 'standard')", conn));
        Assert.Equal("$1,234.50", ScalarAs<string>("SELECT Format(1234.5, 'currency')", conn));
        Assert.Equal("50.00%", ScalarAs<string>("SELECT Format(0.5, 'percent')", conn));
    }

    [Fact]
    public void Date_diff_matches_ucanaccess_rounding()
    {
        // UCanAccess computes the span between the ordered timestamps and rounds
        // the fractional day difference with Math.rint (round half to even).
        Assert.Equal(361L, ScalarAs<long>("SELECT DateDiff('d', #1/5/2021 9:00:00 AM#, #1/1/2022#)"));
        Assert.Equal(361L, ScalarAs<long>("SELECT DateDiff('d', #1/5/2021 9:00:00 AM#, #1/1/2022 10:00:00 AM#)"));
        Assert.Equal(39L, ScalarAs<long>("SELECT DateDiff('d', #11/22/2003 10:42:58 PM#, #1/1/2004#)"));
        Assert.Equal(1L, ScalarAs<long>("SELECT DateDiff('d', #1/5/2021 9:00:00 AM#, #1/6/2021 8:00:00 AM#)"));
        Assert.Equal(-1L, ScalarAs<long>("SELECT DateDiff('d', #1/6/2021#, #1/5/2021#)"));
    }

    [Fact]
    public void Date_add_y_means_day_of_year()
    {
        // Access DateAdd('y', n, d) adds n days (day of year), not years
        Assert.Equal("2020-01-06 00:00:00.000", ScalarAs<string>("SELECT DateAdd('y', 5, #1/1/2020#)"));
        Assert.Equal("2020-01-08 00:00:00.000", ScalarAs<string>("SELECT DateAdd('ww', 1, #1/1/2020#)"));
    }

    [Fact]
    public void Boolean_columns_read_as_bool()
    {
        using var conn = Open("sqljoin.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT active FROM t_master WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal(true, reader.GetValue(0));
    }

    [Fact]
    public void Domain_aggregates()
    {
        using var conn = Open("sqljoin.mdb");
        Assert.Equal(12L, ScalarAs<long>("SELECT DCount('*', 't_detail')", conn));
        Assert.Equal(4L, ScalarAs<long>("SELECT DCount('qty', 't_detail', 'master_id = 1')", conn));
        Assert.Equal(9L, ScalarAs<long>("SELECT DSum('qty', 't_detail', 'master_id = 1')", conn));
        Assert.Equal(124L, ScalarAs<long>("SELECT DSum('qty', 't_detail', 'qty > 5')", conn));
        Assert.Equal(0L, ScalarAs<long>("SELECT DCount('qty', 't_detail', 'master_id = 99')", conn));
        Assert.Equal(10L, ScalarAs<long>("SELECT DMax('qty', 't_detail', 'master_id = 2')", conn));
        Assert.Equal(-0.25, ScalarAs<double>("SELECT DMin('price', 't_detail', 'master_id = 2')", conn), 6);
        Assert.Equal(1.9375, ScalarAs<double>("SELECT DAvg('price', 't_detail', 'master_id = 1')", conn), 6);
        Assert.Equal("Gamma", ScalarAs<string>("SELECT DLookup('name', 't_master', 'id = 3')", conn));
        Assert.Null(Scalar("SELECT DLookup('name', 't_master', 'id = 99')", conn));
        Assert.Equal("a01", ScalarAs<string>("SELECT DFirst('code', 't_detail', 'master_id = 1')", conn));
        Assert.Equal("a04", ScalarAs<string>("SELECT DLast('code', 't_detail', 'master_id = 1')", conn));
    }

    [Fact]
    public void Atn_partition_round()
    {
        using var conn = Open("sqljoin.mdb");
        Assert.Equal(Math.Atan(1), ScalarAs<double>("SELECT Atn(1)", conn), 12);
        Assert.Equal("100:199", ScalarAs<string>("SELECT Partition(100, 0, 500, 100)", conn));
        Assert.Equal("   : 99", ScalarAs<string>("SELECT Partition(50, 100, 500, 100)", conn));
        Assert.Equal("501:   ", ScalarAs<string>("SELECT Partition(700, 100, 500, 100)", conn));
        // Access Round uses Java Math.round semantics (half toward +infinity)
        Assert.Equal(3.0, ScalarAs<double>("SELECT Round(2.5, 0)", conn), 6);
        Assert.Equal(-2.0, ScalarAs<double>("SELECT Round(-2.5, 0)", conn), 6);
        Assert.Equal(1.0, ScalarAs<double>("SELECT Round(1.005, 2)", conn), 6);
        Assert.Equal(123.46, ScalarAs<double>("SELECT Round(123.456, 2)", conn), 6);
        // Access Str(): leading space for positive values, no digit grouping
        Assert.Equal(" 5", ScalarAs<string>("SELECT Str(5)", conn));
        Assert.Equal("-5", ScalarAs<string>("SELECT Str(-5)", conn));
        Assert.Equal(" 1234.5678", ScalarAs<string>("SELECT Str(1234.5678)", conn));
        // CStr uses en-US digit grouping (matches the pinned oracle locale)
        Assert.Equal("1,234", ScalarAs<string>("SELECT CStr(1234)", conn));
        Assert.Equal("1,234.5", ScalarAs<string>("SELECT CStr(1234.5)", conn));
        Assert.Equal(".5", ScalarAs<string>("SELECT CStr(0.5)", conn));
    }

    [Fact]
    public void Avg_on_integer_truncates_like_hsqldb()
    {
        using var conn = Open("sqljoin.mdb");
        Assert.Equal(1L, ScalarAs<long>("SELECT Avg(id) FROM t_detail WHERE id IN (1,2)", conn));
        Assert.Equal(2L, ScalarAs<long>("SELECT Avg(id) FROM t_detail WHERE id IN (1,2,3,4)", conn));
        Assert.Equal(6L, ScalarAs<long>("SELECT Avg(qty) FROM t_detail WHERE master_id = 2", conn));
        // AVG on a money/real column keeps the fractional value
        Assert.Equal(1.9375, ScalarAs<double>("SELECT Avg(price) FROM t_detail WHERE master_id = 1", conn), 6);
    }

    [Fact]
    public void Statistical_aggregates_match_access_definitions()
    {
        using var conn = Open("sqljoin.mdb");
        const string values = "(SELECT 1 AS value UNION ALL SELECT 3 UNION ALL SELECT 5)";
        Assert.Equal(2.0, ScalarAs<double>($"SELECT StDev(value) FROM {values}", conn), 12);
        Assert.Equal(Math.Sqrt(8.0 / 3.0), ScalarAs<double>($"SELECT StDevP(value) FROM {values}", conn), 12);
        Assert.Equal(4.0, ScalarAs<double>($"SELECT Var(value) FROM {values}", conn), 12);
        Assert.Equal(8.0 / 3.0, ScalarAs<double>($"SELECT VarP(value) FROM {values}", conn), 12);
        Assert.Null(Scalar("SELECT StDev(1)", conn));
    }

    [Fact]
    public void First_and_last_aggregates_ignore_null_values()
    {
        using var conn = Open("sqljoin.mdb");
        const string values = "(SELECT NULL AS value UNION ALL SELECT 'first' UNION ALL SELECT 'last')";
        Assert.Equal("first", ScalarAs<string>($"SELECT First(value) FROM {values}", conn));
        Assert.Equal("last", ScalarAs<string>($"SELECT Last(value) FROM {values}", conn));
    }

    [Fact]
    public void User_functions_are_registered_per_connection()
    {
        var connection = (UCanAccessConnection)UCanAccessFactory.Instance.CreateConnection()!;
        connection.RegisterFunction("DoubleText", 1,
            args => $"{args[0]}{args[0]}", deterministic: true);
        connection.ConnectionString = $"Data Source={Path.Combine(AppContext.BaseDirectory, "fixtures", "pivot.mdb")};Read Only=true";
        connection.Open();
        using (connection)
        {
            Assert.Equal("abab", ScalarAs<string>("SELECT DoubleText('ab')", connection));
        }
    }

    [Fact]
    public void User_functions_reject_registration_after_open()
    {
        using var connection = (UCanAccessConnection)UCanAccessFactory.Instance.CreateConnection()!;
        connection.ConnectionString = $"Data Source={Path.Combine(AppContext.BaseDirectory, "fixtures", "pivot.mdb")};Read Only=true";
        connection.Open();
        Assert.Throws<InvalidOperationException>(() => connection.RegisterFunction("late", 1, args => args[0]));
    }

    [Fact]
    public void Money_in_concatenation_keeps_scale()
    {
        using var conn = Open("sqljoin.mdb");
        Assert.Equal("1-10.5000", ScalarAs<string>("SELECT id & '-' & price FROM t_detail WHERE id = 1", conn));
        Assert.Equal("Alpha-1000.0000", ScalarAs<string>("SELECT name & '-' & budget FROM t_master WHERE id = 1", conn));
    }

    [Fact]
    public void Financial_functions()
    {
        // SLN: cost 1000, salvage 100, life 5 -> 180
        Assert.Equal(180.0, ScalarAs<double>("SELECT SLN(1000, 100, 5)"), 6);
        // SYD period 1: (1000-100)*(5-1+1)/15 = 300
        Assert.Equal(300.0, ScalarAs<double>("SELECT SYD(1000, 100, 5, 1)"), 6);
        // PMT: 10% per period is represented as 0.10 by Access/UCanAccess.
        Assert.Equal(-146.76331510, ScalarAs<double>("SELECT PMT(0.1, 12, 1000)"), 4);
    }

    [Fact]
    public void Functions_on_fixture_columns()
    {
        using var conn = Open("pivot.mdb");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT c_cod, Month(c_dt), Year(c_dt) FROM t_pivot WHERE c_cod = 'paperino'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("paperino", reader.GetString(0));
        Assert.Equal(5L, reader.GetInt64(1));
        Assert.Equal(2013L, reader.GetInt64(2));
    }
}
