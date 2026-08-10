namespace UCanAccess.File;

/// <summary>
/// A logical index which is backed by an <see cref="IndexData"/> (port of Jackcess
/// <c>IndexImpl</c>; definition-reading part, cursor machinery added later).
/// </summary>
internal sealed class IndexImpl
{
    /// <summary>index type for primary key indexes</summary>
    internal const byte PrimaryKeyIndexType = 1;

    /// <summary>index type for foreign key indexes</summary>
    internal const byte ForeignKeyIndexType = 2;

    private const byte CascadeUpdatesFlag = 0x01;
    private const byte CascadeDeletesFlag = 0x02;
    private const byte CascadeNullFlag = 0x04;

    private readonly int _indexNumber;
    private readonly byte _indexType;
    private readonly IndexData _data;
    private string? _name;

    /// <summary>
    /// Reads a logical index from the table-definition buffer at the current position
    /// (port of Jackcess <c>IndexImpl(ByteBuffer, List&lt;IndexData&gt;, JetFormat)</c>).
    /// </summary>
    internal IndexImpl(byte[] tableBuffer, List<IndexData> indexDatas, JetFormat format, ref int position)
    {
        position += format.SkipBeforeIndexSlot;

        _indexNumber = ByteUtil.GetIntLittleEndian(tableBuffer, position);
        position += 4;
        int indexDataNumber = ByteUtil.GetIntLittleEndian(tableBuffer, position);
        position += 4;

        // read foreign key reference info
        position += 1; // relIndexType
        position += 4; // relIndexNumber
        position += 4; // relTablePageNumber
        position += 1; // cascadeUpdatesFlag
        position += 1; // cascadeDeletesFlag

        _indexType = tableBuffer[position];
        position += 1;

        position += format.SkipAfterIndexSlot;

        _data = indexDatas[indexDataNumber];
        _data.AddIndex(this);
    }

    internal int IndexNumber => _indexNumber;

    internal byte IndexType => _indexType;

    internal IndexData IndexData => _data;

    internal bool IsPrimaryKey => _indexType == PrimaryKeyIndexType;

    internal bool IsForeignKey => _indexType == ForeignKeyIndexType;

    internal string? Name
    {
        get => _name;
        set => _name = value;
    }
}
