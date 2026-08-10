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
///   UPDATE table SET col = value [, ...] [WHERE condition]
///   DELETE FROM table [WHERE condition]
///
/// WHERE conditions support: comparisons (=, &lt;&gt;, &lt;, &gt;, &lt;=, &gt;=),
/// IS [NOT] NULL, [NOT] IN (...), [NOT] BETWEEN a AND b, [NOT] LIKE 'pattern'
/// (with % and _ wildcards), combined with AND / OR / NOT and parentheses.
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
    public static int Execute(File.Database db, Mirror mirror, string sql, IReadOnlyList<object?>? parameters, bool dryRun = false)
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
        int affected;
        switch (kind)
        {
            case "INSERT":
                affected = ExecuteInsert(db, mirror, tokens, parameters, dryRun);
                break;
            case "UPDATE":
                affected = ExecuteUpdate(db, mirror, tokens, parameters, dryRun);
                break;
            case "DELETE":
                affected = ExecuteDelete(db, mirror, tokens, parameters, dryRun);
                break;
            default:
                throw new NotSupportedException($"Statement type '{kind}' is not supported for writes.");
        }

        // refresh the mirrored copy so subsequent SELECTs see the changes
        if (!dryRun)
        {
            mirror.RefreshAll();
        }

        return affected;
    }

    private static int ExecuteInsert(File.Database db, Mirror mirror, List<Token> tokens, IReadOnlyList<object?>? parameters, bool dryRun)
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
            return ExecuteInsertSelect(mirror, table, columns, tokens, pos, parameters, dryRun);
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
                table.AddRow(BuildInsertRow(table, columns, valueExprs));
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

    private static int ExecuteInsertSelect(Mirror mirror, Table table, List<string>? columns, List<Token> tokens, int selectStart, IReadOnlyList<object?>? parameters, bool dryRun)
    {
        string selectSql = RebuildSql(tokens, selectStart);
        string translated = AccessSqlTranslator.Translate(selectSql, out int pcount, out _,
            mirror.IsMoneyColumn, mirror.IsExactDecimalColumn);
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
                table.AddRow(BuildInsertRow(table, columns, values));
            }
            affected++;
        }
        return affected;
    }

    private static int ExecuteUpdate(File.Database db, Mirror mirror, List<Token> tokens, IReadOnlyList<object?>? parameters, bool dryRun)
    {
        int pos = 0;
        int paramIndex = 0;
        ExpectWord(tokens, ref pos, "update");
        Table table = ReadTable(db, tokens, ref pos);
        ExpectWord(tokens, ref pos, "set");

        // parse SET assignments (right-hand sides are expressions evaluated per row)
        var assignments = new List<(Column Column, RowExpr Expression)>();
        while (true)
        {
            string name = ReadName(tokens, ref pos);
            Column? column = FindColumn(table, name)
                ?? throw new InvalidOperationException($"Unknown column '{name}'.");
            ExpectSymbol(tokens, ref pos, "=");
            var exprParser = new ExprParser(tokens, pos, table, parameters, paramIndex);
            RowExpr expression = exprParser.Parse();
            pos = exprParser.Pos;
            paramIndex = exprParser.ParamCount;
            assignments.Add((column, expression));
            if (Peek(tokens, pos) is { Text: "," })
            {
                pos++;
                continue;
            }
            break;
        }

        // optional WHERE
        WhereClause? where = TryParseWhere(tokens, ref pos, table, parameters, paramIndex, mirror);
        where?.ValidateSyntax();

        int affected = 0;
        foreach (Table.RowLocation location in table.RowLocations())
        {
            if (where != null && !where.Matches(location.Row))
            {
                continue;
            }
            object?[] values = location.Row.ToArray();
            foreach ((Column column, RowExpr expression) in assignments)
            {
                values[column.ColumnIndex] = CoerceValue(column, expression(location.Row));
            }
            if (!dryRun)
            {
                table.UpdateRow(location.PageNumber, location.RowNumber, values);
            }
            affected++;
        }
        return affected;
    }

    private static int ExecuteDelete(File.Database db, Mirror mirror, List<Token> tokens, IReadOnlyList<object?>? parameters, bool dryRun)
    {
        int pos = 0;
        ExpectWord(tokens, ref pos, "delete");
        ExpectWord(tokens, ref pos, "from");
        Table table = ReadTable(db, tokens, ref pos);

        WhereClause? where = TryParseWhere(tokens, ref pos, table, parameters, 0, mirror);
        where?.ValidateSyntax();

        int affected = 0;
        foreach (Table.RowLocation location in table.RowLocations())
        {
            if (where != null && !where.Matches(location.Row))
            {
                continue;
            }
            if (!dryRun)
            {
                table.DeleteRow(location.PageNumber, location.RowNumber);
            }
            affected++;
        }
        return affected;
    }

    // ------------------------------------------------------------------
    // row construction / coercion
    // ------------------------------------------------------------------

    private static object?[] BuildInsertRow(Table table, List<string>? columns, List<object?> values)
    {
        var row = new object?[table.Columns.Count];
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
    {
        if (value == null || value is DBNull)
        {
            return null;
        }
        CultureInfo invariant = CultureInfo.InvariantCulture;
        switch (column.Type)
        {
            case DataType.Byte:
                return Convert.ToByte(value, invariant);
            case DataType.Int:
                return Convert.ToInt16(value, invariant);
            case DataType.Long:
                return Convert.ToInt32(value, invariant);
            case DataType.BigInt:
                return Convert.ToInt64(value, invariant);
            case DataType.Money:
            case DataType.Numeric:
                return Convert.ToDecimal(value, invariant);
            case DataType.Float:
                return Convert.ToSingle(value, invariant);
            case DataType.Double:
                return Convert.ToDouble(value, invariant);
            case DataType.ShortDateTime:
            case DataType.ExtDateTime:
                return value is DateTime dt ? dt : Convert.ToDateTime(value, invariant);
            case DataType.Boolean:
                return value switch
                {
                    bool b => b,
                    string s => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s is "1" or "-1",
                    _ => Convert.ToBoolean(value, invariant),
                };
            case DataType.Guid:
                return value.ToString();
            case DataType.Text:
            case DataType.Memo:
                return value.ToString();
            case DataType.Binary:
            case DataType.Ole:
                return value is byte[] bytes ? bytes : Encoding.UTF8.GetBytes(value.ToString() ?? "");
            default:
                return value;
        }
    }

    // ------------------------------------------------------------------
    // WHERE clause parsing + evaluation
    // ------------------------------------------------------------------

    private sealed class WhereClause
    {
        private readonly Table _table;
        private readonly List<Token> _tokens;
        private readonly IReadOnlyList<object?>? _parameters;
        private readonly Mirror? _mirror;
        private readonly int _start;
        private readonly int _startParamCount;
        private int _pos;
        private int _paramCount;
        private readonly Dictionary<string, (List<object?> Values, int ParameterCount)> _subqueryCache = new();

        internal WhereClause(List<Token> tokens, int start, Table table, IReadOnlyList<object?>? parameters, int startParamCount, Mirror? mirror)
        {
            _tokens = tokens;
            _start = start;
            _pos = start;
            _table = table;
            _parameters = parameters;
            _startParamCount = startParamCount;
            _paramCount = startParamCount;
            _mirror = mirror;
        }

        internal bool Matches(Row row)
        {
            _pos = _start;
            _paramCount = _startParamCount;
            bool result = ParseOr(row);
            if (_pos < _tokens.Count)
            {
                throw new InvalidOperationException($"Unexpected token '{_tokens[_pos].Text}' in WHERE clause.");
            }
            return result;
        }

        internal void ValidateSyntax()
        {
            // Parse once before touching the file.  A data-free table must reject the
            // same malformed WHERE clause as a populated table would.
            _ = Matches(new Row(_table, new object?[_table.Columns.Count]));
        }

        // ---- grammar ----

        private bool ParseOr(Row row)
        {
            bool left = ParseAnd(row);
            while (MatchWord("or"))
            {
                bool right = ParseAnd(row);
                left = left || right;
            }
            return left;
        }

        private bool ParseAnd(Row row)
        {
            bool left = ParseNot(row);
            while (MatchWord("and"))
            {
                bool right = ParseNot(row);
                left = left && right;
            }
            return left;
        }

        private bool ParseNot(Row row)
        {
            if (PeekWord("not"))
            {
                // NOT LIKE / NOT IN / NOT BETWEEN / NOT EXISTS are consumed within
                // ParsePredicate, so a leading NOT here is a boolean negation
                int save = _pos;
                _pos++;
                if (PeekWord("like") || PeekWord("in") || PeekWord("between") || PeekWord("exists"))
                {
                    _pos = save;
                    return ParsePredicate(row);
                }
                return !ParseNot(row);
            }
            return ParsePredicate(row);
        }

        private bool ParsePredicate(Row row)
        {
            Token cur = PeekToken();
            if (cur.Text == "(")
            {
                _pos++;
                bool v = ParseOr(row);
                Expect(")");
                return v;
            }

            // NOT LIKE / NOT IN / NOT BETWEEN / NOT EXISTS (unary NOT operator)
            if (cur.Text.Equals("not", StringComparison.OrdinalIgnoreCase) && PeekAhead(1) is { } nn
                && (nn.Text.Equals("like", StringComparison.OrdinalIgnoreCase)
                    || nn.Text.Equals("in", StringComparison.OrdinalIgnoreCase)
                    || nn.Text.Equals("between", StringComparison.OrdinalIgnoreCase)
                    || nn.Text.Equals("exists", StringComparison.OrdinalIgnoreCase)))
            {
                _pos += 2;
                if (nn.Text.Equals("exists", StringComparison.OrdinalIgnoreCase))
                {
                    Expect("(");
                    List<object?> set = EvaluateSubquery(_pos, out int after);
                    _pos = after;
                    return set.Count == 0;
                }
                object? nl = ParseOperand(row);
                return !ParsePostfix(row, nl);
            }

            // EXISTS (subquery)
            if (cur.Text.Equals("exists", StringComparison.OrdinalIgnoreCase))
            {
                _pos++;
                Expect("(");
                if (PeekWord("select"))
                {
                    List<object?> set = EvaluateSubquery(_pos, out int after);
                    _pos = after;
                    return set.Count > 0;
                }
                throw new InvalidOperationException("EXISTS requires a SELECT subquery.");
            }

            object? left = ParseOperand(row);
            Token next = PeekToken();

            // IS [NOT] NULL
            if (next.Text.Equals("is", StringComparison.OrdinalIgnoreCase))
            {
                _pos++;
                bool negate = MatchWord("not");
                Expect("null");
                bool isNull = left == null || left is DBNull;
                return negate ? !isNull : isNull;
            }

            // [NOT] IN / LIKE / BETWEEN (operand NOT operator)
            if (next.Text.Equals("not", StringComparison.OrdinalIgnoreCase) && PeekAhead(1) is { } nn2
                && (nn2.Text.Equals("in", StringComparison.OrdinalIgnoreCase)
                    || nn2.Text.Equals("like", StringComparison.OrdinalIgnoreCase)
                    || nn2.Text.Equals("between", StringComparison.OrdinalIgnoreCase)))
            {
                _pos += 2;
                return !ParsePostfix(row, left);
            }

            if (next.Text.Equals("in", StringComparison.OrdinalIgnoreCase)
                || next.Text.Equals("like", StringComparison.OrdinalIgnoreCase)
                || next.Text.Equals("between", StringComparison.OrdinalIgnoreCase))
            {
                _pos++;
                return ParsePostfix(row, left);
            }

            // comparison operators
            if (IsComparisonOp(next.Text))
            {
                string op = next.Text;
                _pos++;
                object? right = ParseOperand(row);
                return Compare(left, right, op);
            }

            // bare operand (boolean column)
            return Truthy(left);
        }

        private bool ParsePostfix(Row row, object? left)
        {
            string op = _tokens[_pos - 1].Text.ToUpperInvariant();
            switch (op)
            {
                case "IN":
                    Expect("(");
                    if (PeekWord("select"))
                    {
                        List<object?> set = EvaluateSubquery(_pos, out int after);
                        _pos = after;
                        return set.Any(v => ValuesEqual(left, v));
                    }
                    var inValues = new List<object?>();
                    while (true)
                    {
                        inValues.Add(ParseOperand(row));
                        if (MatchSymbol(","))
                        {
                            continue;
                        }
                        break;
                    }
                    Expect(")");
                    return inValues.Any(v => ValuesEqual(left, v));
                case "LIKE":
                {
                    object? pattern = ParseOperand(row);
                    return left != null && AccessFunctions.AccessLikePattern(left.ToString()!, pattern?.ToString() ?? "");
                }
                case "BETWEEN":
                {
                    object? low = ParseOperand(row);
                    ExpectWord("and");
                    object? high = ParseOperand(row);
                    return Compare(left, low, ">=") && Compare(left, high, "<=");
                }                default:
                    throw new InvalidOperationException($"Unsupported operator '{op}'.");
            }
        }

        /// <summary>
        /// Runs a (non-correlated) SELECT subquery against the SQLite mirror and returns
        /// its first column values. The result is cached per subquery text.
        /// </summary>
        private List<object?> EvaluateSubquery(int start, out int endAfterParen)
        {
            int depth = 0;
            int end = start;
            for (; end < _tokens.Count; end++)
            {
                string t = _tokens[end].Text;
                if (t == "(")
                {
                    depth++;
                }
                else if (t == ")")
                {
                    if (depth == 0)
                    {
                        break;
                    }
                    depth--;
                }
            }
            if (end >= _tokens.Count)
            {
                throw new InvalidOperationException("Unterminated subquery in WHERE clause.");
            }
            endAfterParen = end + 1;

            string subSql = RebuildSql(_tokens, start, end);
            if (_subqueryCache.TryGetValue(subSql, out var cached))
            {
                _paramCount += cached.ParameterCount;
                return cached.Values;
            }
            if (_mirror == null)
            {
                throw new InvalidOperationException("Subqueries in WHERE require the SQLite mirror.");
            }
            string translated = AccessSqlTranslator.Translate(subSql, out int parameterCount, out _,
                _mirror.IsMoneyColumn, _mirror.IsExactDecimalColumn);
            var results = new List<object?>();
            IReadOnlyList<object?>? subqueryParameters = null;
            if (parameterCount > 0)
            {
                if (_parameters == null || _paramCount + parameterCount > _parameters.Count)
                {
                    throw new InvalidOperationException("Not enough parameter values were supplied for the WHERE subquery.");
                }
                subqueryParameters = _parameters.Skip(_paramCount).Take(parameterCount).ToArray();
            }
            using var reader = _mirror.ExecuteReader(translated, subqueryParameters);
            while (reader.Read())
            {
                results.Add(reader.IsDBNull(0) ? null : reader.GetValue(0));
            }
            _paramCount += parameterCount;
            _subqueryCache[subSql] = (results, parameterCount);
            return results;
        }

        private object? ParseOperand(Row row)
        {
            Token t = PeekToken();
            switch (t.Kind)
            {
                case Kind.Number:
                    _pos++;
                    return ParseNumber(t.Text);
                case Kind.Str:
                    _pos++;
                    return t.Text;
                case Kind.Date:
                    _pos++;
                    return ParseDate(t.Text);
                case Kind.Word:
                    if (t.Text.Equals("null", StringComparison.OrdinalIgnoreCase))
                    {
                        _pos++;
                        return null;
                    }
                    if (t.Text.Equals("true", StringComparison.OrdinalIgnoreCase))
                    {
                        _pos++;
                        return true;
                    }
                    if (t.Text.Equals("false", StringComparison.OrdinalIgnoreCase))
                    {
                        _pos++;
                        return false;
                    }
                    if (t.Text == "?" || t.Text.StartsWith("@", StringComparison.Ordinal) || t.Text.StartsWith("$", StringComparison.Ordinal))
                    {
                        _pos++;
                        return ResolveParamValue(t.Text);
                    }
                    _pos++;
                    return GetColumnValue(row, t.Text);
                case Kind.Ident:
                    _pos++;
                    return GetColumnValue(row, t.Text);
                case Kind.Symbol:
                    if (t.Text == "?")
                    {
                        _pos++;
                        return ResolveParamValue("?");
                    }
                    break;
            }
            throw new InvalidOperationException($"Unexpected token '{t.Text}' in expression.");
        }

        private object? GetColumnValue(Row row, string name)
        {
            foreach (Column column in _table.Columns)
            {
                if (column.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return row[name];
                }
            }
            throw new InvalidOperationException($"Unknown column '{name}'.");
        }

        private object? ResolveParamValue(string text)
        {
            if (text == "?")
            {
                int idx = _paramCount++;
                return ParameterAt(_parameters, idx);
            }
            // @pN or $pN -> positional parameter (1-based like Access)
            string digits = text[1..];
            if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
            {
                int idx = p - 1;
                return ParameterAt(_parameters, idx);
            }
            throw new InvalidOperationException($"Unsupported parameter '{text}'.");
        }

        // ---- token helpers ----

        private bool PeekWord(string word)
            => _pos < _tokens.Count && _tokens[_pos].Kind is Kind.Word or Kind.Ident
                && _tokens[_pos].Text.Equals(word, StringComparison.OrdinalIgnoreCase);

        private bool MatchWord(string word)
        {
            if (PeekWord(word))
            {
                _pos++;
                return true;
            }
            return false;
        }

        private bool MatchSymbol(string symbol)
        {
            if (_pos < _tokens.Count && _tokens[_pos].Text == symbol)
            {
                _pos++;
                return true;
            }
            return false;
        }

        private Token PeekToken() => _pos < _tokens.Count ? _tokens[_pos] : throw new InvalidOperationException("Unexpected end of statement.");

        private Token? PeekAhead(int offset)
            => _pos + offset < _tokens.Count ? _tokens[_pos + offset] : null;

        private void Expect(string text)
        {
            Token t = PeekToken();
            if (!t.Text.Equals(text, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Expected '{text}' but found '{t.Text}'.");
            }
            _pos++;
        }

        private void ExpectWord(string word)
        {
            if (!PeekWord(word))
            {
                throw new InvalidOperationException($"Expected '{word}' but found '{PeekToken().Text}'.");
            }
            _pos++;
        }
    }

    private static WhereClause? TryParseWhere(List<Token> tokens, ref int pos, Table table, IReadOnlyList<object?>? parameters, int startParamCount, Mirror? mirror)
    {
        if (pos >= tokens.Count)
        {
            return null;
        }
        if (!tokens[pos].Text.Equals("where", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unexpected token '{tokens[pos].Text}'.");
        }
        pos++;
        return new WhereClause(tokens, pos, table, parameters, startParamCount, mirror);
    }

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

    private static bool IsComparisonOp(string text) => text switch
    {
        "=" or "<>" or "<" or ">" or "<=" or ">=" => true,
        _ => false,
    };

    private static bool Compare(object? left, object? right, string op)
    {
        if (left == null || right == null || left is DBNull || right is DBNull)
        {
            // SQL NULL semantics: any comparison against NULL is false
            return false;
        }
        int cmp = CompareValues(left, right);
        return op switch
        {
            "=" => cmp == 0,
            "<>" => cmp != 0,
            "<" => cmp < 0,
            ">" => cmp > 0,
            "<=" => cmp <= 0,
            ">=" => cmp >= 0,
            _ => throw new InvalidOperationException($"Unsupported operator '{op}'."),
        };
    }

    private static bool ValuesEqual(object? left, object? right)
        => left == null || right == null || left is DBNull || right is DBNull
            ? false
            : CompareValues(left, right) == 0;

    private static int CompareValues(object? a, object? b)
    {
        if (a == null || b == null)
        {
            return 0;
        }
        if (IsNumeric(a) && IsNumeric(b))
        {
            return Convert.ToDecimal(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(b, CultureInfo.InvariantCulture));
        }
        if (a is DateTime da && b is DateTime db)
        {
            return da.CompareTo(db);
        }
        if (a is bool ba && b is bool bb)
        {
            return ba.CompareTo(bb);
        }
        return string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumeric(object? value)
        => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
            or System.Numerics.BigInteger;

    private static bool Truthy(object? value)
    {
        if (value == null || value is DBNull)
        {
            return false;
        }
        if (value is bool b)
        {
            return b;
        }
        if (value is string s)
        {
            return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s != "0";
        }
        return Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0.0d;
    }

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
