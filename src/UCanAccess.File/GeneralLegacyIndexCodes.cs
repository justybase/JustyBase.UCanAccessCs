using System.Text;

namespace UCanAccess.File;

/// <summary>
/// Encoding logic for MS Access "General Legacy" (Access 2000-2007) text index entries.
/// Port of Jackcess <c>GeneralLegacyIndexCodes</c>.
/// </summary>
internal class GeneralLegacyIndexCodes
{
    internal const int MaxTextIndexCharLength = JetFormat.TextFieldMaxLength / JetFormat.TextFieldUnitSize;

    internal const byte EndText = 0x01;
    internal const byte EndExtraText = 0x00;

    // unprintable char is removed from normal text.
    // pattern for unprintable chars in the extra bytes:
    // 01 01 01 <pos> 06 <code>
    internal const int UnprintableCountStart = 7;
    internal const int UnprintableCountMultiplier = 4;
    internal const int UnprintableOffsetFlags = 0x8000;
    internal const byte UnprintableMidfix = 0x06;

    // international char is replaced with ascii char.
    // pattern for international chars in the extra bytes:
    // [ 02 (for each normal char) ] [ <symbol_code> (for each intl char) ]
    internal const byte InternationalExtraPlaceholder = 0x02;

    // see WriteCrazyCodes for details on writing crazy codes
    internal const byte CrazyCodeStart = 0x80;
    internal const byte CrazyCode1 = 0x02;
    internal const byte CrazyCode2 = 0x03;
    internal static readonly byte[] CrazyCodesSuffix = { 0xFF, 0x02, 0x80, 0xFF, 0x80 };
    internal const byte CrazyCodesUnprintSuffix = 0xFF;

    private const string CodesFile = "index_codes_genleg.txt";
    private const string ExtCodesFile = "index_codes_ext_genleg.txt";

    internal const char FirstChar = '\x0000';
    internal const char LastChar = '\x00FF';
    internal const char FirstExtChar = (char)(LastChar + 1);
    internal const char LastExtChar = '\xFFFF';

    private static readonly CharHandler[] CodesValues = LoadCodes(CodesFile, FirstChar, LastChar);
    private static readonly CharHandler[] ExtCodesValues = LoadCodes(ExtCodesFile, FirstExtChar, LastExtChar);

    internal static readonly GeneralLegacyIndexCodes GenLegacyInstance = new();

    /// <summary>
    /// Returns the CharHandler for the given character.
    /// </summary>
    internal virtual CharHandler GetCharHandler(char c)
    {
        if (c <= LastChar)
        {
            return CodesValues[c];
        }

        int extOffset = AsUnsignedChar(c) - AsUnsignedChar(FirstExtChar);
        return ExtCodesValues[extOffset];
    }

    /// <summary>
    /// Loads the CharHandlers for the given range of characters from the embedded resource file.
    /// </summary>
    protected static CharHandler[] LoadCodes(string codesFilePath, char firstChar, char lastChar)
    {
        int numCodes = AsUnsignedChar(lastChar) - AsUnsignedChar(firstChar) + 1;
        var values = new CharHandler[numCodes];

        string[] lines = ReadResourceLines(codesFilePath);

        int start = AsUnsignedChar(firstChar);
        int end = AsUnsignedChar(lastChar);
        int lineIdx = 0;
        for (int i = start; i <= end; ++i)
        {
            char c = (char)i;
            CharHandler ch;
            if (char.IsHighSurrogate(c))
            {
                // surrogate chars are not included in the codes files
                ch = HighSurrogateCharHandler;
            }
            else if (char.IsLowSurrogate(c))
            {
                ch = LowSurrogateCharHandler;
            }
            else
            {
                ch = ParseCodes(lines[lineIdx++]);
            }
            values[i - start] = ch;
        }

        return values;
    }

    /// <summary>
    /// Reads the given embedded resource file into an array of lines.
    /// </summary>
    protected static string[] ReadResourceLines(string fileName)
    {
        var asm = typeof(GeneralLegacyIndexCodes).Assembly;
        string resourceName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("." + fileName, StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lines.Add(line);
        }
        return lines.ToArray();
    }

