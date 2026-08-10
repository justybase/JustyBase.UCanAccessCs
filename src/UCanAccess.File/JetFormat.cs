using System.Text;namespace UCanAccess.File;

/// <summary>
/// Encapsulates constants describing a specific version of the Access Jet format
/// (port of Jackcess <c>JetFormat</c>; VERSION_3 and VERSION_4 implemented).
/// </summary>
public sealed class JetFormat
{
    /// <summary>the "unit" size for text fields</summary>
    public const short TextFieldUnitSize = 2;

    /// <summary>maximum size of a text field (in bytes)</summary>
    public const short TextFieldMaxLength = 255 * TextFieldUnitSize;

    /// <summary>offset of the Jet format version byte in the file</summary>
    private const int OffsetVersion = 20;

    /// <summary>offset of the engine name in the header</summary>
    private const int OffsetEngineName = 0x4;

    /// <summary>length of the engine name in the header</summary>
    private const int LengthEngineName = 0xF;

    private static readonly byte[] MsisamEngine =
        Encoding.ASCII.GetBytes("MSISAM Database");

    /// <summary>mask used to obfuscate the db header</summary>
    private static readonly byte[] BaseHeaderMask =
    {
        0xB5, 0x6F, 0x03, 0x62, 0x61, 0x08, 0xC2, 0x55, 0xEB,
        0xA9, 0x67, 0x72, 0x43, 0x3F, 0x00, 0x9C, 0x7A, 0x9F,
        0x90, 0xFF, 0x80, 0x9A, 0x31, 0xC5, 0x79, 0xBA, 0xED,
        0x30, 0xBC, 0xDF, 0xCC, 0x9D, 0x63, 0xD9, 0xE4, 0xC3,
        0x7B, 0x42, 0xFB, 0x8A, 0xBC, 0x4E, 0x86, 0xFB, 0xEC,
        0x37, 0x5D, 0x44, 0x9C, 0xFA, 0xC6, 0x5E, 0x28, 0xE6,
        0x13, 0xB6, 0x8A, 0x60, 0x54, 0x94, 0x7B, 0x36, 0xF5,
        0x72, 0xDF, 0xB1, 0x77, 0xF4, 0x13, 0x43, 0xCF, 0xAF,
        0xB1, 0x33, 0x34, 0x61, 0x79, 0x5B, 0x92, 0xB5, 0x7C,
        0x2A, 0x05, 0xF1, 0x7C, 0x99, 0x01, 0x1B, 0x98, 0xFD,
        0x12, 0x4F, 0x4A, 0x94, 0x6C, 0x3E, 0x60, 0x26, 0x5F,
        0x95, 0xF8, 0xD0, 0x89, 0x24, 0x85, 0x67, 0xC6, 0x1F,
        0x27, 0x44, 0xD2, 0xEE, 0xCF, 0x65, 0xED, 0xFF, 0x07,
        0xC7, 0x46, 0xA1, 0x78, 0x16, 0x0C, 0xED, 0xE9, 0x2D,
        0x62, 0xD4
    };

