using System.Data.Common;
using System.Text.Json;
using Xunit;

namespace UCanAccess.Tests;

/// <summary>
/// Behavioral parity tests: the same SQL statements are executed through the
/// ORIGINAL UCanAccess (Java, via the SqlDump oracle) and through this port,
/// and the normalized result values are compared.
/// </summary>
public class SqlParityTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "fixtures", "sql");

    /// <summary>known deviations between the Java original and this port (currently none)</summary>
    private static readonly Dictionary<string, string> KnownDeviations = new()
    {
    };

    public static IEnumerable<object[]> Corpus()
    {
        foreach (string sqlFile in Directory.GetFiles(FixtureDir, "*.sql"))
        {
            string name = Path.GetFileNameWithoutExtension(sqlFile);
            yield return new object[] { name };
        }
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Sql_results_match_java_ucanaccess(string corpus)
    {
        string sqlPath = Path.Combine(FixtureDir, corpus + ".sql");
        string jsonPath = Path.Combine(FixtureDir, corpus + ".java.json");
        Assert.True(System.IO.File.Exists(sqlPath), $"missing {sqlPath}");
        Assert.True(System.IO.File.Exists(jsonPath), $"missing {jsonPath}; regenerate with tools/JavaOracle/run.ps1");

        string[] statements = SplitStatements(System.IO.File.ReadAllText(sqlPath)).ToArray();

        string fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", corpus + ".mdb");
        if (!System.IO.File.Exists(fixture))
        {
            fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", corpus + ".accdb");
        }
        Assert.True(System.IO.File.Exists(fixture), $"missing fixture for corpus {corpus}");
        using var conn = UCanAccessFactory.Instance.CreateConnection()!;
        conn.ConnectionString = $"Data Source={fixture};Read Only=true";
        conn.Open();

        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(jsonPath));
        var oracleStatements = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(statements.Length, oracleStatements.Count);

        for (int s = 0; s < statements.Length; s++)
        {
            string sql = statements[s];
            var oracle = oracleStatements[s];

            StatementResult expected = ReadOracleResult(oracle);
            StatementResult actual = ExecuteStatement(conn, sql);

            if (KnownDeviations.TryGetValue(sql, out string? deviation))
            {
                // documented deviation: assert both sides ran, skip value comparison
                Assert.Null(expected.ErrorCategory);
                Assert.Null(actual.ErrorCategory);
                continue;
            }

            Assert.Equal(expected.ErrorCategory, actual.ErrorCategory);
            if (expected.ErrorCategory != null)
            {
                continue;
            }

            Assert.Equal(expected.HasResultSet, actual.HasResultSet);
            if (oracle.TryGetProperty("affectedRows", out JsonElement affectedRows))
            {
                Assert.Equal(affectedRows.GetInt32(), actual.AffectedRows);
            }
            if (oracle.TryGetProperty("columnCount", out JsonElement columnCount))
            {
                Assert.Equal(columnCount.GetInt32(), actual.Columns.Count);
            }
            if (oracle.TryGetProperty("columns", out JsonElement columnNames))
            {
                var expectedNames = columnNames.EnumerateArray()
                    .Select(value => value.GetString() ?? string.Empty).ToList();
                Assert.Equal(expectedNames.Count, actual.Columns.Count);
                for (int i = 0; i < expectedNames.Count; i++)
                {
                    // JDBC assigns generated C1/C2 labels to unaliased expressions,
                    // while SQLite exposes the expression text.  The result-set
                    // position and type remain comparable in that case.
                    if (System.Text.RegularExpressions.Regex.IsMatch(expectedNames[i], "^C[0-9]+$",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        continue;
                    }
                    Assert.Equal(expectedNames[i].ToUpperInvariant(), actual.Columns[i].Name.ToUpperInvariant());
                }
            }
            if (oracle.TryGetProperty("columnTypes", out JsonElement columnTypes))
            {
                var expectedTypes = columnTypes.EnumerateArray()
                    .Select(value => NormalizeOracleType(value))
                    .ToList();
                Assert.Equal(expectedTypes.Count, actual.Columns.Count);
                for (int i = 0; i < expectedTypes.Count; i++)
                {
                    Assert.True(AreCompatibleTypes(expectedTypes[i], actual.Columns[i].Type),
                        $"statement {s} '{sql}' column {i}: expected {expectedTypes[i]}, " +
                        $"actual {actual.Columns[i].Type}");
                }
            }

            Assert.True(expected.OracleRows.Count == actual.Rows.Count,
                $"statement {s} '{sql}': row count {expected.OracleRows.Count} (Java) vs {actual.Rows.Count} (port)");

            for (int r = 0; r < expected.OracleRows.Count; r++)
            {
                var oracleValues = expected.OracleRows[r].EnumerateArray().ToList();
                var myValues = actual.Rows[r];
                Assert.True(oracleValues.Count == myValues.Count,
                    $"statement {s} '{sql}' row {r}: column count {oracleValues.Count} (Java) vs {myValues.Count} (port)");

                for (int c = 0; c < oracleValues.Count; c++)
                {
                    string normalizedExpected = NormalizeJava(oracleValues[c]);
                    string normalizedActual = NormalizeNet(myValues[c]);
                    Assert.True(string.Equals(normalizedExpected, normalizedActual, StringComparison.Ordinal),
                        $"statement {s} '{sql}' row {r} col {c}: {normalizedExpected} (Java) vs {normalizedActual} (port)");
                }
            }
        }
    }

    private sealed record ColumnResult(string Name, string Type);

    private sealed record StatementResult(bool HasResultSet, int AffectedRows,
        List<ColumnResult> Columns, List<List<object?>> Rows, string? ErrorCategory)
    {
        internal List<JsonElement> OracleRows { get; init; } = new();
    }

    private static StatementResult ReadOracleResult(JsonElement oracle)
    {
        bool hasResultSet = oracle.TryGetProperty("resultSet", out JsonElement resultSet)
            ? resultSet.GetBoolean()
            : oracle.TryGetProperty("columns", out _);
        int affectedRows = oracle.TryGetProperty("affectedRows", out JsonElement affected)
            ? affected.GetInt32()
            : 0;
        var columns = new List<ColumnResult>();
        if (oracle.TryGetProperty("columns", out JsonElement names))
        {
            foreach (JsonElement name in names.EnumerateArray())
            {
                columns.Add(new ColumnResult(name.GetString() ?? string.Empty, string.Empty));
            }
        }
        if (oracle.TryGetProperty("columnTypes", out JsonElement types))
        {
            int i = 0;
            foreach (JsonElement type in types.EnumerateArray())
            {
                string name = type.TryGetProperty("name", out JsonElement n)
                    ? n.GetString() ?? (i < columns.Count ? columns[i].Name : string.Empty)
                    : i < columns.Count ? columns[i].Name : string.Empty;
                string normalized = NormalizeOracleType(type);
                if (i < columns.Count)
                {
                    columns[i] = new ColumnResult(name, normalized);
                }
                else
                {
                    columns.Add(new ColumnResult(name, normalized));
                }
                i++;
            }
        }
        var rows = oracle.TryGetProperty("rows", out JsonElement rowValues)
            ? rowValues.EnumerateArray().ToList()
            : new List<JsonElement>();
        string? errorCategory = oracle.TryGetProperty("errorCategory", out JsonElement category)
            ? category.GetString()
            : oracle.TryGetProperty("error", out JsonElement error)
                ? NormalizeErrorText(error.GetString() ?? string.Empty)
                : null;
        return new StatementResult(hasResultSet, affectedRows, columns,
            new List<List<object?>>(), errorCategory) { OracleRows = rows };
    }

    private static string NormalizeOracleType(JsonElement type)
    {
        string name = type.TryGetProperty("typeName", out JsonElement typeName)
            ? typeName.GetString() ?? string.Empty
            : type.TryGetProperty("className", out JsonElement className)
                ? className.GetString() ?? string.Empty
                : type.GetRawText();
        return NormalizeTypeName(name);
    }

    private static string NormalizeTypeName(string name)
    {
        string upper = name.ToUpperInvariant();
        if (upper.Contains("BOOL") || upper is "BIT" or "YESNO") return "BOOLEAN";
        if (upper.Contains("DATE") || upper.Contains("TIME")) return "DATETIME";
        if (upper.Contains("DECIMAL") || upper.Contains("NUMERIC") || upper.Contains("MONEY")
            || upper.Contains("CURRENCY")) return "DECIMAL";
        if (upper.Contains("CHAR") || upper.Contains("TEXT") || upper.Contains("CLOB")
            || upper.Contains("MEMO")
            || upper.Contains("STRING")) return "TEXT";
        if (upper.Contains("BINARY") || upper.Contains("BLOB") || upper.Contains("BYTE[]")) return "BINARY";
        if (upper.Contains("REAL") || upper.Contains("FLOAT") || upper.Contains("DOUBLE")) return "FLOAT";
        if (upper.Contains("INT") || upper.Contains("LONG") || upper.Contains("SHORT")
            || upper.Contains("BYTE") || upper.Contains("NUMBER")) return "INTEGER";
        return upper;
    }

    private static bool AreCompatibleTypes(string expected, string actual)
    {
        if (expected == actual)
        {
            return true;
        }
        bool expectedNumeric = expected is "INTEGER" or "DECIMAL" or "FLOAT";
        bool actualNumeric = actual is "INTEGER" or "DECIMAL" or "FLOAT";
        return expectedNumeric && actualNumeric;
    }

    private static StatementResult ExecuteStatement(DbConnection conn, string sql)
    {
        var rows = new List<List<object?>>();
        try
        {
            string first = FirstWord(sql);
            if (first is not ("SELECT" or "WITH"))
            {
                using var command = conn.CreateCommand();
                command.CommandText = sql;
                return new StatementResult(false, command.ExecuteNonQuery(),
                    new List<ColumnResult>(), rows, null);
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            var columns = Enumerable.Range(0, reader.FieldCount)
                .Select(i => new ColumnResult(reader.GetName(i),
                    NormalizeTypeName(reader.GetDataTypeName(i))))
                .ToList();
            while (reader.Read())
            {
                var row = new List<object?>(reader.FieldCount);
                for (int c = 0; c < reader.FieldCount; c++)
                {
                    row.Add(reader.IsDBNull(c) ? null : reader.GetValue(c));
                }
                rows.Add(row);
            }
            for (int i = 0; i < columns.Count; i++)
            {
                if (columns[i].Type == "BINARY"
                    && rows.Any(row => row[i] is not null && row[i] is not DBNull && IsNumericValue(row[i])))
                {
                    columns[i] = columns[i] with { Type = "DECIMAL" };
                }
                else if (columns[i].Type == "TEXT"
                    && rows.Any(row => row[i] is string value && IsIsoDate(value)))
                {
                    columns[i] = columns[i] with { Type = "DATETIME" };
                }
            }
            return new StatementResult(true, reader.RecordsAffected, columns, rows, null);
        }
        catch (Exception ex)
        {
            return new StatementResult(false, 0, new List<ColumnResult>(), rows,
                NormalizeExceptionCategory(ex));
        }
    }

    private static bool IsNumericValue(object? value)
        => value is byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal;

    private static string FirstWord(string sql)
    {
        string value = StripLeadingComments(sql).TrimStart();
        int end = 0;
        while (end < value.Length && !char.IsWhiteSpace(value[end])) end++;
        return value[..end].ToUpperInvariant();
    }

    private static string NormalizeExceptionCategory(Exception ex)
    {
        if (ex is NotSupportedException) return "unsupported";
        return NormalizeErrorText(ex.ToString());
    }

    private static string NormalizeErrorText(string text)
    {
        string upper = text.ToUpperInvariant();
        if (upper.Contains("CONSTRAINT") || upper.Contains("UNIQUE")
            || upper.Contains("FOREIGN KEY") || upper.Contains("NOT NULL")) return "constraint";
        if (upper.Contains("SYNTAX") || upper.Contains("PARSE") || upper.Contains("NEAR ")
            || upper.Contains("NO SUCH TABLE") || upper.Contains("NO SUCH COLUMN")) return "syntax";
        if (upper.Contains("CONNECTION") || upper.Contains("LOCKED") || upper.Contains("TIMEOUT")) return "connection";
        if (upper.Contains("OVERFLOW") || upper.Contains("OUT OF RANGE")
            || upper.Contains("CONVERSION") || upper.Contains("DATATYPE MISMATCH")) return "data";
        if (upper.Contains("NULL")) return "constraint";
        if (text.Contains("DbException", StringComparison.Ordinal)
            || text.Contains("SQLException", StringComparison.Ordinal)) return "sql";
        return "execution";
    }

    private static string StripLeadingComments(string sql)
    {
        string value = sql;
        while (true)
        {
            value = value.TrimStart();
            if (value.StartsWith("--", StringComparison.Ordinal))
            {
                int newline = value.IndexOf('\n');
                if (newline < 0) return string.Empty;
                value = value[(newline + 1)..];
            }
            else if (value.StartsWith("/*", StringComparison.Ordinal))
            {
                int end = value.IndexOf("*/", 2, StringComparison.Ordinal);
                if (end < 0) return string.Empty;
                value = value[(end + 2)..];
            }
            else return value;
        }
    }

    private static IEnumerable<string> SplitStatements(string sql)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;
        bool bracketed = false, lineComment = false, blockComment = false, sawSemicolon = false;
        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            if (lineComment) { current.Append(c); if (c == '\n') lineComment = false; continue; }
            if (blockComment) { current.Append(c); if (c == '*' && i + 1 < sql.Length && sql[i + 1] == '/') { current.Append(sql[++i]); blockComment = false; } continue; }
            if (quote.HasValue) { current.Append(c); if (c == quote.Value) { if (i + 1 < sql.Length && sql[i + 1] == quote.Value) current.Append(sql[++i]); else quote = null; } continue; }
            if (bracketed) { current.Append(c); if (c == ']') { if (i + 1 < sql.Length && sql[i + 1] == ']') current.Append(sql[++i]); else bracketed = false; } continue; }
            if (c is '\'' or '"') { quote = c; current.Append(c); }
            else if (c == '[') { bracketed = true; current.Append(c); }
            else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { lineComment = true; current.Append(c).Append(sql[++i]); }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { blockComment = true; current.Append(c).Append(sql[++i]); }
            else if (c == ';') { sawSemicolon = true; parts.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        if (sawSemicolon) return parts.Select(StripLeadingComments).Select(s => s.Trim()).Where(s => s.Length > 0);
        return sql.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(StripLeadingComments).Select(s => s.Trim()).Where(s => s.Length > 0);
    }

    // ------------------------------------------------------------------
    // normalization
    // ------------------------------------------------------------------

    private static string NormalizeJava(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => "num:" + CanonicalInteger(value.GetRawText()),
        JsonValueKind.String => "str:" + value.GetString(),
        JsonValueKind.Object when value.TryGetProperty("f", out var f) =>
            "num:" + NormalizeNumber(BitConverter.Int32BitsToSingle(Convert.ToInt32(f.GetString()!.Substring(2), 16))),
        JsonValueKind.Object when value.TryGetProperty("d", out var d) =>
            "num:" + NormalizeNumber(BitConverter.Int64BitsToDouble(Convert.ToInt64(d.GetString()!.Substring(2), 16))),
        JsonValueKind.Object when value.TryGetProperty("dec", out var dec) =>
            "num:" + CanonicalDecimal(dec),
        JsonValueKind.Object when value.TryGetProperty("dt", out var dt) => "dt:" + dt.GetString(),
        JsonValueKind.Object when value.TryGetProperty("b64", out var b64) => "b64:" + b64.GetString(),
        _ => "raw:" + value.GetRawText(),
    };

    private static string CanonicalInteger(string raw)
    {
        if (System.Numerics.BigInteger.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var integer))
        {
            return integer.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return raw;
    }

    private static string CanonicalDecimal(JsonElement dec)
    {
        var parts = dec.EnumerateArray().ToList();
        var unscaled = System.Numerics.BigInteger.Parse(parts[0].GetString()!);
        int scale = parts[1].GetInt32();
        return CanonicalDecimal(unscaled, scale);
    }

    private static string NormalizeNet(object? value) => value switch
    {
        null or DBNull => "null",
        bool b => b ? "true" : "false",
        byte n => "num:" + n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        sbyte n => "num:" + n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        short n => "num:" + n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ushort n => "num:" + n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        int n => "num:" + n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        uint n => "num:" + n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        long n => "num:" + n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ulong n => "num:" + n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        float f => "num:" + NormalizeNumber(f),
        double d => "num:" + NormalizeNumber(d),
        decimal m => "num:" + CanonicalDecimal(m),
        DateTime dt => "dt:" + FormatDateTime(dt),
        byte[] bytes => "b64:" + Convert.ToBase64String(bytes),
        string s when IsIsoDate(s) => "dt:" + s.Replace(' ', 'T'),
        string s => "str:" + s,
        _ => "raw:" + value,
    };

    private static bool IsIsoDate(string s)
        => s.Length == 23 && s[4] == '-' && s[7] == '-' && s[10] == ' ' && s[13] == ':' && s[16] == ':' && s[19] == '.';

    private static string NormalizeNumber(double d)
        => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatDateTime(DateTime value)
        => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

    private static string CanonicalDecimal(decimal value)
    {
        int[] bits = decimal.GetBits(value);
        var unscaled = (System.Numerics.BigInteger)(uint)bits[0]
                       | ((System.Numerics.BigInteger)(uint)bits[1] << 32)
                       | ((System.Numerics.BigInteger)(uint)bits[2] << 64);
        if ((bits[3] & int.MinValue) != 0)
        {
            unscaled = -unscaled;
        }

        return CanonicalDecimal(unscaled, (bits[3] >> 16) & 0x7f);
    }

    private static string CanonicalDecimal(System.Numerics.BigInteger unscaled, int scale)
    {
        bool negative = unscaled.Sign < 0;
        string digits = System.Numerics.BigInteger.Abs(unscaled).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string result;
        if (scale <= 0)
        {
            result = digits + new string('0', -scale);
        }
        else if (digits.Length <= scale)
        {
            result = "0." + new string('0', scale - digits.Length) + digits;
        }
        else
        {
            int point = digits.Length - scale;
            result = digits[..point] + "." + digits[point..];
        }

        if (result.Contains('.'))
        {
            result = result.TrimEnd('0').TrimEnd('.');
        }

        if (result.Length == 0 || result == "0")
        {
            return "0";
        }

        return negative ? "-" + result : result;
    }
}