    /// <summary>
    /// Returns a CharHandler parsed from the given line from an index codes file.
    /// </summary>
    private static CharHandler ParseCodes(string codeLine)
    {
        string prefix = codeLine.Substring(0, 1);
        string suffix = codeLine.Length > 1 ? codeLine.Substring(1) : "";
        string[] codeStrings = suffix.Split(',', StringSplitOptions.None);
        return prefix switch
        {
            "S" => ParseSimpleCodes(codeStrings),
            "I" => ParseInternationalCodes(codeStrings),
            "U" => ParseUnprintableCodes(codeStrings),
            "P" => ParseUnprintableExtCodes(codeStrings),
            "Z" => ParseInternationalExtCodes(codeStrings),
            "G" => ParseSignificantCodes(codeStrings),
            "X" => IgnoredCharHandler,
            _ => throw new InvalidOperationException($"unrecognized codes line: {codeLine}"),
        };
    }

    private static CharHandler ParseSimpleCodes(string[] codeStrings)
    {
        if (codeStrings.Length != 1)
        {
            throw new InvalidOperationException($"unexpected code strings {string.Join(",", codeStrings)}");
        }
        return new SimpleCharHandler(CodesToBytes(codeStrings[0], true)!);
    }

    private static CharHandler ParseInternationalCodes(string[] codeStrings)
    {
        if (codeStrings.Length != 2)
        {
            throw new InvalidOperationException($"unexpected code strings {string.Join(",", codeStrings)}");
        }
        return new InternationalCharHandler(CodesToBytes(codeStrings[0], true)!, CodesToBytes(codeStrings[1], true)!);
    }

    private static CharHandler ParseUnprintableCodes(string[] codeStrings)
    {
        if (codeStrings.Length != 1)
        {
            throw new InvalidOperationException($"unexpected code strings {string.Join(",", codeStrings)}");
        }
        return new UnprintableCharHandler(CodesToBytes(codeStrings[0], true)!);
    }

    private static CharHandler ParseUnprintableExtCodes(string[] codeStrings)
    {
        if (codeStrings.Length != 1)
        {
            throw new InvalidOperationException($"unexpected code strings {string.Join(",", codeStrings)}");
        }
        byte[] bytes = CodesToBytes(codeStrings[0], true)!;
        if (bytes.Length != 1)
        {
            throw new InvalidOperationException($"unexpected code strings {string.Join(",", codeStrings)}");
        }
        return new UnprintableExtCharHandler(bytes[0]);
    }

    private static CharHandler ParseInternationalExtCodes(string[] codeStrings)
    {
        if (codeStrings.Length != 3)
        {
            throw new InvalidOperationException($"unexpected code strings {string.Join(",", codeStrings)}");
        }

        byte crazyFlag = "1".Equals(codeStrings[2], StringComparison.Ordinal) ? CrazyCode1 : CrazyCode2;
        return new InternationalExtCharHandler(CodesToBytes(codeStrings[0], true)!, CodesToBytes(codeStrings[1], false), crazyFlag);
    }

    private static CharHandler ParseSignificantCodes(string[] codeStrings)
    {
        if (codeStrings.Length != 1)
        {
            throw new InvalidOperationException($"unexpected code strings {string.Join(",", codeStrings)}");
        }
        return new SignificantCharHandler(CodesToBytes(codeStrings[0], true)!);
    }

    /// <summary>
    /// Converts a string of hex encoded bytes to a byte[], optionally throwing an exception if no codes are given.
    /// </summary>
    private static byte[]? CodesToBytes(string codes, bool required)
    {
        if (codes.Length == 0)
        {
            if (required)
            {
                throw new InvalidOperationException("empty code bytes");
            }
            return null;
        }
        if (codes.Length % 2 != 0)
        {
            // stripped a leading 0
            codes = "0" + codes;
        }
        byte[] bytes = new byte[codes.Length / 2];
        for (int i = 0; i < bytes.Length; ++i)
        {
            int charIdx = i * 2;
            bytes[i] = (byte)Convert.ToInt32(codes.Substring(charIdx, 2), 16);
        }
        return bytes;
    }