    public static readonly JetFormat Version3 = new(
        "VERSION_3",
        readOnly: false,
        pageSize: 2048,
        offsetMaskedHeader: 24,
        headerMask: BaseHeaderMask.AsSpan(0, BaseHeaderMask.Length - 2).ToArray(),
        offsetHeaderDate: -1,
        offsetPassword: 66,
        sizePassword: 20,
        offsetSortOrder: 58,
        sizeSortOrder: 2,
        offsetCodePage: 60,
        offsetNextTableDefPage: 4,
        offsetNumRows: 12,
        offsetNextAutoNumber: 20,
        offsetNextComplexAutoNumber: -1,
        offsetTableType: 20,
        offsetMaxCols: 21,
        offsetNumVarCols: 23,
        offsetNumCols: 25,
        offsetNumIndexSlots: 27,
        offsetNumIndexes: 31,
        offsetOwnedPages: 35,
        offsetFreeSpacePages: 39,
        offsetIndexDefBlock: 43,
        sizeIndexColumnBlock: 39,
        sizeIndexInfoBlock: 20,
        sizeIndexDefinition: 8,
        offsetColumnType: 0,
        offsetColumnNumber: 1,
        offsetColumnPrecision: 11,
        offsetColumnScale: 12,
        offsetColumnSortOrder: 9,
        offsetColumnCodePage: 11,
        offsetColumnComplexId: -1,
        offsetColumnFlags: 13,
        offsetColumnExtFlags: -1,
        offsetColumnLength: 16,
        offsetColumnVariableTableIndex: 3,
        offsetColumnFixedDataOffset: 14,
        offsetColumnFixedDataRowOffset: 1,
        offsetRowStart: 10,
        offsetUsageMapStart: 5,
        offsetUsageMapPageData: 4,
        offsetReferenceMapPageNumbers: 1,
        offsetFreeSpace: 2,
        offsetNumRowsOnDataPage: 8,
        maxNumRowsOnDataPage: 255,
        offsetIndexCompressedByteCount: 20,
        offsetIndexEntryMask: 22,
        offsetPrevIndexPage: 8,
        offsetNextIndexPage: 12,
        offsetChildTailIndexPage: 16,
        sizeColumnHeader: 18,
        sizeRowLocation: 2,
        sizeLongValueDef: 12,
        maxInlineLongValueSize: 64,
        maxLongValueRowSize: 2032,
        maxCompressedUnicodeSize: 1024,
        sizeTdefHeader: 43,
        sizeTdefTrailer: 2,
        sizeColumnDefBlock: 25,
        sizeIndexEntryMask: 226,
        skipBeforeIndexFlags: 0,
        skipAfterIndexFlags: 0,
        skipBeforeIndexSlot: 0,
        skipAfterIndexSlot: 0,
        skipBeforeIndex: 0,
        sizeNameLength: 1,
        sizeRowColumnCount: 1,
        sizeRowVarColOffset: 1,
        usageMapTableByteLength: 128,
        maxColumnsPerTable: 255,
        maxIndexesPerTable: 32,
        maxTableNameLength: 64,
        maxColumnNameLength: 64,
        maxIndexNameLength: 64,
        sizeTextFieldUnit: 1,
        maxRowSize: 2012,
        charset: null,
        numericFixedSize: 17,
        indexesSupported: true,
        legacyNumericIndexes: true,
        defaultSortOrder: TextSortOrder.General97);

    public static readonly JetFormat Version4 = new(
        "VERSION_4",
        readOnly: false,
        pageSize: 4096,
        offsetMaskedHeader: 24,
        headerMask: BaseHeaderMask,
        offsetHeaderDate: 114,
        offsetPassword: 66,
        sizePassword: 40,
        offsetSortOrder: 110,
        sizeSortOrder: 4,
        offsetCodePage: 60,
        offsetNextTableDefPage: 4,
        offsetNumRows: 16,
        offsetNextAutoNumber: 20,
        offsetNextComplexAutoNumber: -1,
        offsetTableType: 40,
        offsetMaxCols: 41,
        offsetNumVarCols: 43,
        offsetNumCols: 45,
        offsetNumIndexSlots: 47,
        offsetNumIndexes: 51,
        offsetOwnedPages: 55,
        offsetFreeSpacePages: 59,
        offsetIndexDefBlock: 63,
        sizeIndexColumnBlock: 52,
        sizeIndexInfoBlock: 28,
        sizeIndexDefinition: 12,
        offsetColumnType: 0,
        offsetColumnNumber: 5,
        offsetColumnPrecision: 11,
        offsetColumnScale: 12,
        offsetColumnSortOrder: 11,
        offsetColumnCodePage: -1,
        offsetColumnComplexId: -1,
        offsetColumnFlags: 15,
        offsetColumnExtFlags: 16,
        offsetColumnLength: 23,
        offsetColumnVariableTableIndex: 7,
        offsetColumnFixedDataOffset: 21,
        offsetColumnFixedDataRowOffset: 2,
        offsetRowStart: 14,
        offsetUsageMapStart: 5,
        offsetUsageMapPageData: 4,
        offsetReferenceMapPageNumbers: 1,
        offsetFreeSpace: 2,
        offsetNumRowsOnDataPage: 12,
        maxNumRowsOnDataPage: 255,
        offsetIndexCompressedByteCount: 24,
        offsetIndexEntryMask: 27,
        offsetPrevIndexPage: 12,
        offsetNextIndexPage: 16,
        offsetChildTailIndexPage: 20,
        sizeColumnHeader: 25,
        sizeRowLocation: 2,
        sizeLongValueDef: 12,
        maxInlineLongValueSize: 64,
        maxLongValueRowSize: 4076,
        maxCompressedUnicodeSize: 1024,
        sizeTdefHeader: 63,
        sizeTdefTrailer: 2,
        sizeColumnDefBlock: 25,
        sizeIndexEntryMask: 453,
        skipBeforeIndexFlags: 4,
        skipAfterIndexFlags: 5,
        skipBeforeIndexSlot: 4,
        skipAfterIndexSlot: 4,
        skipBeforeIndex: 4,
        sizeNameLength: 2,
        sizeRowColumnCount: 2,
        sizeRowVarColOffset: 2,
        usageMapTableByteLength: 64,
        maxColumnsPerTable: 255,
        maxIndexesPerTable: 32,
        maxTableNameLength: 64,
        maxColumnNameLength: 64,
        maxIndexNameLength: 64,
        sizeTextFieldUnit: TextFieldUnitSize,
        maxRowSize: 4060,
        charset: Encoding.Unicode,
        numericFixedSize: 17,
        indexesSupported: true,
        legacyNumericIndexes: false,
        defaultSortOrder: TextSortOrder.GeneralLegacy);

