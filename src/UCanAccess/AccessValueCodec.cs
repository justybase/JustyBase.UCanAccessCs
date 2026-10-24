using System.Globalization;
using System.Text;
using UCanAccess.File;

namespace UCanAccess;

/// <summary>
/// Centralizes conversion between Access values, CLR values and the SQLite
/// representation used by the mirror. Keeping this policy in one place avoids
/// subtle differences between loading, parameters and reader getters.
/// </summary>
internal static class AccessValueCodec
{
    private const string DateFormat = "yyyy-MM-dd HH:mm:ss.fff";
    private const string ExtendedDateFormat = "yyyy-MM-dd HH:mm:ss.fffffff";

    public static object? ToSqlite(object? value, Column? column = null)
        => value switch
        {
            null or DBNull => DBNull.Value,
            AccessSingleValue[] or AccessAttachment[] or AccessVersion[] when column?.Type == DataType.ComplexType
                => ComplexValueJson.Serialize(value),
            decimal number when column?.Type == DataType.Money
                => ExactDecimal.FromDecimal(number).ToFixedString(4),
            decimal number when column?.Type == DataType.Numeric
                => ExactDecimal.FromDecimal(number).ToFixedString(column!.Scale),
            decimal number => ExactDecimal.FromDecimal(number).ToString(),
            ExactDecimal exact when column?.Type == DataType.Money => exact.ToFixedString(4),
            ExactDecimal exact when column?.Type == DataType.Numeric => exact.ToFixedString(column!.Scale),
            ExactDecimal exact => exact.ToString(),
            bool boolean => boolean ? 1L : 0L,
            byte number => (long)number,
            sbyte number => (long)number,
            short number => (long)number,
            ushort number => (long)number,
            int number => (long)number,
            uint number => (long)number,
            long number => number,
            ulong number when number <= long.MaxValue => (long)number,
            float number => number.ToString("R", CultureInfo.InvariantCulture),
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            DateTime dateTime => FormatDate(dateTime, column?.Type == DataType.ExtDateTime),
            DateTimeOffset dateTimeOffset => FormatDate(dateTimeOffset.DateTime, column?.Type == DataType.ExtDateTime),
            Guid guid => guid.ToString("D"),
            string text when column?.Type == DataType.Money && ExactDecimal.TryParse(text, out ExactDecimal money)
                => money.ToFixedString(4),
            string text when column?.Type == DataType.Numeric && ExactDecimal.TryParse(text, out ExactDecimal numeric)
                => numeric.ToFixedString(column!.Scale),
            string text => text,
            byte[] bytes => bytes,
            _ => value.ToString(),
        };

    public static object? ToSqliteParameter(object? value)
        => value switch
        {
            null or DBNull => DBNull.Value,
            AccessSingleValue[] or AccessAttachment[] or AccessVersion[] => ComplexValueJson.Serialize(value),
            // Single complex values and generic collections (e.g. List<AccessSingleValue>)
            // normalize to the same JSON envelope as the array forms.
            AccessSingleValue single => ComplexValueJson.Serialize(new[] { single }),
            AccessAttachment attachment => ComplexValueJson.Serialize(new[] { attachment }),
            AccessVersion version => ComplexValueJson.Serialize(new[] { version }),
            System.Collections.IEnumerable enumerable and not string and not byte[]
                when SerializeComplexEnumerable(enumerable) is string json => json,
            decimal number => ExactDecimal.FromDecimal(number).ToString(),
            float number => number.ToString("R", CultureInfo.InvariantCulture),
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            DateTime dateTime => FormatDate(dateTime),
            DateTimeOffset dateTimeOffset => FormatDate(dateTimeOffset.DateTime),
            Guid guid => guid.ToString("D"),
            _ => value,
        };

    private static string? SerializeComplexEnumerable(System.Collections.IEnumerable enumerable)
    {
        List<AccessSingleValue>? singles = null;
        List<AccessAttachment>? attachments = null;
        List<AccessVersion>? versions = null;
        foreach (object? item in enumerable)
        {
            switch (item)
            {
                case AccessSingleValue single when attachments == null && versions == null:
                    singles ??= new List<AccessSingleValue>();
                    singles.Add(single);
                    break;
                case AccessAttachment attachment when singles == null && versions == null:
                    attachments ??= new List<AccessAttachment>();
                    attachments.Add(attachment);
                    break;
                case AccessVersion version when singles == null && attachments == null:
                    versions ??= new List<AccessVersion>();
                    versions.Add(version);
                    break;
                default:
                    return null;
            }
        }
        if (singles != null)
        {
            return ComplexValueJson.Serialize(singles.ToArray());
        }
        if (attachments != null)
        {
            return ComplexValueJson.Serialize(attachments.ToArray());
        }
        if (versions != null)
        {
            return ComplexValueJson.Serialize(versions.ToArray());
        }
        return null;
    }

