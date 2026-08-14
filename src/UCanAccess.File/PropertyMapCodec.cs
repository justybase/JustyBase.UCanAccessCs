using System.Buffers.Binary;
using System.Text;

namespace UCanAccess.File;

/// <summary>
/// Minimal codec for the Access property-map block stored in MSysObjects.LvProp.
/// It handles the column-level Required and DefaultValue properties; unknown
/// properties are ignored when reading so existing Access files remain
/// forward-compatible.
/// </summary>
internal static class PropertyMapCodec
{
    private const short PropertyNameList = 0x80;
    private const short ColumnPropertyValueList = 0x01;
    private const byte BooleanType = 0x01;
    private const byte MemoType = 0x0C;
    private const string RequiredName = "Required";
    private const string DefaultValueName = "DefaultValue";

    private static readonly byte[] Jet4Header = { (byte)'M', (byte)'R', (byte)'2', 0 };
    private static readonly byte[] Jet3Header = { (byte)'K', (byte)'K', (byte)'D', 0 };

    internal static bool ReadRequired(byte[]? bytes, Encoding encoding)
        => ReadRequiredColumns(bytes, encoding).Count > 0;

    internal static bool IsRequired(byte[]? bytes, string columnName, Encoding encoding)
        => ReadRequiredColumns(bytes, encoding).Contains(columnName);

    internal static byte[]? WriteRequired(IEnumerable<string> columnNames, Encoding encoding, bool jet3)
    {
        string[] names = columnNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
        {
            return null;
        }

        using var stream = new MemoryStream();
        stream.Write(jet3 ? Jet3Header : Jet4Header);

        byte[] propertyName = encoding.GetBytes(RequiredName);
        WriteInt(stream, 6 + 2 + propertyName.Length);
        WriteShort(stream, PropertyNameList);
        WriteName(stream, propertyName);

        foreach (string columnName in names)
        {
            byte[] mapName = encoding.GetBytes(columnName);
            int mapNameLength = 6 + mapName.Length;
            const int valueLength = 9; // length field + ddl/type/name/data-size + one byte
            WriteInt(stream, 6 + mapNameLength + valueLength);
            WriteShort(stream, ColumnPropertyValueList);

            WriteInt(stream, mapNameLength);
            WriteName(stream, mapName);

            WriteShort(stream, valueLength);
            stream.WriteByte(1); // DDL property
            stream.WriteByte(BooleanType);
            WriteShort(stream, 0); // Required is the first (and only) property name
            WriteShort(stream, 1);
            stream.WriteByte(0xFF); // Access Boolean TRUE is -1
        }

        return stream.ToArray();
    }

    internal static byte[]? WriteColumnProperties(IEnumerable<ColumnBuilder> columns,
        Encoding encoding, bool jet3)
    {
        ColumnBuilder[] definitions = columns
            .Where(column => column.Required || !string.IsNullOrWhiteSpace(column.DefaultValue))
            .ToArray();
        if (definitions.Length == 0)
        {
            return null;
        }

        var propertyNames = new List<string>();
        if (definitions.Any(column => column.Required)) propertyNames.Add(RequiredName);
        if (definitions.Any(column => !string.IsNullOrWhiteSpace(column.DefaultValue)))
            propertyNames.Add(DefaultValueName);

        using var stream = new MemoryStream();
        stream.Write(jet3 ? Jet3Header : Jet4Header);
        using (var nameStream = new MemoryStream())
        {
            foreach (string propertyName in propertyNames)
            {
                WriteName(nameStream, encoding.GetBytes(propertyName));
            }
            byte[] names = nameStream.ToArray();
            WriteInt(stream, 6 + names.Length);
            WriteShort(stream, PropertyNameList);
            stream.Write(names);
        }

        foreach (ColumnBuilder column in definitions)
        {
            byte[] mapName = encoding.GetBytes(column.Name);
            int mapNameLength = 6 + mapName.Length;
            var values = new List<byte[]>();
            if (column.Required)
            {
                values.Add(CreatePropertyValue(BooleanType, propertyNames.IndexOf(RequiredName), new byte[] { 0xFF }));
            }
            if (!string.IsNullOrWhiteSpace(column.DefaultValue))
            {
                values.Add(CreatePropertyValue(MemoType, propertyNames.IndexOf(DefaultValueName),
                    encoding.GetBytes(column.DefaultValue!)));
            }

            int blockLength = 6 + mapNameLength + values.Sum(value => value.Length);
            WriteInt(stream, blockLength);
            WriteShort(stream, ColumnPropertyValueList);
            WriteInt(stream, mapNameLength);
            WriteName(stream, mapName);
            foreach (byte[] value in values)
            {
                stream.Write(value);
            }
        }

        return stream.ToArray();
    }

