using System.Text;

namespace UCanAccess.File;

/// <summary>
/// The physical data behind an index: its definition (read from the table-definition
/// buffer) plus the machinery to maintain its B-tree pages (port of Jackcess
/// <c>IndexData</c>; definition-reading part, write machinery added in later batches).
/// </summary>
internal sealed class IndexData
{
    /// <summary>max number of columns in an index</summary>
    internal const int MaxColumns = 10;

    /// <summary>sentinel for an unused index column slot</summary>
    internal const short ColumnUnused = -1;

    /// <summary>column flag: ascending order (unset = descending)</summary>
    internal const byte AscendingColumnFlag = 0x01;

    /// <summary>index flag: unique entries</summary>
    internal const byte UniqueIndexFlag = 0x01;

    /// <summary>index flag: ignore null values</summary>
    internal const byte IgnoreNullsIndexFlag = 0x02;

    /// <summary>index flag: values required</summary>
    internal const byte RequiredIndexFlag = 0x08;

    /// <summary>index flag: unknown (always set on indexes in Access 2000+)</summary>
    internal const byte UnknownIndexFlag = 0x80;

    /// <summary>constant magic value at the start of every index definition block</summary>
    internal const int MagicIndexNumber = 1923;

    internal const int InvalidIndexPageNumber = 0;

    private readonly Table _table;
    private readonly int _number;
    private int _uniqueEntryCount;
    private readonly int _uniqueEntryCountOffset;
    private readonly List<ColumnDescriptor> _columns = new();
    private readonly List<IndexImpl> _indexes = new();
    private byte _indexFlags;
    private int _rootPageNumber;
    private UsageMap? _ownedPages;
    private bool _backingPrimaryKey;
    private string? _name;
    private string? _unsupportedReason;
    /// <summary>temp buffer used to create index entries</summary>
    private ByteStream _entryBuffer = new();
    /// <summary>temp buffer used to read/write the index pages</summary>
    private byte[]? _indexBuffer;
    /// <summary>modification count for the index, keeps cursors up-to-date</summary>
    private int _modCount;
    /// <summary>whether the index state has been initialized</summary>
    private bool _initialized;
    /// <summary>cache which manages the index pages</summary>
    private readonly IndexPageCache _pageCache;

    /// <summary>special object which will always be greater than any other value, when searching for an index entry range</summary>
    internal static readonly object MinValue = new();

    /// <summary>special object which will always be less than any other value, when searching for an index entry range</summary>
    internal static readonly object MaxValue = new();

    /// <summary>sentinel entry which sorts before any other entry</summary>
    internal static readonly Entry FirstEntry = new(null, RowId.FirstRowId);

    /// <summary>sentinel entry which sorts after any other entry</summary>
    internal static readonly Entry LastEntry = new(null, RowId.LastRowId);

    private IndexData(Table table, int number, int uniqueEntryCount, int uniqueEntryCountOffset)
    {
        _table = table;
        _number = number;
        _uniqueEntryCount = uniqueEntryCount;
        _uniqueEntryCountOffset = uniqueEntryCountOffset;
        _pageCache = new IndexPageCache(this);
    }

    /// <summary>
    /// Creates an IndexData for the given table, reading the unique-entry count
    /// from the table-definition buffer (port of Jackcess <c>IndexData.create</c>).
    /// </summary>
    internal static IndexData Create(Table table, byte[] tableBuffer, int number, JetFormat format)
    {
        int uniqueEntryCountOffset = format.OffsetIndexDefBlock + number * format.SizeIndexDefinition + 4;
        int uniqueEntryCount = ByteUtil.GetIntLittleEndian(tableBuffer, uniqueEntryCountOffset);
        return new IndexData(table, number, uniqueEntryCount, uniqueEntryCountOffset);
    }

    /// <summary>
    /// Reads the rest of the index info from the table-definition buffer
    /// (port of Jackcess <c>IndexData.read</c>).
    /// </summary>
    internal void Read(byte[] tableBuffer, Dictionary<int, byte[]> pageCache, ref int position)
    {
        JetFormat format = _table.Format;
        position += format.SkipBeforeIndex;

        for (int i = 0; i < MaxColumns; i++)
        {
            short columnNumber = ReadShort(tableBuffer, position);
            position += 2;
            byte colFlags = tableBuffer[position];
            position += 1;
            if (columnNumber != ColumnUnused)
            {
                Column? idxCol = null;
                foreach (Column col in _table.Columns)
                {
                    if (col.ColumnNumber == columnNumber)
                    {
                        idxCol = col;
                        break;
                    }
                }
                if (idxCol == null)
                {
                    throw new DatabaseException($"Could not find column with number {columnNumber} for index");
                }
                _columns.Add(NewColumnDescriptor(idxCol, colFlags));
            }
        }

        _ownedPages = UsageMap.Read(_table.Database, tableBuffer, _table.TableDefPageNumber, ref position, pageCache);

        _rootPageNumber = ByteUtil.GetIntLittleEndian(tableBuffer, position);
        position += 4;

        position += format.SkipBeforeIndexFlags;
        _indexFlags = tableBuffer[position];
        position += 1;
        position += format.SkipAfterIndexFlags;
    }

    internal Table Table => _table;

    internal JetFormat Format => _table.Format;

    internal PageChannel PageChannel => _table.Database.PageChannel;

    internal int Number => _number;

    internal int UniqueEntryCount => _uniqueEntryCount;

    internal int UniqueEntryCountOffset => _uniqueEntryCountOffset;

    internal IReadOnlyList<ColumnDescriptor> Columns => _columns;

    internal byte IndexFlags => _indexFlags;

    internal int RootPageNumber => _rootPageNumber;

    internal UsageMap? OwnedPages => _ownedPages;

    internal bool BackingPrimaryKey => _backingPrimaryKey;

    internal string Name
    {
        get
        {
            if (_name == null)
            {
                if (_indexes.Count == 1)
                {
                    _name = _indexes[0].Name ?? string.Empty;
                }
                else if (_indexes.Count > 1)
                {
                    var names = new List<string>(_indexes.Count);
                    foreach (IndexImpl idx in _indexes)
                    {
                        names.Add(idx.Name ?? string.Empty);
                    }
                    _name = "[" + string.Join(", ", names) + "]";
                }
                else
                {
                    _name = _number.ToString();
                }
            }
            return _name;
        }
    }

    /// <summary>whether {@code null} values are actually recorded in the index</summary>
    internal bool ShouldIgnoreNulls => (_indexFlags & IgnoreNullsIndexFlag) != 0;

    /// <summary>whether index entries must be unique</summary>
    internal bool IsUnique => _backingPrimaryKey || (_indexFlags & UniqueIndexFlag) != 0;

    /// <summary>whether values are required in the columns</summary>
    internal bool IsRequired => (_indexFlags & RequiredIndexFlag) != 0;

    /// <summary>the index columns (physical index column blocks)</summary>
    internal List<ColumnDescriptor> ColumnDescriptors => _columns;

    internal string? UnsupportedReason => _unsupportedReason;

    internal bool IsInitialized => _initialized;

    /// <summary>maximum amount of entry data which can be encoded on any index page</summary>
    internal int MaxPageEntrySize => CalcMaxPageEntrySize(Format);

    private static int CalcMaxPageEntrySize(JetFormat format)
    {
        // the max data we can fit on a page is the min of the space on the page
        // vs the number of bytes which can be encoded in the entry mask
        int pageDataSize = format.PageSize - (format.OffsetIndexEntryMask + format.SizeIndexEntryMask);
        int entryMaskSize = format.SizeIndexEntryMask * 8;
        return Math.Min(pageDataSize, entryMaskSize);
    }

