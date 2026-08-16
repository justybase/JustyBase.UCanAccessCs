using JustyBase.NetezzaSqlParser.Ast;
using JustyBase.NetezzaSqlParser.Dialects;
using Xunit;

namespace UCanAccess.Tests;

/// <summary>
/// Verifies the public parser package contract consumed by the provider.
/// These tests intentionally use the NuGet dependency rather than a sibling
/// project reference, matching the production package graph.
/// </summary>
public sealed class AccessParserPackageContractTests
{
    [Fact]
    public void Pinned_parser_recognizes_core_access_statement_shapes()
    {
        var cases = new[]
        {
            ("SELECT TOP 5 [name] FROM [orders] WHERE order_date = #2003-11-22#", typeof(SelectStatement)),
            ("PARAMETERS [p] Long; SELECT * FROM orders WHERE id = [p]", typeof(AccessParameterizedStatement)),
            ("TRANSFORM Sum(amount) SELECT category FROM sales GROUP BY category PIVOT month IN ('Jan', 'Feb')", typeof(AccessCrosstabStatement)),
        };

        foreach ((string sql, Type expectedType) in cases)
        {
            var parser = DialectRuntime.CreateParser(
                DialectRuntime.Tokenize(sql, SqlDialect.Access).ToArray(), SqlDialect.Access);

            Statement? statement = parser.Parse();

            Assert.NotNull(statement);
            Assert.Empty(parser.Errors);
            Assert.IsType(expectedType, statement);
        }
    }

    [Theory]
    [InlineData("SELECT TOP 5 a FROM t")]
    [InlineData("SELECT DISTINCTROW a FROM t")]
    [InlineData("SELECT a FROM t WHERE d = #2003-11-22# AND a = @name")]
    [InlineData("PARAMETERS [p] Long; SELECT * FROM t WHERE id = [p]")]
    [InlineData("TRANSFORM Sum(amount) SELECT category FROM sales GROUP BY category PIVOT month IN ('Jan', 'Feb')")]
    public void Parser_and_provider_translator_share_access_lexical_contract(string sql)
    {
        var parser = DialectRuntime.CreateParser(
            DialectRuntime.Tokenize(sql, SqlDialect.Access).ToArray(), SqlDialect.Access);

        Assert.NotNull(parser.Parse());
        Assert.Empty(parser.Errors);

        string translated = AccessSqlTranslator.Translate(sql);
        Assert.NotEmpty(translated);
    }

    [Fact]
    public void Parser_support_does_not_remove_provider_execution_boundaries()
    {
        const string sql = "SELECT TOP 10 PERCENT a FROM t ORDER BY a";

        var parser = DialectRuntime.CreateParser(
            DialectRuntime.Tokenize(sql, SqlDialect.Access).ToArray(), SqlDialect.Access);

        Assert.IsType<SelectStatement>(parser.Parse());
        Assert.Empty(parser.Errors);
        Assert.Throws<NotSupportedException>(() => AccessSqlTranslator.Translate(sql));
    }
}
