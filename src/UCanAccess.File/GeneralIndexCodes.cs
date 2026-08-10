namespace UCanAccess.File;

/// <summary>
/// Encoding logic for MS Access "General" (Access 2010+) text index entries.
/// Port of Jackcess <c>GeneralIndexCodes</c>. Same byte-format as the legacy variant; only the
/// per-character code tables differ.
/// </summary>
internal sealed class GeneralIndexCodes : GeneralLegacyIndexCodes
{
    private const string CodesFile = "index_codes_gen.txt";
    private const string ExtCodesFile = "index_codes_ext_gen.txt";

    private static readonly CharHandler[] CodesValues = LoadCodes(CodesFile, FirstChar, LastChar);
    private static readonly CharHandler[] ExtCodesValues = LoadCodes(ExtCodesFile, FirstExtChar, LastExtChar);

    internal static readonly GeneralIndexCodes GenInstance = new();

    private GeneralIndexCodes()
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

        int extOffset = AsUnsignedChar(c) - AsUnsignedChar(FirstExtChar);
        return ExtCodesValues[extOffset];
    }
}