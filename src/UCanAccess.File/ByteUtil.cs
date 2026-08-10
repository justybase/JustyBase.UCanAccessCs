using System.Buffers.Binary;
using System.Text;

namespace UCanAccess.File;

/// <summary>
/// Little-endian (and misc) byte access helpers (port of Jackcess <c>ByteUtil</c>).
/// </summary>
internal static class ByteUtil
{
    /// <summary>
    /// Reads a 3-byte little-endian unsigned integer.
    /// </summary>
    public static int Get3ByteInt(ReadOnlySpan<byte> buffer, int offset)
    {
        return buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
    }

    /// <summary>
    /// Reads an unsigned variable-length integer (little-endian).
    /// </summary>
    public static uint GetUnsignedVarInt(ReadOnlySpan<byte> buffer, int offset, int numBytes)
    {
        uint value = 0;
        for (int i = 0; i < numBytes; i++)
        {
            value |= (uint)buffer[offset + i] << (i * 8);
        }
        return value;
    }

    /// <summary>
    /// Returns whether the given buffer range matches the given bytes.
    /// </summary>
    public static bool MatchesRange(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> expected)
    {
        if (offset + expected.Length > buffer.Length)
        {
            return false;
        }
        return buffer.Slice(offset, expected.Length).SequenceEqual(expected);
    }

    /// <summary>
    /// Copies a range of the source buffer.
    /// </summary>
    public static byte[] CopyOf(ReadOnlySpan<byte> source, int offset, int length)
    {
        return source.Slice(offset, length).ToArray();
    }

    public static string ToHexString(ReadOnlySpan<byte> buffer, int offset, int length, bool lowercase)
    {
        var sb = new StringBuilder(length * 2);
        for (int i = 0; i < length; i++)
        {
            sb.Append(buffer[offset + i].ToString(lowercase ? "x2" : "X2"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Swaps the order of the 4 bytes starting at the given offset.
    /// </summary>
    public static void Swap4Bytes(byte[] buffer, int offset)
    {
        (buffer[offset], buffer[offset + 3]) = (buffer[offset + 3], buffer[offset]);
        (buffer[offset + 1], buffer[offset + 2]) = (buffer[offset + 2], buffer[offset + 1]);
    }

    /// <summary>
    /// Swaps the order of the 2 bytes starting at the given offset.
    /// </summary>
    public static void Swap2Bytes(byte[] buffer, int offset)
    {
        (buffer[offset], buffer[offset + 1]) = (buffer[offset + 1], buffer[offset]);
    }

    public static int GetIntLittleEndian(ReadOnlySpan<byte> buffer, int offset)
        => BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));

    public static long GetLongLittleEndian(ReadOnlySpan<byte> buffer, int offset)
        => BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));

    public static short GetShortLittleEndian(ReadOnlySpan<byte> buffer, int offset)
        => BinaryPrimitives.ReadInt16LittleEndian(buffer.Slice(offset));

    public static double GetDoubleLittleEndian(ReadOnlySpan<byte> buffer, int offset)
        => BitConverter.Int64BitsToDouble(GetLongLittleEndian(buffer, offset));

    public static float GetFloatLittleEndian(ReadOnlySpan<byte> buffer, int offset)
        => BitConverter.Int32BitsToSingle(GetIntLittleEndian(buffer, offset));

    // ------------------------------------------------------------------
    // Big-endian helpers (used for index entries)
    // ------------------------------------------------------------------

    /// <summary>Reads a 3-byte big-endian unsigned integer.</summary>
    public static int Get3ByteIntBigEndian(ReadOnlySpan<byte> buffer, int offset)
    {
        return (buffer[offset] << 16) | (buffer[offset + 1] << 8) | buffer[offset + 2];
    }

    public static int GetIntBigEndian(ReadOnlySpan<byte> buffer, int offset)
        => BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(offset));

    /// <summary>Writes a 3-byte big-endian integer.</summary>
    public static void Put3ByteIntBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 16);
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)value;
    }

    public static void PutIntBigEndian(byte[] buffer, int offset, int value)
        => BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset), value);
}

/// <summary>
/// A growable byte sequence (port of Jackcess <c>ByteUtil.ByteStream</c>).
/// </summary>
public class ByteStream
{
    private byte[] _bytes;
    private int _length;

    public ByteStream()
        : this(32)
    {
    }

    internal ByteStream(int capacity)
    {
        _bytes = new byte[capacity];
    }

    public int Length => _length;

    public byte[] GetBytes() => _bytes;

    internal void EnsureNewCapacity(int numBytes) => EnsureCapacity(_length + numBytes);

    internal virtual void EnsureCapacity(int newLength)
    {
        if (newLength > _bytes.Length)
        {
            var temp = new byte[newLength * 2];
            Array.Copy(_bytes, 0, temp, 0, _length);
            _bytes = temp;
        }
    }

    public void Write(int b)
    {
        EnsureNewCapacity(1);
        _bytes[_length++] = (byte)b;
    }

    public void Write(byte[] b) => Write(b, 0, b.Length);

    public void Write(byte[] b, int offset, int length)
    {
        EnsureNewCapacity(length);
        Array.Copy(b, offset, _bytes, _length, length);
        _length += length;
    }

    public byte Get(int offset) => _bytes[offset];

    public void Set(int offset, byte b) => _bytes[offset] = b;

    internal void SetBits(int offset, byte b) => _bytes[offset] |= b;

    internal void WriteFill(int length, byte b)
    {
        EnsureNewCapacity(length);
        int oldLength = _length;
        _length += length;
        Array.Fill(_bytes, b, oldLength, length);
    }

    internal void Skip(int n)
    {
        EnsureNewCapacity(n);
        _length += n;
    }

    public void WriteTo(ByteStream output) => output.Write(_bytes, 0, _length);

    public byte[] ToByteArray() => _bytes.AsSpan(0, _length).ToArray();

    public void Reset() => _length = 0;

    internal void TrimTrailing(byte minTrimCode, byte maxTrimCode)
    {
        int minTrim = minTrimCode & 0xFF;
        int maxTrim = maxTrimCode & 0xFF;

        int idx = _length - 1;
        while (idx >= 0)
        {
            int val = _bytes[idx] & 0xFF;
            if (val >= minTrim && val <= maxTrim)
            {
                idx--;
            }
            else
            {
                break;
            }
        }

        _length = idx + 1;
    }
}
