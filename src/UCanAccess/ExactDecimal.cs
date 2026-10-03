using System.Globalization;
using System.Numerics;
using Microsoft.Data.Sqlite;

namespace UCanAccess;

/// <summary>
/// Exact base-10 value used by the Access MONEY/NUMERIC mirror path.
/// SQLite has no decimal storage class, so values are kept as invariant text
/// and all decimal operations are performed here instead of through double.
/// </summary>
internal readonly struct ExactDecimal : IComparable<ExactDecimal>
{
    public ExactDecimal(BigInteger unscaled, int scale)
    {
        if (scale < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        while (scale > 0 && unscaled != 0 && unscaled % 10 == 0)
        {
            unscaled /= 10;
            scale--;
        }

        Unscaled = unscaled;
        Scale = scale;
    }

    public BigInteger Unscaled { get; }
    public int Scale { get; }

    // Cached powers of ten: every rescale/add/compare/divide needs 10^n.
    // Precomputed once; grown under lock on the rare overshoot path.
    private static BigInteger[] _pow10 = InitPow10(64);
    private static readonly object Pow10Lock = new();

    private static BigInteger[] InitPow10(int count)
    {
        var table = new BigInteger[count];
        BigInteger value = BigInteger.One;
        for (int i = 0; i < count; i++)
        {
            table[i] = value;
            value *= 10;
        }
        return table;
    }

    internal static BigInteger Pow10(int exponent)
    {
        BigInteger[] table = _pow10;
        if ((uint)exponent < (uint)table.Length)
        {
            return table[exponent];
        }
        lock (Pow10Lock)
        {
            table = _pow10;
            if (exponent < table.Length)
            {
                return table[exponent];
            }
            var grown = new BigInteger[Math.Max(exponent + 1, table.Length * 2)];
            Array.Copy(table, grown, table.Length);
            BigInteger value = table[table.Length - 1] * 10;
            for (int i = table.Length; i < grown.Length; i++)
            {
                grown[i] = value;
                value *= 10;
            }
            _pow10 = grown;
            return grown[exponent];
        }
    }

    public static ExactDecimal Parse(object? value)
    {
        if (value is null or DBNull)
        {
            throw new FormatException("NULL is not a decimal value.");
        }

        return value switch
        {
            ExactDecimal exact => exact,
            decimal number => FromDecimal(number),
            BigInteger number => new ExactDecimal(number, 0),
            byte number => new ExactDecimal(number, 0),
            sbyte number => new ExactDecimal(number, 0),
            short number => new ExactDecimal(number, 0),
            ushort number => new ExactDecimal(number, 0),
            int number => new ExactDecimal(number, 0),
            uint number => new ExactDecimal(number, 0),
            long number => new ExactDecimal(number, 0),
            ulong number => new ExactDecimal(number, 0),
            float number => Parse(number.ToString("R", CultureInfo.InvariantCulture)),
            double number => Parse(number.ToString("R", CultureInfo.InvariantCulture)),
            string text => Parse(text),
            _ => Parse(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0"),
        };
    }

    private enum DecimalError { None, Empty, Exponent, Invalid }

    public static ExactDecimal Parse(string text)
    {
        if (TryParseSpan(text.AsSpan(), out ExactDecimal result, out DecimalError error))
        {
            return result;
        }
        throw error switch
        {
            DecimalError.Empty => new FormatException("Empty decimal value."),
            DecimalError.Exponent => new FormatException($"Invalid decimal exponent '{text}'."),
            _ => new FormatException($"Invalid decimal value '{text}'."),
        };
    }

    private static bool TryParseSpan(ReadOnlySpan<char> text, out ExactDecimal result, out DecimalError error)
    {
        result = default;
        error = DecimalError.Invalid;
        ReadOnlySpan<char> value = text.Trim();
        if (value.IsEmpty)
        {
            error = DecimalError.Empty;
            return false;
        }

        int exponent = 0;
        int exponentIndex = value.IndexOfAny('e', 'E');
        if (exponentIndex >= 0)
        {
            if (!int.TryParse(value[(exponentIndex + 1)..], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out exponent))
            {
                error = DecimalError.Exponent;
                return false;
            }
            value = value[..exponentIndex];
        }

        bool negative = false;
        if (!value.IsEmpty && (value[0] == '-' || value[0] == '+'))
        {
            negative = value[0] == '-';
            value = value[1..];
        }

        int dot = value.IndexOf('.');
        int scale = dot < 0 ? 0 : value.Length - dot - 1;
        int digitCount = value.Length - (dot < 0 ? 0 : 1);
        if (digitCount == 0)
        {
            return false;
        }

        // Copy the digits around the dot into one contiguous buffer without
        // allocating intermediate strings (typical values fit on the stack).
        Span<char> buffer = digitCount <= 64 ? stackalloc char[64] : new char[digitCount];
        int pos = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '.')
            {
                continue;
            }
            if (c is < '0' or > '9')
            {
                return false;
            }
            buffer[pos++] = c;
        }

        BigInteger unscaled = BigInteger.Parse(buffer[..pos], NumberStyles.None, CultureInfo.InvariantCulture);
        if (negative)
        {
            unscaled = -unscaled;
        }

        if (exponent > 0)
        {
            unscaled *= Pow10(exponent);
            scale = Math.Max(0, scale - exponent);
        }
        else if (exponent < 0)
        {
            scale += -exponent;
        }

        result = new ExactDecimal(unscaled, scale);
        error = DecimalError.None;
        return true;
    }

    public static ExactDecimal FromDecimal(decimal value)
    {
        int[] bits = decimal.GetBits(value);
        BigInteger unscaled = (uint)bits[0]
            | ((BigInteger)(uint)bits[1] << 32)
            | ((BigInteger)(uint)bits[2] << 64);
        if ((bits[3] & int.MinValue) != 0)
        {
            unscaled = -unscaled;
        }
        return new ExactDecimal(unscaled, (bits[3] >> 16) & 0x7F);
    }

    public ExactDecimal Rescale(int scale)
    {
        if (scale < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }
        if (scale == Scale)
        {
            return this;
        }
        if (scale > Scale)
        {
            return new ExactDecimal(Unscaled * Pow10(scale - Scale), scale);
        }

        BigInteger divisor = Pow10(Scale - scale);
        BigInteger quotient = BigInteger.DivRem(Unscaled, divisor, out BigInteger remainder);
        BigInteger magnitude = BigInteger.Abs(remainder);
        if (magnitude * 2 >= divisor)
        {
            quotient += Unscaled.Sign < 0 ? -1 : 1;
        }
        return new ExactDecimal(quotient, scale);
    }

    public static ExactDecimal Add(ExactDecimal left, ExactDecimal right)
    {
        // Same-scale fast path: MONEY columns are always scale 4, so decimal
        // addition usually needs no rescaling at all.
        if (left.Scale == right.Scale)
        {
            return new ExactDecimal(left.Unscaled + right.Unscaled, left.Scale);
        }
        int scale = Math.Max(left.Scale, right.Scale);
        return new ExactDecimal(left.Unscaled * Pow10(scale - left.Scale)
            + right.Unscaled * Pow10(scale - right.Scale), scale);
    }

    public static ExactDecimal Subtract(ExactDecimal left, ExactDecimal right)
        => Add(left, new ExactDecimal(-right.Unscaled, right.Scale));

    public static ExactDecimal Multiply(ExactDecimal left, ExactDecimal right)
        => new(left.Unscaled * right.Unscaled, left.Scale + right.Scale);

    public static ExactDecimal Divide(ExactDecimal left, ExactDecimal right, int resultScale = 28)
    {
        if (right.Unscaled == 0)
        {
            throw new DivideByZeroException("Decimal division by zero.");
        }

        int scale = Math.Max(0, resultScale + right.Scale - left.Scale);
        BigInteger numerator = left.Unscaled * Pow10(scale);
        BigInteger quotient = BigInteger.DivRem(numerator, right.Unscaled, out BigInteger remainder);
        BigInteger divisor = BigInteger.Abs(right.Unscaled);
        if (BigInteger.Abs(remainder) * 2 >= divisor)
        {
            quotient += numerator.Sign * right.Unscaled.Sign < 0 ? -1 : 1;
        }
        return new ExactDecimal(quotient, resultScale);
    }

    public int CompareTo(ExactDecimal other)
    {
        int scale = Math.Max(Scale, other.Scale);
        BigInteger left = Unscaled * Pow10(scale - Scale);
        BigInteger right = other.Unscaled * Pow10(scale - other.Scale);
        return left.CompareTo(right);
    }

    public string ToFixedString(int scale)
    {
        if (scale < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        BigInteger unscaled = Unscaled;
        if (scale > Scale)
        {
            unscaled *= Pow10(scale - Scale);
        }
        else if (scale < Scale)
        {
            BigInteger divisor = Pow10(Scale - scale);
            BigInteger quotient = BigInteger.DivRem(unscaled, divisor, out BigInteger remainder);
            if (BigInteger.Abs(remainder) * 2 >= divisor)
            {
                quotient += unscaled.Sign < 0 ? -1 : 1;
            }
            unscaled = quotient;
        }

        BigInteger magnitude = BigInteger.Abs(unscaled);
        string digits = magnitude.ToString(CultureInfo.InvariantCulture);
        bool negative = unscaled < 0;
        int intDigits = digits.Length - scale;
        int intLen = Math.Max(intDigits, 1);
        int length = (negative ? 1 : 0) + intLen + (scale == 0 ? 0 : 1 + scale);
        return string.Create(length, (digits, scale, intDigits, intLen, negative), static (span, state) =>
        {
            int pos = 0;
            if (state.negative)
            {
                span[pos++] = '-';
            }
            if (state.intDigits <= 0)
            {
                span[pos++] = '0';
            }
            else
            {
                state.digits.AsSpan(0, state.intDigits).CopyTo(span[pos..]);
                pos += state.intDigits;
            }
            if (state.scale > 0)
            {
                span[pos++] = '.';
                ReadOnlySpan<char> frac = state.intDigits <= 0
                    ? state.digits.AsSpan()
                    : state.digits.AsSpan(state.intDigits);
                for (int i = frac.Length; i < state.scale; i++)
                {
                    span[pos++] = '0';
                }
                frac.CopyTo(span[pos..]);
            }
        });
    }

    public override string ToString()
    {
        if (Scale == 0)
        {
            return Unscaled.ToString(CultureInfo.InvariantCulture);
        }
        return ToFixedString(Scale).TrimEnd('0').TrimEnd('.');
    }

    internal static bool TryParse(object? value, out ExactDecimal result)
    {
        // No exceptions for control flow: invalid inputs return false (except
        // null/DBNull, which historically throw from Parse and keep doing so).
        switch (value)
        {
            case null or DBNull:
                result = Parse(value);
                return true;
            case ExactDecimal exact:
                result = exact;
                return true;
            case decimal number:
                result = FromDecimal(number);
                return true;
            case BigInteger number:
                result = new ExactDecimal(number, 0);
                return true;
            case byte number:
                result = new ExactDecimal(number, 0);
                return true;
            case sbyte number:
                result = new ExactDecimal(number, 0);
                return true;
            case short number:
                result = new ExactDecimal(number, 0);
                return true;
            case ushort number:
                result = new ExactDecimal(number, 0);
                return true;
            case int number:
                result = new ExactDecimal(number, 0);
                return true;
            case uint number:
                result = new ExactDecimal(number, 0);
                return true;
            case long number:
                result = new ExactDecimal(number, 0);
                return true;
            case ulong number:
                result = new ExactDecimal(number, 0);
                return true;
            case float number:
                return TryParseSpan(number.ToString("R", CultureInfo.InvariantCulture).AsSpan(),
                    out result, out _);
            case double number:
                return TryParseSpan(number.ToString("R", CultureInfo.InvariantCulture).AsSpan(),
                    out result, out _);
            case string text:
                return TryParseSpan(text.AsSpan(), out result, out _);
            default:
                string fallback = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0";
                return TryParseSpan(fallback.AsSpan(), out result, out _);
        }
    }
}

internal static class ExactDecimalSql
{
    public const string CollationName = "UCA_DECIMAL";

    public static void Register(SqliteConnection connection)
    {
        connection.CreateCollation(CollationName, CompareText);
        RegisterBinary(connection, "uca_decimal_add", ExactDecimal.Add);
        RegisterBinary(connection, "uca_decimal_subtract", ExactDecimal.Subtract);
        RegisterBinary(connection, "uca_decimal_multiply", ExactDecimal.Multiply);
        RegisterBinary(connection, "uca_decimal_divide", (left, right) => ExactDecimal.Divide(left, right));
        connection.CreateFunction<object?, object?, object?>("uca_decimal_cmp", Compare, true);
        connection.CreateFunction<object?, object?, object?>("uca_decimal_eq", CompareBoolean((c) => c == 0), true);
        connection.CreateFunction<object?, object?, object?>("uca_decimal_ne", CompareBoolean((c) => c != 0), true);
        connection.CreateFunction<object?, object?, object?>("uca_decimal_lt", CompareBoolean((c) => c < 0), true);
        connection.CreateFunction<object?, object?, object?>("uca_decimal_le", CompareBoolean((c) => c <= 0), true);
        connection.CreateFunction<object?, object?, object?>("uca_decimal_gt", CompareBoolean((c) => c > 0), true);
        connection.CreateFunction<object?, object?, object?>("uca_decimal_ge", CompareBoolean((c) => c >= 0), true);

        connection.CreateAggregate<AggregateState, object?>("uca_decimal_sum", new AggregateState(), Step,
            state => state?.Count > 0 ? state.Sum.ToString() : null, true);
        connection.CreateAggregate<AggregateState, object?>("uca_decimal_avg", new AggregateState(), Step,
            state => state?.Count > 0 ? ExactDecimal.Divide(state.Sum, new ExactDecimal(state.Count, 0)).ToString() : null, true);
        connection.CreateAggregate<MinMaxState, object?>("uca_decimal_min", new MinMaxState(), MinStep,
            state => state?.Value?.ToString(), true);
        connection.CreateAggregate<MinMaxState, object?>("uca_decimal_max", new MinMaxState(), MaxStep,
            state => state?.Value?.ToString(), true);
    }

    private static void RegisterBinary(SqliteConnection connection, string name,
        Func<ExactDecimal, ExactDecimal, ExactDecimal> operation)
    {
        connection.CreateFunction<object?, object?, object?>(name, (left, right) =>
        {
            if (left is null or DBNull || right is null or DBNull)
            {
                return null;
            }
            return operation(ExactDecimal.Parse(left), ExactDecimal.Parse(right)).ToString();
        }, true);
    }

    private static object? Compare(object? left, object? right)
    {
        if (left is null or DBNull || right is null or DBNull)
        {
            return null;
        }
        return (long)ExactDecimal.Parse(left).CompareTo(ExactDecimal.Parse(right));
    }

    private static Func<object?, object?, object?> CompareBoolean(Func<int, bool> predicate)
        => (left, right) =>
        {
            object? result = Compare(left, right);
            return result is null ? null : predicate(Convert.ToInt32(result, CultureInfo.InvariantCulture)) ? 1L : 0L;
        };

    private static int CompareText(string? left, string? right)
    {
        if (left == null || right == null)
        {
            return left == right ? 0 : left == null ? -1 : 1;
        }
        if (ExactDecimal.TryParse(left, out ExactDecimal l) && ExactDecimal.TryParse(right, out ExactDecimal r))
        {
            return l.CompareTo(r);
        }
        return string.Compare(left, right, StringComparison.Ordinal);
    }

    internal static int CompareTextForCollation(string? left, string? right)
        => CompareText(left, right);

    private sealed class AggregateState
    {
        public ExactDecimal Sum;
        public long Count;
    }

    private static AggregateState Step(AggregateState? state, object?[] args)
    {
        var next = new AggregateState();
        if (state != null)
        {
            next.Sum = state.Sum;
            next.Count = state.Count;
        }
        foreach (object? arg in args)
        {
            if (arg is null or DBNull)
            {
                continue;
            }
            ExactDecimal value = ExactDecimal.Parse(arg);
            next.Sum = next.Count == 0 ? value : ExactDecimal.Add(next.Sum, value);
            next.Count++;
        }
        return next;
    }

    private sealed class MinMaxState
    {
        public ExactDecimal? Value;
    }

    private static MinMaxState MinStep(MinMaxState? state, object?[] args)
        => MinMaxStep(state, args, min: true);

    private static MinMaxState MaxStep(MinMaxState? state, object?[] args)
        => MinMaxStep(state, args, min: false);

    private static MinMaxState MinMaxStep(MinMaxState? state, object?[] args, bool min)
    {
        var next = new MinMaxState { Value = state?.Value };
        foreach (object? arg in args)
        {
            if (arg is null or DBNull)
            {
                continue;
            }
            ExactDecimal value = ExactDecimal.Parse(arg);
            if (next.Value == null || (value.CompareTo(next.Value.Value) < 0) == min)
            {
                next.Value = value;
            }
        }
        return next;
    }
}