    internal static int AsUnsignedChar(char c) => c & 0xFFFF;

    /// <summary>
    /// Converts an index value for a text column into the entry value (which is based on a variety of nifty codes).
    /// </summary>
    internal virtual void WriteNonNullIndexTextValue(object? value, ByteStream bout, bool isAscending)
    {
        // convert to string
        string str = ToIndexCharSequence(value);

        // record previous entry length so we can do any post-processing
        // necessary for this entry (handling descending)
        int prevLength = bout.Length;

        // now, convert each character to a "code" of one or more bytes
        ExtraCodesStream? extraCodes = null;
        ByteStream? unprintableCodes = null;
        ByteStream? crazyCodes = null;
        int charOffset = 0;
        for (int i = 0; i < str.Length; ++i)
        {
            char c = str[i];
            CharHandler ch = GetCharHandler(c);

            int curCharOffset = charOffset;
            byte[]? bytes = ch.GetInlineBytes(c);
            if (bytes != null)
            {
                // write the "inline" codes immediately
                bout.Write(bytes);

                // only increment the charOffset for chars with inline codes
                charOffset++;
            }

            if (ch.Type == CharType.Simple)
            {
                // common case, skip further code handling
                continue;
            }

            bytes = ch.GetExtraBytes();
            byte extraCodeModifier = ch.GetExtraByteModifier();
            if (bytes != null || extraCodeModifier != 0)
            {
                if (extraCodes == null)
                {
                    extraCodes = new ExtraCodesStream(str.Length);
                }

                // keep track of the extra codes for later
                WriteExtraCodes(curCharOffset, bytes, extraCodeModifier, extraCodes);
            }

            bytes = ch.GetUnprintableBytes();
            if (bytes != null)
            {
                if (unprintableCodes == null)
                {
                    unprintableCodes = new ByteStream();
                }

                // keep track of the unprintable codes for later
                WriteUnprintableCodes(curCharOffset, bytes, unprintableCodes, extraCodes);
            }

            byte crazyFlag = ch.GetCrazyFlag();
            if (crazyFlag != 0)
            {
                if (crazyCodes == null)
                {
                    crazyCodes = new ByteStream();
                }

                // keep track of the crazy flags for later
                crazyCodes.Write(crazyFlag);
            }
        }

        // write end text flag
        bout.Write(EndText);

        bool hasExtraCodes = TrimExtraCodes(extraCodes, (byte)0, InternationalExtraPlaceholder);
        bool hasUnprintableCodes = unprintableCodes != null;
        bool hasCrazyCodes = crazyCodes != null;
        if (hasExtraCodes || hasUnprintableCodes || hasCrazyCodes)
        {
            // we write all the international extra bytes first
            if (hasExtraCodes)
            {
                extraCodes!.WriteTo(bout);
            }

            if (hasCrazyCodes || hasUnprintableCodes)
            {
                // write 2 more end flags
                bout.Write(EndText);
                bout.Write(EndText);

                // next come the crazy flags
                if (hasCrazyCodes)
                {
                    WriteCrazyCodes(crazyCodes!, bout);

                    // if we are writing unprintable codes after this, tack on another
                    // code
                    if (hasUnprintableCodes)
                    {
                        bout.Write(CrazyCodesUnprintSuffix);
                    }
                }

                // then we write all the unprintable extra bytes
                if (hasUnprintableCodes)
                {
                    // write another end flag
                    bout.Write(EndText);

                    unprintableCodes!.WriteTo(bout);
                }
            }
        }

        // handle descending order by inverting the bytes
        if (!isAscending)
        {
            // we actually write the end byte before flipping the bytes, and write
            // another one after flipping
            bout.Write(EndExtraText);

            // flip the bytes that we have written thus far for this text value
            IndexCodes.FlipBytes(bout.GetBytes(), prevLength, bout.Length - prevLength);
        }

        // write end extra text
        bout.Write(EndExtraText);
    }

