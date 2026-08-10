namespace UCanAccess.File;

/// <summary>
/// Supported Access data types (port of Jackcess <c>DataType</c>).
/// </summary>
public enum DataType
{
    Boolean = 0x01,
    Byte = 0x02,
    Int = 0x03,
    Long = 0x04,
    Money = 0x05,
    Float = 0x06,
    Double = 0x07,
    ShortDateTime = 0x08,
    Binary = 0x09,
    Text = 0x0A,
    Ole = 0x0B,
    Memo = 0x0C,
    Unknown0D = 0x0D,
    Guid = 0x0F,
    Numeric = 0x10,
    Unknown11 = 0x11,
    ComplexType = 0x12,
    BigInt = 0x13,
    ExtDateTime = 0x14,
    UnsupportedFixedLen = 0xFE,
    UnsupportedVarLen = 0xFF,
}

internal sealed record DataTypeMeta(
    DataType Type,
    string? TypeName,
    int? SqlType,
    int? FixedSize,
    bool VariableLength,
    bool LongValue,
    int MinSize,
    int DefaultSize,
    int MaxSize,
    bool HasScalePrecision,
    int MinScale,
    int DefaultScale,
    int MaxScale,
    int MinPrecision,
    int DefaultPrecision,
    int MaxPrecision,
    int UnitSize);

internal static class DataTypeInfo
{
    public static readonly DataTypeMeta Unknown = new(
        DataType.Unknown0D, null, null, null, true, false, 0, 0, 0x3FFFFFFF, false, 0, 0, 0, 0, 0, 0, 1);

    private static readonly Dictionary<DataType, DataTypeMeta> All = new()
    {
        [DataType.Boolean] = new(DataType.Boolean, "Bit", 16, 1, false, false, 0, 0, 0, false, 0, 0, 0, 0, 0, 0, 1),
        [DataType.Byte] = new(DataType.Byte, "Byte", -6, 1, false, false, 0, 0, 0, false, 0, 0, 0, 0, 0, 0, 1),
        [DataType.Int] = new(DataType.Int, "Short", 5, 2, false, false, 0, 0, 0, false, 0, 0, 0, 0, 0, 0, 1),
        [DataType.Long] = new(DataType.Long, "Long", 4, 4, false, false, 0, 0, 0, false, 0, 0, 0, 0, 0, 0, 1),
        [DataType.Money] = new(DataType.Money, "Currency", 3, 8, false, false, 0, 0, 0, false, 0, 0, 0, 0, 0, 0, 1),
        [DataType.Float] = new(DataType.Float, "IEEESingle", 6, 4, false, false, 0, 0, 0, false, 0, 0, 0, 0, 0, 0, 1),
        [DataType.Double] = new(DataType.Double, "IEEEDouble", 8, 8, false, false, 0, 0, 0, false, 0, 0, 0, 0, 0, 0, 1),
        [DataType.ShortDateTime] = new(DataType.ShortDateTime, "DateTime", 93, 8, false, false, 0, 0, 0, false, 0, 0, 0, 0, 0, 0, 1),
        [DataType.Binary] = new(DataType.Binary, "Binary", -2, null, true, false, 0, 255, 255, false, 0, 0, 0, 0, 0, 0, 1),
        [DataType.Text] = new(DataType.Text, "Text", 12, null, true, false, 0, 255, 255, false, 0, 0, 0, 0, 0, 0, 2),
        [DataType.Ole] = new(DataType.Ole, "LongBinary", -4, null, true, true, 0, 0, 0x3FFFFFFF, false, 0, 0, 0, 0, 0, 0, 1),
        [DataType.Memo] = new(DataType.Memo, null, -1, null, true, true, 0, 0, 0x3FFFFFFF, false, 0, 0, 0, 0, 0, 0, 2),
        [DataType.Unknown0D] = new(DataType.Unknown0D, null, null, null, true, false, 0, 255, 255, false, 0, 0, 0, 0, 0, 0, 1),
        [DataType.Guid] = new(DataType.Guid, "Guid", null, 16, false, false, 0, 0, 0, false, 0, 0, 0, 0, 0, 0, 1),
        [DataType.Numeric] = new(DataType.Numeric, null, 2, 17, true, false, 17, 17, 17, true, 0, 0, 28, 1, 18, 28, 1),
        [DataType.Unknown11] = new(DataType.Unknown11, "TYPENAME", null, 3992, false, false, 0, 0, 0, false, 0, 0, 0, 0, 0, 0, 1),
        [DataType.ComplexType] = new(DataType.ComplexType, "TYPENAME", null, 4, false, false, 0, 0, 0, false, 0, 0, 0, 0, 0, 0, 1),
        [DataType.BigInt] = new(DataType.BigInt, "TYPENAME", -5, 8, false, false, 0, 0, 0, false, 0, 0, 0, 0, 0, 0, 1),
        [DataType.ExtDateTime] = new(DataType.ExtDateTime, "TYPENAME", null, 42, false, false, 0, 0, 0, false, 0, 0, 0, 0, 0, 0, 1),
    };

    public static DataTypeMeta Get(DataType type)
    {
        if (All.TryGetValue(type, out var meta))
        {
            return meta;
        }
        return Unknown;
    }

    /// <summary>
    /// Returns the Access type name for a query-parameter flag value (e.g. "Long", "Text").
    /// </summary>
    public static string? GetTypeName(short value)
    {
        if (value == 0)
        {
            return "Value";
        }
        return Get((DataType)value).TypeName;
    }

    public static DataType FromByte(byte value) => (DataType)value;

    /// <summary>whether the type stores its value as a fixed number of bytes</summary>
    public static bool IsVariableLength(DataType type) => Get(type).VariableLength;

    public static bool IsTrueVariableLength(DataType type)
        => Get(type).VariableLength && Get(type).MinSize != Get(type).MaxSize;

    public static bool IsLongValue(DataType type) => Get(type).LongValue;

    public static bool IsTextual(DataType type) => type == DataType.Text || type == DataType.Memo;

    public static bool GetHasScalePrecision(DataType type) => Get(type).HasScalePrecision;
}
