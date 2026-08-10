using System.Data;
using System.Data.Common;
using Xunit;

namespace UCanAccess.Tests;

/// <summary>
/// Saved queries: exposed as queryable views, listed through GetSchema("Views"),
/// and their SQL can be executed with parameters (PARAMETERS clause).
/// </summary>
public class QueryTests
{
    private static DbConnection Open(string fixture)
    {
        var conn = UCanAccessFactory.Instance.CreateConnection()!;
        conn.ConnectionString = $"Data Source={Path.Combine(AppContext.BaseDirectory, "fixtures", fixture)};Read Only=true";
        conn.Open();
        return conn;
    }

    [Fact]
    public void Select_from_saved_query_view()
    {
        using var conn = Open("accessLike.mdb");
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM [q_like2]";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.False(reader.Read()); // one row
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM q_like2";
            Assert.Equal(1L, cmd.ExecuteScalar());
        }
    }

    [Fact]
    public void Saved_query_sql_exposed_via_get_schema()
    {
        using var conn = Open("accessLike.mdb");
        DataTable views = conn.GetSchema("Views");
        DataRow? row = views.AsEnumerable()
            .FirstOrDefault(r => r.Field<string>("TABLE_NAME")!.Equals("q_like2", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(row);
        Assert.Contains("LIKE", row!.Field<string>("VIEW_DEFINITION")!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Saved_query_filterable_in_view()
    {
        using var conn = Open("accessLike.mdb");
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Campo2 FROM q_like2 WHERE Campo2 LIKE 'd*'";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
        }
    }
}
