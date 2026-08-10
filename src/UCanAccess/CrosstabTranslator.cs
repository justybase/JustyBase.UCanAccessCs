using System.Text;
using static UCanAccess.AccessTokenizer;

namespace UCanAccess;

/// <summary>
/// Converts the Access crosstab grammar into a regular grouped SELECT.
/// SQLite does not parse Access TRANSFORM directly, so the conversion happens
/// before the normal Access-to-SQLite translation.
/// </summary>
internal static class CrosstabTranslator
{
    public static bool TryBuildDynamicValueQuery(string sql, out string valueQuery)
    {
        valueQuery = string.Empty;
        if (!sql.TrimStart().StartsWith("transform", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        List<Token> tokens = Tokenize(sql);
        while (tokens.Count > 0 && tokens[^1].Text == ";")
        {
            tokens.RemoveAt(tokens.Count - 1);
        }
        int from = FindTopLevel(tokens, "from", 1);
        int pivot = FindTopLevel(tokens, "pivot", from + 1);
        int inKeyword = pivot < 0 ? -1 : FindTopLevel(tokens, "in", pivot + 1);
        if (from < 0 || pivot < 0 || inKeyword >= 0)
        {
            return false;
        }

        int groupBy = FindTopLevel(tokens, "group by", from + 1, pivot);
        int where = FindTopLevel(tokens, "where", from + 1, groupBy >= 0 ? groupBy : pivot);
        int sourceEnd = where >= 0 ? where : groupBy >= 0 ? groupBy : pivot;
        string source = Join(tokens, from + 1, sourceEnd);
        string pivotExpression = Join(tokens, pivot + 1, tokens.Count);
        valueQuery = $"SELECT DISTINCT {pivotExpression} FROM {source}";
        if (where >= 0)
        {
            valueQuery += " WHERE " + Join(tokens, where + 1, groupBy >= 0 ? groupBy : pivot);
        }
        return true;
    }

    public static string AddPivotValues(string sql, IEnumerable<object?> values)
    {
        string body = sql.Trim().TrimEnd(';').TrimEnd();
        string literals = string.Join(", ", values.Where(v => v is not null and not DBNull)
            .Select(ToLiteral));
        if (literals.Length == 0)
        {
            literals = "NULL";
        }
        return body + " IN (" + literals + ")";
    }

    public static bool TryTranslate(string sql, Func<string, bool>? isExactDecimalColumn,
        out string translated)
    {
        translated = string.Empty;
        if (!sql.TrimStart().StartsWith("transform", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        List<Token> tokens = Tokenize(sql);
        while (tokens.Count > 0 && tokens[^1].Text == ";")
        {
            tokens.RemoveAt(tokens.Count - 1);
        }
        if (tokens.Count == 0 || !tokens[0].Text.Equals("transform", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int select = FindTopLevel(tokens, "select", 1);
        int from = FindTopLevel(tokens, "from", select + 1);
        int pivot = FindTopLevel(tokens, "pivot", from + 1);
        if (select < 0 || from < 0 || pivot < 0)
        {
            throw new NotSupportedException(
                "TRANSFORM requires aggregate, SELECT, FROM and PIVOT clauses.");
        }

        int groupBy = FindTopLevel(tokens, "group by", from + 1, pivot);
        int where = FindTopLevel(tokens, "where", from + 1, groupBy >= 0 ? groupBy : pivot);

        int sourceEnd = where >= 0 ? where : groupBy >= 0 ? groupBy : pivot;
        string source = Join(tokens, from + 1, sourceEnd);
        string? whereClause = where >= 0
            ? Join(tokens, where + 1, groupBy >= 0 ? groupBy : pivot)
            : null;

        List<string> rowExpressions = SplitExpressions(tokens, select + 1, from)
            .Select(StripAlias)
            .Where(s => s.Length > 0)
            .ToList();
        if (rowExpressions.Count == 0)
        {
            throw new NotSupportedException("TRANSFORM requires at least one row expression.");
        }

        string aggregateText = StripAlias(Join(tokens, 1, select));
        (string aggregateName, string aggregateArgument) = ParseAggregate(aggregateText);
        List<string> groupExpressions = groupBy >= 0
            ? SplitExpressions(tokens, groupBy + 1, pivot).Select(StripAlias).Where(s => s.Length > 0).ToList()
            : rowExpressions;
        if (groupExpressions.Count == 0)
        {
            groupExpressions = rowExpressions;
        }

        int inKeyword = FindTopLevel(tokens, "in", pivot + 1);
        if (inKeyword < 0)
        {
            throw new NotSupportedException(
                "Dynamic TRANSFORM/PIVOT without an IN (...) list is not supported yet.");
        }

        string pivotExpression = Join(tokens, pivot + 1, inKeyword);
        if (pivotExpression.Length == 0)
        {
            throw new NotSupportedException("PIVOT requires an expression.");
        }

        if (inKeyword + 1 >= tokens.Count || tokens[inKeyword + 1].Text != "(")
        {
            throw new NotSupportedException("PIVOT IN requires a parenthesized value list.");
        }
        int close = FindMatchingParen(tokens, inKeyword + 1);
        if (close != tokens.Count - 1)
        {
            throw new NotSupportedException("Unexpected tokens after PIVOT IN (...).");
        }
        List<string> pivotValues = SplitExpressions(tokens, inKeyword + 2, close)
            .Where(s => s.Length > 0)
            .ToList();
        if (pivotValues.Count == 0)
        {
            throw new NotSupportedException("PIVOT IN requires at least one value.");
        }

        string aggregateFunction = BuildAggregate(aggregateName, aggregateArgument, pivotExpression,
            isExactDecimalColumn);
        var projection = new List<string>(rowExpressions);
        foreach (string value in pivotValues)
        {
            string alias = PivotAlias(value);
            string condition = $"({pivotExpression}) = ({value})";
            string expression = aggregateFunction.Replace("__PIVOT_CONDITION__", condition,
                StringComparison.Ordinal);
            projection.Add($"{expression} AS {QuoteIdentifier(alias)}");
        }

        var sb = new StringBuilder("SELECT ");
        sb.Append(string.Join(", ", projection));
        sb.Append(" FROM ").Append(source);
        if (!string.IsNullOrWhiteSpace(whereClause))
        {
            sb.Append(" WHERE ").Append(whereClause);
        }
        sb.Append(" GROUP BY ").Append(string.Join(", ", groupExpressions));
        translated = sb.ToString();
        return true;
    }

    private static string BuildAggregate(string name, string argument, string pivotExpression,
        Func<string, bool>? isExactDecimalColumn)
    {
        string upper = name.ToUpperInvariant();
        string conditionalArgument = upper == "COUNT" && argument == "*"
            ? "1"
            : $"CASE WHEN __PIVOT_CONDITION__ THEN {argument} END";

        string function = upper switch
        {
            "SUM" when isExactDecimalColumn?.Invoke(argument) == true => "uca_decimal_sum",
            "MIN" when isExactDecimalColumn?.Invoke(argument) == true => "uca_decimal_min",
            "MAX" when isExactDecimalColumn?.Invoke(argument) == true => "uca_decimal_max",
            "COUNT" => "COUNT",
            "SUM" or "AVG" or "MIN" or "MAX" or "STDEV" or "STDEVP" or "VAR" or "VARP" => upper,
            _ => throw new NotSupportedException(
                $"TRANSFORM aggregate '{name}' is not supported. Supported aggregates: COUNT, SUM, AVG, MIN, MAX, STDEV, STDEVP, VAR, VARP."),
        };

        if (upper == "COUNT" && argument == "*")
        {
            conditionalArgument = "CASE WHEN __PIVOT_CONDITION__ THEN 1 END";
        }
        return $"{function}({conditionalArgument})";
    }

    private static (string Name, string Argument) ParseAggregate(string text)
    {
        List<Token> tokens = Tokenize(text);
        if (tokens.Count < 3 || tokens[1].Text != "(" || tokens[^1].Text != ")")
        {
            throw new NotSupportedException("TRANSFORM requires a supported aggregate expression.");
        }
        int close = FindMatchingParen(tokens, 1);
        if (close != tokens.Count - 1)
        {
            throw new NotSupportedException("TRANSFORM aggregate expressions may contain only one aggregate call.");
        }
        return (tokens[0].Text, Join(tokens, 2, close));
    }

    private static int FindTopLevel(List<Token> tokens, string keyword, int start, int end = -1)
    {
        int depth = 0;
        int limit = end < 0 ? tokens.Count : Math.Min(end, tokens.Count);
        for (int i = start; i < limit; i++)
        {
            if (tokens[i].Text == "(") depth++;
            else if (tokens[i].Text == ")") depth = Math.Max(0, depth - 1);
            else if (depth == 0 && tokens[i].Text.Equals(keyword, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private static int FindMatchingParen(List<Token> tokens, int open)
    {
        int depth = 0;
        for (int i = open; i < tokens.Count; i++)
        {
            if (tokens[i].Text == "(") depth++;
            else if (tokens[i].Text == ")" && --depth == 0) return i;
        }
        throw new NotSupportedException("Unbalanced parentheses in TRANSFORM/PIVOT.");
    }

    private static List<string> SplitExpressions(List<Token> tokens, int start, int end)
    {
        var values = new List<string>();
        int depth = 0;
        int itemStart = start;
        for (int i = start; i < end; i++)
        {
            if (tokens[i].Text == "(") depth++;
            else if (tokens[i].Text == ")") depth = Math.Max(0, depth - 1);
            else if (tokens[i].Text == "," && depth == 0)
            {
                values.Add(Join(tokens, itemStart, i));
                itemStart = i + 1;
            }
        }
        values.Add(Join(tokens, itemStart, end));
        return values;
    }

    private static string StripAlias(string value)
    {
        List<Token> tokens = Tokenize(value);
        if (tokens.Count >= 2 && tokens[^2].Text.Equals("as", StringComparison.OrdinalIgnoreCase))
        {
            return Join(tokens, 0, tokens.Count - 2);
        }
        return value.Trim();
    }

    private static string PivotAlias(string value)
    {
        List<Token> tokens = Tokenize(value);
        if (tokens.Count == 1)
        {
            return tokens[0].Text;
        }
        return value.Trim().Trim('[', ']', '"', '`');
    }

    private static string Join(List<Token> tokens, int start, int end)
    {
        var sb = new StringBuilder();
        for (int i = start; i < end; i++)
        {
            Token token = tokens[i];
            if (i > start && !NoSpace(tokens[i - 1], token)) sb.Append(' ');
            sb.Append(Render(token));
        }
        return sb.ToString().Trim();
    }

    private static bool NoSpace(Token previous, Token current)
        => previous.Text is "(" or "." || current.Text is ")" or "," or "(" or ".";

    private static string Render(Token token) => token.Kind switch
    {
        Kind.Ident => QuoteIdentifier(token.Text),
        Kind.Str => "'" + token.Text.Replace("'", "''") + "'",
        Kind.Date => "#" + token.Text + "#",
        _ => token.Text,
    };

    private static string QuoteIdentifier(string name)
        => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static string ToLiteral(object? value)
        => value switch
        {
            string text => "'" + text.Replace("'", "''") + "'",
            DateTime date => "#" + date.ToString("M/d/yyyy HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + "#",
            bool flag => flag ? "-1" : "0",
            decimal number => ExactDecimal.FromDecimal(number).ToString(),
            float number => number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            double number => number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
        };
}
