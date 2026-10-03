using Xunit;

namespace UCanAccess.Tests;

/// <summary>
/// S5: upstream ignoreCase / concatNulls parity.
/// </summary>
public class TextSemanticsTests
{
    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static UCanAccessConnection Open(string fixture, string extra = "")
    {
        var conn = (UCanAccessConnection)UCanAccessFactory.Instance.CreateConnection()!;
        conn.ConnectionString = $"Data Source={Fixture(fixture)};Read Only=true{(extra.Length > 0 ? ";" + extra : "")}";
        conn.Open();
        return conn;
    }

    [Fact]
    public void Translator_concat_wraps_null_by_default()
    {
        string sql = AccessSqlTranslator.Translate("SELECT NULL & 'x' FROM t", out _, out _);
        Assert.Contains("ifnull", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Translator_concat_propagates_null_when_enabled()
    {
        string sql = AccessSqlTranslator.Translate("SELECT NULL & 'x' FROM t", out _, out _,
            null, null, null, concatNulls: true);
        Assert.DoesNotContain("ifnull", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("||", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Translator_pipe_concat_follows_concat_nulls()
    {
        string @default = AccessSqlTranslator.Translate("SELECT NULL || 'x' FROM t", out _, out _);
        Assert.Contains("ifnull", @default, StringComparison.OrdinalIgnoreCase);
        string propagating = AccessSqlTranslator.Translate("SELECT NULL || 'x' FROM t", out _, out _,
            null, null, null, concatNulls: true);
        Assert.DoesNotContain("ifnull", propagating, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IgnoreCase_true_matches_different_case()
    {
        using var conn = Open("generated/genLinkee.mdb", "Ignore Case=true");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM t_linkee WHERE name='LINKEE ONE'";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    [Fact]
    public void IgnoreCase_false_is_case_sensitive()
    {
        using var conn = Open("generated/genLinkee.mdb", "Ignore Case=false");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM t_linkee WHERE name='LINKEE ONE'";
        Assert.Equal(0L, cmd.ExecuteScalar());
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT count(*) FROM t_linkee WHERE name='linkee one'";
        Assert.Equal(1L, cmd2.ExecuteScalar());
    }

    [Fact]
    public void Like_follows_ignore_case()
    {
        using (var conn = Open("generated/genLinkee.mdb", "Ignore Case=true"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM t_linkee WHERE name LIKE 'LINKEE*'";
            Assert.Equal(2L, cmd.ExecuteScalar());
        }
        using (var conn = Open("generated/genLinkee.mdb", "Ignore Case=false"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM t_linkee WHERE name LIKE 'LINKEE*'";
            Assert.Equal(0L, cmd.ExecuteScalar());
        }
    }

    [Fact]
    public void ConcatNulls_false_maps_null_to_empty()
    {
        using var conn = Open("generated/genLinkee.mdb", "Concat Nulls=false");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT NULL & 'x'";
        Assert.Equal("x", cmd.ExecuteScalar());
    }

    [Fact]
    public void ConcatNulls_true_propagates_null()
    {
        using var conn = Open("generated/genLinkee.mdb", "Concat Nulls=true");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT NULL & 'x'";
        Assert.True(cmd.ExecuteScalar() is null or DBNull);
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT NULL || 'x'";
        Assert.True(cmd2.ExecuteScalar() is null or DBNull);
    }

    [Fact]
    public void Connection_string_defaults_match_java()
    {
        var parsed = new UCanAccessConnectionString("Data Source=x.mdb");
        Assert.True(parsed.IgnoreCase);
        Assert.False(parsed.ConcatNulls);
    }
}
