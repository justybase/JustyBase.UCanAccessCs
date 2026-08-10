using JustyBase.NetezzaSqlParser.Dialects;
using JustyBase.NetezzaSqlParser.Lexer;
using Superpower.Model;

namespace UCanAccess;

/// <summary>
/// Adapter from the JustyBase Access lexer (Superpower token stream) to the small
/// token model used by <see cref="AccessSqlTranslator"/>.
/// </summary>
internal static class AccessTokenizer
{
    internal enum Kind
    {
        /// <summary>a bare word (identifier or keyword)</summary>
        Word,
        /// <summary>a single- or double-quoted string literal</summary>
        Str,
        /// <summary>a numeric literal</summary>
        Number,
        /// <summary>an Access date literal (#...#)</summary>
        Date,
        /// <summary>a bracketed or backticked identifier</summary>
        Ident,
        /// <summary>punctuation / operator</summary>
        Symbol,
    }

    internal readonly record struct Token(Kind Kind, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>
    /// Tokenizes Access SQL using the JustyBase AccessLexer and converts the
    /// resulting <see cref="Token{NzToken}"/> stream to the internal token model.
    /// </summary>
    public static List<Token> Tokenize(string sql)
    {
        TokenList<NzToken> tokens = DialectRuntime.Tokenize(sql, SqlDialect.Access);
        var result = new List<Token>();
        foreach (Token<NzToken> t in tokens)
        {
            Token? converted = Convert(t);
            if (converted != null)
            {
                result.Add(converted.Value);
            }
        }
        return result;
    }

    private static readonly HashSet<NzToken> SymbolKinds = new()
    {
        NzToken.NotEquals, NzToken.LessThanEquals, NzToken.GreaterThanEquals,
        NzToken.Concat, NzToken.DoubleColon, NzToken.Assign,
        NzToken.EqualsOp, NzToken.LessThan, NzToken.GreaterThan,
        NzToken.Plus, NzToken.Minus, NzToken.Multiply, NzToken.Divide,
        NzToken.Modulo, NzToken.Caret,
        NzToken.Dot, NzToken.Comma, NzToken.Semicolon,
        NzToken.LParen, NzToken.RParen, NzToken.LBracket, NzToken.RBracket,
        NzToken.AccessAmpersand, NzToken.Parameter,
    };

    private static Token? Convert(Token<NzToken> token)
    {
        string text = token.ToStringValue();
        switch (token.Kind)
        {
            case NzToken.AccessBracketedIdentifier:
                return new Token(Kind.Ident, StripDelims(text, '[', ']'));
            case NzToken.AccessBacktickIdentifier:
            case NzToken.MySqlBacktickIdentifier:
                return new Token(Kind.Ident, StripDelims(text, '`', '`'));
            case NzToken.MssqlBracketedIdentifier:
                return new Token(Kind.Ident, StripDelims(text, '[', ']'));
            case NzToken.QuotedIdentifier:
                return new Token(Kind.Ident, StripDelims(text, '"', '"'));
            case NzToken.StringLiteral:
                return new Token(Kind.Str, UnescapeString(text));
            case NzToken.AccessDateLiteral:
                return new Token(Kind.Date, StripDelims(text, '#', '#'));
            case NzToken.NumberLiteral:
                return new Token(Kind.Number, text);
            case NzToken.WhiteSpace:
            case NzToken.LineComment:
            case NzToken.BlockComment:
                return null;
            default:
                return new Token(SymbolKinds.Contains(token.Kind) ? Kind.Symbol : Kind.Word, text);
        }
    }

    /// <summary>
    /// Strips the surrounding delimiters of a quoted token (brackets, backticks, double quotes).
    /// </summary>
    private static string StripDelims(string text, char open, char close)
        => text.Length >= 2 && text[0] == open && text[^1] == close ? text[1..^1] : text;

    /// <summary>
    /// Unescapes a string literal token ('...' or "..."), removing the surrounding quotes.
    /// </summary>
    private static string UnescapeString(string text)
    {
        if (text.Length < 2)
        {
            return text;
        }
        char quote = text[0];
        string inner = text[1..^1];
        return quote switch
        {
            '\'' => inner.Replace("''", "'"),
            '"' => inner.Replace("\"\"", "\""),
            _ => inner,
        };
    }
}
