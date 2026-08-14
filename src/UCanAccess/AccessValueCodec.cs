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
            decimal number => ExactDecimal.FromDecimal(number).ToString(),
            float number => number.ToString("R", CultureInfo.InvariantCulture),
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            DateTime dateTime => FormatDate(dateTime),
            DateTimeOffset dateTimeOffset => FormatDate(dateTimeOffset.DateTime),
            Guid guid => guid.ToString("D"),
            _ => value,
        };

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
        if (DateTime.TryParseExact(value.ToString(), DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out DateTime parsed))
        {
            return DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        }
        throw new FormatException($"Value '{value}' is not a valid Access date.");
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
