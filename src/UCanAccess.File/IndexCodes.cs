namespace UCanAccess.File;

/// <summary>
/// Various constants used for creating index entries (port of Jackcess <c>IndexCodes</c>).
/// </summary>
internal static class IndexCodes
{
    internal const byte AscStartFlag = 0x7F;
    internal const byte AscNullFlag = 0x00;
    internal const byte DescStartFlag = 0x80;
    internal const byte DescNullFlag = 0xFF;

    internal const byte AscBooleanTrue = 0x00;
    internal const byte AscBooleanFalse = 0xFF;

    internal const byte DescBooleanTrue = AscBooleanFalse;
    internal const byte DescBooleanFalse = AscBooleanTrue;

    internal static bool IsNullEntry(byte startEntryFlag)
        => startEntryFlag == AscNullFlag || startEntryFlag == DescNullFlag;

    internal static byte GetNullEntryFlag(bool isAscending) => isAscending ? AscNullFlag : DescNullFlag;

    internal static byte GetStartEntryFlag(bool isAscending) => isAscending ? AscStartFlag : DescStartFlag;

    /// <summary>
    /// Flips the bits in the specified bytes in the byte array
    /// (port of Jackcess <c>IndexData.flipBytes</c>).
    /// </summary>
    internal static byte[] FlipBytes(byte[] value, int offset, int length)
    {
        for (int i = offset; i < offset + length; ++i)
        {
            value[i] = (byte)~value[i];
        }
        return value;
    }
}