    protected static string ToIndexCharSequence(object? value)
    {
        // all text columns (including memos) are only indexed up to the max
        // number of chars in a VARCHAR column
        string str = value?.ToString() ?? "";
        int len = str.Length;
        if (len > MaxTextIndexCharLength)
        {
            str = str.Substring(0, MaxTextIndexCharLength);
            len = MaxTextIndexCharLength;
        }

        // trailing spaces are ignored for text index entries
        if (len > 0 && str[len - 1] == ' ')
        {
            do
            {
                len--;
            } while (len > 0 && str[len - 1] == ' ');

            str = str.Substring(0, len);
        }

        return str;
    }

    /// <summary>
    /// Encodes the given extra code info in the given stream.
    /// </summary>
    private static void WriteExtraCodes(int charOffset, byte[]? bytes, byte extraCodeModifier, ExtraCodesStream extraCodes)
    {
        // we fill in a placeholder value for any chars w/out extra codes
        int numChars = extraCodes.GetNumChars();
        if (numChars < charOffset)
        {
            int fillChars = charOffset - numChars;
            extraCodes.WriteFill(fillChars, InternationalExtraPlaceholder);
            extraCodes.IncrementNumChars(fillChars);
        }

        if (bytes != null)
        {
            // write the actual extra codes and update the number of chars
            extraCodes.Write(bytes);
            extraCodes.IncrementNumChars(1);
        }
        else
        {
            // extra code modifiers modify the existing extra code bytes and do not
            // count as additional extra code chars
            int lastIdx = extraCodes.Length - 1;
            if (lastIdx >= 0)
            {
                // the extra code modifier is added to the last extra code written
                byte lastByte = extraCodes.Get(lastIdx);
                lastByte += extraCodeModifier;
                extraCodes.Set(lastIdx, lastByte);
            }
            else
            {
                // there is no previous extra code, add a new code (but keep track of
                // this "unprintable code" prefix)
                extraCodes.Write(extraCodeModifier);
                extraCodes.SetUnprintablePrefixLen(1);
            }
        }
    }

    /// <summary>
    /// Trims any bytes in the given range off of the end of the given stream, returning whether or not there are any
    /// bytes left in the given stream after trimming.
    /// </summary>
    private static bool TrimExtraCodes(ByteStream? extraCodes, byte minTrimCode, byte maxTrimCode)
    {
        if (extraCodes == null)
        {
            return false;
        }

        extraCodes.TrimTrailing(minTrimCode, maxTrimCode);

        // anything left?
        return extraCodes.Length > 0;
    }

    /// <summary>
    /// Encodes the given unprintable char codes in the given stream.
    /// </summary>
    private static void WriteUnprintableCodes(int charOffset, byte[] bytes, ByteStream unprintableCodes, ExtraCodesStream? extraCodes)
    {
        // the offset seems to be calculated based on the number of bytes in the
        // "extra codes" part of the entry (even if there are no extra codes bytes
        // actually written in the final entry).
        int unprintCharOffset = charOffset;
        if (extraCodes != null)
        {
            // we need to account for some extra codes which have not been written
            // yet. additionally, any unprintable bytes added to the beginning of
            // the extra codes are ignored.
            unprintCharOffset = extraCodes.Length + charOffset - extraCodes.GetNumChars() - extraCodes.GetUnprintablePrefixLen();
        }

        // we write a whacky combo of bytes for each unprintable char which
        // includes a funky offset and extra char itself
        int offset = UnprintableCountStart + UnprintableCountMultiplier * unprintCharOffset | UnprintableOffsetFlags;

        // write offset as big-endian short
        unprintableCodes.Write(offset >> 8 & 0xFF);
        unprintableCodes.Write(offset & 0xFF);

        unprintableCodes.Write(UnprintableMidfix);
        unprintableCodes.Write(bytes);
    }

