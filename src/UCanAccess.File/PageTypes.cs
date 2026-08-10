namespace UCanAccess.File;

/// <summary>
/// Codes for database page types (port of Jackcess <c>PageTypes</c>).
/// </summary>
internal static class PageTypes
{
    /// <summary>invalid page type</summary>
    public const byte Invalid = 0x00;

    /// <summary>data page</summary>
    public const byte Data = 0x01;

    /// <summary>table definition page</summary>
    public const byte TableDef = 0x02;

    /// <summary>intermediate index page pointing to other index pages</summary>
    public const byte IndexNode = 0x03;

    /// <summary>leaf index page containing actual entries</summary>
    public const byte IndexLeaf = 0x04;

    /// <summary>table usage map page</summary>
    public const byte UsageMap = 0x05;
}
