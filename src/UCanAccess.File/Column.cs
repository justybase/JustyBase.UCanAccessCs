using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace UCanAccess.File;

/// <summary>
/// A column of a table, including the logic to decode its values
/// (port of Jackcess <c>ColumnImpl</c> / <c>TextColumnImpl</c> / <c>LongValueColumnImpl</c>).
/// </summary>
public sealed class Column
{
    private const byte FixedLenFlagMask = 0x01;
    private const byte AutoNumberFlagMask = 0x04;
    private const byte AutoNumberGuidFlagMask = 0x40;
    private const byte HyperlinkFlagMask = 0x80;
    private const byte CompressedUnicodeExtFlagMask = 0x01;
    private const byte CalculatedExtFlagMask = 0xC0;

    private const byte LongValueTypeThisPage = 0x80;
    private const byte LongValueTypeOtherPage = 0x40;
    private const byte LongValueTypeOtherPages = 0x00;
    private const int LongValueTypeMask = unchecked((int)0xC0000000);

    private static readonly byte[] TextCompressionHeader = { 0xFF, 0xFE };

    internal Column(Table table, byte[] buffer, int offset, string name, DataType type, int displayIndex)
    {
        Table = table;
        Name = name;
        Type = type;
        DisplayIndex = displayIndex;

        JetFormat format = table.Format;
        ColumnNumber = ReadShort(buffer, offset + format.OffsetColumnNumber);
        ColumnLength = ReadShort(buffer, offset + format.OffsetColumnLength);

        byte flags = buffer[offset + format.OffsetColumnFlags];
        byte extFlags = format.OffsetColumnExtFlags >= 0 ? buffer[offset + format.OffsetColumnExtFlags] : (byte)0;

        VariableLength = (flags & FixedLenFlagMask) == 0;
        AutoNumber = (flags & (AutoNumberFlagMask | AutoNumberGuidFlagMask)) != 0;
        Calculated = (extFlags & CalculatedExtFlagMask) != 0;
        CompressedUnicode = (extFlags & CompressedUnicodeExtFlagMask) != 0;
        Required = table.Database.IsColumnRequired(table.TableDefPageNumber, name);
        DefaultValue = table.Database.GetColumnDefault(table.TableDefPageNumber, name);

        VarLenTableIndex = ReadShort(buffer, offset + format.OffsetColumnVariableTableIndex);
        FixedDataOffset = ReadShort(buffer, offset + format.OffsetColumnFixedDataOffset);

        if (type == DataType.Text || type == DataType.Memo)
        {
            TextSortOrder = ReadTextSortOrder(buffer, offset + format.OffsetColumnSortOrder, format);
        }

        // precision/scale: NUMERIC reads them from the column definition, other
        // types (MONEY) use the data-type defaults (like Jackcess)
        if (type == DataType.Numeric && format.OffsetColumnScale >= 0)
        {
            Precision = buffer[offset + format.OffsetColumnPrecision];
            Scale = buffer[offset + format.OffsetColumnScale];
        }
        else if (type == DataType.Money)
        {
            Precision = 19;
            Scale = 4;
        }
    }

    /// <summary>
    /// Reads the collating sort order for a text column from the table-definition buffer
    /// (port of Jackcess <c>ColumnImpl.readSortOrder</c>).
    /// </summary>
    private static TextSortOrder ReadTextSortOrder(byte[] buffer, int position, JetFormat format)
    {
        short value = ReadShort(buffer, position);

        if (value == 0)
        {
            // probably a file we wrote, before handling sort order
            return format.DefaultSortOrder;
        }

        short version = format.DefaultSortOrder.Version;
        if (format.SizeSortOrder == 4)
        {
            version = (sbyte)buffer[position + 3];
        }

        if (value == UCanAccess.File.TextSortOrder.General.Value)
        {
            if (version == UCanAccess.File.TextSortOrder.General.Version)
            {
                return UCanAccess.File.TextSortOrder.General;
            }
            if (version == UCanAccess.File.TextSortOrder.GeneralLegacy.Version)
            {
                return UCanAccess.File.TextSortOrder.GeneralLegacy;
            }
            if (version == UCanAccess.File.TextSortOrder.General97.Version)
            {
                return UCanAccess.File.TextSortOrder.General97;
            }
        }
        return new TextSortOrder(value, version);
    }

    /// <summary>the owning table</summary>
    public Table Table { get; }

    public string Name { get; }

    public DataType Type { get; }

    public short ColumnNumber { get; }

    public short ColumnLength { get; }

    public int ColumnIndex { get; internal set; }

    public int DisplayIndex { get; }

    public bool VariableLength { get; }

    public bool AutoNumber { get; }

    /// <summary>whether Access rejects NULL values for this column</summary>
    public bool Required { get; internal set; }

    /// <summary>the Access SQL expression used when an INSERT omits this column</summary>
    public string? DefaultValue { get; }

    public bool Calculated { get; }

    public bool CompressedUnicode { get; }