    /// <summary>Access 2007 (.accdb, Jet 12); structurally Jet 4 with complex-type support.</summary>
    public static readonly JetFormat Version12 = new(
        "VERSION_12",
        readOnly: false,
        pageSize: 4096,
        offsetMaskedHeader: 24,
        headerMask: BaseHeaderMask,
        offsetHeaderDate: 114,
        offsetPassword: 66,
        sizePassword: 40,
        offsetSortOrder: 110,
        sizeSortOrder: 4,
        offsetCodePage: 60,
        offsetNextTableDefPage: 4,
        offsetNumRows: 16,
        offsetNextAutoNumber: 20,
        offsetNextComplexAutoNumber: 28,
        offsetTableType: 40,
        offsetMaxCols: 41,
        offsetNumVarCols: 43,
        offsetNumCols: 45,
        offsetNumIndexSlots: 47,
        offsetNumIndexes: 51,
        offsetOwnedPages: 55,
        offsetFreeSpacePages: 59,
        offsetIndexDefBlock: 63,
        sizeIndexColumnBlock: 52,
        sizeIndexInfoBlock: 28,
        sizeIndexDefinition: 12,
        offsetColumnType: 0,
        offsetColumnNumber: 5,
        offsetColumnPrecision: 11,
        offsetColumnScale: 12,
        offsetColumnSortOrder: 11,
        offsetColumnCodePage: -1,
        offsetColumnComplexId: 11,
        offsetColumnFlags: 15,
        offsetColumnExtFlags: 16,
        offsetColumnLength: 23,
        offsetColumnVariableTableIndex: 7,
        offsetColumnFixedDataOffset: 21,
        offsetColumnFixedDataRowOffset: 2,
        offsetRowStart: 14,
        offsetUsageMapStart: 5,
        offsetUsageMapPageData: 4,
        offsetReferenceMapPageNumbers: 1,
        offsetFreeSpace: 2,
        offsetNumRowsOnDataPage: 12,
        maxNumRowsOnDataPage: 255,
        offsetIndexCompressedByteCount: 24,
        offsetIndexEntryMask: 27,
        offsetPrevIndexPage: 12,
        offsetNextIndexPage: 16,
        offsetChildTailIndexPage: 20,
        sizeColumnHeader: 25,
        sizeRowLocation: 2,
        sizeLongValueDef: 12,
        maxInlineLongValueSize: 64,
        maxLongValueRowSize: 4076,
        maxCompressedUnicodeSize: 1024,
        sizeTdefHeader: 63,
        sizeTdefTrailer: 2,
        sizeColumnDefBlock: 25,
        sizeIndexEntryMask: 453,
        skipBeforeIndexFlags: 4,
        skipAfterIndexFlags: 5,
        skipBeforeIndexSlot: 4,
        skipAfterIndexSlot: 4,
        skipBeforeIndex: 4,
        sizeNameLength: 2,
        sizeRowColumnCount: 2,
        sizeRowVarColOffset: 2,
        usageMapTableByteLength: 64,
        maxColumnsPerTable: 255,
        maxIndexesPerTable: 32,
        maxTableNameLength: 64,
        maxColumnNameLength: 64,
        maxIndexNameLength: 64,
        sizeTextFieldUnit: TextFieldUnitSize,
        maxRowSize: 4060,
        charset: Encoding.Unicode,
        numericFixedSize: 17,
        indexesSupported: true,
        legacyNumericIndexes: false,
        defaultSortOrder: TextSortOrder.GeneralLegacy);

