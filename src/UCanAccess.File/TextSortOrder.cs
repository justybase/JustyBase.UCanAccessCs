namespace UCanAccess.File;

/// <summary>
/// The collating sort order of a text column, identified by LCID and a version byte
/// (port of Jackcess <c>ColumnImpl.SortOrder</c>).
/// </summary>
public readonly struct TextSortOrder : IEquatable<TextSortOrder>
{
    /// <summary>Windows LCID for the "General" English/default sort order used by all three General variants.</summary>
    private const short GeneralSortOrderValue = 1033;

    /// <summary>Sort order used by Access 97 databases (LCID 1033, version -1), encoded by <see cref="General97IndexCodes"/>.</summary>
    public static readonly TextSortOrder General97 = new(GeneralSortOrderValue, -1);

    /// <summary>Sort order used by Access 2000-2007 databases (LCID 1033, version 0), encoded by <see cref="GeneralLegacyIndexCodes"/>.</summary>
    public static readonly TextSortOrder GeneralLegacy = new(GeneralSortOrderValue, 0);

    /// <summary>Sort order used by Access 2010 and later databases (LCID 1033, version 1), encoded by <see cref="GeneralIndexCodes"/>.</summary>
    public static readonly TextSortOrder General = new(GeneralSortOrderValue, 1);

    /// <summary>Sort order used by databases configured with the Polish collation (LCID 1045, version 0).</summary>
    public static readonly TextSortOrder Polish = new(1045, 0);

    /// <summary>Sort order used by databases configured with the Russian/Cyrillic collation (LCID 1049, version 0).</summary>
    public static readonly TextSortOrder Russian = new(1049, 0);

    /// <summary>Sort order used by databases configured with the Turkish collation (LCID 1055, version 0).</summary>
    public static readonly TextSortOrder Turkish = new(1055, 0);

    /// <summary>Sort order used by databases configured with the Ukrainian/Cyrillic collation (LCID 1058, version 0).</summary>
    public static readonly TextSortOrder Ukrainian = new(1058, 0);

    public TextSortOrder(short value, short version)
    {
        Value = value;
        Version = version;
    }

    /// <summary>the LCID of the collation</summary>
    public short Value { get; }

    /// <summary>the collation version (varies per file format)</summary>
    public short Version { get; }

    public bool Equals(TextSortOrder other) => Value == other.Value && Version == other.Version;

    public override bool Equals(object? obj) => obj is TextSortOrder other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Value, Version);

    public override string ToString() => $"LCID {Value}, version {Version}";
}
