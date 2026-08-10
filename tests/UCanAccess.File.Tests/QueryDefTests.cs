using UCanAccess.File;
using Xunit;

namespace UCanAccess.File.Tests;

public class QueryDefTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Reads_select_query_from_accesslike()
    {
        using var db = Database.Open(Fixture("accessLike.mdb"));
        var queries = db.GetQueries();
        var q = Assert.Single(queries);
        Assert.Equal("q_like2", q.Name);
        Assert.Equal(QueryType.Select, q.Type);
        Assert.NotNull(q.Sql);
        Assert.Contains("t_like2", q.Sql);
        Assert.Contains("LIKE", q.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reads_crosstab_queries_from_pivot()
    {
        using var db = Database.Open(Fixture("pivot.mdb"));
        var queries = db.GetQueries();
        Assert.Equal(2, queries.Count);
        Assert.All(queries, q => Assert.Equal(QueryType.CrossTab, q.Type));
        Assert.Contains(queries, q => q.Name == "q_stdev");
        Assert.Contains(queries, q => q.Name == "q_trim");
        Assert.Null(queries[0].Sql); // not reconstructable yet
    }

    [Fact]
    public void Reads_query_with_reserved_word_column()
    {
        using var db = Database.Open(Fixture("reservedWordLeave.mdb"));
        var queries = db.GetQueries();
        var q = Assert.Single(queries);
        Assert.Equal("Without Leave", q.Name);
        Assert.Equal(QueryType.Select, q.Type);
        Assert.Contains("LEAVE", q.Sql);
    }
}