    /// <summary>Access 2010 (.accdb, Jet 14); adds calculated columns and the General sort order.</summary>
    public static readonly JetFormat Version14 = new(
        "VERSION_14",
        readOnly: false,
        pageSize: 4096,
        offsetMaskedHeader: 24,
        headerMask: BaseHeaderMask,
        offsetHeaderDate: 114,
        offsetPassword: 66,
        sizePassword: 40,
        offsetSortOrder: 110,
        sizeSortOrder: 4,
        offsetCodePage: 60,
        offsetNextTableDefPage: 4,
        offsetNumRows: 16,
        offsetNextAutoNumber: 20,
        offsetNextComplexAutoNumber: 28,
        offsetTableType: 40,
        offsetMaxCols: 41,
        offsetNumVarCols: 43,
        offsetNumCols: 45,
        offsetNumIndexSlots: 47,
        offsetNumIndexes: 51,
        offsetOwnedPages: 55,
        offsetFreeSpacePages: 59,
        offsetIndexDefBlock: 63,
        sizeIndexColumnBlock: 52,
        sizeIndexInfoBlock: 28,
        sizeIndexDefinition: 12,
        offsetColumnType: 0,
        offsetColumnNumber: 5,
        offsetColumnPrecision: 11,
        offsetColumnScale: 12,
        offsetColumnSortOrder: 11,
        offsetColumnCodePage: -1,
        offsetColumnComplexId: 11,
        offsetColumnFlags: 15,
        offsetColumnExtFlags: 16,
        offsetColumnLength: 23,
        offsetColumnVariableTableIndex: 7,
        offsetColumnFixedDataOffset: 21,
        offsetColumnFixedDataRowOffset: 2,
        offsetRowStart: 14,
        offsetUsageMapStart: 5,
        offsetUsageMapPageData: 4,
        offsetReferenceMapPageNumbers: 1,
        offsetFreeSpace: 2,
        offsetNumRowsOnDataPage: 12,
        maxNumRowsOnDataPage: 255,
        offsetIndexCompressedByteCount: 24,
        offsetIndexEntryMask: 27,
        offsetPrevIndexPage: 12,
        offsetNextIndexPage: 16,
        offsetChildTailIndexPage: 20,
        sizeColumnHeader: 25,
        sizeRowLocation: 2,
        sizeLongValueDef: 12,
        maxInlineLongValueSize: 64,
        maxLongValueRowSize: 4076,
        maxCompressedUnicodeSize: 1024,
        sizeTdefHeader: 63,
        sizeTdefTrailer: 2,
        sizeColumnDefBlock: 25,
        sizeIndexEntryMask: 453,
        skipBeforeIndexFlags: 4,
        skipAfterIndexFlags: 5,
        skipBeforeIndexSlot: 4,
        skipAfterIndexSlot: 4,
        skipBeforeIndex: 4,
        sizeNameLength: 2,
        sizeRowColumnCount: 2,
        sizeRowVarColOffset: 2,
        usageMapTableByteLength: 64,
        maxColumnsPerTable: 255,
        maxIndexesPerTable: 32,
        maxTableNameLength: 64,
        maxColumnNameLength: 64,
        maxIndexNameLength: 64,
        sizeTextFieldUnit: TextFieldUnitSize,
        maxRowSize: 4060,
        charset: Encoding.Unicode,
        numericFixedSize: 17,
        indexesSupported: true,
        legacyNumericIndexes: false,
        defaultSortOrder: TextSortOrder.General);