    /// <summary>forces initialization of this index (actual parsing of index pages)</summary>
    internal void Initialize()
    {
        if (!_initialized)
        {
            _pageCache.SetRootPageNumber(RootPageNumber);
            _initialized = true;
        }
    }

    /// <summary>
    /// Writes the current index state to the database (port of Jackcess <c>IndexData.update</c>).
    /// </summary>
    internal void Update()
    {
        // make sure we've parsed the entries
        Initialize();

        if (_unsupportedReason != null)
        {
            throw new NotSupportedException($"Cannot write indexes of this type due to {_unsupportedReason}");
        }
        _pageCache.Write();
    }

    /// <summary>
    /// Returns the valid insertion point for an index indicating a missing entry.
    /// </summary>
    internal static int MissingIndexToInsertionPoint(int idx) => -(idx + 1);

    /// <summary>adds the given page to the set of pages owned by this index</summary>
    internal void AddOwnedPage(int pageNumber) => _ownedPages!.AddPageNumber(pageNumber);

    /// <summary>
    /// Adds a logical index backed by this data (port of Jackcess <c>IndexData.addIndex</c>).
    /// </summary>
    internal void AddIndex(IndexImpl index)
    {
        _indexes.Add(index);
        _backingPrimaryKey |= index.IsPrimaryKey;
        _name = null;
    }

    /// <summary>
    /// Marks this index as unsupported for write operations (port of Jackcess
    /// <c>IndexData.setUnsupportedReason</c>).
    /// </summary>
    internal void SetUnsupportedReason(string reason, Column col)
    {
        _unsupportedReason = $"{reason} (table {_table.Name}, index {Name})";
    }

    private static short ReadShort(byte[] buffer, int offset)
        => (short)(buffer[offset] | (buffer[offset + 1] << 8));

    /// <summary>
    /// Parses the entries stored on an index page (port of Jackcess
    /// <c>IndexData.readDataPage</c>, entry portion).
    /// </summary>
    internal static List<Entry> ReadEntries(byte[] buffer, JetFormat format)
        => ParseIndexPage(buffer, format, out _, out _, out _);

    private static List<Entry> ParseIndexPage(byte[] buffer, JetFormat format, out bool isLeaf, out byte[] entryPrefix, out int totalEntrySize)
    {
        isLeaf = buffer[0] switch
        {
            PageTypes.IndexLeaf => true,
            PageTypes.IndexNode => false,
            var t => throw new DatabaseException($"Unexpected page type {t}"),
        };

        // note, "header" data is in LITTLE_ENDIAN format, entry data is in
        // BIG_ENDIAN format
        int entryPrefixLength = (int)ByteUtil.GetUnsignedVarInt(buffer, format.OffsetIndexCompressedByteCount, 2);
        int entryMaskLength = format.SizeIndexEntryMask;
        int entryMaskPos = format.OffsetIndexEntryMask;
        int entryPos = entryMaskPos + entryMaskLength;
        int lastStart = 0;
        totalEntrySize = 0;
        byte[]? parsedPrefix = null;
        var entries = new List<Entry>();

        Entry prevEntry = FirstEntry;
        for (int i = 0; i < entryMaskLength; i++)
        {
            byte entryMask = buffer[entryMaskPos + i];
            for (int j = 0; j < 8; j++)
            {
                if ((entryMask & (1 << j)) != 0)
                {
                    int length = i * 8 + j - lastStart;

                    // determine if we can read straight from the index page (if no
                    // entryPrefix). otherwise, create temp buf with complete entry.
                    byte[] curEntryBuf;
                    int curEntryLen = length;
                    if (parsedPrefix != null)
                    {
                        curEntryBuf = new byte[length + parsedPrefix.Length];
                        Array.Copy(parsedPrefix, 0, curEntryBuf, 0, parsedPrefix.Length);
                        Array.Copy(buffer, entryPos + lastStart, curEntryBuf, parsedPrefix.Length, length);
                        curEntryLen += parsedPrefix.Length;
                    }
                    else
                    {
                        curEntryBuf = buffer.AsSpan(entryPos + lastStart, length).ToArray();
                    }
                    totalEntrySize += curEntryLen;

                    Entry entry = isLeaf
                        ? new Entry(curEntryBuf, 0, curEntryLen)
                        : new NodeEntry(curEntryBuf, 0, curEntryLen);
                    if (prevEntry.CompareTo(entry) >= 0)
                    {
                        throw new DatabaseException("Unexpected order in index entries");
                    }

                    entries.Add(entry);

                    if (entries.Count == 1 && entryPrefixLength > 0)
                    {
                        // read any shared entry prefix
                        parsedPrefix = buffer.AsSpan(entryPos + lastStart, entryPrefixLength).ToArray();
                    }

                    lastStart += length;
                    prevEntry = entry;
                }
            }
        }

        entryPrefix = parsedPrefix ?? Array.Empty<byte>();
        return entries;
    }

    /// <summary>
    /// Reads an index page, populating the given page model (port of Jackcess
    /// <c>IndexData.readDataPage</c>).
    /// </summary>
    internal void ReadDataPage(DataPage dataPage)
    {
        byte[] buffer = GetIndexBuffer();
        PageChannel.ReadPage(buffer, dataPage.PageNumber);

        var entries = ParseIndexPage(buffer, Format, out bool isLeaf, out byte[] entryPrefix, out int totalEntrySize);

        dataPage.IsLeaf = isLeaf;
        dataPage.EntryPrefix = entryPrefix;
        dataPage.Entries = entries;
        dataPage.TotalEntrySize = totalEntrySize;

        dataPage.PrevPageNumber = ByteUtil.GetIntLittleEndian(buffer, Format.OffsetPrevIndexPage);
        dataPage.NextPageNumber = ByteUtil.GetIntLittleEndian(buffer, Format.OffsetNextIndexPage);
        dataPage.ChildTailPageNumber = ByteUtil.GetIntLittleEndian(buffer, Format.OffsetChildTailIndexPage);
    }

    /// <summary>
    /// Writes the given index page to the database (port of Jackcess
    /// <c>IndexData.writeDataPage</c>).
    /// </summary>
    internal void WriteDataPage(DataPage dataPage)
    {
        if (dataPage.CompressedEntrySize > MaxPageEntrySize)
        {
            throw new InvalidOperationException(
                $"data page is too large (page {dataPage.PageNumber}, entries {dataPage.Entries.Count}, total {dataPage.TotalEntrySize}, compressed {dataPage.CompressedEntrySize}, prefix {dataPage.EntryPrefix.Length}, max {MaxPageEntrySize})");
        }

        byte[] buffer = GetIndexBuffer();
        WriteDataPage(buffer, dataPage, _table.TableDefPageNumber, Format);
        PageChannel.WritePage(buffer, dataPage.PageNumber);
    }

