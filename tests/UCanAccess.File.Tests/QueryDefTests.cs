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

    [Fact]
    public void Creates_and_drops_select_querydef_roundtrip()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uca-querydef-{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Fixture("accessLike.mdb"), path);
        try
        {
            using (var db = Database.Open(path, readOnly: false))
            {
                db.CreateView("q_managed", "SELECT Campo2 FROM t_like2 WHERE Campo2 LIKE 'd*'");
                QueryDef query = Assert.Single(db.GetQueries(), q => q.Name == "q_managed");
                Assert.Equal(QueryType.Select, query.Type);
                Assert.Contains("Campo2", query.Sql);
                Assert.Contains("WHERE", query.Sql, StringComparison.OrdinalIgnoreCase);
            }

            using (var reopened = Database.Open(path, readOnly: true))
            {
                QueryDef query = Assert.Single(reopened.GetQueries(), q => q.Name == "q_managed");
                Assert.Contains("t_like2", query.Sql);
                Assert.Contains("LIKE", query.Sql, StringComparison.OrdinalIgnoreCase);
            }

            using (var db = Database.Open(path, readOnly: false))
            {
                db.DropView("q_managed");
                Assert.DoesNotContain(db.GetQueries(), query => query.Name == "q_managed");
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Creates_parameterized_select_querydef_with_parameter_metadata()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uca-querydef-param-{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Fixture("accessLike.mdb"), path);
        try
        {
            using (var db = Database.Open(path, readOnly: false))
            {
                db.CreateView("q_managed_param",
                    "PARAMETERS p TEXT(20); SELECT Campo2 FROM t_like2 WHERE Campo2 = p");
            }

            using var reopened = Database.Open(path, readOnly: true);
            QueryDef query = Assert.Single(reopened.GetQueries(), q => q.Name == "q_managed_param");
            Assert.Equal(QueryType.Select, query.Type);
            Assert.Contains("PARAMETERS p Text(20)", query.Sql);
            Assert.Contains("Campo2 = p", query.Sql);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Creates_join_querydef_roundtrip()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uca-querydef-join-{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Fixture("generated/genEmpty.mdb"), path);
        try
        {
            using (var db = Database.Open(path, readOnly: false))
            {
                Table left = db.CreateTable("q_left", new[]
                {
                    new ColumnBuilder("id", DataType.Long),
                });
                left.AddRow(new object?[] { 1 });
                Table right = db.CreateTable("q_right", new[]
                {
                    new ColumnBuilder("id", DataType.Long),
                    new ColumnBuilder("left_id", DataType.Long),
                });
                right.AddRow(new object?[] { 2, 1 });

                db.CreateView("q_join",
                    "SELECT l.id, r.id AS right_id FROM q_left AS l "
                    + "INNER JOIN q_right AS r ON l.id = r.left_id");
            }

            using var reopened = Database.Open(path, readOnly: true);
            QueryDef query = Assert.Single(reopened.GetQueries(), q => q.Name == "q_join");
            Assert.Contains("INNER JOIN", query.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("l.id", query.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("r.left_id", query.Sql, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Preserves_top_count_distinct_and_delimited_table_names()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uca-querydef-top-{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Fixture("accessLike.mdb"), path);
        try
        {
            using (var db = Database.Open(path, readOnly: false))
            {
                db.CreateView("q_top_managed",
                    "SELECT DISTINCT TOP 5 Campo2 FROM [t_like2]");
            }

            using var reopened = Database.Open(path, readOnly: true);
            QueryDef query = Assert.Single(reopened.GetQueries(), q => q.Name == "q_top_managed");
            Assert.Contains("DISTINCT TOP 5", query.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("FROM t_like2", query.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"t_like2\"", query.Sql, StringComparison.Ordinal);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
