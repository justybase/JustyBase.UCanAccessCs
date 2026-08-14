using System.Globalization;
using System.Text;
using UCanAccess.File;
using static UCanAccess.AccessTokenizer;

namespace UCanAccess;

/// <summary>
/// Executes Access SQL data-modification statements (INSERT, UPDATE, DELETE) directly
/// against the MDB file through the <see cref="File.Table"/> write API, then refreshes
/// the SQLite <see cref="Mirror"/> so subsequent SELECTs see the changes.
///
/// Supported grammar (subset):
///   INSERT INTO table [(col1, col2, ...)] VALUES (v1, v2, ...) [,(v1, v2, ...)]
///   INSERT INTO table VALUES (v1, v2, ...)
///   UPDATE table [JOIN ...] SET col = expression [, ...] [WHERE condition]
///   DELETE FROM table [JOIN ...] [WHERE condition]
///
/// UPDATE and DELETE selection expressions are translated through the mirror,
/// so Access functions, joins, correlated subqueries and SQL three-valued NULL
/// logic are evaluated by the query engine before file rows are mutated.
/// </summary>
public static class AccessDml
{
    private static readonly string[] DateFormats =
    {
        "M/d/yyyy", "M/d/yyyy H:mm:ss", "M/d/yyyy H:mm", "M/d/yyyy h:mm:ss tt", "M/d/yyyy h:mm tt",
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm",
        "d/M/yyyy",
    };

    /// <summary>
    /// Executes the given DML statement against the database.
    /// </summary>
    /// <returns>the number of rows affected</returns>
    public static int Execute(File.Database db, Mirror mirror, string sql, IReadOnlyList<object?>? parameters,
        bool dryRun = false, Action<Table, object?[]>? onInsertedRow = null)
    {
        List<Token> tokens = Tokenize(NormalizeParameters(sql));
        while (tokens.Count > 0 && tokens[^1].Text == ";")
        {
            tokens.RemoveAt(tokens.Count - 1);
        }
        if (tokens.Count == 0)
        {
            throw new NotSupportedException("Empty statement.");
        }

        string kind = tokens[0].Text.ToUpperInvariant();
        int placeholderCount = tokens.Count(token => token.Text == "?");
        int suppliedCount = parameters?.Count ?? 0;
        if (placeholderCount != suppliedCount)
        {
            throw new InvalidOperationException(
                $"The statement expects {placeholderCount} positional parameter value(s), but {suppliedCount} were supplied.");
        }
        if (!dryRun)
        {
            mirror.ThrowIfActiveReaders();
        }
        int affected;
        switch (kind)
        {
            case "INSERT":
                affected = ExecuteInsert(db, mirror, tokens, parameters, dryRun, onInsertedRow);
                break;
            case "UPDATE":
                affected = HasTopLevelJoin(tokens)
                    ? ExecuteUpdateJoin(db, mirror, tokens, parameters, dryRun)
                    : ExecuteUpdate(db, mirror, tokens, parameters, dryRun);
                break;
            case "DELETE":
                affected = HasTopLevelJoin(tokens)
                    ? ExecuteDeleteJoin(db, mirror, tokens, parameters, dryRun)
                    : ExecuteDelete(db, mirror, tokens, parameters, dryRun);
                break;
            default:
                throw new NotSupportedException($"Statement type '{kind}' is not supported for writes.");
        }

        // refresh the mirrored copy so subsequent SELECTs see the changes
        if (!dryRun)
        {
            string? targetTable = GetMutationTarget(tokens, kind);
            if (targetTable == null)
            {
                mirror.RefreshAll();
            }
            else
            {
                mirror.RefreshTables(GetRefreshTables(db, targetTable, kind));
            }
        }

        return affected;
    }

