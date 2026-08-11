using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using static UCanAccess.AccessTokenizer;

namespace UCanAccess;

/// <summary>
/// Translates a subset of Access SQL into SQLite SQL.
///
/// Handles: bracketed/backticked identifiers, single/double-quoted strings,
/// Access date literals (#..#), DISTINCTROW, TOP n, string concatenation with
/// '&amp;' (Access null semantics) and Access-style LIKE wildcards (via a custom
/// <c>access_like</c> function).
/// </summary>
public static class AccessSqlTranslator
{
    private static readonly HashSet<string> WordBoundaries = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "not", "is", "in", "between", "like", "exists",
        "select", "from", "where", "group", "order", "by", "having", "on",
        // the Access lexer emits "GROUP BY" / "ORDER BY" as a single keyword token
        "group by", "order by",
        "join", "inner", "left", "right", "full", "outer", "union", "except", "intersect",
        "limit", "offset", "into", "values", "set", "when", "then", "else", "as",
    };

    private static readonly HashSet<string> SymbolBoundaries = new(StringComparer.Ordinal)
    {
        "=", "<>", "<", ">", "<=", ">=", "&", "||", "+", "-", "*", "/", "%", "^", "\\",
    };

    /// <summary>whether the given token terminates an operand of a low-precedence operator</summary>
    private static bool IsBoundary(Token t)
        => t.Kind switch
        {
            Kind.Word => WordBoundaries.Contains(t.Text),
            Kind.Symbol => SymbolBoundaries.Contains(t.Text),
            _ => false,
        };

    private static readonly string[] DateFormats =
    {
        "M/d/yyyy", "M/d/yyyy H:mm:ss", "M/d/yyyy H:mm", "M/d/yyyy h:mm:ss tt", "M/d/yyyy h:mm tt",
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm",
        "d/M/yyyy",
    };

    /// <summary>
    /// Translates Access SQL to SQLite SQL.
    /// </summary>
    public static string Translate(string accessSql)
        => Translate(accessSql, out _, out _);

    /// <summary>
    /// Translates Access SQL to SQLite SQL, converting '?' placeholders to @pN parameters.
    /// </summary>
    /// <param name="parameterCount">the number of '?' placeholders found</param>
    public static string Translate(string accessSql, out int parameterCount)
        => Translate(accessSql, out parameterCount, out _);

    /// <summary>
    /// Translates Access SQL to SQLite SQL, converting '?', '@name' and ':name' placeholders
    /// (and parameters declared through a <c>PARAMETERS ...;</c> clause) to @pN parameters.
    /// </summary>
    /// <param name="parameterCount">the number of parameter placeholders found</param>
    /// <param name="namedParameters">
    /// the parameter names in placeholder order (empty string for bare '?' placeholders),
    /// or null when the statement has no parameters
    /// </param>
    public static string Translate(string accessSql, out int parameterCount, out IReadOnlyList<string>? namedParameters,
        Func<string, bool>? isMoneyColumn = null,
        Func<string, bool>? isExactDecimalColumn = null,
        Func<string, bool>? isDateColumn = null)
    {
        if (CrosstabTranslator.TryTranslate(accessSql, isExactDecimalColumn, out string crosstabSql))
        {
            return Translate(crosstabSql, out parameterCount, out namedParameters,
                isMoneyColumn, isExactDecimalColumn, isDateColumn);
        }

        string prepared = Preprocess(accessSql, out List<string> names);
        var tokens = Tokenize(prepared);
        var work = new List<Token>(tokens);

        // SELECT DISTINCTROW ... / SELECT TOP n ...
        bool hasSelect = IsSelectStatement(work);
        bool hasWindowClause = work.Any(token => token.Kind == Kind.Word
            && token.Text.Equals("over", StringComparison.OrdinalIgnoreCase));
        if (hasWindowClause && !hasSelect)
        {
            throw new NotSupportedException("Window functions are supported only in SELECT queries.");
        }
        string? topN = null;
        if (hasSelect)
        {
            int i = 1;
            if (i < work.Count && work[i].Text.Equals("distinctrow", StringComparison.OrdinalIgnoreCase))
            {
                work[i] = new Token(Kind.Word, "DISTINCT");
                i++;
            }
            if (i < work.Count && work[i].Text.Equals("top", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= work.Count || work[i + 1].Kind != Kind.Number)
                {
                    throw new NotSupportedException("TOP requires a numeric argument.");
                }
                topN = work[i + 1].Text;
                if (i + 2 < work.Count && work[i + 2].Text.Equals("percent", StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotSupportedException("TOP ... PERCENT is not supported yet.");
                }
                work.RemoveRange(i, 2);
            }
        }

        // rewrite LIKE / NOT LIKE and '&' concatenation operators, and '?' placeholders
        int pCount = 0;

        // SQLite reserves ISNULL as a keyword; use the registered function name instead
        for (int i = 0; i < work.Count; i++)
        {
            if (work[i].Kind == Kind.Word && work[i].Text.Equals("IsNull", StringComparison.OrdinalIgnoreCase))
            {
                work[i] = new Token(Kind.Word, "access_isnull");
            }
        }

        for (int i = 0; i < work.Count; i++)
        {
            Token t = work[i];
            if (t.Text == "?")
            {
                work[i] = new Token(Kind.Word, $"@p{pCount++}");
            }
            else if (t.Kind == Kind.Word && t.Text.Equals("like", StringComparison.OrdinalIgnoreCase))
            {
                bool negated = i > 0 && work[i - 1].Text.Equals("not", StringComparison.OrdinalIgnoreCase);
                int opIdx = i;
                int leftEnd = negated ? opIdx - 1 : opIdx;

                int leftStart = FindLeftOperandStart(work, leftEnd);
                int rightEnd = FindRightOperandEnd(work, opIdx + 1);

                // consume optional ESCAPE clause
                if (rightEnd < work.Count && work[rightEnd].Text.Equals("escape", StringComparison.OrdinalIgnoreCase))
                {
                    rightEnd = Math.Min(work.Count, rightEnd + 2);
                }

                string left = Join(work, leftStart, leftEnd);
                string pattern = Join(work, opIdx + 1, rightEnd);

                string replacement = negated ? "NOT " : "";
                replacement += $"access_like({left}, {pattern})";
                ReplaceTokens(work, leftStart, rightEnd, replacement);

                i = leftStart;
            }
            else if (t.Text == "&" && IsStringConcatContext(work, i))
            {
                int leftStart = FindLeftOperandStart(work, i);
                int rightEnd = FindRightOperandEnd(work, i + 1);

                string left = Join(work, leftStart, i);
                string right = Join(work, i + 1, rightEnd);
                if (isMoneyColumn != null)
                {
                    left = MaybeWrapMoney(work, leftStart, i, left, isMoneyColumn);
                    right = MaybeWrapMoney(work, i + 1, rightEnd, right, isMoneyColumn);
                }

                string replacement = $"(ifnull({left}, '') || ifnull({right}, ''))";
                ReplaceTokens(work, leftStart, rightEnd, replacement);

                i = leftStart;
            }
        }

        RewriteDateExpressions(work, isDateColumn);
        RewriteExactDecimalExpressions(work, isExactDecimalColumn);
        RewriteExactDecimalAggregates(work, isExactDecimalColumn);

        // Access sorts NULL values first in both ASC and DESC; SQLite puts NULLs
        // last in DESC. Rewrite bare "col DESC" sort keys so NULLs sort first.
        RewriteOrderBy(work);

        // rebuild output
        var sb = new StringBuilder();
        for (int i = 0; i < work.Count; i++)
        {
            if (i > 0 && !NeedsNoSpace(work[i - 1], work[i]))
            {
                sb.Append(' ');
            }
            sb.Append(Render(work[i]));
        }

        string sql = sb.ToString().TrimEnd(' ', ';').Trim();
        if (topN != null)
        {
            sql += $" LIMIT {topN}";
        }
        parameterCount = pCount;
        namedParameters = names.Count > 0 ? names : null;
        return sql;
    }

    private static bool IsSelectStatement(List<Token> work)
    {
        if (work.Count == 0)
        {
            return false;
        }
        if (work[0].Text.Equals("select", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!work[0].Text.Equals("with", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A CTE contains nested SELECT tokens.  The statement is a SELECT
        // only when its outer SELECT appears after the CTE definitions.
        int depth = 0;
        for (int i = 1; i < work.Count; i++)
        {
            if (work[i].Text == "(")
            {
                depth++;
            }
            else if (work[i].Text == ")")
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0 && work[i].Text.Equals("select", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Normalizes parameter syntax that the Access lexer cannot tokenize directly:
    /// strips a leading <c>PARAMETERS ...;</c> clause (turning bracketed parameter
    /// references into '?'), converts '@name' and ':name' into '?', and removes a
    /// trailing <c>WITH OWNERACCESS OPTION</c>. The <paramref name="names"/> list gets
    /// one entry per placeholder, in order (empty for a bare '?').
    /// </summary>
    private static string Preprocess(string sql, out List<string> names)
    {
        names = new List<string>();

        string s = sql.TrimStart();

        // collect parameter names declared by a PARAMETERS clause and strip it
        HashSet<string> declared = new(StringComparer.OrdinalIgnoreCase);
        if (s.StartsWith("PARAMETERS", StringComparison.OrdinalIgnoreCase))
        {
            int semi = -1;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c is '\'' or '"')
                {
                    i = SkipQuoted(s, i);
                    continue;
                }
                if (c == ';')
                {
                    semi = i;
                    break;
                }
            }
            if (semi >= 0)
            {
                string clause = s[..semi];
                foreach (Match m in Regex.Matches(clause, @"\[([^\]]+)\]"))
                {
                    declared.Add(m.Groups[1].Value);
                }
                s = s[(semi + 1)..].TrimStart();
            }
        }

        // strip trailing WITH OWNERACCESS OPTION
        s = Regex.Replace(s, @"\s+WITH\s+OWNERACCESS\s+OPTION\s*$", "", RegexOptions.IgnoreCase).TrimEnd();

        // normalize parameters to '?' placeholders
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length;)
        {
            char c = s[i];
            if (c is '\'' or '"')
            {
                int end = SkipQuoted(s, i);
                sb.Append(s, i, end - i + 1);
                i = end + 1;
                continue;
            }
            if (c == '`')
            {
                int end = SkipQuoted(s, i);
                sb.Append(s, i, end - i + 1);
                i = end + 1;
                continue;
            }
            if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
            {
                int end = s.IndexOf('\n', i + 2);
                end = end < 0 ? s.Length : end + 1;
                sb.Append(s, i, end - i);
                i = end;
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int close = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                int end = close < 0 ? s.Length : close + 2;
                sb.Append(s, i, end - i);
                i = end;
                continue;
            }
            if (c == '[')
            {
                int close = SkipBracketed(s, i);
                if (close < 0)
                {
                    sb.Append(s, i, s.Length - i);
                    break;
                }
                if (close > i && declared.Contains(s[(i + 1)..close]))
                {
                    names.Add(s[(i + 1)..close]);
                    sb.Append('?');
                    i = close + 1;
                    continue;
                }
                sb.Append(s, i, close - i + 1);
                i = close + 1;
                continue;
            }
            else if ((c is '@' or ':' or '$') && i + 1 < s.Length
                && (char.IsLetter(s[i + 1]) || s[i + 1] == '_'))
            {
                int j = i + 1;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_'))
                {
                    j++;
                }
                names.Add(s[(i + 1)..j]);
                sb.Append('?');
                i = j;
                continue;
            }
            else if (c == '?')
            {
                names.Add("");
                sb.Append('?');
                i++;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static int SkipBracketed(string s, int start)
    {
        for (int i = start + 1; i < s.Length; i++)
        {
            if (s[i] == ']')
            {
                if (i + 1 < s.Length && s[i + 1] == ']')
                {
                    i++;
                    continue;
                }
                return i;
            }
        }
        return -1;
    }

    private static int SkipQuoted(string s, int start)
    {
        char quote = s[start];
        for (int i = start + 1; i < s.Length; i++)
        {
            if (s[i] == quote)
            {
                if (i + 1 < s.Length && s[i + 1] == quote)
                {
                    i++;
                    continue;
                }
                return i;
            }
        }
        return s.Length - 1;
    }

    /// <summary>
    /// Rewrites bare "column DESC" ORDER BY keys to "(column IS NULL) DESC, column DESC"
    /// so NULL values sort first (matching Access/HSQLDB); ASC keys already sort NULLs
    /// first in SQLite. Complex sort expressions are left untouched.
    /// </summary>
    private static void RewriteOrderBy(List<Token> work)
    {
        int orderBy = -1;
        int scanDepth = 0;
        for (int i = 0; i < work.Count; i++)
        {
            if (work[i].Text == "(")
            {
                scanDepth++;
            }
            else if (work[i].Text == ")")
            {
                scanDepth = Math.Max(0, scanDepth - 1);
            }
            else if (scanDepth == 0 && work[i].Kind == Kind.Word
                && work[i].Text.Equals("order by", StringComparison.OrdinalIgnoreCase))
            {
                orderBy = i;
                break;
            }
        }
        if (orderBy < 0)
        {
            return;
        }

        int j = orderBy + 1;
        while (j < work.Count)
        {
            int keyStart = j;
            int depth = 0;
            while (j < work.Count)
            {
                Token current = work[j];
                if (current.Text == "(")
                {
                    depth++;
                }
                else if (current.Text == ")")
                {
                    if (depth == 0)
                    {
                        break;
                    }
                    depth--;
                }
                if (depth == 0 && current.Text is "," or ";")
                {
                    break;
                }
                j++;
            }

            int keyEnd = j;
            int direction = keyEnd - 1;
            if (direction >= keyStart
                && work[direction].Kind == Kind.Word
                && work[direction].Text.Equals("desc", StringComparison.OrdinalIgnoreCase))
            {
                int expressionEnd = direction;
                if (IsSimpleIdentifier(work, keyStart, expressionEnd))
                {
                    string col = Join(work, keyStart, expressionEnd);
                    string replacement = $"({col} IS NULL) DESC, {col} DESC";
                    ReplaceTokens(work, keyStart, keyEnd, replacement);
                    j = keyStart + Tokenize(replacement).Count;
                }
            }
            if (j == keyEnd)
            {
                j++;
            }
        }
    }

    private static bool IsSimpleIdentifier(List<Token> work, int start, int end)
        => end - start == 1
            ? work[start].Kind is Kind.Word or Kind.Ident
            : end - start == 3
                && work[start].Kind is Kind.Word or Kind.Ident
                && work[start + 1].Text == "."
                && work[start + 2].Kind is Kind.Word or Kind.Ident;

    private static void RewriteDateExpressions(List<Token> work, Func<string, bool>? isDateColumn)
    {
        if (isDateColumn == null)
        {
            return;
        }

        for (int i = 0; i < work.Count; i++)
        {
            string operation = work[i].Text;
            if (operation is not ("+" or "-") || IsUnaryOperator(work, i))
            {
                continue;
            }

            int leftStart = FindLeftOperandStart(work, i);
            int rightEnd = FindRightOperandEnd(work, i + 1);
            if (leftStart >= i || rightEnd <= i + 1)
            {
                continue;
            }

            bool leftDate = IsDateOperand(work, leftStart, i, isDateColumn);
            bool rightDate = IsDateOperand(work, i + 1, rightEnd, isDateColumn);
            bool leftNumeric = IsNumericExpression(work, leftStart, i);
            bool rightNumeric = IsNumericExpression(work, i + 1, rightEnd);
            string left = Join(work, leftStart, i);
            string right = Join(work, i + 1, rightEnd);
            string? replacement = null;

            if (operation == "-" && leftDate && rightDate)
            {
                replacement = $"uca_date_diff_days({left}, {right})";
            }
            else if (leftDate && rightNumeric)
            {
                replacement = operation == "+"
                    ? $"uca_date_add_days({left}, {right})"
                    : $"uca_date_add_days({left}, -({right}))";
            }
            else if (operation == "+" && rightDate && leftNumeric)
            {
                replacement = $"uca_date_add_days({right}, {left})";
            }

            if (replacement == null)
            {
                continue;
            }

            ReplaceTokens(work, leftStart, rightEnd, replacement);
            i = leftStart + Tokenize(replacement).Count - 1;
        }
    }

    private static bool IsDateOperand(List<Token> work, int start, int end, Func<string, bool> isDateColumn)
    {
        if (end - start == 1 && work[start].Kind == Kind.Date)
        {
            return true;
        }
        if (end - start == 1 && work[start].Kind is Kind.Word or Kind.Ident)
        {
            return isDateColumn(work[start].Text);
        }
        if (end - start == 3 && work[start + 1].Text == "."
            && work[start].Kind is Kind.Word or Kind.Ident
            && work[start + 2].Kind is Kind.Word or Kind.Ident)
        {
            return isDateColumn($"{work[start].Text}.{work[start + 2].Text}");
        }
        return false;
    }

    private static bool IsNumericExpression(List<Token> work, int start, int end)
    {
        if (start >= end)
        {
            return false;
        }
        return work.Skip(start).Take(end - start).All(token =>
            token.Kind == Kind.Number
            || token.Text is "+" or "-" or "(" or ")" or "."
            || token.Text == "?"
            || token.Kind is Kind.Word or Kind.Ident &&
                (token.Text.StartsWith("@", StringComparison.Ordinal)
                    || token.Text.StartsWith(":", StringComparison.Ordinal)
                    || token.Text.StartsWith("$", StringComparison.Ordinal)));
    }

    private static void RewriteExactDecimalAggregates(List<Token> work, Func<string, bool>? isExactDecimalColumn)
    {
        if (isExactDecimalColumn == null)
        {
            return;
        }

        for (int i = 0; i + 3 < work.Count; i++)
        {
            string aggregate = work[i].Text.ToLowerInvariant();
            string replacement = aggregate switch
            {
                "sum" => "uca_decimal_sum",
                "min" => "uca_decimal_min",
                "max" => "uca_decimal_max",
                _ => string.Empty,
            };
            if (replacement.Length == 0 || work[i + 1].Text != "(")
            {
                continue;
            }

            string name;
            if (i + 3 < work.Count && work[i + 2].Kind is Kind.Word or Kind.Ident
                && work[i + 3].Text == ")")
            {
                name = work[i + 2].Text;
            }
            else if (i + 5 < work.Count && work[i + 3].Text == "."
                && work[i + 4].Kind is Kind.Word or Kind.Ident && work[i + 5].Text == ")")
            {
                name = $"{work[i + 2].Text}.{work[i + 4].Text}";
            }
            else
            {
                continue;
            }

            if (isExactDecimalColumn(name))
            {
                work[i] = new Token(Kind.Word, replacement);
            }
        }
    }

    private static void RewriteExactDecimalExpressions(List<Token> work, Func<string, bool>? isExactDecimalColumn)
    {
        if (isExactDecimalColumn == null)
        {
            return;
        }

        for (int i = 0; i < work.Count; i++)
        {
            string operation = work[i].Text;
            bool comparison = operation is "=" or "<>" or "<" or ">" or "<=" or ">=";
            bool arithmetic = operation is "+" or "-" or "*" or "/";
            if (!comparison && !arithmetic || (operation is "+" or "-") && IsUnaryOperator(work, i))
            {
                continue;
            }

            int leftStart = FindLeftOperandStart(work, i);
            int rightEnd = FindRightOperandEnd(work, i + 1);
            if (leftStart >= i || rightEnd <= i + 1)
            {
                continue;
            }
            string left = Join(work, leftStart, i);
            string right = Join(work, i + 1, rightEnd);
            if (!IsExactOperand(work, leftStart, i, isExactDecimalColumn)
                && !IsExactOperand(work, i + 1, rightEnd, isExactDecimalColumn))
            {
                continue;
            }

            string replacement;
            if (comparison)
            {
                replacement = $"uca_decimal_cmp({left}, {right}) {operation} 0";
            }
            else
            {
                string function = operation switch
                {
                    "+" => "uca_decimal_add",
                    "-" => "uca_decimal_subtract",
                    "*" => "uca_decimal_multiply",
                    "/" => "uca_decimal_divide",
                    _ => throw new InvalidOperationException(),
                };
                replacement = $"{function}({left}, {right})";
            }
            ReplaceTokens(work, leftStart, rightEnd, replacement);
            i = leftStart + Tokenize(replacement).Count - 1;
        }
    }

    private static bool IsExactOperand(List<Token> work, int start, int end,
        Func<string, bool> isExactDecimalColumn)
    {
        if (end - start == 1 && work[start].Kind is Kind.Word or Kind.Ident)
        {
            return isExactDecimalColumn(work[start].Text);
        }
        if (end - start == 3 && work[start + 1].Text == ".")
        {
            return isExactDecimalColumn($"{work[start].Text}.{work[start + 2].Text}");
        }
        return work.Skip(start).Take(end - start)
            .Any(t => t.Text.StartsWith("uca_decimal_", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnaryOperator(List<Token> work, int index)
    {
        if (index == 0)
        {
            return true;
        }
        Token previous = work[index - 1];
        return previous.Text == "(" || previous.Kind == Kind.Symbol && previous.Text != ")"
            || previous.Kind == Kind.Word && IsBoundary(previous);
    }

    /// <summary>
    /// '&amp;' is string concatenation in Access; treat it as such when it sits between
    /// two non-numeric operands. In numeric contexts SQLite's '+' is used, and Access
    /// '&amp;' on numbers is still concatenation, so we always treat it as concatenation.
    /// </summary>
    private static bool IsStringConcatContext(List<Token> work, int opIdx)
        => opIdx > 0 && opIdx < work.Count - 1;

    /// <summary>
    /// If the operand is a single column reference that the caller knows to be a MONEY
    /// column, wrap it in <c>money_str()</c> so concatenation keeps the 4-decimal scale.
    /// </summary>
    private static string MaybeWrapMoney(List<Token> work, int start, int end, string rendered, Func<string, bool> isMoneyColumn)
    {
        if (end - start == 1)
        {
            Token t = work[start];
            if (t.Kind is Kind.Word or Kind.Ident && isMoneyColumn(t.Text))
            {
                return $"money_str({rendered})";
            }
        }
        else if (end - start == 3 && work[start + 1].Text == ".")
        {
            string qualifiedName = $"{work[start].Text}.{work[start + 2].Text}";
            if (isMoneyColumn(qualifiedName))
            {
                return $"money_str({rendered})";
            }
        }
        return rendered;
    }

    private static bool NeedsNoSpace(Token prev, Token cur)
    {
        // no space before/after parens or around dots
        return prev.Text is "(" or "." || cur.Text is ")" or "," or "(" or ".";
    }

    private static void ReplaceTokens(List<Token> work, int start, int endExclusive, string sqlFragment)
    {
        work.RemoveRange(start, endExclusive - start);
        work.InsertRange(start, Tokenize(sqlFragment));
    }

    private static string Join(List<Token> work, int start, int end)
    {
        var sb = new StringBuilder();
        for (int i = start; i < end; i++)
        {
            if (i > start && !NeedsNoSpace(work[i - 1], work[i]))
            {
                sb.Append(' ');
            }
            sb.Append(Render(work[i]));
        }
        return sb.ToString();
    }

    private static string Render(Token token) => token.Kind switch
    {
        Kind.Ident => "\"" + token.Text.Replace("\"", "\"\"") + "\"",
        Kind.Str => "'" + token.Text.Replace("'", "''") + "'",
        Kind.Date => "'" + FormatDate(token.Text) + "'",
        _ => token.Text,
    };

    private static string FormatDate(string text)
    {
        string trimmed = text.Trim();
        if (DateTime.TryParseExact(trimmed, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out DateTime dt))
        {
            return dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }
        throw new NotSupportedException($"Unsupported date literal '#{text}#'.");
    }

    /// <summary>
    /// Finds the index of the first token of the left operand of the operator at <c>scanFrom</c>
    /// (the operator token itself; the scan goes backwards from <c>scanFrom - 1</c>).
    /// </summary>
    private static int FindLeftOperandStart(List<Token> work, int scanFrom)
    {
        int leftStart = scanFrom;
        int depth = 0;
        for (int j = scanFrom - 1; j >= 0; j--)
        {
            Token t = work[j];
            if (t.Text == ")")
            {
                depth++;
                continue;
            }
            if (t.Text == "(")
            {
                if (depth > 0)
                {
                    depth--;
                    continue;
                }
                leftStart = j + 1;
                break;
            }
            if (depth == 0 && (t.Text == "," || IsBoundary(t)))
            {
                leftStart = j + 1;
                break;
            }
            leftStart = j;
        }
        return leftStart;
    }

    /// <summary>
    /// Finds the exclusive end index of the right operand starting at <c>start</c>.
    /// </summary>
    private static int FindRightOperandEnd(List<Token> work, int start)
    {
        int rightEnd = start;
        int depth = 0;
        for (int j = start; j < work.Count; j++)
        {
            Token t = work[j];
            if (t.Text == "(")
            {
                depth++;
                rightEnd = j + 1;
                continue;
            }
            if (t.Text == ")")
            {
                if (depth == 0)
                {
                    rightEnd = j;
                    break;
                }
                depth--;
                rightEnd = j + 1;
                continue;
            }
            if (depth == 0 && (t.Text == "," || IsBoundary(t)))
            {
                rightEnd = j;
                break;
            }
            rightEnd = j + 1;
        }
        return rightEnd;
    }
}
