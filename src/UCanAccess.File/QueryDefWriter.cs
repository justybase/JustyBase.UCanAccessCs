using System.Text;
using System.Text.RegularExpressions;

namespace UCanAccess.File;

/// <summary>
/// Writes the standard Access catalog representation of a saved SELECT query.
/// The writer deliberately accepts a conservative, composable SELECT subset;
/// unsupported grammar is rejected before MSysObjects or MSysQueries is changed.
/// </summary>
internal static class QueryDefWriter
{
    private const byte StartAttribute = 0;
    private const byte ParameterAttribute = 2;
    private const byte FlagAttribute = 3;
    private const byte TableAttribute = 5;
    private const byte ColumnAttribute = 6;
    private const byte JoinAttribute = 7;
    private const byte WhereAttribute = 8;
    private const byte GroupByAttribute = 9;
    private const byte HavingAttribute = 10;
    private const byte OrderByAttribute = 11;
    private const byte EndAttribute = 255;

    private const short SelectStarFlag = 0x01;
    private const short DistinctFlag = 0x02;
    private const short DistinctRowFlag = 0x08;
    private const short TopFlag = 0x10;
    private const short PercentFlag = 0x20;

    private static readonly byte[] DefaultOrder = { 0, 0, 0, 1 };

    internal static void Create(Database database, string viewName, string selectSql)
    {
        if (database.IsReadOnly)
        {
            throw new DatabaseException("The database was opened read-only.");
        }
        ValidateName(viewName, "view");
        if (database.GetTable(viewName) != null
            || database.GetSystemTable(viewName) != null
            || database.GetQueries().Any(query => query.Name.Equals(viewName,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"An object named '{viewName}' already exists.");
        }

        QuerySpec specification = Parse(selectSql);
        Table queries = database.GetSystemTable("MSysQueries")
            ?? throw new DatabaseException("The database does not contain MSysQueries.");
        int objectId = database.AllocateQueryObjectId();
        bool catalogAdded = false;
        try
        {
            database.AddToSystemCatalog(viewName, objectId, Database.TypeQuery, objectId);
            catalogAdded = true;
            foreach (QueryRowSpec row in specification.Rows)
            {
                queries.AddRow(BuildValues(queries, objectId, row));
            }
        }
        catch
        {
            // The surrounding DDL path is staged atomically, but clean up the
            // in-memory/catalog state as well when the file-layer API is called
            // directly or a later row fails.
            foreach (Table.RowLocation location in queries.RowLocations()
                .Where(location => TryGetInt(location.Row, "ObjectId") == objectId)
                .ToArray())
            {
                try { queries.DeleteRow(location.PageNumber, location.RowNumber); } catch { }
            }
            if (catalogAdded)
            {
                try { database.RemoveFromSystemCatalog(viewName); } catch { }
            }
            throw;
        }
    }

    internal static void Drop(Database database, string viewName)
    {
        if (database.IsReadOnly)
        {
            throw new DatabaseException("The database was opened read-only.");
        }
        QueryDef query = database.GetQueries().FirstOrDefault(item =>
            item.Type == QueryType.Select
            && item.Name.Equals(viewName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"View '{viewName}' does not exist.");
        Table queries = database.GetSystemTable("MSysQueries")
            ?? throw new DatabaseException("The database does not contain MSysQueries.");
        foreach (Table.RowLocation location in queries.RowLocations()
            .Where(location => TryGetInt(location.Row, "ObjectId") == query.ObjectId)
            .ToArray())
        {
            queries.DeleteRow(location.PageNumber, location.RowNumber);
        }
        database.RemoveFromSystemCatalog(query.Name);
    }

    private static object?[] BuildValues(Table queries, int objectId, QueryRowSpec specification)
    {
        var values = new object?[queries.Columns.Count];
        for (int i = 0; i < queries.Columns.Count; i++)
        {
            values[i] = queries.Columns[i].Name switch
            {
                "ObjectId" => objectId,
                "Attribute" => specification.Attribute,
                "Order" => specification.Order ?? DefaultOrder,
                "Name1" => specification.Name1,
                "Name2" => specification.Name2,
                "Expression" => specification.Expression,
                "Flag" => specification.Flag,
                "LvExtra" => specification.Extra,
                _ => null,
            };
        }
        return values;
    }

    private static QuerySpec Parse(string sql)
    {
        string input = sql.Trim();
        while (input.EndsWith(';'))
        {
            input = input[..^1].TrimEnd();
        }
        if (input.Length == 0)
        {
            throw new InvalidOperationException("CREATE VIEW requires a SELECT query.");
        }

        var rows = new List<QueryRowSpec>
        {
            new(StartAttribute),
        };
        string statement = input;
        if (StartsWithKeyword(statement, "PARAMETERS"))
        {
            int separator = FindTopLevelChar(statement, ';');
            if (separator < 0)
            {
                throw new NotSupportedException("A PARAMETERS clause must be terminated before SELECT.");
            }
            foreach (QueryRowSpec parameter in ParseParameters(statement[10..separator]))
            {
                rows.Add(parameter);
            }
            statement = statement[(separator + 1)..].TrimStart();
        }
        if (!StartsWithKeyword(statement, "SELECT"))
        {
            throw new NotSupportedException("Only SELECT QueryDef definitions are supported.");
        }

        int selectStart = KeywordEnd(statement, 0, "SELECT");
        int from = FindTopLevelKeyword(statement, "FROM", selectStart);
        int where = FindTopLevelKeyword(statement, "WHERE", from < 0 ? selectStart : from + 4);
        int groupBy = FindTopLevelKeyword(statement, "GROUP BY", from < 0 ? selectStart : from + 4);
        int having = FindTopLevelKeyword(statement, "HAVING", groupBy < 0 ? (from < 0 ? selectStart : from + 4) : groupBy + 8);
        int orderBy = FindTopLevelKeyword(statement, "ORDER BY", having >= 0 ? having + 6 : groupBy >= 0 ? groupBy + 8 : where >= 0 ? where + 5 : from >= 0 ? from + 4 : selectStart);

        int selectEnd = from >= 0 ? from : statement.Length;
        string selectPart = statement[selectStart..selectEnd].Trim();
        short selectFlags = 0;
        string? topValue = null;
        if (StartsWithKeyword(selectPart, "DISTINCTROW"))
        {
            selectFlags |= DistinctRowFlag;
            selectPart = selectPart[11..].TrimStart();
        }
        else if (StartsWithKeyword(selectPart, "DISTINCT"))
        {
            selectFlags |= DistinctFlag;
            selectPart = selectPart[8..].TrimStart();
        }
        if (StartsWithKeyword(selectPart, "TOP"))
        {
            int topEnd = KeywordEnd(selectPart, 0, "TOP");
            int numberEnd = FindTokenEnd(selectPart, topEnd);
            string top = selectPart[topEnd..numberEnd].Trim();
            if (!int.TryParse(top, out _))
            {
                throw new NotSupportedException("CREATE VIEW supports only numeric TOP values.");
            }
            topValue = top;
            selectFlags |= TopFlag;
            selectPart = selectPart[numberEnd..].TrimStart();
            if (StartsWithKeyword(selectPart, "PERCENT"))
            {
                throw new NotSupportedException("TOP PERCENT is not supported by the pinned compatibility baseline.");
            }
        }
        if (selectPart.Length == 0)
        {
            throw new InvalidOperationException("SELECT must contain at least one projection.");
        }
        if (selectPart == "*")
        {
            selectFlags |= SelectStarFlag;
        }
        else
        {
            foreach (string projection in SplitTopLevel(selectPart, ','))
            {
                if (projection.Length == 0)
                {
                    throw new InvalidOperationException("SELECT contains an empty projection.");
                }
                (string expression, string? alias) = SplitAlias(projection);
                rows.Add(new QueryRowSpec(ColumnAttribute, expression, Name1: alias));
            }
        }
        if (selectFlags != 0)
        {
            rows.Add(new QueryRowSpec(FlagAttribute, Name1: topValue, Flag: selectFlags));
        }

        if (from >= 0)
        {
            int fromEnd = FirstClauseAfter(from + 4, where, groupBy, having, orderBy, statement.Length);
            string fromPart = statement[(from + 4)..fromEnd].Trim();
            if (fromPart.Length == 0)
            {
                throw new InvalidOperationException("FROM requires at least one table source.");
            }
            rows.AddRange(ParseFromSources(fromPart));
        }

        if (where >= 0)
        {
            int end = FirstClauseAfter(where + 5, groupBy, having, orderBy, statement.Length);
            rows.Add(new QueryRowSpec(WhereAttribute, Expression: statement[(where + 5)..end].Trim()));
        }
        if (groupBy >= 0)
        {
            int end = FirstClauseAfter(groupBy + 8, having, orderBy, statement.Length);
            foreach (string expression in SplitTopLevel(statement[(groupBy + 8)..end], ','))
            {
                rows.Add(new QueryRowSpec(GroupByAttribute, Expression: expression.Trim()));
            }
        }
        if (having >= 0)
        {
            int end = FirstClauseAfter(having + 6, orderBy, statement.Length);
            rows.Add(new QueryRowSpec(HavingAttribute, Expression: statement[(having + 6)..end].Trim()));
        }
        if (orderBy >= 0)
        {
            int end = FirstClauseAfter(orderBy + 8, statement.Length);
            foreach (string expression in SplitTopLevel(statement[(orderBy + 8)..end], ','))
            {
                string item = expression.Trim();
                string? direction = null;
                if (EndsWithKeyword(item, "DESC"))
                {
                    direction = "D";
                    item = item[..^4].TrimEnd();
                }
                else if (EndsWithKeyword(item, "ASC"))
                {
                    direction = "A";
                    item = item[..^3].TrimEnd();
                }
                rows.Add(new QueryRowSpec(OrderByAttribute, Expression: item, Name1: direction));
            }
        }
        rows.Add(new QueryRowSpec(EndAttribute));
        return new QuerySpec(AssignOrders(rows));
    }

    private static IReadOnlyList<QueryRowSpec> AssignOrders(IReadOnlyList<QueryRowSpec> rows)
    {
        var counters = new Dictionary<byte, int>();
        var ordered = new List<QueryRowSpec>(rows.Count);
        foreach (QueryRowSpec row in rows)
        {
            if (row.Order != null)
            {
                ordered.Add(row);
                continue;
            }
            counters.TryGetValue(row.Attribute, out int current);
            current++;
            counters[row.Attribute] = current;
            ordered.Add(row with { Order = new byte[]
            {
                0, 0, (byte)((current >> 8) & 0xFF), (byte)(current & 0xFF),
            }});
        }
        return ordered;
    }

    private static IEnumerable<QueryRowSpec> ParseParameters(string text)
    {
        foreach (string declaration in SplitTopLevel(text, ','))
        {
            string[] parts = declaration.Trim().Split((char[]?)null!, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                throw new InvalidOperationException($"Invalid PARAMETERS declaration '{declaration}'.");
            }
            string typeToken = parts[1];
            int open = typeToken.IndexOf('(');
            string type = (open < 0 ? typeToken : typeToken[..open]).ToUpperInvariant();
            short flag = type switch
            {
                "BIT" or "YESNO" or "BOOLEAN" => (short)DataType.Boolean,
                "BYTE" => (short)DataType.Byte,
                "SHORT" or "INTEGER" => (short)DataType.Int,
                "LONG" or "COUNTER" => (short)DataType.Long,
                "CURRENCY" or "MONEY" => (short)DataType.Money,
                "SINGLE" => (short)DataType.Float,
                "DOUBLE" => (short)DataType.Double,
                "DATETIME" or "DATE" => (short)DataType.ShortDateTime,
                "TEXT" or "VARCHAR" => (short)DataType.Text,
                "MEMO" or "LONGCHAR" => (short)DataType.Memo,
                "GUID" => (short)DataType.Guid,
                "DECIMAL" or "NUMERIC" => (short)DataType.Numeric,
                "BIGINT" => (short)DataType.BigInt,
                _ => throw new NotSupportedException($"Unsupported QueryDef parameter type '{parts[1]}'."),
            };
            int extra = 0;
            string? sizeToken = open >= 0
                ? typeToken[open..]
                : parts.Length > 2 ? parts[2] : null;
            if (sizeToken is { Length: > 2 } && sizeToken.StartsWith('(') && sizeToken.EndsWith(')'))
            {
                _ = int.TryParse(sizeToken[1..^1], out extra);
            }
            yield return new QueryRowSpec(ParameterAttribute, Name1: UnquoteIdentifier(parts[0]),
                Flag: flag, Extra: extra);
        }
    }

    private static IReadOnlyList<QueryRowSpec> ParseFromSources(string fromPart)
    {
        var rows = new List<QueryRowSpec>();
        JoinInfo? firstJoin = FindJoin(fromPart, 0);
        if (firstJoin == null)
        {
            foreach (string source in SplitTopLevel(fromPart, ','))
            {
                rows.Add(ParseTableSource(source, out _));
            }
            return rows;
        }

        if (SplitTopLevel(fromPart, ',').Count != 1)
        {
            throw new NotSupportedException("CREATE VIEW does not mix comma and explicit JOIN sources.");
        }

        string baseText = fromPart[..firstJoin.Value.TypeStart].Trim();
        if (baseText.Length == 0)
        {
            throw new InvalidOperationException("JOIN requires a left table source.");
        }
        rows.Add(ParseTableSource(baseText, out string currentKey));

        JoinInfo current = firstJoin.Value;
        while (true)
        {
            int on = FindTopLevelKeyword(fromPart, "ON", current.JoinEnd);
            if (on < 0)
            {
                throw new NotSupportedException("CREATE VIEW JOIN sources require an ON expression.");
            }
            JoinInfo? next = FindJoin(fromPart, on + 2);
            int onEnd = next?.TypeStart ?? fromPart.Length;
            string rightText = fromPart[current.JoinEnd..on].Trim();
            if (rightText.Length == 0)
            {
                throw new InvalidOperationException("JOIN requires a right table source.");
            }
            rows.Add(ParseTableSource(rightText, out string rightKey));

            string onExpression = fromPart[(on + 2)..onEnd].Trim();
            if (onExpression.Length == 0)
            {
                throw new InvalidOperationException("JOIN requires a non-empty ON expression.");
            }
            rows.Add(new QueryRowSpec(JoinAttribute, Expression: onExpression,
                Flag: current.Flag, Name1: currentKey, Name2: rightKey));
            currentKey = rightKey;

            if (next == null)
            {
                break;
            }
            current = next.Value;
        }
        return rows;
    }

    private static QueryRowSpec ParseTableSource(string source, out string key)
    {
        (string table, string? alias) = SplitAlias(source);
        if (table.Length == 0)
        {
            throw new InvalidOperationException("FROM contains an empty table source.");
        }
        string name = UnquoteIdentifier(table);
        key = alias ?? name;
        return new QueryRowSpec(TableAttribute, Name1: name, Name2: alias);
    }

    private static JoinInfo? FindJoin(string text, int start)
    {
        int join = FindTopLevelKeyword(text, "JOIN", start);
        if (join < 0)
        {
            return null;
        }
        string prefix = text[start..join];
        Match type = Regex.Match(prefix, @"(?is)\b(INNER|LEFT|RIGHT|FULL)(?:\s+OUTER)?\s*$");
        short flag;
        int typeStart;
        if (!type.Success)
        {
            flag = 1;
            typeStart = join;
        }
        else
        {
            flag = type.Groups[1].Value.ToUpperInvariant() switch
            {
                "INNER" => (short)1,
                "LEFT" => (short)2,
                "RIGHT" => (short)3,
                "FULL" => throw new NotSupportedException("FULL JOIN QueryDefs are not writable yet."),
                _ => (short)1,
            };
            typeStart = start + type.Index;
        }
        return new JoinInfo(join, join + 4, typeStart, flag);
    }

    private static (string Expression, string? Alias) SplitAlias(string text)
    {
        string value = text.Trim();
        int asPosition = FindTopLevelKeyword(value, "AS", 0);
        if (asPosition >= 0)
        {
            string expression = value[..asPosition].Trim();
            string alias = value[(asPosition + 2)..].Trim();
            return (expression, alias.Length == 0 ? null : UnquoteIdentifier(alias));
        }
        int space = FindLastTopLevelSpace(value);
        if (space > 0)
        {
            string candidate = value[(space + 1)..].Trim();
            if (IsIdentifier(candidate))
            {
                return (value[..space].Trim(), UnquoteIdentifier(candidate));
            }
        }
        return (value, null);
    }

    private static int FirstClauseAfter(int start, params int[] positions)
        => positions.Where(position => position >= start).DefaultIfEmpty(int.MaxValue).Min();

    private static bool StartsWithKeyword(string text, string keyword)
        => text.TrimStart().StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
            && (text.TrimStart().Length == keyword.Length
                || !IsIdentifierChar(text.TrimStart()[keyword.Length]));

    private static bool EndsWithKeyword(string text, string keyword)
        => text.EndsWith(keyword, StringComparison.OrdinalIgnoreCase)
            && (text.Length == keyword.Length
                || !IsIdentifierChar(text[text.Length - keyword.Length - 1]));

    private static int KeywordEnd(string text, int start, string keyword)
        => start + keyword.Length;

    private static int FindTokenEnd(string text, int start)
    {
        int i = start;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
        return i;
    }

    private static int FindTopLevelChar(string text, char target)
    {
        ScanState state = default;
        for (int i = 0; i < text.Length; i++)
        {
            if (!state.Advance(text, ref i)) continue;
            if (state.Depth == 0 && text[i] == target) return i;
        }
        return -1;
    }

    private static int FindTopLevelKeyword(string text, string keyword, int start)
    {
        ScanState state = default;
        for (int i = 0; i < text.Length; i++)
        {
            if (!state.Advance(text, ref i) || i < start || state.Depth != 0) continue;
            if (!text.AsSpan(i).StartsWith(keyword, StringComparison.OrdinalIgnoreCase)) continue;
            bool left = i == 0 || !IsIdentifierChar(text[i - 1]);
            int end = i + keyword.Length;
            bool right = end >= text.Length || !IsIdentifierChar(text[end]);
            if (left && right) return i;
        }
        return -1;
    }

    private static bool ContainsTopLevelKeyword(string text, string keyword)
        => FindTopLevelKeyword(text, keyword, 0) >= 0;

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var result = new List<string>();
        int start = 0;
        ScanState state = default;
        for (int i = 0; i < text.Length; i++)
        {
            if (!state.Advance(text, ref i)) continue;
            if (state.Depth == 0 && text[i] == separator)
            {
                result.Add(text[start..i].Trim());
                start = i + 1;
            }
        }
        result.Add(text[start..].Trim());
        return result;
    }

    private static int FindLastTopLevelSpace(string text)
    {
        int last = -1;
        ScanState state = default;
        for (int i = 0; i < text.Length; i++)
        {
            if (!state.Advance(text, ref i)) continue;
            if (state.Depth == 0 && char.IsWhiteSpace(text[i])) last = i;
        }
        return last;
    }

    private static bool IsIdentifier(string value)
        => value.Length > 0 && (value[0] == '[' || value.All(IsIdentifierChar));

    private static bool IsIdentifierChar(char value)
        => char.IsLetterOrDigit(value) || value is '_' or '$' or '@' or ':' or '?' or ']';

    private static string UnquoteIdentifier(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length < 2)
        {
            return trimmed;
        }
        if (trimmed[0] == '[' && trimmed[^1] == ']')
        {
            return trimmed[1..^1].Replace("]]", "]", StringComparison.Ordinal);
        }
        if (trimmed[0] == '"' && trimmed[^1] == '"')
        {
            return trimmed[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        }
        if (trimmed[0] == '`' && trimmed[^1] == '`')
        {
            return trimmed[1..^1].Replace("``", "`", StringComparison.Ordinal);
        }
        return trimmed;
    }

    private static void ValidateName(string name, string kind)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException($"A {kind} name is required.", nameof(name));
        }
        if (name.Length > 64 || name.Contains('\0'))
        {
            throw new ArgumentException($"The {kind} name is invalid.", nameof(name));
        }
    }

    private static int TryGetInt(Row row, string name)
        => row.TryGetValue(name, out object? value) ? value switch
        {
            int integer => integer,
            short small => small,
            byte tiny => tiny,
            _ => 0,
        } : 0;

    private sealed record QuerySpec(IReadOnlyList<QueryRowSpec> Rows);

    private sealed record QueryRowSpec(byte Attribute, string? Expression = null,
        short? Flag = null, int? Extra = null, string? Name1 = null, string? Name2 = null,
        byte[]? Order = null);

    private readonly record struct JoinInfo(int JoinIndex, int JoinEnd, int TypeStart, short Flag);

    private struct ScanState
    {
        internal int Depth;
        private char _quote;
        private bool _bracket;

        internal bool Advance(string text, ref int index)
        {
            char current = text[index];
            if (_quote != '\0')
            {
                if (current == _quote)
                {
                    if (index + 1 < text.Length && text[index + 1] == _quote)
                    {
                        index++;
                    }
                    else
                    {
                        _quote = '\0';
                    }
                }
                return false;
            }
            if (_bracket)
            {
                if (current == ']') _bracket = false;
                return false;
            }
            if (current is '\'' or '"')
            {
                _quote = current;
                return false;
            }
            if (current == '[')
            {
                _bracket = true;
                return false;
            }
            if (current == '(')
            {
                Depth++;
                return false;
            }
            if (current == ')')
            {
                Depth = Math.Max(0, Depth - 1);
                return false;
            }
            return true;
        }
    }
}