    /// <summary>Access 2016/2019 (.accdb, Jet 16); same structure as Jet 14 for basic tables.</summary>
    public static readonly JetFormat Version16 = new(
        "VERSION_16",
        readOnly: false,
        pageSize: 4096,
        offsetMaskedHeader: 24,
        headerMask: BaseHeaderMask,
        offsetHeaderDate: 114,
        offsetPassword: 66,
        sizePassword: 40,
        offsetSortOrder: 110,
        sizeSortOrder: 4,
        offsetCodePage: 60,
        offsetNextTableDefPage: 4,
        offsetNumRows: 16,
        offsetNextAutoNumber: 20,
        offsetNextComplexAutoNumber: 28,
        offsetTableType: 40,
        offsetMaxCols: 41,
        offsetNumVarCols: 43,
        offsetNumCols: 45,
        offsetNumIndexSlots: 47,
        offsetNumIndexes: 51,
        offsetOwnedPages: 55,
        offsetFreeSpacePages: 59,
        offsetIndexDefBlock: 63,
        sizeIndexColumnBlock: 52,
        sizeIndexInfoBlock: 28,
        sizeIndexDefinition: 12,
        offsetColumnType: 0,
        offsetColumnNumber: 5,
        offsetColumnPrecision: 11,
        offsetColumnScale: 12,
        offsetColumnSortOrder: 11,
        offsetColumnCodePage: -1,
        offsetColumnComplexId: 11,
        offsetColumnFlags: 15,
        offsetColumnExtFlags: 16,
        offsetColumnLength: 23,
        offsetColumnVariableTableIndex: 7,
        offsetColumnFixedDataOffset: 21,
        offsetColumnFixedDataRowOffset: 2,
        offsetRowStart: 14,
        offsetUsageMapStart: 5,
        offsetUsageMapPageData: 4,
        offsetReferenceMapPageNumbers: 1,
        offsetFreeSpace: 2,
        offsetNumRowsOnDataPage: 12,
        maxNumRowsOnDataPage: 255,
        offsetIndexCompressedByteCount: 24,
        offsetIndexEntryMask: 27,
        offsetPrevIndexPage: 12,
        offsetNextIndexPage: 16,
        offsetChildTailIndexPage: 20,
        sizeColumnHeader: 25,
        sizeRowLocation: 2,
        sizeLongValueDef: 12,
        maxInlineLongValueSize: 64,
        maxLongValueRowSize: 4076,
        maxCompressedUnicodeSize: 1024,
        sizeTdefHeader: 63,
        sizeTdefTrailer: 2,
        sizeColumnDefBlock: 25,
        sizeIndexEntryMask: 453,
        skipBeforeIndexFlags: 4,
        skipAfterIndexFlags: 5,
        skipBeforeIndexSlot: 4,
        skipAfterIndexSlot: 4,
        skipBeforeIndex: 4,
        sizeNameLength: 2,
        sizeRowColumnCount: 2,
        sizeRowVarColOffset: 2,
        usageMapTableByteLength: 64,
        maxColumnsPerTable: 255,
        maxIndexesPerTable: 32,
        maxTableNameLength: 64,
        maxColumnNameLength: 64,
        maxIndexNameLength: 64,
        sizeTextFieldUnit: TextFieldUnitSize,
        maxRowSize: 4060,
        charset: Encoding.Unicode,
        numericFixedSize: 17,
        indexesSupported: true,
        legacyNumericIndexes: false,
        defaultSortOrder: TextSortOrder.General);

