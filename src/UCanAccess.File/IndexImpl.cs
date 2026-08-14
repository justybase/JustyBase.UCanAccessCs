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
    private readonly byte _relatedTableType;
    private int _relatedIndexNumber;
    private readonly int _relatedIndexNumberOffset;
    private int _relatedTablePageNumber;
    private readonly int _relatedTablePageNumberOffset;
    private readonly bool _cascadeUpdates;
    private readonly bool _cascadeDeletes;
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
        _relatedTableType = tableBuffer[position++];
        _relatedIndexNumberOffset = position;
        _relatedIndexNumber = ByteUtil.GetIntLittleEndian(tableBuffer, position);
        position += 4;
        _relatedTablePageNumberOffset = position;
        _relatedTablePageNumber = ByteUtil.GetIntLittleEndian(tableBuffer, position);
        position += 4;
        _cascadeUpdates = tableBuffer[position++] != 0;
        _cascadeDeletes = tableBuffer[position++] != 0;

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

    internal byte RelatedTableType => _relatedTableType;

    internal int RelatedIndexNumber => _relatedIndexNumber;

    internal int RelatedTablePageNumber => _relatedTablePageNumber;

    /// <summary>offset of the related table-definition page in the table definition buffer</summary>
    internal int RelatedTablePageNumberOffset => _relatedTablePageNumberOffset;

    /// <summary>offset of the referenced parent index number in the table definition buffer</summary>
    internal int RelatedIndexNumberOffset => _relatedIndexNumberOffset;

    internal bool RetargetRelatedTablePage(int oldPageNumber, int newPageNumber)
    {
        if (!IsForeignKey || _relatedTablePageNumber != oldPageNumber)
        {
            return false;
        }
        _relatedTablePageNumber = newPageNumber;
        return true;
    }

    internal bool RetargetRelatedIndex(int oldIndexNumber, int newIndexNumber)
    {
        if (!IsForeignKey || _relatedIndexNumber != oldIndexNumber)
        {
            return false;
        }
        _relatedIndexNumber = newIndexNumber;
        return true;
    }

    internal bool CascadeUpdates => _cascadeUpdates;

    internal bool CascadeDeletes => _cascadeDeletes;

    internal string? Name
    {
        get => _name;
        set => _name = value;
    }
}
