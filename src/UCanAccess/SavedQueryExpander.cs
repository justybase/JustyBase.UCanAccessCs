using System.Text;
using UCanAccess.File;

namespace UCanAccess;

/// <summary>
/// Expands parameterized Access QueryDefs into derived tables at command
/// execution time.  SQLite views cannot carry Access QueryDef parameters, so
/// these definitions are deliberately kept out of the persistent mirror.
/// </summary>
internal static class SavedQueryExpander
{
    internal static string Expand(string sql, Database database)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(database);
        return ExpandSql(sql, database, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static string ExpandSql(string sql, Database database, HashSet<string> stack)
    {
        string result = sql;
        int searchFrom = 0;
        while (TryFindQuerySource(result, searchFrom, database, out SourceMatch match, out QueryDef? query))
        {
            if (query == null || !query.HasParameters)
            {
                searchFrom = match.End;
                continue;
            }

            if (!stack.Add(query.Name))
            {
                throw new InvalidOperationException(
                    $"Saved QueryDef expansion contains a cycle involving '{query.Name}'.");
            }

            try
            {
                string querySql = query.Sql
                    ?? throw new NotSupportedException(
                        $"Saved parameterized QueryDef '{query.Name}' cannot be reconstructed.");
                querySql = StripParametersClause(querySql);
                querySql = ExpandSql(querySql, database, stack);
                querySql = ReplaceParameterReferences(querySql, query.ParameterNames);
                result = result[..match.Start] + "(" + querySql + ")" + result[match.End..];
                searchFrom = match.Start + querySql.Length + 2;
            }
            finally
            {
                stack.Remove(query.Name);
            }
        }

        return result;
    }

    private static string StripParametersClause(string sql)
    {
        string trimmed = sql.TrimStart();
        if (!StartsWithKeyword(trimmed, "PARAMETERS"))
        {
            return sql;
        }

        int separator = FindTopLevelChar(trimmed, ';');
        if (separator < 0)
        {
            throw new NotSupportedException("A parameterized QueryDef has no terminating PARAMETERS clause.");
        }
        return trimmed[(separator + 1)..].TrimStart();
    }

    private static string ReplaceParameterReferences(string sql, IReadOnlyList<string> names)
    {
        if (names.Count == 0 || sql.Length == 0)
        {
            return sql;
        }

        var result = new StringBuilder(sql.Length + names.Count * 2);
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            if (c == '\'' || c == '"' || c == '`')
            {
                int end = FindQuotedEnd(sql, i, c);
                string token = end > i ? sql[i..end] : sql[i..];
                string identifier = UnquoteIdentifier(token);
                if (c != '\''
                    && names.Any(name => name.Equals(identifier, StringComparison.OrdinalIgnoreCase))
                    && !HasQualifier(sql, i))
                {
                    result.Append('@').Append(identifier);
                }
                else
                {
                    result.Append(token);
                }
                i = end;
                continue;
            }
            if (c == '[')
            {
                int end = FindBracketedEnd(sql, i);
                if (end <= i)
                {
                    result.Append(c);
                    i++;
                    continue;
                }
                string token = sql[i..end];
                string identifier = UnquoteIdentifier(token);
                if (names.Any(name => name.Equals(identifier, StringComparison.OrdinalIgnoreCase))
                    && !HasQualifier(sql, i))
                {
                    result.Append('@').Append(identifier);
                }
                else
                {
                    result.Append(token);
                }
                i = end;
                continue;
            }
            if (IsIdentifierStart(c))
            {
                int start = i++;
                while (i < sql.Length && IsIdentifierPart(sql[i])) i++;
                string token = sql[start..i];
                if (names.Any(name => name.Equals(token, StringComparison.OrdinalIgnoreCase))
                    && (start == 0 || sql[start - 1] is not '@' and not ':' and not '?' and not '$')
                    && !HasQualifier(sql, start))
                {
                    result.Append('@').Append(token);
                }
                else
                {
                    result.Append(token);
                }
                continue;
            }
            result.Append(c);
            i++;
        }
        return result.ToString();
    }

    private static bool TryFindQuerySource(string sql, int start, Database database,
        out SourceMatch match, out QueryDef? query)
    {
        match = default;
        query = null;
        ScanState state = default;
        for (int i = Math.Max(0, start); i < sql.Length; i++)
        {
            if (!state.Advance(sql, ref i)) continue;
            if (!IsKeywordAt(sql, i, "FROM") && !IsKeywordAt(sql, i, "JOIN"))
            {
                continue;
            }

            int sourceStart = SkipWhiteSpace(sql, i + (IsKeywordAt(sql, i, "FROM") ? 4 : 4));
            if (!TryReadIdentifier(sql, sourceStart, out int sourceEnd, out string sourceName))
            {
                continue;
            }
            // A qualified table name is a physical/schema object, not a
            // QueryDef reference in the Access catalog.
            if (sourceName.Contains('.', StringComparison.Ordinal))
            {
                continue;
            }
            string normalized = UnquoteIdentifier(sourceName);
            query = database.GetQueries().FirstOrDefault(item =>
                item.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (query == null)
            {
                continue;
            }
            match = new SourceMatch(sourceStart, sourceEnd);
            return true;
        }
        return false;
    }

    private static int SkipWhiteSpace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        return index;
    }