    public int VarLenTableIndex { get; }

    public int FixedDataOffset { get; }

    public byte Precision { get; }

    public byte Scale { get; }

    /// <summary>the collating sort order for text columns (null for non-text)</summary>
    public TextSortOrder? TextSortOrder { get; }

    private static short ReadShort(byte[] buffer, int offset)
    {
        if (offset < 0)
        {
            return 0;
        }
        return (short)(buffer[offset] | (buffer[offset + 1] << 8));
    }

    internal int FixedDataSize
    {
        get
        {
            var meta = DataTypeInfo.Get(Type);
            if (meta.FixedSize is int fixedSize)
            {
                return Math.Max(fixedSize, ColumnLength);
            }
            return ColumnLength;
        }
    }

    /// <summary>
    /// Whether boolean columns are stored in the null mask.
    /// </summary>
    internal bool StoreInNullMask => Type == DataType.Boolean;

    /// <summary>Decodes the value stored for this column in the null mask.</summary>
    internal object ReadFromNullMask(bool isNull) => !isNull;

    internal object? Read(byte[] data, int offset, int length)
    {
        // calculated columns store their value wrapped with a small header
        // (CALC_EXTRA_DATA_LEN bytes: length at offset 16, data at offset 20);
        // unwrap it so the value can be decoded normally
        if (Calculated)
        {
            ReadOnlySpan<byte> wrapped = data.AsSpan(offset, length);
            if (wrapped.Length >= 20)
            {
                int dataLen = ByteUtil.GetIntLittleEndian(wrapped, 16);
                int len = Math.Min(wrapped.Length - 20, dataLen);
                data = wrapped.Slice(20, len).ToArray();
                offset = 0;
                length = data.Length;
            }
        }

        switch (Type)
        {
            case DataType.Boolean:
                throw new DatabaseException("Tried to read a boolean from data instead of null mask.");
            case DataType.Byte:
                return data[offset];
            case DataType.Int:
                return (short)(data[offset] | (data[offset + 1] << 8));
            case DataType.Long:
                return ByteUtil.GetIntLittleEndian(data, offset);
            case DataType.Double:
                return ByteUtil.GetDoubleLittleEndian(data, offset);
            case DataType.Float:
                return ByteUtil.GetFloatLittleEndian(data, offset);
            case DataType.ShortDateTime:
                return ReadDateValue(data, offset);
            case DataType.Binary:
                return data.AsSpan(offset, length).ToArray();
            case DataType.Text:
                return DecodeTextValue(data.AsSpan(offset, length));
            case DataType.Money:
                return ReadCurrencyValue(data, offset);
            case DataType.Numeric:
                return ReadNumericValue(data, offset, length);
            case DataType.Guid:
                return ReadGuidValue(data.AsSpan(offset, length));
            case DataType.Ole:
                return length > 0 ? ReadLongValue(data.AsSpan(offset, length)) : null;
            case DataType.Memo:
                return length > 0 ? ReadLongStringValue(data.AsSpan(offset, length)) : null;
            case DataType.Unknown0D:
            case DataType.Unknown11:
            case DataType.UnsupportedFixedLen:
            case DataType.UnsupportedVarLen:
                return data.AsSpan(offset, length).ToArray();
            case DataType.ComplexType:
                return ByteUtil.GetIntLittleEndian(data, offset);
            case DataType.BigInt:
                return ByteUtil.GetLongLittleEndian(data, offset);
            case DataType.ExtDateTime:
                return ReadExtendedDateValue(data, offset);
            default:
                throw new DatabaseException($"Unrecognized data type: {Type}");
        }
    }

    private static decimal ReadCurrencyValue(byte[] data, int offset)
    {
        long value = ByteUtil.GetLongLittleEndian(data, offset);
        ulong magnitude = unchecked((ulong)(value < 0 ? -value : value));
        int lo = (int)magnitude;
        int mid = (int)(magnitude >> 32);
        return new decimal(lo, mid, 0, value < 0, 4);
    }

    private decimal ReadNumericValue(byte[] data, int offset, int length)
    {
        if (length < 17)
        {
            throw new DatabaseException($"Invalid numeric value, length {length}");
        }
        bool negate = data[offset] != 0;

        byte[] tmp = new byte[16];
        Array.Copy(data, offset + 1, tmp, 0, 16);

        // fix endianness of each 4 byte segment (little-endian ints -> big-endian)
        for (int i = 0; i < tmp.Length; i += 4)
        {
            ByteUtil.Swap4Bytes(tmp, i);
        }

        // the magnitude is unsigned big-endian
        var magnitude = new BigInteger(tmp, isUnsigned: true, isBigEndian: true);
        if (negate)
        {
            magnitude = -magnitude;
        }

        // value = magnitude / 10^scale
        int scale = Math.Min((int)Scale, 28);
        var result = (decimal)magnitude;
        if (scale > 0)
        {
            result = result / Pow10Decimal(scale);
        }
        return result;
    }