    /// <summary>
    /// Serializes the given data page into the given buffer (port of Jackcess
    /// <c>IndexData.writeDataPage(ByteBuffer, DataPage, int, JetFormat)</c>).
    /// </summary>
    private static void WriteDataPage(byte[] buffer, DataPage dataPage, int tdefPageNumber, JetFormat format)
    {
        buffer[0] = dataPage.IsLeaf ? PageTypes.IndexLeaf : PageTypes.IndexNode;
        buffer[1] = 0x01;
        WriteInt(buffer, 4, tdefPageNumber);
        WriteInt(buffer, 8, 0); // unknown
        WriteInt(buffer, format.OffsetPrevIndexPage, dataPage.PrevPageNumber);
        WriteInt(buffer, format.OffsetNextIndexPage, dataPage.NextPageNumber);
        WriteInt(buffer, format.OffsetChildTailIndexPage, dataPage.ChildTailPageNumber);

        byte[] entryPrefix = dataPage.EntryPrefix;
        WriteShort(buffer, format.OffsetIndexCompressedByteCount, (short)entryPrefix.Length);
        buffer[format.OffsetIndexCompressedByteCount + 2] = 0; // unknown

        byte[] entryMask = new byte[format.SizeIndexEntryMask];
        // first entry includes the prefix
        int totalSize = entryPrefix.Length;
        foreach (Entry entry in dataPage.Entries)
        {
            totalSize += entry.Size - entryPrefix.Length;
            int idx = totalSize / 8;
            if (idx >= entryMask.Length)
            {
                throw new InvalidOperationException(
                    $"entry mask overflow (page {dataPage.PageNumber}, entries {dataPage.Entries.Count}, total {dataPage.TotalEntrySize}, compressed {dataPage.CompressedEntrySize}, prefix {entryPrefix.Length}, totalSize {totalSize}, max {format.SizeIndexEntryMask * 8})");
            }
            entryMask[idx] |= (byte)(1 << (totalSize % 8));
        }
        Array.Copy(entryMask, 0, buffer, format.OffsetIndexEntryMask, entryMask.Length);

        // first entry includes the prefix
        int pos = format.OffsetIndexEntryMask + format.SizeIndexEntryMask;
        Array.Copy(entryPrefix, 0, buffer, pos, entryPrefix.Length);
        pos += entryPrefix.Length;

        foreach (Entry entry in dataPage.Entries)
        {
            var bout = new ByteStream();
            entry.Write(bout, entryPrefix);
            byte[] entryBytes = bout.ToByteArray();
            Array.Copy(entryBytes, 0, buffer, pos, entryBytes.Length);
            pos += entryBytes.Length;
        }

        // update free space
        WriteShort(buffer, 2, (short)(format.PageSize - pos));
    }

    private byte[] GetIndexBuffer()
    {
        _indexBuffer ??= new byte[Format.PageSize];
        Array.Clear(_indexBuffer, 0, _indexBuffer.Length);
        return _indexBuffer;
    }