    internal static string? GetDefault(byte[]? bytes, string columnName, Encoding encoding)
    {
        if (bytes == null || bytes.Length < 4)
        {
            return null;
        }
        bool jet4 = bytes.AsSpan(0, 4).SequenceEqual(Jet4Header);
        bool jet3 = bytes.AsSpan(0, 4).SequenceEqual(Jet3Header);
        if (!jet4 && !jet3)
        {
            return null;
        }

        int pos = 4;
        List<string>? propertyNames = null;
        while (pos + 6 <= bytes.Length)
        {
            int length = ReadInt(bytes, pos);
            short blockType = ReadShort(bytes, pos + 4);
            if (length < 6 || pos + length > bytes.Length)
            {
                break;
            }
            int blockStart = pos + 6;
            int blockEnd = pos + length;
            if (blockType == PropertyNameList)
            {
                propertyNames = new List<string>();
                int cursor = blockStart;
                while (cursor + 2 <= blockEnd)
                {
                    string? name = ReadName(bytes, ref cursor, blockEnd, encoding);
                    if (name == null) break;
                    propertyNames.Add(name);
                }
            }
            else if (blockType == ColumnPropertyValueList && propertyNames != null)
            {
                int cursor = blockStart;
                if (cursor + 4 > blockEnd) break;
                int mapNameLength = ReadInt(bytes, cursor);
                int mapEnd = cursor + mapNameLength;
                cursor += 4;
                string? mapName = mapNameLength >= 6 && mapEnd <= blockEnd
                    ? ReadName(bytes, ref cursor, mapEnd, encoding)
                    : null;
                cursor = mapEnd;
                if (!string.Equals(mapName, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    pos = blockEnd;
                    continue;
                }
                while (cursor + 2 <= blockEnd)
                {
                    int valueStart = cursor;
                    int valueLength = ReadUnsignedShort(bytes, cursor);
                    int valueEnd = valueStart + valueLength;
                    cursor += 2;
                    if (valueLength < 8 || valueEnd > blockEnd || cursor + 6 > valueEnd)
                    {
                        break;
                    }
                    _ = bytes[cursor++]; // DDL flag
                    byte dataType = bytes[cursor++];
                    int propertyIndex = ReadUnsignedShort(bytes, cursor);
                    cursor += 2;
                    int dataSize = ReadUnsignedShort(bytes, cursor);
                    cursor += 2;
                    if (propertyIndex < propertyNames.Count
                        && propertyNames[propertyIndex].Equals(DefaultValueName, StringComparison.OrdinalIgnoreCase)
                        && dataType == MemoType && dataSize >= 0 && cursor + dataSize <= valueEnd)
                    {
                        return encoding.GetString(bytes, cursor, dataSize).TrimEnd('\0');
                    }
                    cursor = valueEnd;
                }
            }
            pos = blockEnd;
        }
        return null;
    }

    private static byte[] CreatePropertyValue(byte dataType, int propertyIndex, byte[] data)
    {
        int valueLength = 2 + 1 + 1 + 2 + 2 + data.Length;
        using var stream = new MemoryStream();
        WriteShort(stream, checked((short)valueLength));
        stream.WriteByte(1); // DDL property
        stream.WriteByte(dataType);
        WriteShort(stream, checked((short)propertyIndex));
        WriteShort(stream, checked((short)data.Length));
        stream.Write(data);
        return stream.ToArray();
    }

    private static HashSet<string> ReadRequiredColumns(byte[]? bytes, Encoding encoding)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (bytes == null || bytes.Length < 4)
        {
            return result;
        }

        bool jet4 = bytes.AsSpan(0, 4).SequenceEqual(Jet4Header);
        bool jet3 = bytes.AsSpan(0, 4).SequenceEqual(Jet3Header);
        if (!jet4 && !jet3)
        {
            return result;
        }

        int pos = 4;
        List<string>? propertyNames = null;
        while (pos + 6 <= bytes.Length)
        {
            int length = ReadInt(bytes, pos);
            short blockType = ReadShort(bytes, pos + 4);
            if (length < 6 || pos + length > bytes.Length)
            {
                break;
            }

            int blockStart = pos + 6;
            int blockEnd = pos + length;
            if (blockType == PropertyNameList)
            {
                propertyNames = new List<string>();
                int cursor = blockStart;
                while (cursor + 2 <= blockEnd)
                {
                    string? name = ReadName(bytes, ref cursor, blockEnd, encoding);
                    if (name == null)
                    {
                        break;
                    }
                    propertyNames.Add(name);
                }
            }
            else if (blockType == ColumnPropertyValueList && propertyNames != null)
            {
                int cursor = blockStart;
                string? mapName = null;
                if (cursor + 4 <= blockEnd)
                {
                    int mapNameLength = ReadInt(bytes, cursor);
                    int mapEnd = cursor + mapNameLength;
                    cursor += 4;
                    if (mapNameLength >= 6 && mapEnd <= blockEnd)
                    {
                        mapName = ReadName(bytes, ref cursor, mapEnd, encoding);
                        cursor = mapEnd;
                    }
                }

                while (cursor + 2 <= blockEnd)
                {
                    int valueLength = ReadUnsignedShort(bytes, cursor);
                    int valueEnd = cursor + valueLength;
                    cursor += 2;
                    if (valueLength < 9 || valueEnd > blockEnd || cursor + 7 > valueEnd)
                    {
                        break;
                    }

                    _ = bytes[cursor++]; // DDL flag
                    byte dataType = bytes[cursor++];
                    int propertyIndex = ReadUnsignedShort(bytes, cursor);
                    cursor += 2;
                    int dataSize = ReadUnsignedShort(bytes, cursor);
                    cursor += 2;
                    bool value = dataSize > 0 && cursor < valueEnd && bytes[cursor] != 0;

                    if (value && dataType == BooleanType && propertyIndex < propertyNames.Count
                        && propertyNames[propertyIndex].Equals(RequiredName, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrEmpty(mapName))
                    {
                        result.Add(mapName);
                    }
                    cursor = valueEnd;
                }
            }

            pos = blockEnd;
        }

        return result;
    }

    private static string? ReadName(byte[] bytes, ref int position, int end, Encoding encoding)
    {
        if (position + 2 > end)
        {
            return null;
        }
        int length = ReadUnsignedShort(bytes, position);
        position += 2;
        if (length < 0 || position + length > end)
        {
            return null;
        }
        string result = encoding.GetString(bytes, position, length);
        position += length;
        return result;
    }

    private static void WriteName(Stream stream, byte[] name)
    {
        WriteShort(stream, checked((short)name.Length));
        stream.Write(name);
    }

    private static int ReadInt(byte[] bytes, int offset)
        => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));

    private static short ReadShort(byte[] bytes, int offset)
        => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset, 2));

    private static int ReadUnsignedShort(byte[] bytes, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));

    private static void WriteInt(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteShort(Stream stream, short value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