    private static bool TryReadIdentifier(string text, int start, out int end, out string value)
    {
        end = start;
        value = string.Empty;
        if (start >= text.Length) return false;
        char first = text[start];
        if (first == '[')
        {
            int close = text.IndexOf(']', start + 1);
            if (close < 0) return false;
            end = close + 1;
            value = text[start..end];
            return true;
        }
        if (first is '"' or '`')
        {
            int close = text.IndexOf(first, start + 1);
            if (close < 0) return false;
            end = close + 1;
            value = text[start..end];
            return true;
        }
        if (!IsIdentifierStart(first)) return false;
        end = start + 1;
        while (end < text.Length && (IsIdentifierPart(text[end]) || text[end] == '.')) end++;
        value = text[start..end];
        return true;
    }

    private static string UnquoteIdentifier(string value)
    {
        string text = value.Trim();
        if (text.Length >= 2 && text[0] == '[' && text[^1] == ']')
            return text[1..^1].Replace("]]", "]", StringComparison.Ordinal);
        if (text.Length >= 2 && (text[0] == '"' || text[0] == '`') && text[^1] == text[0])
            return text[1..^1];
        return text;
    }

    private static bool StartsWithKeyword(string text, string keyword)
        => text.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
            && (text.Length == keyword.Length || !IsIdentifierPart(text[keyword.Length]));

    private static bool IsKeywordAt(string text, int index, string keyword)
        => index >= 0 && index + keyword.Length <= text.Length
            && text.AsSpan(index, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase)
            && (index == 0 || !IsIdentifierPart(text[index - 1]))
            && (index + keyword.Length == text.Length || !IsIdentifierPart(text[index + keyword.Length]));

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

    private static int CopyQuoted(string text, int start, char quote, StringBuilder destination)
    {
        int i = start;
        destination.Append(text[i++]);
        while (i < text.Length)
        {
            char c = text[i++];
            destination.Append(c);
            if (c == quote)
            {
                if (i < text.Length && text[i] == quote)
                {
                    destination.Append(text[i++]);
                }
                else
                {
                    break;
                }
            }
        }
        return i;
    }

    private static int FindQuotedEnd(string text, int start, char quote)
    {
        int i = start + 1;
        while (i < text.Length)
        {
            if (text[i] != quote)
            {
                i++;
                continue;
            }
            if (i + 1 < text.Length && text[i + 1] == quote)
            {
                i += 2;
                continue;
            }
            return i + 1;
        }
        return text.Length;
    }

    private static int CopyBracketed(string text, int start, StringBuilder destination)
    {
        int i = start;
        destination.Append(text[i++]);
        while (i < text.Length)
        {
            char c = text[i++];
            destination.Append(c);
            if (c == ']') break;
        }
        return i;
    }

    private static int FindBracketedEnd(string text, int start)
    {
        int i = start + 1;
        while (i < text.Length)
        {
            if (text[i] != ']')
            {
                i++;
                continue;
            }
            if (i + 1 < text.Length && text[i + 1] == ']')
            {
                i += 2;
                continue;
            }
            return i + 1;
        }
        return -1;
    }

    private static bool HasQualifier(string text, int start)
    {
        int i = start - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        return i >= 0 && text[i] == '.';
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c is '_' or '#';
    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';

    private readonly record struct SourceMatch(int Start, int End);

    private struct ScanState
    {
        public int Depth;
        private char _quote;
        private bool _lineComment;
        private bool _blockComment;

        public bool Advance(string text, ref int index)
        {
            char c = text[index];
            if (_lineComment)
            {
                if (c == '\n') _lineComment = false;
                return false;
            }
            if (_blockComment)
            {
                if (c == '*' && index + 1 < text.Length && text[index + 1] == '/')
                {
                    _blockComment = false;
                    index++;
                }
                return false;
            }
            if (_quote != '\0')
            {
                if (c == _quote)
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
            if (c is '\'' or '"' or '`')
            {
                _quote = c;
                return false;
            }
            if (c == '-' && index + 1 < text.Length && text[index + 1] == '-')
            {
                _lineComment = true;
                index++;
                return false;
            }
            if (c == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                _blockComment = true;
                index++;
                return false;
            }
            if (c == '[')
            {
                int end = text.IndexOf(']', index + 1);
                if (end > index) index = end;
                return false;
            }
            if (c == '(')
            {
                Depth++;
                return true;
            }
            if (c == ')')
            {
                Depth = Math.Max(0, Depth - 1);
                return true;
            }
            return true;
        }
    }
}