    private static void WriteInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteShort(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    /// <summary>
    /// Constructs the appropriate <see cref="ColumnDescriptor"/> for the given column and
    /// index flags (port of Jackcess <c>IndexData.newColumnDescriptor</c>).
    /// </summary>
    private ColumnDescriptor NewColumnDescriptor(Column col, byte flags)
    {
        switch (col.Type)
        {
            case DataType.Text:
            case DataType.Memo:
                TextSortOrder sortOrder = col.TextSortOrder ?? Format.DefaultSortOrder;
                if (sortOrder.Equals(UCanAccess.File.TextSortOrder.General))
                {
                    return new GenTextColumnDescriptor(this, col, flags);
                }
                if (sortOrder.Equals(UCanAccess.File.TextSortOrder.GeneralLegacy))
                {
                    return new GenLegTextColumnDescriptor(this, col, flags);
                }
                if (sortOrder.Equals(UCanAccess.File.TextSortOrder.General97))
                {
                    return new Gen97TextColumnDescriptor(this, col, flags);
                }
                if (sortOrder.Equals(UCanAccess.File.TextSortOrder.Russian))
                {
                    return new GenLegTextColumnDescriptor(this, col, flags);
                }
                if (sortOrder.Equals(UCanAccess.File.TextSortOrder.Polish))
                {
                    return new GenLegTextColumnDescriptor(this, col, flags);
                }
                if (sortOrder.Equals(UCanAccess.File.TextSortOrder.Turkish))
                {
                    return new GenLegTextColumnDescriptor(this, col, flags);
                }
                if (sortOrder.Equals(UCanAccess.File.TextSortOrder.Ukrainian))
                {
                    return new GenLegTextColumnDescriptor(this, col, flags);
                }
                // unsupported sort order
                SetUnsupportedReason($"unsupported collating sort order {sortOrder} for text index", col);
                return new ReadOnlyColumnDescriptor(this, col, flags);
            case DataType.Int:
            case DataType.Long:
            case DataType.Money:
            case DataType.ComplexType:
            case DataType.BigInt:
                return new IntegerColumnDescriptor(this, col, flags);
            case DataType.Float:
            case DataType.Double:
            case DataType.ShortDateTime:
                return new FloatingPointColumnDescriptor(this, col, flags);
            case DataType.Numeric:
                return Format.LegacyNumericIndexes
                    ? new LegacyFixedPointColumnDescriptor(this, col, flags)
                    : new FixedPointColumnDescriptor(this, col, flags);
            case DataType.Byte:
                return new ByteColumnDescriptor(this, col, flags);
            case DataType.Boolean:
                return new BooleanColumnDescriptor(this, col, flags);
            case DataType.Guid:
                return new GuidColumnDescriptor(this, col, flags);
            case DataType.Binary:
                return new BinaryColumnDescriptor(this, col, flags);
            default:
                // we can't modify this index at this point in time
                SetUnsupportedReason($"unsupported data type {col.Type} for index", col);
                return new ReadOnlyColumnDescriptor(this, col, flags);
        }
    }

    /// <summary>
    /// Encodes column values into the byte sequences that make up an index entry
    /// (port of Jackcess <c>IndexData.createEntryBytes</c>).
    /// </summary>
    internal byte[] CreateEntryBytes(object?[] values)
    {
        _entryBuffer.Reset();

        foreach (ColumnDescriptor col in _columns)
        {
            object? value = values[col.Column.ColumnIndex];
            if (IsRawData(value))
            {
                // ignore it, we could not parse it
                continue;
            }

            if (ReferenceEquals(value, MinValue))
            {
                // null is the "least" value
                _entryBuffer.Write(IndexCodes.GetNullEntryFlag(true));
                continue;
            }
            if (ReferenceEquals(value, MaxValue))
            {
                // the opposite null is the "greatest" value
                _entryBuffer.Write(IndexCodes.GetNullEntryFlag(false));
                continue;
            }

            col.WriteValue(value, _entryBuffer);
        }

        return _entryBuffer.ToByteArray();
    }

    private static bool IsRawData(object? value) => false;

    // ------------------------------------------------------------------
    // Write machinery (port of the IndexData.add/update/delete row support)
    // ------------------------------------------------------------------

    /// <summary>prepares to add a row to this index (all constraints checked before this method returns)</summary>
    internal PendingChange? PrepareAddRow(object?[] row, RowId rowId, PendingChange? nextChange)
        => PrepareAddRow(row, rowId, new AddRowPendingChange(this, nextChange));

    private PendingChange? PrepareAddRow(object?[] row, RowId rowId, AddRowPendingChange change)
    {
        int nullCount = CountNullValues(row);
        bool isNullEntry = nullCount == _columns.Count;
        if (ShouldIgnoreNulls && isNullEntry)
        {
            // nothing to do
            return change;
        }
        if (nullCount > 0 && (BackingPrimaryKey || IsRequired))
        {
            throw new DatabaseException($"Null value found in row [{string.Join(",", row)}] for primary key or required index");
        }

        // make sure we've parsed the entries
        Initialize();

        return PrepareAddEntry(new Entry(CreateEntryBytes(row), rowId), isNullEntry, row, change);
    }

    /// <summary>adds an entry to the correct index data page, maintaining the order</summary>
    private PendingChange? PrepareAddEntry(Entry newEntry, bool isNullEntry, object?[] row, AddRowPendingChange change)
    {
        DataPage dataPage = FindDataPage(newEntry);
        int idx = dataPage.Entries.BinarySearch(newEntry);
        if (idx < 0)
        {
            // this is a new entry
            idx = MissingIndexToInsertionPoint(idx);

            var newPos = new Position(dataPage, idx, newEntry, true);
            Position? nextPos = GetNextPosition(newPos);
            Position? prevPos = GetPreviousPosition(newPos);

            // determine if the addition of this entry would break the uniqueness
            // constraint
            bool isDupeEntry = nextPos != null && newEntry.EqualsEntryBytes(nextPos.Entry)
                || prevPos != null && newEntry.EqualsEntryBytes(prevPos.Entry);
            if (IsUnique && !isNullEntry && isDupeEntry)
            {
                throw new DatabaseException($"New row [{string.Join(",", row)}] violates uniqueness constraint for index");
            }

            change.SetAddRow(newEntry, dataPage, idx, isDupeEntry);
        }
        else
        {
            change.SetOldRow(newEntry);
        }
        return change;
    }

    /// <summary>completes a prepared row addition</summary>
    private void CommitAddRow(Entry? newEntry, DataPage dataPage, int idx, bool isDupeEntry, Entry? oldEntry)
    {
        if (newEntry != null)
        {
            dataPage.AddEntry(idx, newEntry);
            // if we are adding a duplicate entry, or replacing an existing entry,
            // then the unique entry count doesn't change
            if (!isDupeEntry && oldEntry == null)
            {
                _uniqueEntryCount++;
            }
            _modCount++;
        }
    }

    /// <summary>prepares to update a row in this index (all constraints checked before this method returns)</summary>
    internal PendingChange? PrepareUpdateRow(object?[] oldRow, RowId rowId, object?[] newRow, PendingChange? nextChange)
    {
        var change = new UpdateRowPendingChange(this, nextChange);
        change.SetOldRow(DeleteRowImpl(oldRow, rowId));

        try
        {
            PrepareAddRow(newRow, rowId, change);
            return change;
        }
        catch (DatabaseException)
        {
            // need to undo the deletion before bailing
            change.Rollback();
            throw;
        }
    }

    /// <summary>removes a row from this index</summary>
    internal void DeleteRow(object?[] row, RowId rowId)
    {
        DeleteRowImpl(row, rowId);
    }

    private Entry? DeleteRowImpl(object?[] row, RowId rowId)
    {
        int nullCount = CountNullValues(row);
        if (ShouldIgnoreNulls && nullCount == _columns.Count)
        {
            // nothing to do
            return null;
        }

        // make sure we've parsed the entries
        Initialize();

        var oldEntry = new Entry(CreateEntryBytes(row), rowId);
        Entry? removedEntry = RemoveEntry(oldEntry);
        if (removedEntry != null)
        {
            _modCount++;
        }
        return removedEntry;
    }

    /// <summary>undoes a previous row deletion</summary>
    private void RollbackDeletedRow(Entry? removedEntry)
    {
        if (removedEntry == null)
        {
            // no change was made
            return;
        }

        // unfortunately, stuff might have shuffled around when we first removed
        // the row, so in order to re-insert it, we need to re-find and insert it.
        DataPage dataPage = FindDataPage(removedEntry);
        int idx = dataPage.Entries.BinarySearch(removedEntry);
        if (idx < 0)
        {
            dataPage.AddEntry(MissingIndexToInsertionPoint(idx), removedEntry);
        }
    }

    /// <summary>
    /// Removes an entry from the relevant index data page, maintaining the order. Will
    /// search by rowId if the entry is not found (in case a partial entry was provided).
    /// </summary>
    private Entry? RemoveEntry(Entry oldEntry)
    {
        DataPage dataPage = FindDataPage(oldEntry);
        int idx = dataPage.Entries.BinarySearch(oldEntry);
        bool doRemove = false;
        if (idx < 0)
        {
            // the caller may have only read some of the row data; search for the page/row numbers
            (DataPage Page, int Index)? found = FindEntryByRowId(oldEntry.RowId);
            if (found != null)
            {
                dataPage = found.Value.Page;
                idx = found.Value.Index;
                doRemove = true;
            }
        }
        else
        {
            doRemove = true;
        }

        return doRemove ? dataPage.RemoveEntry(idx) : null;
    }

    /// <summary>scans the leaf pages of this index for an entry with the given row id</summary>
    private (DataPage Page, int Index)? FindEntryByRowId(RowId rowId)
    {
        DataPage? page = _pageCache.FindCacheDataPage(FirstEntry);
        while (page != null)
        {
            for (int i = 0; i < page.Entries.Count; i++)
            {
                if (page.Entries[i].RowId.Equals(rowId))
                {
                    return (page, i);
                }
            }
            int next = page.NextPageNumber;
            if (next == InvalidIndexPageNumber)
            {
                break;
            }
            page = _pageCache.GetCacheDataPage(next);
        }
        return null;
    }

    internal static void CommitAll(PendingChange? change)
    {
        while (change != null)
        {
            change.Commit();
            change = change.Next;
        }
    }

    internal static void RollbackAll(PendingChange? change)
    {
        while (change != null)
        {
            change.Rollback();
            change = change.Next;
        }
    }

    /// <summary>finds the data page for the given entry</summary>
    private DataPage FindDataPage(Entry entry) => _pageCache.FindCacheDataPage(entry);

    /// <summary>gets the data page for the given page number</summary>
    private DataPage? GetDataPage(int pageNumber) => _pageCache.GetCacheDataPage(pageNumber);

    /// <summary>determines the number of null values for this index from the given row</summary>
    private int CountNullValues(object?[]? values)
    {
        if (values == null)
        {
            return _columns.Count;
        }

        // annoyingly, the values array could come from different sources, one
        // of which will make it a different size than the other. we need to
        // handle both situations.
        int nullCount = 0;
        foreach (ColumnDescriptor col in _columns)
        {
            object? value = values[col.Column.ColumnIndex];
            if (col.IsNullValue(value))
            {
                nullCount++;
            }
        }
        return nullCount;
    }

    /// <summary>finds the position of the given entry (or the position between two entries)</summary>
    internal Position FindEntryPosition(Entry entry)
    {
        DataPage dataPage = FindDataPage(entry);
        int idx = dataPage.Entries.BinarySearch(entry);
        bool between = false;
        if (idx < 0)
        {
            // given entry was not found exactly. our current position is now really
            // between two indexes, but we cannot support that as an integer value,
            // so we set a flag instead
            idx = MissingIndexToInsertionPoint(idx);
            between = true;
        }
        return new Position(dataPage, idx, entry, between);
    }

    /// <summary>updates the given position, taking boundaries into account</summary>
    internal Position UpdatePosition(Entry entry, Position firstPos, Position lastPos)
    {
        if (!entry.IsValid)
        {
            // no use searching if "updating" the first/last pos
            if (firstPos.EqualsEntry(entry))
            {
                return firstPos;
            }
            if (lastPos.EqualsEntry(entry))
            {
                return lastPos;
            }
            throw new ArgumentException("Invalid entry given " + entry);
        }

        Position pos = FindEntryPosition(entry);
        if (pos.CompareTo(lastPos) >= 0)
        {
            return lastPos;
        }
        if (pos.CompareTo(firstPos) <= 0)
        {
            return firstPos;
        }
        return pos;
    }

    internal Position? GetNextPosition(Position curPos)
    {
        // get the next index (between-ness is handled internally)
        int nextIdx = curPos.NextIndex;
        if (nextIdx < curPos.DataPage.Entries.Count)
        {
            return new Position(curPos.DataPage, nextIdx);
        }

        int nextPageNumber = curPos.DataPage.NextPageNumber;
        DataPage? nextDataPage = null;
        while (nextPageNumber != InvalidIndexPageNumber)
        {
            DataPage? dp = GetDataPage(nextPageNumber);
            if (!dp!.IsEmpty)
            {
                nextDataPage = dp;
                break;
            }
            nextPageNumber = dp.NextPageNumber;
        }
        return nextDataPage != null ? new Position(nextDataPage, 0) : null;
    }

    internal Position? GetPreviousPosition(Position curPos)
    {
        // get the previous index (between-ness is handled internally)
        int prevIdx = curPos.PrevIndex;
        if (prevIdx >= 0)
        {
            return new Position(curPos.DataPage, prevIdx);
        }

        int prevPageNumber = curPos.DataPage.PrevPageNumber;
        DataPage? prevDataPage = null;
        while (prevPageNumber != InvalidIndexPageNumber)
        {
            DataPage? dp = GetDataPage(prevPageNumber);
            if (!dp!.IsEmpty)
            {
                prevDataPage = dp;
                break;
            }
            prevPageNumber = dp.PrevPageNumber;
        }
        return prevDataPage != null ? new Position(prevDataPage, prevDataPage.Entries.Count - 1) : null;
    }

    /// <summary>
    /// A position of an entry within the index, used to locate adjacent entries
    /// (port of Jackcess <c>IndexData.Position</c>).
    /// </summary>
    internal sealed class Position : IComparable<Position>
    {
        internal readonly DataPage DataPage;
        internal readonly int Index;
        internal readonly Entry Entry;
        internal readonly bool Between;

        internal Position(DataPage dataPage, int idx, Entry entry, bool between)
        {
            DataPage = dataPage;
            Index = idx;
            Entry = entry;
            Between = between;
        }

        internal Position(DataPage dataPage, int idx)
            : this(dataPage, idx, dataPage.Entries[idx], false)
        {
        }

        internal int NextIndex => Between ? Index : Index + 1;

        internal int PrevIndex => Index - 1;

        internal bool EqualsEntry(Entry entry) => Entry.Equals(entry);

        public int CompareTo(Position? other)
        {
            if (ReferenceEquals(this, other))
            {
                return 0;
            }

            if (SameDataPage(DataPage, other!.DataPage))
            {
                // "simple" index comparison (handle between-ness)
                int idxCmp = Index < other.Index ? -1 : Index > other.Index ? 1 : Between == other.Between ? 0 : Between ? -1 : 1;
                if (idxCmp != 0)
                {
                    return idxCmp;
                }
            }

            // compare the entries
            return Entry.CompareTo(other.Entry);
        }

        public override bool Equals(object? obj) => obj is Position other && ReferenceEquals(this, other) || obj is Position p && CompareTo(p) == 0;

        public override int GetHashCode() => Entry.GetHashCode();

        private static bool SameDataPage(DataPage a, DataPage b)
            => a.PageNumber == b.PageNumber && a.GetType() == b.GetType();
    }

    /// <summary>
    /// Utility class to traverse the entries in the index (port of Jackcess
    /// <c>IndexData.EntryCursor</c>).
    /// </summary>
    internal sealed class EntryCursor
    {
        private readonly IndexData _index;
        private readonly DirHandler _forwardDirHandler;
        private readonly DirHandler _reverseDirHandler;
        private readonly Position _firstPos;
        private readonly Position _lastPos;
        private Position _curPos = null!;
        private Position _prevPos = null!;

        private EntryCursor(IndexData index, Position firstPos, Position lastPos)
        {
            _index = index;
            _firstPos = firstPos;
            _lastPos = lastPos;
            _forwardDirHandler = new ForwardDirHandler(this);
            _reverseDirHandler = new ReverseDirHandler(this);
            Reset();
        }

        internal static EntryCursor Create(IndexData index, object?[]? startRow, bool startInclusive, object?[]? endRow, bool endInclusive)
        {
            index.Initialize();
            Entry startEntry = IndexData.FirstEntry;
            if (startRow != null)
            {
                byte[] startEntryBytes = index.CreateEntryBytes(startRow);
                startEntry = new Entry(startEntryBytes, startInclusive ? RowId.FirstRowId : RowId.LastRowId);
            }
            Entry endEntry = IndexData.LastEntry;
            if (endRow != null)
            {
                byte[] endEntryBytes = index.CreateEntryBytes(endRow!);
                endEntry = new Entry(endEntryBytes, endInclusive ? RowId.LastRowId : RowId.FirstRowId);
            }
            return new EntryCursor(index, index.FindEntryPosition(startEntry), index.FindEntryPosition(endEntry));
        }

        /// <summary>the first entry (exclusive) as defined by this cursor</summary>
        internal Entry FirstEntry => _firstPos.Entry;

        /// <summary>the last entry (exclusive) as defined by this cursor</summary>
        internal Entry LastEntry => _lastPos.Entry;

        internal void Reset() => BeforeFirst();

        internal void BeforeFirst() => Reset(true);

        internal void AfterLast() => Reset(false);

        private void Reset(bool moveForward)
        {
            _curPos = GetDirHandler(moveForward).BeginningPosition;
            _prevPos = _curPos;
        }

        /// <summary>repositions the cursor so that the next row will be the first entry &gt;= the given row</summary>
        internal void BeforeEntry(object?[] row)
        {
            RestorePosition(new Entry(_index.CreateEntryBytes(row), RowId.FirstRowId));
        }

        /// <summary>repositions the cursor so that the previous row will be the first entry &lt;= the given row</summary>
        internal void AfterEntry(object?[] row)
        {
            RestorePosition(new Entry(_index.CreateEntryBytes(row), RowId.LastRowId));
        }

        /// <summary>valid entry if there was a next entry, last entry otherwise</summary>
        internal Entry GetNextEntry() => GetAnotherPosition(true).Entry;

        /// <summary>valid entry if there was a previous entry, first entry otherwise</summary>
        internal Entry GetPreviousEntry() => GetAnotherPosition(false).Entry;

        private void RestorePosition(Entry curEntry)
            => RestorePosition(curEntry, _curPos.Entry);

        private void RestorePosition(Entry curEntry, Entry prevEntry)
        {
            if (!_curPos.EqualsEntry(curEntry) || !_prevPos.EqualsEntry(prevEntry))
            {
                _prevPos = _index.UpdatePosition(prevEntry, _firstPos, _lastPos);
                _curPos = _index.UpdatePosition(curEntry, _firstPos, _lastPos);
            }
        }

        private Position GetAnotherPosition(bool moveForward)
        {
            DirHandler handler = GetDirHandler(moveForward);
            if (_curPos.Equals(handler.EndPosition))
            {
                // at end, no more
                return _curPos;
            }

            _prevPos = _curPos;
            _curPos = handler.GetAnotherPosition(_curPos);
            return _curPos;
        }

        private DirHandler GetDirHandler(bool moveForward) => moveForward ? _forwardDirHandler : _reverseDirHandler;

        /// <summary>
        /// Handles moving the cursor in a given direction.
        /// </summary>
        private abstract class DirHandler
        {
            protected readonly EntryCursor Cursor;

            protected DirHandler(EntryCursor cursor)
            {
                Cursor = cursor;
            }

            internal abstract Position GetAnotherPosition(Position curPos);

            internal abstract Position BeginningPosition { get; }

            internal abstract Position EndPosition { get; }
        }

        private sealed class ForwardDirHandler : DirHandler
        {
            internal ForwardDirHandler(EntryCursor cursor)
                : base(cursor)
            {
            }

            internal override Position GetAnotherPosition(Position curPos)
            {
                Position? newPos = Cursor._index.GetNextPosition(curPos);
                if (newPos == null || newPos.CompareTo(Cursor._lastPos) >= 0)
                {
                    newPos = Cursor._lastPos;
                }
                return newPos;
            }

            internal override Position BeginningPosition => Cursor._firstPos;

            internal override Position EndPosition => Cursor._lastPos;
        }

        private sealed class ReverseDirHandler : DirHandler
        {
            internal ReverseDirHandler(EntryCursor cursor)
                : base(cursor)
            {
            }

            internal override Position GetAnotherPosition(Position curPos)
            {
                Position? newPos = Cursor._index.GetPreviousPosition(curPos);
                if (newPos == null || newPos.CompareTo(Cursor._firstPos) <= 0)
                {
                    newPos = Cursor._firstPos;
                }
                return newPos;
            }

            internal override Position BeginningPosition => Cursor._lastPos;

            internal override Position EndPosition => Cursor._firstPos;
        }
    }

    /// <summary>
    /// Maintains information about a pending index update (port of Jackcess
    /// <c>IndexData.PendingChange</c>).
    /// </summary>
    internal abstract class PendingChange
    {
        private readonly IndexData _indexData;
        private readonly PendingChange? _next;

        protected PendingChange(IndexData indexData, PendingChange? next)
        {
            _indexData = indexData;
            _next = next;
        }

        internal IndexData Index => _indexData;

        internal PendingChange? Next => _next;

        /// <summary>completes the pending change</summary>
        internal abstract void Commit();

        /// <summary>undoes the pending change</summary>
        internal abstract void Rollback();
    }

    /// <summary>pending change for a row addition</summary>
    private class AddRowPendingChange : PendingChange
    {
        private Entry? _addEntry;
        private DataPage? _addDataPage;
        private int _addIdx;
        private bool _isDupe;
        private Entry? _oldEntry;

        internal AddRowPendingChange(IndexData indexData, PendingChange? next)
            : base(indexData, next)
        {
        }

        internal void SetAddRow(Entry addEntry, DataPage dataPage, int idx, bool isDupe)
        {
            _addEntry = addEntry;
            _addDataPage = dataPage;
            _addIdx = idx;
            _isDupe = isDupe;
        }

        internal void SetOldRow(Entry? oldEntry) => _oldEntry = oldEntry;

        internal Entry? OldEntry => _oldEntry;

        internal override void Commit() => Index.CommitAddRow(_addEntry, _addDataPage!, _addIdx, _isDupe, _oldEntry);

        internal override void Rollback()
        {
            _addEntry = null;
            _addDataPage = null;
            _addIdx = -1;
        }
    }

    /// <summary>pending change for a row update (a deletion followed by an addition)</summary>
    private sealed class UpdateRowPendingChange : AddRowPendingChange
    {
        internal UpdateRowPendingChange(IndexData indexData, PendingChange? next)
            : base(indexData, next)
        {
        }

        internal override void Rollback()
        {
            base.Rollback();
            Index.RollbackDeletedRow(OldEntry);
        }
    }

    /// <summary>flips the first bit in the byte at the given index</summary>
    private static byte[] FlipFirstBitInByte(byte[] value, int index)
    {
        value[index] ^= 0x80;
        return value;
    }

    /// <summary>writes the value of the given column type to a byte array and returns it (always big endian)</summary>
    internal static byte[] EncodeNumberColumnValue(object value, Column column)
        => column.WriteIndexValue(value);

    /// <summary>
    /// Writes a binary value using the general binary entry encoding rules
    /// (port of Jackcess <c>IndexData.writeGeneralBinaryEntry</c>).
    /// </summary>
    internal static void WriteGeneralBinaryEntry(byte[] valueBytes, bool isAscending, ByteStream bout)
    {
        int dataLen = valueBytes.Length;

        // binary data is written in 8 byte segments with a trailing length byte. The
        // length byte is the amount of valid bytes in the segment (where 9 indicates
        // that there is more data _after_ this segment).
        var partialEntryBytes = new byte[9];

        // first, write any intermediate segments
        int segmentLen = dataLen;
        int pos = 0;
        while (segmentLen > 8)
        {
            Array.Copy(valueBytes, pos, partialEntryBytes, 0, 8);
            if (!isAscending)
            {
                // note, we do _not_ flip the length byte for intermediate segments
                IndexCodes.FlipBytes(partialEntryBytes, 0, 8);
            }

            // we are writing intermediate segments (there is more data after this
            // segment), so the length is always 9.
            partialEntryBytes[8] = 9;

            pos += 8;
            segmentLen -= 8;

            bout.Write(partialEntryBytes);
        }

        // write the last segment (with slightly different rules)
        if (segmentLen > 0)
        {
            Array.Copy(valueBytes, pos, partialEntryBytes, 0, segmentLen);

            // clear out any intermediate bytes between the real data and the final length byte
            for (int i = segmentLen; i < 8; ++i)
            {
                partialEntryBytes[i] = 0;
            }

            partialEntryBytes[8] = (byte)segmentLen;

            if (!isAscending)
            {
                // note, we _do_ flip the last length byte
                IndexCodes.FlipBytes(partialEntryBytes, 0, 9);
            }

            bout.Write(partialEntryBytes);
        }
    }

    /// <summary>
    /// Describes a single column of an index together with its per-column flags and the
    /// logic to encode a value into its index-entry segment
    /// (port of Jackcess <c>IndexData.ColumnDescriptor</c>).
    /// </summary>
    internal abstract class ColumnDescriptor
    {
        private readonly IndexData _index;
        private readonly Column _column;
        private readonly byte _flags;

        protected ColumnDescriptor(IndexData index, Column column, byte flags)
        {
            _index = index;
            _column = column;
            _flags = flags;
        }

        internal IndexData Index => _index;

        internal Column Column => _column;

        internal byte Flags => _flags;

        internal bool IsAscending => (_flags & AscendingColumnFlag) != 0;

        /// <summary>encodes the given value as one index column segment</summary>
        internal void WriteValue(object? value, ByteStream bout)
        {
            if (IsNullValue(value))
            {
                // write null value
                bout.Write(IndexCodes.GetNullEntryFlag(IsAscending));
                return;
            }

            // write the start flag
            bout.Write(IndexCodes.GetStartEntryFlag(IsAscending));
            // write the rest of the value
            WriteNonNullValue(value, bout);
        }

        internal virtual bool IsNullValue(object? value) => value == null;

        protected abstract void WriteNonNullValue(object? value, ByteStream bout);
    }

    /// <summary>column descriptor for integer based columns</summary>
    private sealed class IntegerColumnDescriptor : ColumnDescriptor
    {
        internal IntegerColumnDescriptor(IndexData index, Column column, byte flags)
            : base(index, column, flags)
        {
        }

        protected override void WriteNonNullValue(object? value, ByteStream bout)
        {
            byte[] valueBytes = EncodeNumberColumnValue(value!, Column);

            // bit twiddling rules:
            // - isAsc => flipFirstBit
            // - !isAsc => flipFirstBit, flipBytes

            FlipFirstBitInByte(valueBytes, 0);
            if (!IsAscending)
            {
                IndexCodes.FlipBytes(valueBytes, 0, valueBytes.Length);
            }

            bout.Write(valueBytes);
        }
    }

    /// <summary>column descriptor for floating point based columns</summary>
    private sealed class FloatingPointColumnDescriptor : ColumnDescriptor
    {
        internal FloatingPointColumnDescriptor(IndexData index, Column column, byte flags)
            : base(index, column, flags)
        {
        }

        protected override void WriteNonNullValue(object? value, ByteStream bout)
        {
            byte[] valueBytes = EncodeNumberColumnValue(value!, Column);

            // determine if the number is negative by testing if the first bit is set
            bool isNegative = (valueBytes[0] & 0x80) != 0;

            if (!isNegative)
            {
                FlipFirstBitInByte(valueBytes, 0);
            }
            if (isNegative == IsAscending)
            {
                IndexCodes.FlipBytes(valueBytes, 0, valueBytes.Length);
            }

            bout.Write(valueBytes);
        }
    }

    /// <summary>column descriptor for fixed point based columns (legacy sort order)</summary>
    private class LegacyFixedPointColumnDescriptor : ColumnDescriptor
    {
        internal LegacyFixedPointColumnDescriptor(IndexData index, Column column, byte flags)
            : base(index, column, flags)
        {
        }

        protected virtual void HandleNegationAndOrder(bool isNegative, byte[] valueBytes)
        {
            if (isNegative == IsAscending)
            {
                IndexCodes.FlipBytes(valueBytes, 0, valueBytes.Length);
            }

            // reverse the sign byte (after any previous byte flipping)
            valueBytes[0] = isNegative ? (byte)0x00 : (byte)0xFF;
        }

        protected override void WriteNonNullValue(object? value, ByteStream bout)
        {
            byte[] valueBytes = EncodeNumberColumnValue(value!, Column);

            // determine if the number is negative by testing if the first bit is set
            bool isNegative = (valueBytes[0] & 0x80) != 0;

            HandleNegationAndOrder(isNegative, valueBytes);

            bout.Write(valueBytes);
        }
    }

    /// <summary>column descriptor for new-style fixed point based columns</summary>
    private sealed class FixedPointColumnDescriptor : LegacyFixedPointColumnDescriptor
    {
        internal FixedPointColumnDescriptor(IndexData index, Column column, byte flags)
            : base(index, column, flags)
        {
        }

        protected override void HandleNegationAndOrder(bool isNegative, byte[] valueBytes)
        {
            // reverse the sign byte (before any byte flipping)
            valueBytes[0] = (byte)0xFF;

            if (isNegative == IsAscending)
            {
                IndexCodes.FlipBytes(valueBytes, 0, valueBytes.Length);
            }
        }
    }

    /// <summary>column descriptor for byte based columns</summary>
    private sealed class ByteColumnDescriptor : ColumnDescriptor
    {
        internal ByteColumnDescriptor(IndexData index, Column column, byte flags)
            : base(index, column, flags)
        {
        }

        protected override void WriteNonNullValue(object? value, ByteStream bout)
        {
            byte[] valueBytes = EncodeNumberColumnValue(value!, Column);

            // bit twiddling rules:
            // - isAsc => nothing
            // - !isAsc => flipBytes
            if (!IsAscending)
            {
                IndexCodes.FlipBytes(valueBytes, 0, valueBytes.Length);
            }

            bout.Write(valueBytes);
        }
    }

    /// <summary>column descriptor for boolean columns</summary>
    private sealed class BooleanColumnDescriptor : ColumnDescriptor
    {
        internal BooleanColumnDescriptor(IndexData index, Column column, byte flags)
            : base(index, column, flags)
        {
        }

        internal override bool IsNullValue(object? value) => false;

        protected override void WriteNonNullValue(object? value, ByteStream bout)
        {
            bout.Write(Column.ToBooleanValue(value)
                ? IsAscending ? IndexCodes.AscBooleanTrue : IndexCodes.DescBooleanTrue
                : IsAscending ? IndexCodes.AscBooleanFalse : IndexCodes.DescBooleanFalse);
        }
    }

    /// <summary>column descriptor for text columns using the General Legacy sort order</summary>
    private sealed class GenLegTextColumnDescriptor : ColumnDescriptor
    {
        internal GenLegTextColumnDescriptor(IndexData index, Column column, byte flags)
            : base(index, column, flags)
        {
        }

        protected override void WriteNonNullValue(object? value, ByteStream bout)
            => GeneralLegacyIndexCodes.GenLegacyInstance.WriteNonNullIndexTextValue(value, bout, IsAscending);
    }

    /// <summary>column descriptor for text columns using the General (Access 2010+) sort order</summary>
    private sealed class GenTextColumnDescriptor : ColumnDescriptor
    {
        internal GenTextColumnDescriptor(IndexData index, Column column, byte flags)
            : base(index, column, flags)
        {
        }

        protected override void WriteNonNullValue(object? value, ByteStream bout)
            => GeneralIndexCodes.GenInstance.WriteNonNullIndexTextValue(value, bout, IsAscending);
    }

    /// <summary>column descriptor for text columns using the General 97 sort order</summary>
    private sealed class Gen97TextColumnDescriptor : ColumnDescriptor
    {
        internal Gen97TextColumnDescriptor(IndexData index, Column column, byte flags)
            : base(index, column, flags)
        {
        }

        protected override void WriteNonNullValue(object? value, ByteStream bout)
            => General97IndexCodes.Gen97Instance.WriteNonNullIndexTextValue(value, bout, IsAscending);
    }

    /// <summary>column descriptor for guid columns</summary>
    private sealed class GuidColumnDescriptor : ColumnDescriptor
    {
        internal GuidColumnDescriptor(IndexData index, Column column, byte flags)
            : base(index, column, flags)
        {
        }

        protected override void WriteNonNullValue(object? value, ByteStream bout)
            => WriteGeneralBinaryEntry(EncodeNumberColumnValue(value!, Column), IsAscending, bout);
    }

    /// <summary>column descriptor for BINARY columns</summary>
    private sealed class BinaryColumnDescriptor : ColumnDescriptor
    {
        internal BinaryColumnDescriptor(IndexData index, Column column, byte flags)
            : base(index, column, flags)
        {
        }

        protected override void WriteNonNullValue(object? value, ByteStream bout)
            => WriteGeneralBinaryEntry(Column.ToByteArray(value), IsAscending, bout);
    }

    /// <summary>
    /// Sentinel column descriptor used when Jackcess cannot encode values for a particular
    /// column in an index (port of Jackcess <c>IndexData.ReadOnlyColumnDescriptor</c>).
    /// </summary>
    private sealed class ReadOnlyColumnDescriptor : ColumnDescriptor
    {
        private readonly IndexData _index;

        internal ReadOnlyColumnDescriptor(IndexData index, Column column, byte flags)
            : base(index, column, flags)
        {
            _index = index;
        }

        protected override void WriteNonNullValue(object? value, ByteStream bout)
            => throw new NotSupportedException($"Cannot write indexes of this type due to {_index._unsupportedReason}");
    }

    /// <summary>type attributes for Entries which simplify comparisons</summary>
    internal enum EntryType
    {
        /// <summary>always compares less than valid rowIds</summary>
        AlwaysFirst,

        /// <summary>always compares less than other valid entries with equal entry bytes</summary>
        FirstValid,

        /// <summary>always compares normally</summary>
        Normal,

        /// <summary>always compares greater than other valid entries with equal entry bytes</summary>
        LastValid,

        /// <summary>always compares greater than valid rowIds</summary>
        AlwaysLast,
    }

    /// <summary>
    /// Compares two index entry byte sequences (port of Jackcess <c>BYTE_CODE_COMPARATOR</c>).
    /// </summary>
    internal static int ByteCodeCompare(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }
        if (left == null)
        {
            return -1;
        }
        if (right == null)
        {
            return 1;
        }

        int len = Math.Min(left.Length, right.Length);
        int pos = 0;
        while (pos < len && left[pos] == right[pos])
        {
            pos++;
        }
        if (pos < len)
        {
            return (left[pos] & 0xFF) < (right[pos] & 0xFF) ? -1 : 1;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static EntryType DetermineEntryType(byte[]? entryBytes, RowId rowId)
    {
        if (entryBytes != null)
        {
            return rowId.IdType == RowId.Type.Normal
                ? EntryType.Normal
                : rowId.IdType == RowId.Type.AlwaysFirst ? EntryType.FirstValid : EntryType.LastValid;
        }
        if (!rowId.IsValid)
        {
            // this is a "special" entry (first/last)
            return rowId.IdType == RowId.Type.AlwaysFirst ? EntryType.AlwaysFirst : EntryType.AlwaysLast;
        }
        throw new ArgumentException("Values was null for valid entry");
    }

    /// <summary>
    /// A single entry in an index leaf (port of Jackcess <c>IndexData.Entry</c>).
    /// </summary>
    internal class Entry : IComparable<Entry>
    {
        private readonly RowId _rowId;
        private readonly byte[]? _entryBytes;
        private readonly EntryType _type;

        protected Entry(byte[]? entryBytes, RowId rowId, EntryType type)
        {
            _rowId = rowId;
            _entryBytes = entryBytes;
            _type = type;
        }

        internal Entry(byte[]? entryBytes, RowId rowId)
            : this(entryBytes, rowId, DetermineEntryType(entryBytes, rowId))
        {
        }

        /// <summary>Reads an existing entry in from a buffer (with optional extra trailing bytes).</summary>
        protected Entry(byte[] buffer, int offset, int entryLen, int extraTrailingLen)
        {
            // we need 4 trailing bytes for the rowId, plus whatever the caller wants
            int colEntryLen = entryLen - (4 + extraTrailingLen);

            _entryBytes = buffer.AsSpan(offset, colEntryLen).ToArray();

            int page = ByteUtil.Get3ByteIntBigEndian(buffer, offset + colEntryLen);
            int row = buffer[offset + colEntryLen + 3];
            _rowId = new RowId(page, row);
            _type = EntryType.Normal;
        }

        internal Entry(byte[] buffer, int offset, int entryLen)
            : this(buffer, offset, entryLen, 0)
        {
        }

        internal RowId RowId => _rowId;

        internal EntryType Type => _type;

        internal virtual int? SubPageNumber => null;

        internal virtual bool IsLeafEntry => true;

        internal bool IsValid => _entryBytes != null;

        internal byte[]? EntryBytes => _entryBytes;

        protected byte[]? GetEntryBytes() => _entryBytes;

        /// <summary>size of this entry in the db</summary>
        internal virtual int Size => (_entryBytes?.Length ?? 0) + 4;

        /// <summary>writes this entry into the given stream, omitting the given prefix bytes</summary>
        internal virtual void Write(ByteStream output, byte[] prefix)
        {
            byte[]? entryBytes = _entryBytes!;
            if (prefix.Length <= entryBytes.Length)
            {
                // write entry bytes, not including prefix
                output.Write(entryBytes, prefix.Length, entryBytes.Length - prefix.Length);
                var tmp = new byte[3];
                ByteUtil.Put3ByteIntBigEndian(tmp, 0, _rowId.PageNumber);
                output.Write(tmp);
            }
            else if (prefix.Length <= entryBytes.Length + 3)
            {
                // the prefix includes part of the page number, write to temp buffer
                // and copy last bytes to output buffer
                var tmp = new byte[3];
                ByteUtil.Put3ByteIntBigEndian(tmp, 0, _rowId.PageNumber);
                int skip = prefix.Length - entryBytes.Length;
                output.Write(tmp, skip, tmp.Length - skip);
            }
            else
            {
                // since the row number would never be the same if the page number is
                // the same, nothing past the page number should ever be included in the prefix
                throw new InvalidOperationException("prefix should never be this long");
            }

            output.Write((byte)_rowId.RowNumber);
        }

        /// <summary>whether the entry bytes are equal between this entry and the given entry</summary>
        internal bool EqualsEntryBytes(Entry other) => ByteCodeCompare(_entryBytes, other._entryBytes) == 0;

        public override bool Equals(object? obj) => obj is Entry other && ReferenceEquals(this, other) || (obj != null && obj.GetType() == GetType() && CompareTo((Entry)obj) == 0);

        public override int GetHashCode() => _rowId.GetHashCode();

        public int CompareTo(Entry? other)
        {
            if (ReferenceEquals(this, other))
            {
                return 0;
            }

            if (IsValid && other!.IsValid)
            {
                // comparing two valid entries: first, compare by actual byte values
                int entryCmp = ByteCodeCompare(_entryBytes, other._entryBytes);
                if (entryCmp != 0)
                {
                    return entryCmp;
                }
            }
            else
            {
                // if the entries are of mixed validity (or both invalid), defer to the EntryType
                int typeCmp = _type.CompareTo(other!.Type);
                if (typeCmp != 0)
                {
                    return typeCmp;
                }
            }

            // at this point the RowId decides the final result
            return _rowId.CompareTo(other!.RowId);
        }

        /// <summary>returns a copy of this entry as a node entry with the given sub page number</summary>
        internal Entry AsNodeEntry(int subPageNumber) => new NodeEntry(_entryBytes, _rowId, _type, subPageNumber);
    }

    /// <summary>
    /// A single node entry in an index (points to a sub-page in the index) (port of
    /// Jackcess <c>IndexData.NodeEntry</c>).
    /// </summary>
    internal sealed class NodeEntry : Entry
    {
        private readonly int _subPageNumber;

        internal NodeEntry(byte[]? entryBytes, RowId rowId, EntryType type, int subPageNumber)
            : base(entryBytes, rowId, type)
        {
            _subPageNumber = subPageNumber;
        }

        /// <summary>reads an existing node entry in from a buffer</summary>
        internal NodeEntry(byte[] buffer, int offset, int entryLen)
            : base(buffer, offset, entryLen, 4)
        {
            _subPageNumber = ByteUtil.GetIntBigEndian(buffer, offset + entryLen - 4);
        }

        internal override int? SubPageNumber => _subPageNumber;

        internal override bool IsLeafEntry => false;

        internal override int Size => base.Size + 4;

        internal override void Write(ByteStream output, byte[] prefix)
        {
            base.Write(output, prefix);
            var tmp = new byte[4];
            ByteUtil.PutIntBigEndian(tmp, 0, _subPageNumber);
            output.Write(tmp);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(this, obj))
            {
                return true;
            }
            if (obj is not NodeEntry other)
            {
                return false;
            }
            return CompareTo(other) == 0 && SubPageNumber == other.SubPageNumber;
        }

        public override int GetHashCode()
        {
            int hashCode = base.GetHashCode();
            if (IsValid)
            {
                hashCode += GetEntryBytes()!.GetHashCode();
            }
            else
            {
                hashCode += Type.GetHashCode();
            }
            hashCode += RowId.GetHashCode();
            hashCode += _subPageNumber;
            return hashCode;
        }
    }
}
