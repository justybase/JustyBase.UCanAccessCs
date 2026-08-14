using System.Globalization;

namespace UCanAccess.File;

/// <summary>
/// Evaluates the small, portable subset of Access default expressions which can
/// be stored without bringing the SQL mirror into the file layer.  More complex
/// expressions are rejected at insert time rather than written with a different
/// meaning.
/// </summary>
internal static class DefaultValueEvaluator
{
    internal static object? Evaluate(string expression)
    {
        string value = expression.Trim();
        while (HasWrappingParentheses(value))
        {
            value = value[1..^1].Trim();
        }
        if (value.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return null;
        if (value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
            || value.Equals("YES", StringComparison.OrdinalIgnoreCase)
            || value.Equals("ON", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Equals("FALSE", StringComparison.OrdinalIgnoreCase)
            || value.Equals("NO", StringComparison.OrdinalIgnoreCase)
            || value.Equals("OFF", StringComparison.OrdinalIgnoreCase)) return false;
        if (value.Equals("NOW", StringComparison.OrdinalIgnoreCase)
            || value.Equals("NOW()", StringComparison.OrdinalIgnoreCase)) return DateTime.Now;
        if (value.Equals("DATE", StringComparison.OrdinalIgnoreCase)
            || value.Equals("DATE()", StringComparison.OrdinalIgnoreCase)) return DateTime.Today;

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        }
        if (value.Length >= 2 && value[0] == '#' && value[^1] == '#'
            && DateTime.TryParse(value[1..^1], CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out DateTime date))
        {
            return date;
        }
        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number))
        {
            return number;
        }

        throw new NotSupportedException(
            $"Default expression '{expression}' is not supported by the managed file writer.");
    }

    private static bool HasWrappingParentheses(string value)
    {
        if (value.Length < 2 || value[0] != '(' || value[^1] != ')')
        {
            return false;
        }
        int depth = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '(')
            {
                depth++;
            }
            else if (value[i] == ')')
            {
                depth--;
                if (depth == 0 && i != value.Length - 1)
                {
                    return false;
                }
            }
        }
        return depth == 0;
    }
}