    /// <summary>
    /// Encode the given crazy code bytes into the given byte stream.
    /// </summary>
    private static void WriteCrazyCodes(ByteStream crazyCodes, ByteStream bout)
    {
        // CRAZY_CODE_2 flags at the end are ignored, so ditch them
        TrimExtraCodes(crazyCodes, CrazyCode2, CrazyCode2);

        if (crazyCodes.Length > 0)
        {
            // the crazy codes get encoded into 6 bit sequences where each code is 2
            // bits (where the first 2 bits in the byte are a common prefix).
            byte curByte = CrazyCodeStart;
            int idx = 0;
            for (int i = 0; i < crazyCodes.Length; ++i)
            {
                byte nextByte = crazyCodes.Get(i);
                nextByte = (byte)(nextByte << (2 - idx) * 2);
                curByte |= nextByte;

                idx++;
                if (idx == 3)
                {
                    // write current byte and reset
                    bout.Write(curByte);
                    curByte = CrazyCodeStart;
                    idx = 0;
                }
            }

            // write last byte
            if (idx > 0)
            {
                bout.Write(curByte);
            }
        }

        // write crazy code suffix (note, we write this even if all the codes are
        // trimmed
        bout.Write(CrazyCodesSuffix);
    }

    /// <summary>
    /// The types of char encoding strategies used when creating text index entries.
    /// </summary>
    internal enum CharType
    {
        Simple,
        International,
        Unprintable,
        UnprintableExt,
        InternationalExt,
        Significant,
        Surrogate,
        Ignored,
    }

    /// <summary>
    /// Holds the MS Access index byte-encoding information for a single Unicode character.
    /// </summary>
    internal abstract class CharHandler
    {
        public abstract CharType Type { get; }

        public virtual byte[]? GetInlineBytes(char c) => null;

        public virtual byte[]? GetExtraBytes() => null;

        public virtual byte[]? GetUnprintableBytes() => null;

        public virtual byte GetExtraByteModifier() => 0;

        public virtual byte GetCrazyFlag() => 0;

        public virtual bool IsSignificantChar() => false;
    }

    /// <summary>CharHandler for Type.Simple</summary>
    private sealed class SimpleCharHandler : CharHandler
    {
        private readonly byte[] _bytes;

        internal SimpleCharHandler(byte[] bytes)
        {
            _bytes = bytes;
        }

        public override CharType Type => CharType.Simple;

        public override byte[] GetInlineBytes(char c) => _bytes;
    }

    /// <summary>CharHandler for Type.International</summary>
    private sealed class InternationalCharHandler : CharHandler
    {
        private readonly byte[] _bytes;
        private readonly byte[] _extraBytes;

        internal InternationalCharHandler(byte[] bytes, byte[] extraBytes)
        {
            _bytes = bytes;
            _extraBytes = extraBytes;
        }

        public override CharType Type => CharType.International;

        public override byte[] GetInlineBytes(char c) => _bytes;

        public override byte[] GetExtraBytes() => _extraBytes;
    }

    /// <summary>CharHandler for Type.Unprintable</summary>
    private sealed class UnprintableCharHandler : CharHandler
    {
        private readonly byte[] _unprintBytes;

        internal UnprintableCharHandler(byte[] unprintBytes)
        {
            _unprintBytes = unprintBytes;
        }

        public override CharType Type => CharType.Unprintable;

        public override byte[] GetUnprintableBytes() => _unprintBytes;
    }

    /// <summary>CharHandler for Type.UnprintableExt</summary>
    private sealed class UnprintableExtCharHandler : CharHandler
    {
        private readonly byte _extraByteMod;

        internal UnprintableExtCharHandler(byte extraByteMod)
        {
            _extraByteMod = extraByteMod;
        }

        public override CharType Type => CharType.UnprintableExt;

        public override byte GetExtraByteModifier() => _extraByteMod;
    }

    /// <summary>CharHandler for Type.InternationalExt</summary>
    private sealed class InternationalExtCharHandler : CharHandler
    {
        private readonly byte[] _bytes;
        private readonly byte[]? _extraBytes;
        private readonly byte _crazyFlag;

        internal InternationalExtCharHandler(byte[] bytes, byte[]? extraBytes, byte crazyFlag)
        {
            _bytes = bytes;
            _extraBytes = extraBytes;
            _crazyFlag = crazyFlag;
        }

