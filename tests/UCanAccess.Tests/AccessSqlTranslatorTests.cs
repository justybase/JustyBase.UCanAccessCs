using Xunit;

namespace UCanAccess.Tests;

public class AccessSqlTranslatorTests
{
    [Fact]
    public void Transform_pivot_with_in_list_becomes_conditional_aggregation()
    {
        string sql = AccessSqlTranslator.Translate(
            "TRANSFORM Sum(amount) SELECT category FROM sales GROUP BY category PIVOT month IN ('Jan', 'Feb')",
            out int parameterCount, out _, null, name => name.Equals("amount", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(0, parameterCount);
        Assert.Contains("uca_decimal_sum", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CASE WHEN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("month", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AS 'Jan'", sql, StringComparison.Ordinal);
        Assert.Contains("AS 'Feb'", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY category", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Transform_pivot_preserves_parameters()
    {
        string sql = AccessSqlTranslator.Translate(
            "TRANSFORM Sum(amount) SELECT category FROM sales WHERE region = ? GROUP BY category PIVOT month IN (1, 2)",
            out int parameterCount, out _);

        Assert.Equal(1, parameterCount);
        Assert.Contains("WHERE region = @p0", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Low_level_translator_requires_explicit_dynamic_pivot_values()
    {
        var exception = Assert.Throws<NotSupportedException>(() => AccessSqlTranslator.Translate(
            "TRANSFORM Sum(amount) SELECT category FROM sales GROUP BY category PIVOT month"));

        Assert.Contains("Dynamic TRANSFORM/PIVOT", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT * FROM t", "SELECT * FROM t")]
    [InlineData("SELECT a, b FROM t", "SELECT a, b FROM t")]
    [InlineData("SELECT * FROM [my table]", "SELECT * FROM \"my table\"")]
    [InlineData("SELECT [my col] FROM t", "SELECT \"my col\" FROM t")]
    [InlineData("SELECT `backtick` FROM t", "SELECT \"backtick\" FROM t")]
    [InlineData("SELECT * FROM t WHERE a = 'x'", "SELECT * FROM t WHERE a = 'x'")]
    [InlineData("SELECT * FROM t WHERE a = \"x\"", "SELECT * FROM t WHERE a = 'x'")]
    [InlineData("SELECT * FROM t WHERE a = 'O''Brien'", "SELECT * FROM t WHERE a = 'O''Brien'")]
    [InlineData("SELECT DISTINCTROW a FROM t", "SELECT DISTINCT a FROM t")]
    [InlineData("SELECT TOP 5 a FROM t", "SELECT a FROM t LIMIT 5")]
    [InlineData("SELECT a FROM t WHERE d = #11/22/2003 10:42:58 PM#", "SELECT a FROM t WHERE d = '2003-11-22 22:42:58.000'")]
    [InlineData("SELECT a FROM t WHERE d = #2003-11-22#", "SELECT a FROM t WHERE d = '2003-11-22 00:00:00.000'")]
    [InlineData("SELECT * FROM t WHERE a LIKE 'p*'", "SELECT * FROM t WHERE access_like(a, 'p*')")]
    [InlineData("SELECT * FROM t WHERE a NOT LIKE 'p*'", "SELECT * FROM t WHERE NOT access_like(a, 'p*')")]
    [InlineData("SELECT a FROM t WHERE a = 'x';", "SELECT a FROM t WHERE a = 'x'")]
    public void Translates_correctly(string input, string expected)
    {
        Assert.Equal(expected, AccessSqlTranslator.Translate(input));
    }

    [Fact]
    public void Concatenation_with_ampersand_handles_nulls()
    {
        string sql = AccessSqlTranslator.Translate("SELECT a & b FROM t");
        Assert.Contains("ifnull", sql);
        Assert.Contains("||", sql);
    }

    [Fact]
    public void Parameters_are_numbered()
    {
        string sql = AccessSqlTranslator.Translate("SELECT * FROM t WHERE a = ? AND b = ?", out int count);
        Assert.Equal(2, count);
        Assert.Contains("@p0", sql);
        Assert.Contains("@p1", sql);
        Assert.DoesNotContain("?", sql);
    }

    [Fact]
    public void Named_parameters_are_converted_to_positional()
    {
        string sql = AccessSqlTranslator.Translate(
            "SELECT * FROM t WHERE a = @x AND b = :y AND c = ?", out int count, out IReadOnlyList<string>? names);
        Assert.Equal(3, count);
        Assert.Equal("@p0", sql.Split(' ').First(t => t.StartsWith("@p")));
        Assert.DoesNotContain("@x", sql);
        Assert.DoesNotContain(":y", sql);
        Assert.NotNull(names);
        Assert.Equal(new[] { "x", "y", "" }, names);
    }

    [Fact]
    public void Named_parameter_inside_string_is_not_converted()
    {
        string sql = AccessSqlTranslator.Translate("SELECT * FROM t WHERE a = 'keep@x'", out int count, out _);
        Assert.Equal(0, count);
        Assert.Contains("'keep@x'", sql);
    }

    [Fact]
    public void Parameter_markers_inside_identifiers_and_comments_are_not_converted()
    {
        string sql = AccessSqlTranslator.Translate(
            "SELECT [mail@host], `cost:name` FROM t -- @ignored\nWHERE id = @id",
            out int count, out IReadOnlyList<string>? names);
        Assert.Equal(1, count);
        Assert.Equal(new[] { "id" }, names);
        Assert.Contains("\"mail@host\"", sql);
        Assert.Contains("\"cost:name\"", sql);
        Assert.DoesNotContain("ignored", sql);
    }

    [Fact]
    public void Qualified_desc_order_keys_place_nulls_first()
    {
        string sql = AccessSqlTranslator.Translate("SELECT a.id FROM a ORDER BY a.id DESC");
        Assert.Contains("(a.id IS NULL) DESC, a.id DESC", sql);
    }

    [Fact]
    public void Parameters_clause_declares_bracketed_parameters()
    {
        string sql = AccessSqlTranslator.Translate(
            "PARAMETERS [p] Long; SELECT * FROM t WHERE id = [p] AND x = [p]", out int count, out IReadOnlyList<string>? names);
        Assert.Equal(2, count);
        Assert.DoesNotContain("PARAMETERS", sql);
        Assert.Contains("id = @p0", sql);
        Assert.Contains("x = @p1", sql);
        Assert.Equal(new[] { "p", "p" }, names);
    }

    [Fact]
    public void Owneraccess_option_is_stripped()
    {
        string sql = AccessSqlTranslator.Translate("SELECT * FROM t WITH OWNERACCESS OPTION");
        Assert.Equal("SELECT * FROM t", sql);
    }

    [Fact]
    public void Question_mark_inside_string_is_not_a_parameter()
    {
        string sql = AccessSqlTranslator.Translate("SELECT * FROM t WHERE a = 'x?y'", out int count);
        Assert.Equal(0, count);
        Assert.Contains("'x?y'", sql);
    }

    [Fact]
    public void Like_with_parameter_becomes_function_call()
    {
        string sql = AccessSqlTranslator.Translate("SELECT * FROM t WHERE a LIKE ?", out int count);
        Assert.Equal(1, count);
        Assert.Contains("access_like", sql);
        Assert.Contains("@p0", sql);
    }

    [Fact]
    public void Top_percent_is_rejected_by_the_5_1_6_compatibility_baseline()
    {
        var exception = Assert.Throws<NotSupportedException>(() =>
            AccessSqlTranslator.Translate("SELECT TOP 10 PERCENT a FROM t ORDER BY a"));

        Assert.Contains("5.1.6", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Window_order_by_is_not_rewritten_as_outer_order_by()
    {
        string sql = AccessSqlTranslator.Translate(
            "SELECT ROW_NUMBER() OVER (PARTITION BY grp ORDER BY score DESC ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS rn FROM t ORDER BY score DESC");

        Assert.Contains("OVER(PARTITION BY grp ORDER BY score DESC ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)", sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(score IS NULL) DESC, score DESC", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Window_clauses_are_restricted_to_select()
    {
        Assert.Throws<NotSupportedException>(() =>
            AccessSqlTranslator.Translate("UPDATE t SET value = ROW_NUMBER() OVER (ORDER BY id)"));
    }

    [Fact]
    public void Window_clause_in_a_cte_is_still_a_select_query()
    {
        string sql = AccessSqlTranslator.Translate(
            "WITH ranked AS (SELECT id, ROW_NUMBER() OVER (ORDER BY id) AS rn FROM t) " +
            "SELECT id, rn FROM ranked ORDER BY id");

        Assert.Contains("ROW_NUMBER() OVER(ORDER BY id)", sql, StringComparison.OrdinalIgnoreCase);
    }
}