    private static decimal Pow10Decimal(int exponent)
    {
        decimal value = 1m;
        decimal factor = 10m;
        while (exponent > 0)
        {
            if ((exponent & 1) == 1)
            {
                value *= factor;
            }
            factor *= factor;
            exponent >>= 1;
        }
        return value;
    }

    private static DateTime ReadDateValue(byte[] data, int offset)
    {
        long dateBits = ByteUtil.GetLongLittleEndian(data, offset);
        double oa = BitConverter.Int64BitsToDouble(dateBits);
        return LdtFromLocalDateDouble(oa);
    }

    /// <summary>
    /// Port of Jackcess <c>ldtFromLocalDateDouble</c>: converts an Access OADate double to a DateTime.
    /// </summary>
    internal static DateTime LdtFromLocalDateDouble(double value)
    {
        const long secondsPerDay = 24L * 60L * 60L;
        const long millisPerSecond = 1000L;

        long dateSeconds = (long)value * secondsPerDay;

        // the fractional part of the double represents the time. it is always a
        // positive fraction of the day (even if the double is negative)
        double secondsDouble = Math.Abs(value) % 1.0d * secondsPerDay;
        long timeSeconds = (long)secondsDouble;
        long timeMillis = (long)(RoundToMillis(secondsDouble % 1.0d) * millisPerSecond);

        // BASE_LDT = 1899-12-30T00:00
        var baseLdt = new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified);
        return baseLdt
            .AddSeconds(dateSeconds + timeSeconds)
            .AddTicks(timeMillis * 10_000);
    }

    private static double RoundToMillis(double dbl)
        => dbl == 0d ? dbl : Math.Round(dbl, 3, MidpointRounding.AwayFromZero);

    private static DateTime ReadExtendedDateValue(byte[] data, int offset)
    {
        // format: <19digits>:<19digits>:7 0x00
        long numDays = ReadExtDateLong(data, offset, 19);
        long seconds = ReadExtDateLong(data, offset + 20, 12);
        long nanos = ReadExtDateLong(data, offset + 32, 7) * 100L;
        var baseLdt = new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified);
        return baseLdt.AddDays(numDays).AddSeconds(seconds).AddTicks(nanos / 100);
    }

    private static long ReadExtDateLong(byte[] data, int offset, int numChars)
    {
        long val = 0;
        for (int i = 0; i < numChars; ++i)
        {
            char digit = (char)data[offset + i];
            val = val * 10L + (digit - '0');
        }
        return val;
    }

    /// <summary>
    /// Serializes a value into its raw column bytes (little-endian), following the
    /// Jackcess <c>ColumnImpl.write</c> algorithm.
    /// </summary>
    internal byte[] Write(object? value, int remainingRowLength)
        => Write(value, remainingRowLength, bigEndian: false);

    /// <summary>
    /// Serializes a value into its raw column bytes, following the Jackcess
    /// <c>ColumnImpl.write(obj, remainingRowLength, byteOrder)</c> algorithm.
    /// </summary>
    internal byte[] Write(object? value, int remainingRowLength, bool bigEndian)
    {
        // Complex flat tables created by Access can mark their numeric
        // foreign-key/flag columns as variable-length even though their wire
        // representation is still the ordinary fixed-width integer.  Keep
        // using the scalar encoder for those columns; the row writer will put
        // the returned bytes in the variable-column slot.
        if (!VariableLength || Type == DataType.Numeric
            || Type is DataType.Byte or DataType.Int or DataType.Long or DataType.BigInt
            or DataType.Float or DataType.Double or DataType.ShortDateTime
            or DataType.ComplexType or DataType.Money or DataType.Guid)
        {
            return WriteFixedLengthField(value, bigEndian);
        }

        switch (Type)
        {
            case DataType.Numeric:
                return WriteNumericValue(value, bigEndian);
            case DataType.Text:
                return EncodeTextValue(ToCharSequence(value), 0, GetLengthInUnits(), forceUncompressed: false);
            case DataType.Binary:
            case DataType.Unknown0D:
            case DataType.UnsupportedVarLen:
                return ToByteArray(value);
            case DataType.Memo:
                return WriteLongValue(EncodeTextValue(ToCharSequence(value), 0, int.MaxValue, forceUncompressed: false), remainingRowLength);
            case DataType.Ole:
                return WriteLongValue(ToByteArray(value), remainingRowLength);
            default:
                throw new DatabaseException($"unexpected inline var length type: {Type}");
        }
    }

    /// <summary>
    /// Serializes a value in big-endian byte order, as used when encoding index entries
    /// (port of Jackcess <c>ColumnImpl.write(obj, 0, IndexData.ENTRY_BYTE_ORDER)</c>).
    /// </summary>
    internal byte[] WriteIndexValue(object? value) => Write(value, 0, bigEndian: true);

    /// <summary>
    /// Writes a long value (MEMO/OLE), returning the LVAL definition bytes
    /// (port of Jackcess <c>LongValueColumnImpl.writeLongValue</c>).
    /// </summary>
    private byte[] WriteLongValue(byte[] value, int remainingRowLength)
    {
        JetFormat format = Table.Format;
        PageChannel pageChannel = Table.Database.PageChannel;

        if (value.Length > DataTypeInfo.Get(Type).MaxSize)
        {
            throw new DatabaseException($"value too big for column, max {DataTypeInfo.Get(Type).MaxSize}, got {value.Length}");
        }

        byte type;
        int lvalDefLen = format.SizeLongValueDef;
        if (format.SizeLongValueDef + value.Length <= remainingRowLength && value.Length <= format.MaxInlineLongValueSize)
        {
            type = LongValueTypeThisPage;
            lvalDefLen += value.Length;
        }
        else if (value.Length <= format.MaxLongValueRowSize)
        {
            type = LongValueTypeOtherPage;
        }
        else
        {
            type = LongValueTypeOtherPages;
        }

        var def = new byte[lvalDefLen];
        int lengthWithFlags = value.Length | (type << 24);
        def[0] = (byte)lengthWithFlags;
        def[1] = (byte)(lengthWithFlags >> 8);
        def[2] = (byte)(lengthWithFlags >> 16);
        def[3] = (byte)(lengthWithFlags >> 24);

        if (type == LongValueTypeThisPage)
        {
            Array.Copy(value, 0, def, format.SizeLongValueDef, value.Length);
        }
        else
        {
            int firstLvalPageNum;
            byte firstLvalRow;
            if (type == LongValueTypeOtherPage)
            {
                var (lvalPage, lvalPageNum) = NewLongValuePage();
                firstLvalPageNum = lvalPageNum;
                var (rowNumber, rowLocation) = Table.AddDataPageRow(lvalPage, value.Length, format, 0);
                firstLvalRow = (byte)rowNumber;
                Array.Copy(value, 0, lvalPage, rowLocation, value.Length);
                pageChannel.WritePage(lvalPage, firstLvalPageNum);
            }
            else
            {
                var (firstPage, firstPageNum) = NewLongValuePage();
                firstLvalPageNum = firstPageNum;
                firstLvalRow = 0;
                int lvalPageNum = firstLvalPageNum;
                byte[] lvalPage = firstPage;
                int remainingLen = value.Length;
                int valueOffset = 0;
                while (remainingLen > 0)
                {
                    int chunkLength = Math.Min(format.MaxLongValueRowSize - 4, remainingLen);
                    byte[]? nextLvalPage = null;
                    int nextLvalPageNum = 0;
                    byte nextLvalRowNum = 0;
                    if (chunkLength < remainingLen)
                    {
                        (nextLvalPage, nextLvalPageNum) = NewLongValuePage();
                        nextLvalRowNum = 0;
                    }

                    var (_, rowLocation) = Table.AddDataPageRow(lvalPage, chunkLength + 4, format, 0);
                    lvalPage[rowLocation] = nextLvalRowNum;
                    lvalPage[rowLocation + 1] = (byte)nextLvalPageNum;
                    lvalPage[rowLocation + 2] = (byte)(nextLvalPageNum >> 8);
                    lvalPage[rowLocation + 3] = (byte)(nextLvalPageNum >> 16);
                    Array.Copy(value, valueOffset, lvalPage, rowLocation + 4, chunkLength);
                    valueOffset += chunkLength;
                    remainingLen -= chunkLength;

                    pageChannel.WritePage(lvalPage, lvalPageNum);

                    lvalPage = nextLvalPage!;
                    lvalPageNum = nextLvalPageNum;
                }
            }

            def[4] = firstLvalRow;
            def[5] = (byte)firstLvalPageNum;
            def[6] = (byte)(firstLvalPageNum >> 8);
            def[7] = (byte)(firstLvalPageNum >> 16);
        }

        return def;
    }

    /// <summary>
    /// Allocates a new long-value page ('LVAL' header) and returns it with its page number.
    /// </summary>
    private (byte[] page, int pageNumber) NewLongValuePage()
    {
        JetFormat format = Table.Format;
        PageChannel pageChannel = Table.Database.PageChannel;

        int pageNumber = pageChannel.AllocateNewPage();
        // Keep LVAL pages in a separate table-owned set.  They are not ordinary row
        // pages and must never enter the row-data usage map used by RowLocations().
        Table.RegisterLongValuePage(pageNumber);
        var page = new byte[format.PageSize];
        page[0] = PageTypes.Data;
        page[1] = 1;
        PutShort(page, format.OffsetFreeSpace, (short)format.DataPageInitialFreeSpace);
        page[4] = (byte)'L';
        page[5] = (byte)'V';
        page[6] = (byte)'A';
        page[7] = (byte)'L';
        PutShort(page, format.OffsetNumRowsOnDataPage, 0);
        return (page, pageNumber);
    }

    private static void PutShort(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private int GetLengthInUnits()
        => Type == DataType.Text || Type == DataType.Memo ? ColumnLength / Table.Format.SizeTextFieldUnit : ColumnLength;

    internal static byte[] ToByteArray(object? value)
        => value switch
        {
            byte[] bytes => bytes,
            null => Array.Empty<byte>(),
            _ => System.Text.Encoding.UTF8.GetBytes(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""),
        };

    /// <summary>
    /// Interprets a boolean value (null == false) (port of Jackcess <c>ColumnImpl.toBooleanValue</c>).
    /// </summary>
    internal static bool ToBooleanValue(object? value)
    {
        if (value is null)
        {
            return false;
        }
        if (value is bool b)
        {
            return b;
        }
        if (value is decimal d)
        {
            return d != 0m;
        }
        if (value is System.Numerics.BigInteger bi)
        {
            return bi != 0;
        }
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double)
        {
            return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture) != 0.0d;
        }
        return bool.TryParse(value.ToString(), out bool parsed) && parsed;
    }

    private static string ToCharSequence(object? value)
        => value?.ToString() ?? "";

    private static object? ToNumber(object? value)
    {
        if (value is bool b)
        {
            return b ? -1 : 0;
        }
        return value;
    }

    private byte[] WriteFixedLengthField(object? value, bool bigEndian)
    {
        switch (Type)
        {
            case DataType.Boolean:
                return Array.Empty<byte>();
            case DataType.Byte:
                return new[] { Convert.ToByte(ToNumber(value), System.Globalization.CultureInfo.InvariantCulture) };
            case DataType.Int:
                return WriteInt16(Convert.ToInt16(ToNumber(value), System.Globalization.CultureInfo.InvariantCulture), bigEndian);
            case DataType.Long:
                return WriteInt32(Convert.ToInt32(ToNumber(value), System.Globalization.CultureInfo.InvariantCulture), bigEndian);
            case DataType.Money:
                return WriteCurrencyValue(value, bigEndian);
            case DataType.Float:
                return WriteSingle(Convert.ToSingle(ToNumber(value), System.Globalization.CultureInfo.InvariantCulture), bigEndian);
            case DataType.Double:
                return WriteDouble(Convert.ToDouble(ToNumber(value), System.Globalization.CultureInfo.InvariantCulture), bigEndian);
            case DataType.ShortDateTime:
                return WriteDouble(ToDateDouble(ToDateTime(value)), bigEndian);
            case DataType.Text:
                return EncodeTextValue(ToCharSequence(value), GetLengthInUnits(), GetLengthInUnits(), forceUncompressed: true);
            case DataType.Guid:
                return WriteGuidValue(ToCharSequence(value), bigEndian);
            case DataType.Numeric:
                return WriteNumericValue(value, bigEndian);
            case DataType.Binary:
            case DataType.Unknown0D:
            case DataType.Unknown11:
            case DataType.ComplexType:
                return WriteInt32(Convert.ToInt32(ToNumber(value), System.Globalization.CultureInfo.InvariantCulture), bigEndian);
            case DataType.BigInt:
                return WriteInt64(Convert.ToInt64(ToNumber(value), System.Globalization.CultureInfo.InvariantCulture), bigEndian);
            case DataType.ExtDateTime:
                throw new DatabaseException("Writing EXT_DATE_TIME values is not supported yet.");
            default:
                throw new DatabaseException($"Unsupported data type: {Type}");
        }
    }

    private static byte[] WriteInt16(short value, bool bigEndian)
    {
        var bytes = new byte[2];
        if (bigEndian) BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        else BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] WriteInt32(int value, bool bigEndian)
    {
        var bytes = new byte[4];
        if (bigEndian) BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        else BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] WriteInt64(long value, bool bigEndian)
    {
        var bytes = new byte[8];
        if (bigEndian) BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        else BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] WriteSingle(float value, bool bigEndian)
    {
        var bytes = new byte[4];
        if (bigEndian) BinaryPrimitives.WriteSingleBigEndian(bytes, value);
        else BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] WriteDouble(double value, bool bigEndian)
    {
        var bytes = new byte[8];
        if (bigEndian) BinaryPrimitives.WriteDoubleBigEndian(bytes, value);
        else BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        return bytes;
    }

    private static DateTime ToDateTime(object? value) => value switch
    {
        DateTime dt => dt,
        _ => DateTime.MinValue,
    };

    private byte[] WriteCurrencyValue(object? value, bool bigEndian)
    {
        decimal decVal = Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
        decVal = decimal.Round(decVal, 4, MidpointRounding.AwayFromZero);
        long unscaled = decimal.ToInt64(decimal.Truncate(decVal * 10000m));
        return WriteInt64(unscaled, bigEndian);
    }

    private byte[] WriteNumericValue(object? value, bool bigEndian)
    {
        decimal decVal = Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
        int signum = decVal < 0 ? -1 : decVal > 0 ? 1 : 0;
        if (signum < 0)
        {
            decVal = -decVal;
        }
        decVal = decimal.Round(decVal, Scale, MidpointRounding.AwayFromZero);

        var magnitude = (BigInteger)(decVal * Pow10Decimal(Scale));
        if (Precision > 0 && magnitude.ToString(System.Globalization.CultureInfo.InvariantCulture).Length > Precision)
        {
            throw new DatabaseException(
                $"Numeric value {value} exceeds precision {Precision} for column '{Name}'.");
        }
        byte[] bigEndianBytes = magnitude.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bigEndianBytes.Length > 16)
        {
            throw new DatabaseException($"Numeric value {value} is too large for column '{Name}'.");
        }

        var result = new byte[17];
        result[0] = signum < 0 ? (byte)0x80 : (byte)0;
        int copyLen = Math.Min(16, bigEndianBytes.Length);
        Array.Copy(bigEndianBytes, bigEndianBytes.Length - copyLen, result, 17 - copyLen, copyLen);
        if (!bigEndian)
        {
            for (int i = 1; i < 17; i += 4)
            {
                ByteUtil.Swap4Bytes(result, i);
            }
        }
        return result;
    }

    private static double ToDateDouble(DateTime ldt)
    {
        var baseLdt = new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified);
        long ticks = ldt.Ticks - baseLdt.Ticks;
        const long ticksPerSecond = TimeSpan.TicksPerSecond;
        const long secondsPerDay = 86400;

        long dateTimeSeconds = ticks / ticksPerSecond;
        long timeSeconds = dateTimeSeconds % secondsPerDay;
        if (timeSeconds < 0)
        {
            timeSeconds += secondsPerDay;
        }
        long dateSeconds = dateTimeSeconds - timeSeconds;
        long timeNanos = ((ticks % ticksPerSecond) + ticksPerSecond) % ticksPerSecond * 100L;

        double timeDouble = (RoundToMillis(timeNanos / 1_000_000_000.0d) + timeSeconds) / secondsPerDay;
        double dateDouble = dateSeconds / (double)secondsPerDay;
        if (dateSeconds < 0)
        {
            timeDouble = -timeDouble;
        }
        return dateDouble + timeDouble;
    }

    private static byte[] WriteGuidValue(string text, bool bigEndian)
    {
        string s = text.Trim().Trim('{', '}');
        string[] groups = s.Split('-');
        if (groups.Length != 5)
        {
            throw new DatabaseException($"Invalid GUID: {text}");
        }
        var bytes = new byte[16];
        // the first 3 components are integer components which respect endianness;
        // in big-endian (index) order they are written in natural hex order
        WriteHexGroup(bytes, 0, groups[0], reverse: !bigEndian);
        WriteHexGroup(bytes, 4, groups[1], reverse: !bigEndian);
        WriteHexGroup(bytes, 6, groups[2], reverse: !bigEndian);
        WriteHexGroup(bytes, 8, groups[3], reverse: false);
        WriteHexGroup(bytes, 10, groups[4], reverse: false);
        return bytes;
    }

    private static void WriteHexGroup(byte[] buffer, int offset, string hex, bool reverse)
    {
        if (hex.Length != buffer.Length - offset && hex.Length != 4 && hex.Length != 2 && hex.Length != 6 && hex.Length != 8 && hex.Length != 12)
        {
            // validate length per group below instead
        }
        byte[] groupBytes = Convert.FromHexString(hex);
        for (int i = 0; i < groupBytes.Length; i++)
        {
            buffer[offset + (reverse ? groupBytes.Length - 1 - i : i)] = groupBytes[i];
        }
    }

    private byte[] EncodeTextValue(string text, int minChars, int maxChars, bool forceUncompressed)
    {
        if (text.Length > maxChars || text.Length < minChars)
        {
            throw new DatabaseException(
                $"Text is wrong length for {Type} column, max {maxChars}, min {minChars}, got {text.Length}");
        }

        if (!forceUncompressed && CompressedUnicode
            && text.Length <= Table.Format.MaxCompressedUnicodeSize
            && IsUnicodeCompressible(text))
        {
            var encoded = new byte[2 + text.Length];
            encoded[0] = 0xFF;
            encoded[1] = 0xFE;
            for (int i = 0; i < text.Length; i++)
            {
                encoded[i + 2] = (byte)text[i];
            }
            return encoded;
        }

        return Charset.GetBytes(text);
    }

    private static bool IsUnicodeCompressible(string text)
    {
        if (text.Length <= 2)
        {
            return false;
        }
        foreach (char c in text)
        {
            if (c < 1 || c > 0xFF)
            {
                return false;
            }
        }
        return true;
    }

    private string DecodeTextValue(ReadOnlySpan<byte> data)
    {
        bool isCompressed = data.Length > 1 && data[0] == TextCompressionHeader[0] && data[1] == TextCompressionHeader[1];
        if (!isCompressed)
        {
            return Charset.GetString(data);
        }

        var textBuf = new StringBuilder(data.Length);
        int dataStart = TextCompressionHeader.Length;
        int dataEnd = dataStart;
        bool inCompressedMode = true;
        while (dataEnd < data.Length)
        {
            if (data[dataEnd] == 0)
            {
                DecodeTextSegment(data, dataStart, dataEnd, inCompressedMode, textBuf);
                inCompressedMode = !inCompressedMode;
                dataEnd++;
                dataStart = dataEnd;
            }
            else
            {
                dataEnd++;
            }
        }
        DecodeTextSegment(data, dataStart, dataEnd, inCompressedMode, textBuf);
        return textBuf.ToString();
    }

    private void DecodeTextSegment(ReadOnlySpan<byte> data, int dataStart, int dataEnd, bool inCompressedMode, StringBuilder textBuf)
    {
        if (dataEnd <= dataStart)
        {
            return;
        }

        if (inCompressedMode)
        {
            for (int i = dataStart; i < dataEnd; ++i)
            {
                textBuf.Append((char)data[i]);
            }
        }
        else
        {
            textBuf.Append(Charset.GetString(data.Slice(dataStart, dataEnd - dataStart)));
        }
    }

    /// <summary>
    /// The encoding used to decode this column's text (the database encoding).
    /// </summary>
    private Encoding Charset => Table.Database.TextEncoding;

    private static string ReadGuidValue(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16)
        {
            throw new DatabaseException("GUID value is shorter than 16 bytes.");
        }
        byte[] tmp = data.ToArray();
        ByteUtil.Swap4Bytes(tmp, 0);
        ByteUtil.Swap2Bytes(tmp, 4);
        ByteUtil.Swap2Bytes(tmp, 6);

        var sb = new StringBuilder(22);
        sb.Append('{');
        sb.Append(ByteUtil.ToHexString(tmp, 0, 4, false));
        sb.Append('-');
        sb.Append(ByteUtil.ToHexString(tmp, 4, 2, false));
        sb.Append('-');
        sb.Append(ByteUtil.ToHexString(tmp, 6, 2, false));
        sb.Append('-');
        sb.Append(ByteUtil.ToHexString(tmp, 8, 2, false));
        sb.Append('-');
        sb.Append(ByteUtil.ToHexString(tmp, 10, 6, false));
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// Reads a long value (MEMO/OLE) from its definition bytes.
    /// </summary>
    private byte[] ReadLongValue(ReadOnlySpan<byte> lvalDefinition)
    {
        if (lvalDefinition.Length < 4)
        {
            throw new DatabaseException("Long value definition is shorter than its length header.");
        }
        int lengthWithFlags = ByteUtil.GetIntLittleEndian(lvalDefinition, 0);
        int length = lengthWithFlags & ~LongValueTypeMask;
        byte type = (byte)((uint)(lengthWithFlags & LongValueTypeMask) >> 24);

        if (length < 0 || length > DataTypeInfo.Get(Type).MaxSize)
        {
            throw new DatabaseException($"Invalid long value length {length} for column '{Name}'.");
        }
        if (type != LongValueTypeThisPage && type != LongValueTypeOtherPage && type != LongValueTypeOtherPages)
        {
            throw new DatabaseException($"Unrecognized long value type: {type}");
        }

        var rtn = new byte[length];

        if (type == LongValueTypeThisPage)
        {
            if (lvalDefinition.Length < Table.Format.SizeLongValueDef)
            {
                throw new DatabaseException("Inline long value definition is shorter than its header.");
            }
            // inline long value: data follows the 12-byte header
            int rowLen = lvalDefinition.Length - 12;
            int copyLen = Math.Min(rowLen, length);
            lvalDefinition.Slice(12, copyLen).CopyTo(rtn);
            if (copyLen < length)
            {
                Array.Resize(ref rtn, copyLen);
            }
        }
        else
        {
            if (lvalDefinition.Length != Table.Format.SizeLongValueDef)
            {
                throw new DatabaseException(
                    $"Expected {Table.Format.SizeLongValueDef} bytes in long value definition, but found {lvalDefinition.Length}");
            }

            int rowNum = lvalDefinition[4];
            int pageNum = ByteUtil.Get3ByteInt(lvalDefinition, 5);
            PageChannel pageChannel = Table.Database.PageChannel;
            byte[] lvalPage = new byte[Table.Format.PageSize];

            if (type == LongValueTypeOtherPage)
            {
                Table.RegisterLongValuePage(pageNum);
                (lvalPage, int rowStart, int rowEnd) = ReadLongValueRow(pageNum, rowNum);
                int rowLen = rowEnd - rowStart;
                if (rowLen < length)
                {
                    throw new DatabaseException($"Long value page row contains {rowLen} bytes, expected {length}.");
                }
                Array.Copy(lvalPage, rowStart, rtn, 0, length);
            }
            else if (type == LongValueTypeOtherPages)
            {
                var result = new MemoryStream(length);
                int remainingLen = length;
                var visitedRows = new HashSet<(int Page, int Row)>();
                while (remainingLen > 0)
                {
                    Table.RegisterLongValuePage(pageNum);
                    if (!visitedRows.Add((pageNum, rowNum)))
                    {
                        throw new DatabaseException("Cyclic long-value page chain detected.");
                    }
                    (lvalPage, int rowStart, int rowEnd) = ReadLongValueRow(pageNum, rowNum);

                    // read next page information
                    rowNum = lvalPage[rowStart];
                    pageNum = ByteUtil.Get3ByteInt(lvalPage, rowStart + 1);

                    // update rowEnd and remainingLen based on chunkLength
                    int chunkLength = rowEnd - rowStart - 4;
                    if (chunkLength <= 0)
                    {
                        throw new DatabaseException("Invalid long-value chunk length.");
                    }
                    if (chunkLength > remainingLen)
                    {
                        rowEnd -= chunkLength - remainingLen;
                        chunkLength = remainingLen;
                    }
                    remainingLen -= chunkLength;

                    result.Write(lvalPage, rowStart + 4, rowEnd - (rowStart + 4));
                }
                rtn = result.ToArray();
            }
        }

        return rtn;
    }

    internal void CollectLongValuePages(ReadOnlySpan<byte> lvalDefinition, HashSet<int> pages)
    {
        if (lvalDefinition.Length == 0)
        {
            return;
        }
        if (lvalDefinition.Length < 4)
        {
            throw new DatabaseException("Long value definition is shorter than its length header.");
        }

        int lengthWithFlags = ByteUtil.GetIntLittleEndian(lvalDefinition, 0);
        int length = lengthWithFlags & ~LongValueTypeMask;
        byte type = (byte)((uint)(lengthWithFlags & LongValueTypeMask) >> 24);
        if (length < 0 || length > DataTypeInfo.Get(Type).MaxSize)
        {
            throw new DatabaseException($"Invalid long value length {length} for column '{Name}'.");
        }
        if (type == LongValueTypeThisPage)
        {
            return;
        }
        if (type != LongValueTypeOtherPage && type != LongValueTypeOtherPages
            || lvalDefinition.Length != Table.Format.SizeLongValueDef)
        {
            throw new DatabaseException("Invalid external long value definition.");
        }

        int rowNum = lvalDefinition[4];
        int pageNum = ByteUtil.Get3ByteInt(lvalDefinition, 5);
        if (type == LongValueTypeOtherPage)
        {
            _ = ReadLongValueRow(pageNum, rowNum);
            pages.Add(pageNum);
            return;
        }

        int remaining = length;
        var visitedRows = new HashSet<(int Page, int Row)>();
        while (remaining > 0)
        {
            if (!visitedRows.Add((pageNum, rowNum)))
            {
                throw new DatabaseException("Cyclic long-value page chain detected.");
            }
            (byte[] page, int rowStart, int rowEnd) = ReadLongValueRow(pageNum, rowNum);
            pages.Add(pageNum);
            int chunkLength = rowEnd - rowStart - 4;
            if (chunkLength <= 0)
            {
                throw new DatabaseException("Invalid long-value chunk length.");
            }
            if (chunkLength > remaining)
            {
                chunkLength = remaining;
            }
            remaining -= chunkLength;
            rowNum = page[rowStart];
            pageNum = ByteUtil.Get3ByteInt(page, rowStart + 1);
        }
    }

    private (byte[] Page, int RowStart, int RowEnd) ReadLongValueRow(int pageNumber, int rowNumber)
    {
        byte[] page = new byte[Table.Format.PageSize];
        Table.Database.PageChannel.ReadPage(page, pageNumber);
        if (page[0] != PageTypes.Data)
        {
            throw new DatabaseException($"Long value page {pageNumber} is not a data page.");
        }
        int rows = Table.GetRowsOnDataPage(page, Table.Format);
        if (rowNumber < 0 || rowNumber >= rows)
        {
            throw new DatabaseException($"Invalid long value row {rowNumber} on page {pageNumber}.");
        }
        short rawStart = (short)(page[Table.GetRowStartOffset(rowNumber, Table.Format)]
            | (page[Table.GetRowStartOffset(rowNumber, Table.Format) + 1] << 8));
        if (Table.IsDeletedRow(rawStart) || Table.IsOverflowRow(rawStart))
        {
            throw new DatabaseException($"Long value row ({pageNumber}, {rowNumber}) is deleted or overflowed.");
        }
        int rowStart = Table.CleanRowStart(rawStart);
        int rowEnd = Table.FindRowEnd(page, rowNumber, Table.Format);
        if (rowStart < 0 || rowEnd < rowStart || rowEnd > Table.Format.PageSize)
        {
            throw new DatabaseException($"Invalid long value row bounds on page {pageNumber}.");
        }
        return (page, rowStart, rowEnd);
    }

    private string? ReadLongStringValue(ReadOnlySpan<byte> lvalDefinition)
    {
        byte[] binData = ReadLongValue(lvalDefinition);
        if (binData.Length == 0)
        {
            return "";
        }
        return DecodeTextValue(binData);
    }
}