    public static object? CoerceForColumn(Column column, object? value)
    {
        if (value == null || value is DBNull)
        {
            return null;
        }
        CultureInfo invariant = CultureInfo.InvariantCulture;
        return column.Type switch
        {
            DataType.Byte => Convert.ToByte(value, invariant),
            DataType.Int => Convert.ToInt16(value, invariant),
            DataType.Long => Convert.ToInt32(value, invariant),
            DataType.BigInt => Convert.ToInt64(value, invariant),
            DataType.Money or DataType.Numeric => Convert.ToDecimal(value, invariant),
            DataType.Float => Convert.ToSingle(value, invariant),
            DataType.Double => Convert.ToDouble(value, invariant),
            DataType.ShortDateTime or DataType.ExtDateTime => ParseDate(value),
            DataType.Boolean => value switch
            {
                bool boolean => boolean,
                string text => text.Equals("true", StringComparison.OrdinalIgnoreCase) || text is "1" or "-1",
                _ => Convert.ToBoolean(value, invariant),
            },
            DataType.Guid => value is Guid guid ? guid.ToString("D") : value.ToString(),
            DataType.Text or DataType.Memo => value.ToString(),
            DataType.Binary or DataType.Ole => value is byte[] bytes
                ? bytes
                : Encoding.UTF8.GetBytes(value.ToString() ?? string.Empty),
            _ => value,
        };
    }

    public static object ConvertFromSqlite(object value, DataType? type, bool exactDecimalProjection = false)
    {
        if (exactDecimalProjection)
        {
            return decimal.Parse(value.ToString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        return type switch
        {
            DataType.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            DataType.Byte => Convert.ToByte(value, CultureInfo.InvariantCulture),
            DataType.Int => Convert.ToInt16(value, CultureInfo.InvariantCulture),
            DataType.Long or DataType.BigInt => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            DataType.Float => Convert.ToSingle(value, CultureInfo.InvariantCulture),
            DataType.Double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            DataType.Money or DataType.Numeric => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            DataType.ComplexType when value is string json => ComplexValueJson.Deserialize(json) ?? DBNull.Value,
            DataType.ShortDateTime or DataType.ExtDateTime => ParseDate(value),
            DataType.Guid when value is string text => Guid.Parse(text),
            _ => value,
        };
    }

    public static DateTime ParseDate(object value)
    {
        if (value is DateTime dateTime)
        {
            return DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified);
        }
        if (value is string text && TryParseMirrorDate(text.AsSpan(), out DateTime fast))
        {
            return fast;
        }
        if (DateTime.TryParseExact(value.ToString(), DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out DateTime parsed))
        {
            return DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        }
        throw new FormatException($"Value '{value}' is not a valid Access date.");
    }

    /// <summary>
    /// Parses the canonical mirror representation written by
    /// <see cref="FormatDate(DateTime)"/> — <c>yyyy-MM-dd HH:mm:ss[.fffffff]</c>.
    /// The mirror writes every date column in that shape, so the common case
    /// avoids the culture-aware format-list search (ICU-driven and several
    /// times slower); anything else falls back to <see cref="DateFormats"/>.
    /// </summary>
    private static bool TryParseMirrorDate(ReadOnlySpan<char> text, out DateTime value)
    {
        value = default;
        if (text.Length is < 19 or > 27)
        {
            return false;
        }
        if (text[4] != '-' || text[7] != '-' || text[10] != ' ' || text[13] != ':' || text[16] != ':')
        {
            return false;
        }
        if (!TryReadNumber(text, 0, 4, out int year)
            || !TryReadNumber(text, 5, 2, out int month)
            || !TryReadNumber(text, 8, 2, out int day)
            || !TryReadNumber(text, 11, 2, out int hour)
            || !TryReadNumber(text, 14, 2, out int minute)
            || !TryReadNumber(text, 17, 2, out int second))
        {
            return false;
        }
        long fractionTicks = 0;
        if (text.Length > 19)
        {
            if (text[19] != '.')
            {
                return false;
            }
            int digits = text.Length - 20;
            if (digits is < 1 or > 7 || !TryReadNumber(text, 20, digits, out int fraction))
            {
                return false;
            }
            // .f, .ff, ... .fffffff all describe the same fraction of a second.
            for (int i = digits; i < 7; i++)
            {
                fraction *= 10;
            }
            fractionTicks = fraction;
        }
        if (year == 0 || month is 0 or > 12 || day == 0 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }
        value = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified)
            .AddTicks(fractionTicks);
        return true;
    }

    private static bool TryReadNumber(ReadOnlySpan<char> text, int start, int length, out int result)
    {
        int value = 0;
        for (int i = start; i < start + length; i++)
        {
            int digit = text[i] - '0';
            if ((uint)digit > 9)
            {
                result = 0;
                return false;
            }
            value = (value * 10) + digit;
        }
        result = value;
        return true;
    }

    public static string FormatDate(DateTime value)
        => FormatDate(value, extended: false);

    private static string FormatDate(DateTime value, bool extended)
        => DateTime.SpecifyKind(value, DateTimeKind.Unspecified)
            .ToString(extended ? ExtendedDateFormat : DateFormat, CultureInfo.InvariantCulture);

    private static readonly string[] DateFormats =
    {
        ExtendedDateFormat,
        DateFormat,
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd",
        "M/d/yyyy H:mm:ss",
        "M/d/yyyy h:mm:ss tt",
        "M/d/yyyy h:mm tt",
        "M/d/yyyy H:mm",
        "M/d/yyyy",
        "d/M/yyyy H:mm:ss",
        "d/M/yyyy",
        "O",
        "s",
    };
}