    private JetFormat(
        string name,
        bool readOnly,
        int pageSize,
        int offsetMaskedHeader,
        byte[] headerMask,
        int offsetHeaderDate,
        int offsetPassword,
        int sizePassword,
        int offsetSortOrder,
        int sizeSortOrder,
        int offsetCodePage,
        int offsetNextTableDefPage,
        int offsetNumRows,
        int offsetNextAutoNumber,
        int offsetNextComplexAutoNumber,
        int offsetTableType,
        int offsetMaxCols,
        int offsetNumVarCols,
        int offsetNumCols,
        int offsetNumIndexSlots,
        int offsetNumIndexes,
        int offsetOwnedPages,
        int offsetFreeSpacePages,
        int offsetIndexDefBlock,
        int sizeIndexColumnBlock,
        int sizeIndexInfoBlock,
        int sizeIndexDefinition,
        int offsetColumnType,
        int offsetColumnNumber,
        int offsetColumnPrecision,
        int offsetColumnScale,
        int offsetColumnSortOrder,
        int offsetColumnCodePage,
        int offsetColumnComplexId,
        int offsetColumnFlags,
        int offsetColumnExtFlags,
        int offsetColumnLength,
        int offsetColumnVariableTableIndex,
        int offsetColumnFixedDataOffset,
        int offsetColumnFixedDataRowOffset,
        int offsetRowStart,
        int offsetUsageMapStart,
        int offsetUsageMapPageData,
        int offsetReferenceMapPageNumbers,
        int offsetFreeSpace,
        int offsetNumRowsOnDataPage,
        int maxNumRowsOnDataPage,
        int offsetIndexCompressedByteCount,
        int offsetIndexEntryMask,
        int offsetPrevIndexPage,
        int offsetNextIndexPage,
        int offsetChildTailIndexPage,
        int sizeColumnHeader,
        int sizeRowLocation,
        int sizeLongValueDef,
        int maxInlineLongValueSize,
        int maxLongValueRowSize,
        int maxCompressedUnicodeSize,
        int sizeTdefHeader,
        int sizeTdefTrailer,
        int sizeColumnDefBlock,
        int sizeIndexEntryMask,
        int skipBeforeIndexFlags,
        int skipAfterIndexFlags,
        int skipBeforeIndexSlot,
        int skipAfterIndexSlot,
        int skipBeforeIndex,
        int sizeNameLength,
        int sizeRowColumnCount,
        int sizeRowVarColOffset,
        int usageMapTableByteLength,
        int maxColumnsPerTable,
        int maxIndexesPerTable,
        int maxTableNameLength,
        int maxColumnNameLength,
        int maxIndexNameLength,
        int sizeTextFieldUnit,
        int maxRowSize,
        Encoding? charset,
        int numericFixedSize,
        bool indexesSupported,
        bool legacyNumericIndexes,
        TextSortOrder defaultSortOrder)
    {
        Name = name;
        ReadOnly = readOnly;
        PageSize = pageSize;
        OffsetMaskedHeader = offsetMaskedHeader;
        HeaderMask = headerMask;
        OffsetHeaderDate = offsetHeaderDate;
        OffsetPassword = offsetPassword;
        SizePassword = sizePassword;
        OffsetSortOrder = offsetSortOrder;
        SizeSortOrder = sizeSortOrder;
        OffsetCodePage = offsetCodePage;
        OffsetNextTableDefPage = offsetNextTableDefPage;
        OffsetNumRows = offsetNumRows;
        OffsetNextAutoNumber = offsetNextAutoNumber;
        OffsetNextComplexAutoNumber = offsetNextComplexAutoNumber;
        OffsetTableType = offsetTableType;
        OffsetMaxCols = offsetMaxCols;
        OffsetNumVarCols = offsetNumVarCols;
        OffsetNumCols = offsetNumCols;
        OffsetNumIndexSlots = offsetNumIndexSlots;
        OffsetNumIndexes = offsetNumIndexes;
        OffsetOwnedPages = offsetOwnedPages;
        OffsetFreeSpacePages = offsetFreeSpacePages;
        OffsetIndexDefBlock = offsetIndexDefBlock;
        SizeIndexColumnBlock = sizeIndexColumnBlock;
        SizeIndexInfoBlock = sizeIndexInfoBlock;
        SizeIndexDefinition = sizeIndexDefinition;
        OffsetColumnType = offsetColumnType;
        OffsetColumnNumber = offsetColumnNumber;
        OffsetColumnPrecision = offsetColumnPrecision;
        OffsetColumnScale = offsetColumnScale;
        OffsetColumnSortOrder = offsetColumnSortOrder;
        OffsetColumnCodePage = offsetColumnCodePage;
        OffsetColumnComplexId = offsetColumnComplexId;
        OffsetColumnFlags = offsetColumnFlags;
        OffsetColumnExtFlags = offsetColumnExtFlags;
        OffsetColumnLength = offsetColumnLength;
        OffsetColumnVariableTableIndex = offsetColumnVariableTableIndex;
        OffsetColumnFixedDataOffset = offsetColumnFixedDataOffset;
        OffsetColumnFixedDataRowOffset = offsetColumnFixedDataRowOffset;
        OffsetRowStart = offsetRowStart;
        OffsetUsageMapStart = offsetUsageMapStart;
        OffsetUsageMapPageData = offsetUsageMapPageData;
        OffsetReferenceMapPageNumbers = offsetReferenceMapPageNumbers;
        OffsetFreeSpace = offsetFreeSpace;
        OffsetNumRowsOnDataPage = offsetNumRowsOnDataPage;
        MaxNumRowsOnDataPage = maxNumRowsOnDataPage;
        OffsetIndexCompressedByteCount = offsetIndexCompressedByteCount;
        OffsetIndexEntryMask = offsetIndexEntryMask;
        OffsetPrevIndexPage = offsetPrevIndexPage;
        OffsetNextIndexPage = offsetNextIndexPage;
        OffsetChildTailIndexPage = offsetChildTailIndexPage;
        SizeColumnHeader = sizeColumnHeader;
        SizeRowLocation = sizeRowLocation;
        SizeLongValueDef = sizeLongValueDef;
        MaxInlineLongValueSize = maxInlineLongValueSize;
        MaxLongValueRowSize = maxLongValueRowSize;
        MaxCompressedUnicodeSize = maxCompressedUnicodeSize;
        SizeTdefHeader = sizeTdefHeader;
        SizeTdefTrailer = sizeTdefTrailer;
        SizeColumnDefBlock = sizeColumnDefBlock;
        SizeIndexEntryMask = sizeIndexEntryMask;
        SkipBeforeIndexFlags = skipBeforeIndexFlags;
        SkipAfterIndexFlags = skipAfterIndexFlags;
        SkipBeforeIndexSlot = skipBeforeIndexSlot;
        SkipAfterIndexSlot = skipAfterIndexSlot;
        SkipBeforeIndex = skipBeforeIndex;
        SizeNameLength = sizeNameLength;
        SizeRowColumnCount = sizeRowColumnCount;
        SizeRowVarColOffset = sizeRowVarColOffset;
        UsageMapTableByteLength = usageMapTableByteLength;
        MaxColumnsPerTable = maxColumnsPerTable;
        MaxIndexesPerTable = maxIndexesPerTable;
        MaxTableNameLength = maxTableNameLength;
        MaxColumnNameLength = maxColumnNameLength;
        MaxIndexNameLength = maxIndexNameLength;
        SizeTextFieldUnit = sizeTextFieldUnit;
        MaxRowSize = maxRowSize;
        Charset = charset;
        NumericFixedSize = numericFixedSize;
        IndexesSupported = indexesSupported;
        LegacyNumericIndexes = legacyNumericIndexes;
        DefaultSortOrder = defaultSortOrder;
    }

