namespace UCanAccess.File;

/// <summary>
/// Encoding logic for MS Access "General 97" text index entries (Access 97; LCID 1033, version -1).
/// Port of Jackcess <c>General97IndexCodes</c>.
/// </summary>
internal sealed class General97IndexCodes : GeneralLegacyIndexCodes
{
    private const string CodesFile = "index_codes_gen_97.txt";
    private const string ExtMappingsFile = "index_mappings_ext_gen_97.txt";

    // we only have a small range of extended chars which can be mapped back into
    // the valid chars
    private const char FirstMapChar = '\x0152';
    private const char LastMapChar = '\x2122';

    private const byte ExtCodesBoundsNibble = 0x00;

    private static readonly CharHandler[] CodesValues = LoadCodes(CodesFile, FirstChar, LastChar);

    // mappings for a small subset of the rest of the chars in BMP 0. since these
    // codes are for single byte encodings, you would think you wouldn't need any
    // ext codes; however, some chars in the extended range have corollaries in the
    // single byte range. this array holds the mappings from the ext range to the
    // single byte range. chars without mappings go to 0 (ignored).
    private static readonly short[] ExtMappingsValues = LoadMappings(ExtMappingsFile, FirstMapChar, LastMapChar);

    internal static readonly General97IndexCodes Gen97Instance = new();

    private General97IndexCodes()
    {
    }

    /// <summary>
    /// Returns the CharHandler for the given character.
    /// </summary>
    internal override CharHandler GetCharHandler(char c)
    {
        if (c <= LastChar)
        {
            return CodesValues[c];
        }

        if (c < FirstMapChar || c > LastMapChar)
        {
            // outside the mapped range, ignored
            return IgnoredCharHandler;
        }

        // some ext chars are equivalent to single byte chars. most chars have no
        // equivalent, and they map to 0 (which is an "ignored" char, so it all
        // works out)
        int extOffset = AsUnsignedChar(c) - AsUnsignedChar(FirstMapChar);
        return CodesValues[ExtMappingsValues[extOffset]];
    }

    /// <summary>
    /// Converts a 97 index value for a text column into the entry value (which is based on a variety of nifty codes).
    /// </summary>
    internal override void WriteNonNullIndexTextValue(object? value, ByteStream bout, bool isAscending)
    {
        // convert to string
        string str = ToIndexCharSequence(value);

        // record previous entry length so we can do any post-processing
        // necessary for this entry (handling descending)
        int prevLength = bout.Length;

        // now, convert each character to a "code" of one or more bytes
        NibbleStream? extraCodes = null;
        int sigCharCount = 0;
        for (int i = 0; i < str.Length; ++i)
        {
            char c = str[i];
            CharHandler ch = GetCharHandler(c);

            byte[]? bytes = ch.GetInlineBytes(c);
            if (bytes != null)
            {
                // write the "inline" codes immediately
                bout.Write(bytes);
            }

            if (ch.Type == CharType.Simple)
            {
                // common case, skip further code handling
                continue;
            }

            if (ch.IsSignificantChar())
            {
                sigCharCount++;
                // significant chars never have extra bytes
                continue;
            }

            bytes = ch.GetExtraBytes();
            if (bytes != null)
            {
                if (extraCodes == null)
                {
                    extraCodes = new NibbleStream(str.Length);
                    extraCodes.WriteNibble(ExtCodesBoundsNibble);
                }

                // keep track of the extra code for later
                WriteExtraCodes(sigCharCount, bytes, extraCodes);
                sigCharCount = 0;
            }
        }

        if (extraCodes != null)
        {
            // write the extra codes to the end
            extraCodes.WriteNibble(ExtCodesBoundsNibble);
            extraCodes.WriteTo(bout);
        }
        else
        {
            // write end extra text
            bout.Write(EndExtraText);
        }

        // handle descending order by inverting the bytes
        if (!isAscending)
        {
            // flip the bytes that we have written thus far for this text value
            IndexCodes.FlipBytes(bout.GetBytes(), prevLength, bout.Length - prevLength);
        }
    }

    private static void WriteExtraCodes(int numSigChars, byte[] bytes, NibbleStream extraCodes)
    {
        // need to fill in placeholder nibbles for any "significant" chars
        if (numSigChars > 0)
        {
            extraCodes.WriteFillNibbles(numSigChars, InternationalExtraPlaceholder);
        }

        // there should only ever be a single "extra" byte
        extraCodes.WriteNibble(bytes[0]);
    }

    internal static short[] LoadMappings(string mappingsFilePath, char firstChar, char lastChar)
    {
        int firstCharCode = AsUnsignedChar(firstChar);
        int numMappings = AsUnsignedChar(lastChar) - firstCharCode + 1;
        var values = new short[numMappings];

        string[] lines = ReadResourceLines(mappingsFilePath);
        foreach (string mappingLine in lines)
        {
            string trimmed = mappingLine.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            string[] mappings = trimmed.Split(',');
            int fromCode = int.Parse(mappings[0]);
            int toCode = int.Parse(mappings[1]);

            values[fromCode - firstCharCode] = (short)toCode;
        }

        return values;
    }

    /// <summary>
    /// Extension of ByteStream which enables writing individual nibbles.
    /// </summary>
    private sealed class NibbleStream : ByteStream
    {
        private int _nibbleLen;

        internal NibbleStream(int length)
            : base(length)
        {
        }

        public bool NextIsHi => _nibbleLen % 2 == 0;

        private static int AsLowNibble(int b) => b & 0x0F;

        private static int AsHiNibble(int b) => b << 4 & 0xF0;

        private void WriteLowNibble(int b)
        {
            int byteOff = _nibbleLen / 2;
            SetBits(byteOff, (byte)AsLowNibble(b));
        }

        internal void WriteNibble(int b)
        {
            if (NextIsHi)
            {
                Write(AsHiNibble(b));
            }
            else
            {
                WriteLowNibble(b);
            }

            ++_nibbleLen;
        }

        internal void WriteFillNibbles(int length, byte b)
        {
            int newNibbleLen = _nibbleLen + length;
            EnsureCapacity((newNibbleLen + 1) / 2);

            if (!NextIsHi)
            {
                WriteLowNibble(b);
                length--;
            }

            if (length > 1)
            {
                byte doubleB = (byte)(AsHiNibble(b) | AsLowNibble(b));

                do
                {
                    Write(doubleB);
                    length -= 2;
                } while (length > 1);
            }

            if (length == 1)
            {
                Write(AsHiNibble(b));
            }

            _nibbleLen = newNibbleLen;
        }
    }
}