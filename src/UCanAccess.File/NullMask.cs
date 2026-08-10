namespace UCanAccess.File;

/// <summary>
/// Bitmask that indicates whether or not each column in a row is null.
/// Also holds values of boolean columns (port of Jackcess <c>NullMask</c>).
/// </summary>
internal sealed class NullMask
{
    private readonly int _columnCount;
    private readonly byte[] _mask;

    public NullMask(int columnCount)
    {
        _columnCount = columnCount;
        _mask = new byte[(columnCount + 7) / 8];
    }

    public void Read(ReadOnlySpan<byte> buffer, int offset)
    {
        buffer.Slice(offset, _mask.Length).CopyTo(_mask);
    }

    /// <summary>Writes the mask bytes to the given buffer at the given offset.</summary>
    public void WriteTo(byte[] buffer, int offset)
    {
        Array.Copy(_mask, 0, buffer, offset, _mask.Length);
    }

    /// <summary>Whether the value for that column is null. For boolean columns, returns the actual value (non-null == true).</summary>
    public bool IsNull(Column column)
    {
        int columnNumber = column.ColumnNumber;
        // if new columns were added to the table, old null masks may not include
        // them (meaning the field is null)
        return columnNumber >= _columnCount || (_mask[ByteIndex(columnNumber)] & BitMask(columnNumber)) == 0;
    }

    public void MarkNotNull(Column column)
    {
        int columnNumber = column.ColumnNumber;
        int maskIndex = ByteIndex(columnNumber);
        _mask[maskIndex] = (byte)(_mask[maskIndex] | BitMask(columnNumber));
    }

    public int ByteSize => _mask.Length;

    private static int ByteIndex(int columnNumber) => columnNumber / 8;

    private static byte BitMask(int columnNumber) => (byte)(1 << (columnNumber % 8));
}