    public string Name { get; }

    /// <summary>whether this format supports writes</summary>
    public bool ReadOnly { get; }

    /// <summary>database page size in bytes</summary>
    public int PageSize { get; }

    public int OffsetMaskedHeader { get; }
    public byte[] HeaderMask { get; }
    public int OffsetHeaderDate { get; }
    public int OffsetPassword { get; }
    public int SizePassword { get; }
    public int OffsetSortOrder { get; }
    public int SizeSortOrder { get; }
    public int OffsetCodePage { get; }
    public int OffsetNextTableDefPage { get; }
    public int OffsetNumRows { get; }
    public int OffsetNextAutoNumber { get; }
    public int OffsetNextComplexAutoNumber { get; }
    public int OffsetTableType { get; }
    public int OffsetMaxCols { get; }
    public int OffsetNumVarCols { get; }
    public int OffsetNumCols { get; }
    public int OffsetNumIndexSlots { get; }
    public int OffsetNumIndexes { get; }
    public int OffsetOwnedPages { get; }
    public int OffsetFreeSpacePages { get; }
    public int OffsetIndexDefBlock { get; }
    public int SizeIndexColumnBlock { get; }
    public int SizeIndexInfoBlock { get; }
    public int SizeIndexDefinition { get; }
    public int OffsetColumnType { get; }
    public int OffsetColumnNumber { get; }
    public int OffsetColumnPrecision { get; }
    public int OffsetColumnScale { get; }
    public int OffsetColumnSortOrder { get; }
    public int OffsetColumnCodePage { get; }
    public int OffsetColumnComplexId { get; }
    public int OffsetColumnFlags { get; }
    public int OffsetColumnExtFlags { get; }
    public int OffsetColumnLength { get; }
    public int OffsetColumnVariableTableIndex { get; }
    public int OffsetColumnFixedDataOffset { get; }
    public int OffsetColumnFixedDataRowOffset { get; }
    public int OffsetRowStart { get; }
    public int OffsetUsageMapStart { get; }
    public int OffsetUsageMapPageData { get; }
    public int OffsetReferenceMapPageNumbers { get; }
    public int OffsetFreeSpace { get; }
    public int OffsetNumRowsOnDataPage { get; }
    public int MaxNumRowsOnDataPage { get; }
    public int OffsetIndexCompressedByteCount { get; }
    public int OffsetIndexEntryMask { get; }
    public int OffsetPrevIndexPage { get; }
    public int OffsetNextIndexPage { get; }
    public int OffsetChildTailIndexPage { get; }
    public int SizeColumnHeader { get; }
    public int SizeRowLocation { get; }
    public int SizeLongValueDef { get; }
    public int MaxInlineLongValueSize { get; }
    public int MaxLongValueRowSize { get; }
    public int MaxCompressedUnicodeSize { get; }
    public int SizeTdefHeader { get; }
    public int SizeTdefTrailer { get; }
    public int SizeColumnDefBlock { get; }
    public int SizeIndexEntryMask { get; }
    public int SkipBeforeIndexFlags { get; }
    public int SkipAfterIndexFlags { get; }
    public int SkipBeforeIndexSlot { get; }
    public int SkipAfterIndexSlot { get; }
    public int SkipBeforeIndex { get; }
    public int SizeNameLength { get; }
    public int SizeRowColumnCount { get; }
    public int SizeRowVarColOffset { get; }
    public int UsageMapTableByteLength { get; }
    public int MaxColumnsPerTable { get; }
    public int MaxIndexesPerTable { get; }
    public int MaxTableNameLength { get; }
    public int MaxColumnNameLength { get; }
    public int MaxIndexNameLength { get; }
    public int SizeTextFieldUnit { get; }
    public int MaxRowSize { get; }