    private static IReadOnlyList<string> GetRefreshTables(File.Database db, string targetTable, string kind)
    {
        var result = new List<string> { targetTable };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { targetTable };
        var pending = new Queue<string>();
        pending.Enqueue(targetTable);

        while (pending.Count > 0)
        {
            string current = pending.Dequeue();
            foreach (Relationship relationship in db.GetRelationships(current))
            {
                if (!relationship.FromTable.Name.Equals(current, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool cascades = kind.Equals("DELETE", StringComparison.OrdinalIgnoreCase)
                    ? relationship.CascadeDeletes || relationship.CascadeNullOnDelete
                    : kind.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
                        ? relationship.CascadeUpdates
                        : false;
                if (!cascades || !seen.Add(relationship.ToTable.Name))
                {
                    continue;
                }

                result.Add(relationship.ToTable.Name);
                pending.Enqueue(relationship.ToTable.Name);
            }
        }

        return result;
    }

    private static string? GetMutationTarget(List<Token> tokens, string kind)
    {
        int index;
        if (kind == "DELETE")
        {
            int fromIndex = FindTopLevelWord(tokens, "from", 1);
            index = fromIndex < 0 ? -1 : fromIndex + 1;
        }
        else
        {
            index = kind switch
            {
                "INSERT" => 2,
                "UPDATE" => 1,
                _ => -1,
            };
        }
        if (index < 0 || index >= tokens.Count)
        {
            return null;
        }
        Token token = tokens[index];
        return token.Kind is Kind.Word or Kind.Ident ? token.Text : null;
    }

    private static int ExecuteInsert(File.Database db, Mirror mirror, List<Token> tokens,
        IReadOnlyList<object?>? parameters, bool dryRun, Action<Table, object?[]>? onInsertedRow)
    {
        int pos = 0;
        int paramIndex = 0;
        ExpectWord(tokens, ref pos, "insert");
        ExpectWord(tokens, ref pos, "into");
        Table table = ReadTable(db, tokens, ref pos);

        // optional column list
        List<string>? columns = null;
        if (Peek(tokens, pos) is { Text: "(" })
        {
            columns = new List<string>();
            pos++; // (
            while (true)
            {
                columns.Add(ReadName(tokens, ref pos));
                if (Peek(tokens, pos) is { Text: "," })
                {
                    pos++;
                    continue;
                }
                break;
            }
            ExpectSymbol(tokens, ref pos, ")");
        }

        // INSERT ... SELECT
        if (Peek(tokens, pos) is { } first && first.Text.Equals("select", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteInsertSelect(mirror, table, columns, tokens, pos, parameters, dryRun, onInsertedRow);
        }

        ExpectWord(tokens, ref pos, "values");

        int affected = 0;
        while (true)
        {
            ExpectSymbol(tokens, ref pos, "(");
            var valueExprs = new List<object?>();
            while (true)
            {
                object? value = ParseValue(tokens, ref pos, ref paramIndex, parameters);
                valueExprs.Add(value);
                if (Peek(tokens, pos) is { Text: "," })
                {
                    pos++;
                    continue;
                }
                break;
            }
            ExpectSymbol(tokens, ref pos, ")");

            if (!dryRun)
            {
                object?[] committed = table.AddRow(BuildInsertRow(table, columns, valueExprs));
                onInsertedRow?.Invoke(table, committed);
            }
            affected++;

            if (Peek(tokens, pos) is { Text: "," })
            {
                pos++;
                continue;
            }
            break;
        }

        EnsureEnd(tokens, pos);

        return affected;
    }

    private static int ExecuteInsertSelect(Mirror mirror, Table table, List<string>? columns, List<Token> tokens,
        int selectStart, IReadOnlyList<object?>? parameters, bool dryRun, Action<Table, object?[]>? onInsertedRow)
    {
        string selectSql = RebuildSql(tokens, selectStart);
        string translated = AccessSqlTranslator.Translate(selectSql, out int pcount, out _,
            mirror.IsMoneyColumn, mirror.IsExactDecimalColumn, mirror.IsDateColumn);
        IReadOnlyList<object?>? selectParams = pcount > 0 ? parameters : null;
        using var reader = mirror.ExecuteReader(translated, selectParams);
        int affected = 0;
        while (reader.Read())
        {
            var values = new List<object?>(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                values.Add(reader.IsDBNull(i) ? null : reader.GetValue(i));
            }
            if (!dryRun)
            {
                object?[] committed = table.AddRow(BuildInsertRow(table, columns, values));
                onInsertedRow?.Invoke(table, committed);
            }
            affected++;
        }
        return affected;
    }

    private static int ExecuteUpdateJoin(File.Database db, Mirror mirror, List<Token> tokens,
        IReadOnlyList<object?>? parameters, bool dryRun)
    {
        int setIndex = FindTopLevelWord(tokens, "set", 1);
        if (setIndex < 0)
        {
            throw new NotSupportedException("UPDATE JOIN requires a SET clause.");
        }
        int whereIndex = FindTopLevelWord(tokens, "where", setIndex + 1);
        int end = whereIndex < 0 ? tokens.Count : whereIndex;
        string targetName = tokens[1].Text;
        Table table = ResolveTable(db, targetName);
        string targetReference = GetTargetReference(tokens, 1, setIndex);
        List<(Column Column, string Expression)> assignments = ParseAssignments(tokens, setIndex + 1, end, table);
        string fromClause = RebuildSql(tokens, 1, setIndex);
        string selectSql = $"SELECT DISTINCT {targetReference}.rowid, "
            + string.Join(", ", assignments.Select(item => item.Expression))
            + $" FROM {fromClause}"
            + (whereIndex < 0 ? string.Empty : " " + RebuildSql(tokens, whereIndex, tokens.Count));
        string translated = AccessSqlTranslator.Translate(selectSql, out int parameterCount, out _,
            mirror.IsMoneyColumn, mirror.IsExactDecimalColumn, mirror.IsDateColumn);
        if (parameterCount != (parameters?.Count ?? 0))
        {
            throw new InvalidOperationException(
                $"The UPDATE JOIN expects {parameterCount} positional parameter value(s), but {parameters?.Count ?? 0} were supplied.");
        }

        var updates = new Dictionary<(int Page, int Row), (Table.RowLocation Location, object?[] Values)>();
        using (MirrorReader reader = mirror.ExecuteReader(translated, parameters))
        {
            while (reader.Read())
            {
                long sqliteRowId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                if (!mirror.TryGetRowLocation(targetName, sqliteRowId, out Table.RowLocation location))
                {
                    throw new InvalidOperationException(
                        $"The mirror did not contain a file locator for target table '{targetName}'.");
                }
                object?[] values = new object?[assignments.Count];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = reader.IsDBNull(i + 1) ? null : reader.GetValue(i + 1);
                }
                var key = (location.PageNumber, location.RowNumber);
                if (updates.TryGetValue(key, out var existing)
                    && !existing.Values.SequenceEqual(values, AccessObjectComparer.Instance))
                {
                    throw new NotSupportedException(
                        "UPDATE JOIN matched one target row with different source values.");
                }
                updates[key] = (location, values);
            }
        }

        foreach ((Table.RowLocation location, object?[] values) in updates.Values
                     .OrderByDescending(item => item.Location.PageNumber)
                     .ThenByDescending(item => item.Location.RowNumber))
        {
            if (dryRun)
            {
                continue;
            }
            object?[] row = location.Row.ToArray();
            for (int i = 0; i < assignments.Count; i++)
            {
                row[assignments[i].Column.ColumnIndex] = CoerceValue(assignments[i].Column, values[i]);
            }
            table.UpdateRow(location.PageNumber, location.RowNumber, row);
        }
        return updates.Count;
    }

    private static int ExecuteDeleteJoin(File.Database db, Mirror mirror, List<Token> tokens,
        IReadOnlyList<object?>? parameters, bool dryRun)
    {
        int fromIndex = FindTopLevelWord(tokens, "from", 1);
        if (fromIndex < 0 || fromIndex + 1 >= tokens.Count)
        {
            throw new NotSupportedException("DELETE JOIN requires a FROM clause.");
        }
        int whereIndex = FindTopLevelWord(tokens, "where", fromIndex + 1);
        int end = whereIndex < 0 ? tokens.Count : whereIndex;
        string targetName = tokens[fromIndex + 1].Text;
        Table table = ResolveTable(db, targetName);
        string targetReference = GetTargetReference(tokens, fromIndex + 1, end);
        string fromClause = RebuildSql(tokens, fromIndex + 1, end);
        string selectSql = $"SELECT DISTINCT {targetReference}.rowid FROM {fromClause}"
            + (whereIndex < 0 ? string.Empty : " " + RebuildSql(tokens, whereIndex, tokens.Count));
        string translated = AccessSqlTranslator.Translate(selectSql, out int parameterCount, out _,
            mirror.IsMoneyColumn, mirror.IsExactDecimalColumn, mirror.IsDateColumn);
        if (parameterCount != (parameters?.Count ?? 0))
        {
            throw new InvalidOperationException(
                $"The DELETE JOIN expects {parameterCount} positional parameter value(s), but {parameters?.Count ?? 0} were supplied.");
        }

        var locations = new Dictionary<(int Page, int Row), Table.RowLocation>();
        using (MirrorReader reader = mirror.ExecuteReader(translated, parameters))
        {
            while (reader.Read())
            {
                long sqliteRowId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                if (!mirror.TryGetRowLocation(targetName, sqliteRowId, out Table.RowLocation location))
                {
                    throw new InvalidOperationException(
                        $"The mirror did not contain a file locator for target table '{targetName}'.");
                }
                locations[(location.PageNumber, location.RowNumber)] = location;
            }
        }

        foreach (Table.RowLocation location in locations.Values
                     .OrderByDescending(item => item.PageNumber)
                     .ThenByDescending(item => item.RowNumber))
        {
            if (!dryRun)
            {
                table.DeleteRow(location.PageNumber, location.RowNumber);
            }
        }
        return locations.Count;
    }

    private static bool HasTopLevelJoin(List<Token> tokens)
        => FindTopLevelWord(tokens, "join", 1) >= 0;

    private static int FindTopLevelWord(List<Token> tokens, string word, int start)
    {
        int depth = 0;
        for (int i = start; i < tokens.Count; i++)
        {
            if (tokens[i].Text == "(")
            {
                depth++;
            }
            else if (tokens[i].Text == ")")
            {
                depth--;
            }
            else if (depth == 0 && tokens[i].Text.Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private static string GetTargetReference(List<Token> tokens, int tableIndex, int end = -1)
    {
        end = end < 0 ? tokens.Count : end;
        int next = tableIndex + 1;
        if (next < end && tokens[next].Text.Equals("as", StringComparison.OrdinalIgnoreCase))
        {
            return RenderToken(tokens[next + 1]);
        }
        if (next < end && tokens[next].Kind is Kind.Word or Kind.Ident
            && !tokens[next].Text.Equals("inner", StringComparison.OrdinalIgnoreCase)
            && !tokens[next].Text.Equals("left", StringComparison.OrdinalIgnoreCase)
            && !tokens[next].Text.Equals("right", StringComparison.OrdinalIgnoreCase)
            && !tokens[next].Text.Equals("full", StringComparison.OrdinalIgnoreCase)
            && !tokens[next].Text.Equals("join", StringComparison.OrdinalIgnoreCase))
        {
            return RenderToken(tokens[next]);
        }
        return RenderToken(tokens[tableIndex]);
    }

    private static List<(Column Column, string Expression)> ParseAssignments(
        List<Token> tokens, int start, int end, Table table)
    {
        var result = new List<(Column Column, string Expression)>();
        foreach ((int Start, int End) range in SplitTopLevel(tokens, start, end, ","))
        {
            int equals = FindTopLevelSymbol(tokens, "=", range.Start, range.End);
            if (equals <= range.Start || equals + 1 >= range.End)
            {
                throw new NotSupportedException("Each UPDATE JOIN assignment must contain an expression.");
            }
            string columnName = tokens[equals - 1].Text;
            Column? column = table.Columns.FirstOrDefault(item =>
                item.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
            if (column == null)
            {
                throw new InvalidOperationException($"Unknown target column '{columnName}'.");
            }
            result.Add((column, RebuildSql(tokens, equals + 1, range.End)));
        }
        return result;
    }

    private static int FindTopLevelSymbol(List<Token> tokens, string symbol, int start, int end)
    {
        int depth = 0;
        for (int i = start; i < end; i++)
        {
            if (tokens[i].Text == "(") depth++;
            else if (tokens[i].Text == ")") depth--;
            else if (depth == 0 && tokens[i].Text == symbol) return i;
        }
        return -1;
    }

    private static IEnumerable<(int Start, int End)> SplitTopLevel(
        List<Token> tokens, int start, int end, string separator)
    {
        int depth = 0;
        int partStart = start;
        for (int i = start; i < end; i++)
        {
            if (tokens[i].Text == "(") depth++;
            else if (tokens[i].Text == ")") depth--;
            else if (depth == 0 && tokens[i].Text == separator)
            {
                yield return (partStart, i);
                partStart = i + 1;
            }
        }
        if (partStart < end)
        {
            yield return (partStart, end);
        }
    }

    private static Table ResolveTable(File.Database db, string name)
    {
        try
        {
            return db.GetTable(name) ?? db.GetLinkedTable(name)
                ?? throw new InvalidOperationException($"Table '{name}' does not exist.");
        }
        catch (DatabaseException ex) when (ex.Message.Contains("linked table", StringComparison.OrdinalIgnoreCase))
        {
            return db.GetLinkedTable(name)
                ?? throw new InvalidOperationException($"Table '{name}' does not exist.");
        }
    }

    private sealed class AccessObjectComparer : IEqualityComparer<object?>
    {
        public static readonly AccessObjectComparer Instance = new();

        public new bool Equals(object? x, object? y)
            => x == null || x is DBNull ? y == null || y is DBNull : y != null && y is not DBNull
                && string.Equals(Convert.ToString(x, CultureInfo.InvariantCulture),
                    Convert.ToString(y, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(object? obj)
            => Convert.ToString(obj, CultureInfo.InvariantCulture)?.ToUpperInvariant().GetHashCode() ?? 0;
    }

    private static int ExecuteUpdate(File.Database db, Mirror mirror, List<Token> tokens, IReadOnlyList<object?>? parameters, bool dryRun)
        => ExecuteUpdateFromMirror(db, mirror, tokens, parameters, dryRun);

    private static int ExecuteDelete(File.Database db, Mirror mirror, List<Token> tokens, IReadOnlyList<object?>? parameters, bool dryRun)
        => ExecuteDeleteFromMirror(db, mirror, tokens, parameters, dryRun);

    private static int ExecuteUpdateFromMirror(File.Database db, Mirror mirror, List<Token> tokens,
        IReadOnlyList<object?>? parameters, bool dryRun)
    {
        int setIndex = FindTopLevelWord(tokens, "set", 1);
        if (setIndex < 0)
        {
            throw new NotSupportedException("UPDATE requires a SET clause.");
        }
        int whereIndex = FindTopLevelWord(tokens, "where", setIndex + 1);
        int end = whereIndex < 0 ? tokens.Count : whereIndex;
        string targetName = tokens[1].Text;
        Table table = ResolveTable(db, targetName);
        List<(Column Column, string Expression)> assignments = ParseAssignments(tokens, setIndex + 1, end, table);
        string targetReference = GetTargetReference(tokens, 1, setIndex);
        string fromClause = RebuildSql(tokens, 1, setIndex);
        string selectSql = $"SELECT {targetReference}.rowid, "
            + string.Join(", ", assignments.Select(item => item.Expression))
            + $" FROM {fromClause}"
            + (whereIndex < 0 ? string.Empty : " " + RebuildSql(tokens, whereIndex, tokens.Count));
        string translated = AccessSqlTranslator.Translate(selectSql, out int parameterCount, out _,
            mirror.IsMoneyColumn, mirror.IsExactDecimalColumn, mirror.IsDateColumn);
        if (parameterCount != (parameters?.Count ?? 0))
        {
            throw new InvalidOperationException(
                $"The UPDATE expects {parameterCount} positional parameter value(s), but {parameters?.Count ?? 0} were supplied.");
        }

        var updates = new List<(Table.RowLocation Location, object?[] Values)>();
        using (MirrorReader reader = mirror.ExecuteReader(translated, parameters))
        {
            while (reader.Read())
            {
                long sqliteRowId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                if (!mirror.TryGetRowLocation(targetName, sqliteRowId, out Table.RowLocation location))
                {
                    throw new InvalidOperationException(
                        $"The mirror did not contain a file locator for target table '{targetName}'.");
                }
                object?[] values = new object?[assignments.Count];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = reader.IsDBNull(i + 1) ? null : reader.GetValue(i + 1);
                }
                updates.Add((location, values));
            }
        }

        foreach ((Table.RowLocation location, object?[] values) in updates
                     .OrderByDescending(item => item.Location.PageNumber)
                     .ThenByDescending(item => item.Location.RowNumber))
        {
            if (dryRun)
            {
                continue;
            }
            object?[] row = location.Row.ToArray();
            for (int i = 0; i < assignments.Count; i++)
            {
                row[assignments[i].Column.ColumnIndex] = CoerceValue(assignments[i].Column, values[i]);
            }
            table.UpdateRow(location.PageNumber, location.RowNumber, row);
        }
        return updates.Count;
    }

    private static int ExecuteDeleteFromMirror(File.Database db, Mirror mirror, List<Token> tokens,
        IReadOnlyList<object?>? parameters, bool dryRun)
    {
        if (tokens.Count < 3 || !tokens[1].Text.Equals("from", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("DELETE requires FROM followed by a table name.");
        }
        string targetName = tokens[2].Text;
        Table table = ResolveTable(db, targetName);
        int whereIndex = FindTopLevelWord(tokens, "where", 3);
        string targetReference = GetTargetReference(tokens, 2, whereIndex < 0 ? tokens.Count : whereIndex);
        string fromClause = RebuildSql(tokens, 2, whereIndex < 0 ? tokens.Count : whereIndex);
        string selectSql = $"SELECT {targetReference}.rowid FROM {fromClause}"
            + (whereIndex < 0 ? string.Empty : " " + RebuildSql(tokens, whereIndex, tokens.Count));
        string translated = AccessSqlTranslator.Translate(selectSql, out int parameterCount, out _,
            mirror.IsMoneyColumn, mirror.IsExactDecimalColumn, mirror.IsDateColumn);
        if (parameterCount != (parameters?.Count ?? 0))
        {
            throw new InvalidOperationException(
                $"The DELETE expects {parameterCount} positional parameter value(s), but {parameters?.Count ?? 0} were supplied.");
        }

        var locations = new List<Table.RowLocation>();
        using (MirrorReader reader = mirror.ExecuteReader(translated, parameters))
        {
            while (reader.Read())
            {
                long sqliteRowId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                if (!mirror.TryGetRowLocation(targetName, sqliteRowId, out Table.RowLocation location))
                {
                    throw new InvalidOperationException(
                        $"The mirror did not contain a file locator for target table '{targetName}'.");
                }
                locations.Add(location);
            }
        }

        foreach (Table.RowLocation location in locations
                     .OrderByDescending(item => item.PageNumber)
                     .ThenByDescending(item => item.RowNumber))
        {
            if (!dryRun)
            {
                table.DeleteRow(location.PageNumber, location.RowNumber);
            }
        }
        return locations.Count;
    }

    // ------------------------------------------------------------------
    // row construction / coercion
    // ------------------------------------------------------------------

    private static object?[] BuildInsertRow(Table table, List<string>? columns, List<object?> values)
    {
        var row = Enumerable.Repeat<object?>(Table.MissingValue, table.Columns.Count).ToArray();
        if (columns == null)
        {
            if (values.Count > table.Columns.Count)
            {
                throw new InvalidOperationException("Too many values for table columns.");
            }
            for (int i = 0; i < values.Count; i++)
            {
                Column column = table.Columns[i];
                row[i] = CoerceValue(column, values[i]);
            }
            return row;
        }

        if (columns.Count != values.Count)
        {
            throw new InvalidOperationException("The number of columns does not match the number of values.");
        }
        for (int i = 0; i < columns.Count; i++)
        {
            Column? column = FindColumn(table, columns[i])
                ?? throw new InvalidOperationException($"Unknown column '{columns[i]}'.");
            row[column.ColumnIndex] = CoerceValue(column, values[i]);
        }
        return row;
    }

    private static Column? FindColumn(Table table, string name)
    {
        foreach (Column column in table.Columns)
        {
            if (column.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return column;
            }
        }
        return null;
    }

    /// <summary>
    /// Converts a SQL value to the CLR value expected by the given column.
    /// </summary>
    private static object? CoerceValue(Column column, object? value)
        => AccessValueCodec.CoerceForColumn(column, value);

    private static void EnsureEnd(List<Token> tokens, int pos)
    {
        if (pos < tokens.Count && tokens[pos].Text != ";")
        {
            throw new InvalidOperationException($"Unexpected token '{tokens[pos].Text}'.");
        }
    }

    // ------------------------------------------------------------------
    // SET expressions (evaluated per row)
    // ------------------------------------------------------------------

    private delegate object? RowExpr(Row row);

    private sealed class ExprParser
    {
        private readonly List<Token> _tokens;
        private readonly Table _table;
        private readonly IReadOnlyList<object?>? _parameters;
        private int _pos;
        private int _paramCount;

        internal ExprParser(List<Token> tokens, int pos, Table table, IReadOnlyList<object?>? parameters, int startParamCount)
        {
            _tokens = tokens;
            _pos = pos;
            _table = table;
            _parameters = parameters;
            _paramCount = startParamCount;
        }

        internal int Pos => _pos;

        internal int ParamCount => _paramCount;

        internal RowExpr Parse()
        {
            // The expression ends at the first token that is not part of the grammar
            // (WHERE / ',' / end of statement); the caller inspects Pos afterwards.
            return ParseAdd();
        }

        private RowExpr ParseAdd()
        {
            RowExpr left = ParseMul();
            while (_pos < _tokens.Count && _tokens[_pos].Text is "+" or "-" or "&")
            {
                string op = _tokens[_pos++].Text;
                RowExpr right = ParseMul();
                RowExpr l = left;
                RowExpr r = right;
                left = row => op switch
                {
                    "&" => StrConcat(l(row), r(row)),
                    "+" => AddValues(l(row), r(row)),
                    _ => NumArith(l(row), r(row), (a, b) => a - b),
                };
            }
            return left;
        }

        private RowExpr ParseMul()
        {
            RowExpr left = ParseUnary();
            while (_pos < _tokens.Count && _tokens[_pos].Text is "*" or "/")
            {
                string op = _tokens[_pos++].Text;
                RowExpr right = ParseUnary();
                RowExpr l = left;
                RowExpr r = right;
                left = row => NumArith(l(row), r(row), op == "*" ? (a, b) => a * b : (a, b) => a / b);
            }
            return left;
        }

        private RowExpr ParseUnary()
        {
            if (Match("-"))
            {
                RowExpr inner = ParseUnary();
                return row => Negate(inner(row));
            }
            if (Match("+"))
            {
                return ParseUnary();
            }
            if (Match("("))
            {
                RowExpr inner = ParseAdd();
                Expect(")");
                return inner;
            }
            return ParseOperand();
        }

        private RowExpr ParseOperand()
        {
            Token t = PeekToken();
            switch (t.Kind)
            {
                case Kind.Number:
                    _pos++;
                    decimal n = ParseNumber(t.Text);
                    return _ => n;
                case Kind.Str:
                    _pos++;
                    string s = t.Text;
                    return _ => s;
                case Kind.Date:
                    _pos++;
                    DateTime dt = ParseDate(t.Text);
                    return _ => dt;
                case Kind.Word:
                    if (t.Text.Equals("null", StringComparison.OrdinalIgnoreCase))
                    {
                        _pos++;
                        return _ => null;
                    }
                    if (t.Text.Equals("true", StringComparison.OrdinalIgnoreCase))
                    {
                        _pos++;
                        return _ => true;
                    }
                    if (t.Text.Equals("false", StringComparison.OrdinalIgnoreCase))
                    {
                        _pos++;
                        return _ => false;
                    }
                    if (t.Text == "?")
                    {
                        _pos++;
                        int idx = _paramCount++;
                        return row => ParameterAt(_parameters, idx);
                    }
                    _pos++;
                    string colName = t.Text;
                    return row => GetColumnValue(_table, row, colName);
                case Kind.Ident:
                    _pos++;
                    string ident = t.Text;
                    return row => GetColumnValue(_table, row, ident);
                case Kind.Symbol:
                    if (t.Text == "?")
                    {
                        _pos++;
                        int idx = _paramCount++;
                        return row => ParameterAt(_parameters, idx);
                    }
                    throw new InvalidOperationException($"Unexpected token '{t.Text}' in SET expression.");
                default:
                    throw new InvalidOperationException($"Unexpected token '{t.Text}' in SET expression.");
            }
        }

        private bool Match(string text)
        {
            if (_pos < _tokens.Count && _tokens[_pos].Text == text)
            {
                _pos++;
                return true;
            }
            return false;
        }

        private Token PeekToken() => _pos < _tokens.Count
            ? _tokens[_pos]
            : throw new InvalidOperationException("Unexpected end of statement in SET expression.");

        private void Expect(string text)
        {
            Token t = PeekToken();
            if (!t.Text.Equals(text, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Expected '{text}' but found '{t.Text}'.");
            }
            _pos++;
        }
    }

    private static object? GetColumnValue(Table table, Row row, string name)
    {
        foreach (Column column in table.Columns)
        {
            if (column.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return row[name];
            }
        }
        throw new InvalidOperationException($"Unknown column '{name}'.");
    }

    private static object? StrConcat(object? a, object? b)
        => (a is null or DBNull ? "" : a.ToString()) + (b is null or DBNull ? "" : b.ToString());

    private static object? Negate(object? value)
    {
        if (value is null or DBNull)
        {
            return null;
        }
        return -Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    private static object? AddValues(object? a, object? b)
    {
        if (a is null or DBNull || b is null or DBNull)
        {
            return null;
        }
        if (a is string sa && b is string sb)
        {
            return sa + sb;
        }
        if (a is DateTime da && b is DateTime db)
        {
            throw new InvalidOperationException("Cannot add two dates.");
        }
        if (IsNumeric(a) && IsNumeric(b))
        {
            return Convert.ToDecimal(a, CultureInfo.InvariantCulture) + Convert.ToDecimal(b, CultureInfo.InvariantCulture);
        }
        return a.ToString() + b.ToString();
    }

    private static object? NumArith(object? a, object? b, Func<decimal, decimal, decimal> op)
    {
        if (a is null or DBNull || b is null or DBNull)
        {
            return null;
        }
        decimal da = Convert.ToDecimal(a, CultureInfo.InvariantCulture);
        decimal db = Convert.ToDecimal(b, CultureInfo.InvariantCulture);
        return op(da, db);
    }

    private static bool IsNumeric(object? value)
        => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
            or System.Numerics.BigInteger;

    // ------------------------------------------------------------------
    // value parsing
    // ------------------------------------------------------------------

    private static object? ParameterAt(IReadOnlyList<object?>? parameters, int index)
    {
        if (parameters == null || index < 0 || index >= parameters.Count)
        {
            throw new InvalidOperationException($"No value was supplied for positional parameter {index + 1}.");
        }
        return parameters[index];
    }

    /// <summary>
    /// Replaces ADO.NET-style named parameters (<c>@name</c>, <c>$name</c>, <c>:name</c>) with the
    /// Access <c>?</c> placeholder, so the Access lexer can tokenize them. String
    /// literals are respected.
    /// </summary>
    private static string NormalizeParameters(string sql)
    {
        if (string.IsNullOrEmpty(sql) || (!sql.Contains('@') && !sql.Contains('$') && !sql.Contains(':')))
        {
            return sql;
        }
        var sb = new StringBuilder(sql.Length);
        bool inStr = false;
        bool inLineComment = false;
        bool inBlockComment = false;
        bool inBracketIdentifier = false;
        char quote = '\0';
        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            if (inLineComment)
            {
                sb.Append(c);
                if (c == '\n')
                {
                    inLineComment = false;
                }
                continue;
            }
            if (inBlockComment)
            {
                sb.Append(c);
                if (c == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
                {
                    sb.Append(sql[++i]);
                    inBlockComment = false;
                }
                continue;
            }
            if (inStr)
            {
                sb.Append(c);
                if (c == quote)
                {
                    if (i + 1 < sql.Length && sql[i + 1] == quote)
                    {
                        sb.Append(sql[i + 1]);
                        i++;
                    }
                    else
                    {
                        inStr = false;
                    }
                }
                continue;
            }
            if (inBracketIdentifier)
            {
                sb.Append(c);
                if (c == ']')
                {
                    if (i + 1 < sql.Length && sql[i + 1] == ']')
                    {
                        sb.Append(sql[++i]);
                    }
                    else
                    {
                        inBracketIdentifier = false;
                    }
                }
                continue;
            }
            if (c is '\'' or '"')
            {
                inStr = true;
                quote = c;
                sb.Append(c);
                continue;
            }
            if (c == '`')
            {
                int end = SkipQuotedIdentifier(sql, i);
                sb.Append(sql, i, end - i + 1);
                i = end;
                continue;
            }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                sb.Append(c);
                sb.Append(sql[++i]);
                inLineComment = true;
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                sb.Append(c);
                sb.Append(sql[++i]);
                inBlockComment = true;
                continue;
            }
            if (c == '[')
            {
                sb.Append(c);
                inBracketIdentifier = true;
                continue;
            }
            if ((c is '@' or '$' or ':') && i + 1 < sql.Length
                && (char.IsLetter(sql[i + 1]) || sql[i + 1] == '_'))
            {
                sb.Append('?');
                i++;
                while (i + 1 < sql.Length && (char.IsLetterOrDigit(sql[i + 1]) || sql[i + 1] == '_'))
                {
                    i++;
                }
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static int SkipQuotedIdentifier(string sql, int start)
    {
        for (int i = start + 1; i < sql.Length; i++)
        {
            if (sql[i] == '`')
            {
                if (i + 1 < sql.Length && sql[i + 1] == '`')
                {
                    i++;
                    continue;
                }
                return i;
            }
        }
        return sql.Length - 1;
    }

    private static object? ParseValue(List<Token> tokens, ref int pos, ref int paramIndex, IReadOnlyList<object?>? parameters)
    {
        Token t = Peek(tokens, pos) ?? throw new InvalidOperationException("Unexpected end of statement.");

        // unary sign
        if (t.Text is "-" or "+" && pos + 1 < tokens.Count)
        {
            string sign = t.Text;
            pos++;
            object? inner = ParseValue(tokens, ref pos, ref paramIndex, parameters);
            if (inner is null or DBNull)
            {
                return null;
            }
            decimal d = Convert.ToDecimal(inner, CultureInfo.InvariantCulture);
            return sign == "-" ? -d : d;
        }

        switch (t.Kind)
        {
            case Kind.Number:
                pos++;
                return ParseNumber(t.Text);
            case Kind.Str:
                pos++;
                return t.Text;
            case Kind.Date:
                pos++;
                return ParseDate(t.Text);
            case Kind.Word:
                if (t.Text.Equals("null", StringComparison.OrdinalIgnoreCase))
                {
                    pos++;
                    return null;
                }
                if (t.Text.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    pos++;
                    return true;
                }
                if (t.Text.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    pos++;
                    return false;
                }
                break;
            case Kind.Symbol:
                if (t.Text == "?")
                {
                    pos++;
                    int idx = paramIndex++;
                    return ParameterAt(parameters, idx);
                }
                break;
        }

        // The normalizer converts named parameters to '?'.  Keep this branch for
        // callers that invoke the value parser with an already-tokenized statement.
        if (t.Kind is Kind.Word or Kind.Ident && (t.Text.StartsWith("@", StringComparison.Ordinal)
            || t.Text.StartsWith("$", StringComparison.Ordinal)
            || t.Text.StartsWith(":", StringComparison.Ordinal)))
        {
            pos++;
            string digits = t.Text[1..];
            if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
            {
                int idx = p - 1;
                return ParameterAt(parameters, idx);
            }
        }
        throw new InvalidOperationException($"Unsupported value '{t.Text}'.");
    }

    private static decimal ParseNumber(string text)
    {
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d))
        {
            return d;
        }
        throw new InvalidOperationException($"Invalid number '{text}'.");
    }

    private static DateTime ParseDate(string text)
    {
        string trimmed = text.Trim();
        if (DateTime.TryParseExact(trimmed, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out DateTime dt))
        {
            return dt;
        }
        throw new InvalidOperationException($"Unsupported date literal '#{text}#'.");
    }

    // ------------------------------------------------------------------
    // statement-level token helpers
    // ------------------------------------------------------------------

    private static Token? Peek(List<Token> tokens, int pos) => pos < tokens.Count ? tokens[pos] : null;

    private static Token PeekOrThrow(List<Token> tokens, int pos)
        => Peek(tokens, pos) ?? throw new InvalidOperationException("Unexpected end of statement.");

    private static void ExpectWord(List<Token> tokens, ref int pos, string word)
    {
        Token t = PeekOrThrow(tokens, pos);
        if (!t.Text.Equals(word, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected '{word}' but found '{t.Text}'.");
        }
        pos++;
    }

    private static void ExpectSymbol(List<Token> tokens, ref int pos, string symbol)
    {
        Token t = PeekOrThrow(tokens, pos);
        if (t.Text != symbol)
        {
            throw new InvalidOperationException($"Expected '{symbol}' but found '{t.Text}'.");
        }
        pos++;
    }

    private static string ReadName(List<Token> tokens, ref int pos)
    {
        Token t = PeekOrThrow(tokens, pos);
        if (t.Kind is Kind.Word or Kind.Ident)
        {
            pos++;
            return t.Text;
        }
        throw new InvalidOperationException($"Expected a name but found '{t.Text}'.");
    }

    private static Table ReadTable(File.Database db, List<Token> tokens, ref int pos)
    {
        string name = ReadName(tokens, ref pos);
        Table? table;
        try
        {
            table = db.GetTable(name);
        }
        catch (DatabaseException ex) when (ex.Message.Contains("linked table", StringComparison.OrdinalIgnoreCase))
        {
            table = null;
        }
        if (table == null)
        {
            // linked tables resolve through their link (writes go to the linkee)
            table = db.GetLinkedTable(name);
        }
        return table
            ?? throw new InvalidOperationException($"Table '{name}' does not exist.");
    }

    // ------------------------------------------------------------------
    // SQL rebuilding (for embedded SELECT statements)
    // ------------------------------------------------------------------

    private static string RebuildSql(List<Token> tokens, int start)
        => RebuildSql(tokens, start, tokens.Count);

    private static string RebuildSql(List<Token> tokens, int start, int end)
    {
        var sb = new StringBuilder();
        for (int i = start; i < end; i++)
        {
            if (i > start && !NeedsNoSpace(tokens[i - 1], tokens[i]))
            {
                sb.Append(' ');
            }
            sb.Append(RenderToken(tokens[i]));
        }
        return sb.ToString();
    }

    private static bool NeedsNoSpace(Token prev, Token cur)
        => prev.Text is "(" or "." || cur.Text is ")" or "," or "(" or ".";

    private static string RenderToken(Token token) => token.Kind switch
    {
        Kind.Ident => "\"" + token.Text.Replace("\"", "\"\"") + "\"",
        Kind.Str => "'" + token.Text.Replace("'", "''") + "'",
        Kind.Date => "#" + token.Text + "#",
        _ => token.Text,
    };
}