        public override CharType Type => CharType.InternationalExt;

        public override byte[] GetInlineBytes(char c) => _bytes;

        public override byte[]? GetExtraBytes() => _extraBytes;

        public override byte GetCrazyFlag() => _crazyFlag;
    }

    /// <summary>CharHandler for Type.Significant</summary>
    private sealed class SignificantCharHandler : CharHandler
    {
        private readonly byte[] _bytes;

        internal SignificantCharHandler(byte[] bytes)
        {
            _bytes = bytes;
        }

        public override CharType Type => CharType.Significant;

        public override byte[] GetInlineBytes(char c) => _bytes;

        public override bool IsSignificantChar() => true;
    }

    /// <summary>shared CharHandler instance for Type.Ignored</summary>
    internal static readonly CharHandler IgnoredCharHandler = new IgnoredCharHandlerClass();

    private sealed class IgnoredCharHandlerClass : CharHandler
    {
        public override CharType Type => CharType.Ignored;
    }

    /// <summary>the surrogate char buffers are computed on the fly. Re-use a buffer for those.</summary>
    [ThreadStatic]
    private static byte[]? _surrogateCharBuf;

    internal const byte SurrogateExtraByte = 0x3F;

    private static byte[] GetSurrogateCharBuf() => _surrogateCharBuf ??= new byte[2];

    private static byte[] ToSurrogateInlineBytes(int idxC)
    {
        byte[] bytes = GetSurrogateCharBuf();
        bytes[0] = (byte)((idxC >> 8) & 0xFF);
        bytes[1] = (byte)(idxC & 0xFF);
        return bytes;
    }

    /// <summary>shared CharHandler instance for "high surrogate" chars (which are computed)</summary>
    internal static readonly CharHandler HighSurrogateCharHandler = new SurrogateCharHandler(true);

    /// <summary>shared CharHandler instance for "low surrogate" chars (which are computed)</summary>
    internal static readonly CharHandler LowSurrogateCharHandler = new SurrogateCharHandler(false);

    private sealed class SurrogateCharHandler : CharHandler
    {
        private readonly bool _isHigh;

        internal SurrogateCharHandler(bool isHigh)
        {
            _isHigh = isHigh;
        }

        public override CharType Type => CharType.Surrogate;

        public override byte[] GetExtraBytes() => new[] { SurrogateExtraByte };

        public override byte[] GetInlineBytes(char c)
        {
            if (_isHigh)
            {
                // the high surrogate bytes seems to be computed from a fixed offset
                int idxC = AsUnsignedChar(c) - 10238;
                return ToSurrogateInlineBytes(idxC);
            }

            // the low surrogate bytes are computed with a specific value based on
            // its location in a 1024 character block.
            int charOffset = (AsUnsignedChar(c) - 0xDC00) % 1024;

            int idxOffset;
            if (charOffset < 8)
            {
                idxOffset = 9992;
            }
            else if (charOffset < 8 + 254)
            {
                idxOffset = 9990;
            }
            else if (charOffset < 8 + 254 + 254)
            {
                idxOffset = 9988;
            }
            else if (charOffset < 8 + 254 + 254 + 254)
            {
                idxOffset = 9986;
            }
            else
            {
                idxOffset = 9984;
            }
            int idxC2 = AsUnsignedChar(c) - idxOffset;
            return ToSurrogateInlineBytes(idxC2);
        }
    }

    /// <summary>
    /// Extension of ByteStream which keeps track of an additional char count and the length of any "unprintable" code
    /// prefix.
    /// </summary>
    private sealed class ExtraCodesStream : ByteStream
    {
        private int _numChars;
        private int _unprintablePrefixLen;

        internal ExtraCodesStream(int length)
            : base(length)
        {
        }

        internal int GetNumChars() => _numChars;

        internal void IncrementNumChars(int inc) => _numChars += inc;

        internal int GetUnprintablePrefixLen() => _unprintablePrefixLen;

        internal void SetUnprintablePrefixLen(int len) => _unprintablePrefixLen = len;
    }
}