    /// <summary>number of bytes for a NUMERIC column value</summary>
    public int NumericFixedSize { get; }

    /// <summary>whether this format supports indexes</summary>
    public bool IndexesSupported { get; }

    /// <summary>whether numeric index entries use the legacy (Access 2000) byte ordering</summary>
    public bool LegacyNumericIndexes { get; }

    /// <summary>the default collating sort order for text columns</summary>
    public TextSortOrder DefaultSortOrder { get; }

    /// <summary>maximum database size in bytes (2 GB for Jet 4, 1 GB for Jet 3)</summary>
    public long MaxDatabaseSize => PageSize == 2048 ? 1L << 30 : 2L << 30;

    /// <summary>initial free space reported by a fresh data page</summary>
    public int DataPageInitialFreeSpace => PageSize - 14;

    /// <summary>the default charset for text (UTF-16LE for Jet4; null for Jet3 which uses the code page)</summary>
    public Encoding? Charset { get; }

    public override string ToString() => Name;

    /// <summary>
    /// Determines the Jet format of the given file by examining the version byte.
    /// </summary>
    public static JetFormat GetFormat(byte[] header)
    {
        byte version = header[OffsetVersion];
        switch (version)
        {
            case 0x0:
                return Version3;
            case 0x1:
                return ByteUtil.MatchesRange(header, OffsetEngineName, MsisamEngine)
                    ? throw new DatabaseException("MSISAM format is not supported yet.")
                    : Version4;
            case 0x2:
                return Version12;
            case 0x3:
                return Version14;
            case 0x5:
                return Version16;
            default:
                throw new DatabaseException($"Unsupported {(version < 0x0 ? "older" : "newer")} version: {version}");
        }
    }

    /// <summary>
    /// Returns a charset for the given code page (Jet 3 databases), falling back to the platform default.
    /// </summary>
    public static Encoding GetEncodingForCodePage(int codePage)
    {
        if (codePage > 0)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(codePage);
            }
            catch (Exception)
            {
                // unknown code page, fall back to default
            }
        }
        return Encoding.Default;
    }
}
