using System.Text;

namespace UCanAccess.File;

/// <summary>
/// An Access table: its definition and row data (port of Jackcess <c>TableImpl</c>).
/// </summary>
public sealed class Table
{
    /// <summary>Marker used by the SQL layer for an omitted INSERT column.</summary>
    internal static readonly object MissingValue = new();
    private const short OffsetMask = 0x1FFF;
    private const short DeletedRowMask = unchecked((short)0x8000);
    private const short OverflowRowMask = (short)0x4000;
    private const int MaxByte = 256;

    private readonly Database _database;
    private readonly byte _tableType;
    private readonly List<Column> _columns = new();
    private readonly Dictionary<string, int> _columnIndexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly UsageMap _ownedPages;
    private readonly UsageMap _freeSpacePages;
    /// <summary>shared buffers of pages being mutated (keeps co-located usage maps consistent)</summary>
    private readonly Dictionary<int, byte[]> _pageBuffers = new();
    private int _rowCount;
    private int _lastLongAutoNumber;
    private readonly List<Column> _varColumns = new();
    private readonly HashSet<int> _longValuePages = new();
    private readonly Dictionary<int, int> _longValuePageReferences = new();
    private bool _longValuePageReferencesInitialized;
    private readonly List<IndexData> _indexDatas = new();
    private readonly List<IndexImpl> _indexes = new();
    private readonly Dictionary<int, ComplexColumnInfo> _complexColumns = new();
    private bool _complexColumnsLoaded;
    /// <summary>offset within the table-definition buffer where the index definitions start</summary>
    private int _indexBlockStart;
    /// <summary>the live first page of the table definition (shared with the usage maps)</summary>
    private byte[] _tableDefPage = Array.Empty<byte>();
    /// <summary>the complete table definition buffer (may span multiple pages)</summary>
    private byte[] _tableDef = Array.Empty<byte>();

    internal Table(Database database, byte[] tableBuffer, int pageNumber, string name, int flags)
    {
        _database = database;
        TableDefPageNumber = pageNumber;
        Name = name;
        Flags = flags;

        JetFormat format = database.Format;

        // load the complete table definition (may span multiple pages)
        tableBuffer = LoadCompleteTableDefinitionBuffer(tableBuffer);
        _tableDef = tableBuffer;
        // the first page is the live definition buffer shared with the usage maps
        _tableDefPage = tableBuffer.Length == format.PageSize
            ? tableBuffer
            : tableBuffer.AsSpan(0, format.PageSize).ToArray();

        _rowCount = ByteUtil.GetIntLittleEndian(_tableDefPage, format.OffsetNumRows);
        _lastLongAutoNumber = ByteUtil.GetIntLittleEndian(_tableDefPage, format.OffsetNextAutoNumber);
        if (format.OffsetNextComplexAutoNumber >= 0)
        {
            _lastComplexTypeAutoNumber = ByteUtil.GetIntLittleEndian(_tableDefPage, format.OffsetNextComplexAutoNumber);
        }
        _tableType = _tableDefPage[format.OffsetTableType];
        MaxColumnCount = ReadShort(_tableDefPage, format.OffsetMaxCols);
        MaxVarColumnCount = ReadShort(_tableDefPage, format.OffsetNumVarCols);
        short columnCount = ReadShort(_tableDefPage, format.OffsetNumCols);
        LogicalIndexCount = ByteUtil.GetIntLittleEndian(_tableDefPage, format.OffsetNumIndexSlots);
        IndexCount = ByteUtil.GetIntLittleEndian(_tableDefPage, format.OffsetNumIndexes);

        int pos = format.OffsetOwnedPages;
        _ownedPages = UsageMap.Read(database, _tableDefPage, pageNumber, ref pos, _pageBuffers);
        _freeSpacePages = UsageMap.Read(database, _tableDefPage, pageNumber, ref pos, _pageBuffers);

        ReadColumnDefinitions(_tableDef, columnCount);
        ReadIndexDefinitions();
    }

    public Database Database => _database;

    internal JetFormat Format => _database.Format;

    public string Name { get; }

    public int Flags { get; }

    public int TableDefPageNumber { get; }

    public int RowCount => _rowCount;

    public int LastLongAutoNumber => _lastLongAutoNumber;

    /// <summary>whether the table has an autonumber column</summary>
    internal bool AutoNumbered => _columns.Any(c => c.AutoNumber);

    public short MaxColumnCount { get; }

    public short MaxVarColumnCount { get; }

    public int LogicalIndexCount { get; }

    public int IndexCount { get; }

    public bool IsSystem => _tableType == 0x53;

    /// <summary>columns in "data" order (sorted by column number)</summary>
    public IReadOnlyList<Column> Columns => _columns;

    internal IReadOnlyDictionary<string, int> ColumnIndexes => _columnIndexes;

    internal UsageMap OwnedPages => _ownedPages;

    internal UsageMap FreeSpacePages => _freeSpacePages;

    internal void RegisterLongValuePage(int pageNumber) => _longValuePages.Add(pageNumber);

    internal IReadOnlyCollection<int> CollectLongValuePages()
    {
        if (!HasLongValueColumns)
        {
            return _longValuePages;
        }
        EnsureLongValuePageReferences();
        return _longValuePages;
    }

    internal void UpdateLongValuePageReferences(IEnumerable<int> removedPages, IEnumerable<int> addedPages)
    {
        var removed = removedPages.ToHashSet();
        var added = addedPages.ToHashSet();
        if (removed.Count == 0 && added.Count == 0)
        {
            return;
        }
        EnsureLongValuePageReferences();

        foreach (int pageNumber in removed.Except(added))
        {
            if (_longValuePageReferences.TryGetValue(pageNumber, out int count) && count > 0)
            {
                _longValuePageReferences[pageNumber] = count - 1;
            }
        }
        foreach (int pageNumber in added.Except(removed))
        {
            _longValuePages.Add(pageNumber);
            _longValuePageReferences.TryGetValue(pageNumber, out int count);
            _longValuePageReferences[pageNumber] = count + 1;
        }

        foreach (int pageNumber in removed.Except(added))
        {
            if (_longValuePageReferences.TryGetValue(pageNumber, out int count) && count != 0)
            {
                continue;
            }
            _database.PageChannel.DeallocatePage(pageNumber);
            _longValuePageReferences.Remove(pageNumber);
            _longValuePages.Remove(pageNumber);
        }
    }

    /// <summary>releases pages allocated for a row which was never committed</summary>
    private void ReleaseAllocatedLongValuePages(IEnumerable<int> pages)
    {
        foreach (int pageNumber in pages.ToHashSet())
        {
            _database.PageChannel.DeallocatePage(pageNumber);
            _longValuePageReferences.Remove(pageNumber);
            _longValuePages.Remove(pageNumber);
        }
    }

    /// <summary>the physical indexes of this table (index data definitions)</summary>
    internal IReadOnlyList<IndexData> IndexDatas => _indexDatas;

    /// <summary>the logical indexes of this table</summary>
    internal IReadOnlyList<IndexImpl> Indexes => _indexes;

    /// <summary>table-definition and usage-map pages owned by this table metadata</summary>
    internal IReadOnlyCollection<int> MetadataPageNumbers
    {
        get
        {
            var pages = new HashSet<int>(_pageBuffers.Keys) { TableDefPageNumber };
            int page = TableDefPageNumber;
            while (page != 0)
            {
                byte[] buffer = new byte[Format.PageSize];
                _database.PageChannel.ReadPage(buffer, page);
                int next = ByteUtil.GetIntLittleEndian(buffer, Format.OffsetNextTableDefPage);
                pages.Add(page);
                page = next;
            }
            pages.UnionWith(_ownedPages.ReferenceMapPageNumbers);
            pages.UnionWith(_freeSpacePages.ReferenceMapPageNumbers);
            foreach (IndexData indexData in _indexDatas)
            {
                if (indexData.OwnedPages != null)
                {
                    pages.UnionWith(indexData.OwnedPages.ReferenceMapPageNumbers);
                }
            }
            return pages;
        }
    }

    internal static IReadOnlyCollection<int> EnumeratePages(UsageMap? map)
    {
        if (map == null)
        {
            return Array.Empty<int>();
        }
        var result = new List<int>();
        UsageMap.PageCursor cursor = map.Cursor();
        while (true)
        {
            int page = cursor.GetNextPage();
            if (page == PageChannelImpl.InvalidPageNumber)
            {
                break;
            }
            result.Add(page);
        }
        return result;
    }

    internal void BuildIndex(IndexData indexData, IEnumerable<RowLocation> rows)
    {
        foreach (RowLocation location in rows)
        {
            IndexData.PendingChange? change = indexData.PrepareAddRow(
                location.Row.ToArray(), new RowId(location.PageNumber, location.RowNumber), null);
            IndexData.CommitAll(change);
        }
        PersistIndex(indexData);
    }

    internal void PersistIndex(IndexData indexData)
    {
        PutInt(_tableDefPage, indexData.UniqueEntryCountOffset, indexData.UniqueEntryCount);
        indexData.Update();
        _database.PageChannel.WritePage(_tableDefPage, TableDefPageNumber);
    }

    private int _lastComplexTypeAutoNumber;

    /// <summary>
    /// Adds a row of values to the table and writes it to the database file.
    /// </summary>
    public object?[] AddRow(object?[] values)
        => AddRow(values, preserveAutoNumbers: false);

    /// <summary>
    /// Adds a row while preserving values read from an existing table during a
    /// DDL migration.  Normal inserts intentionally always generate Access
    /// AutoNumber values; migration replay is the one case where an existing
    /// AutoNumber must be written back verbatim.
    /// </summary>
    internal object?[] AddRowPreservingAutoNumbers(object?[] values)
        => AddRow(values, preserveAutoNumbers: true);

    private object?[] AddRow(object?[] values, bool preserveAutoNumbers)
    {
        if (_database.IsReadOnly)
        {
            throw new DatabaseException("The database was opened read-only.");
        }
        if (values.Length < _columns.Count)
        {
            var padded = Enumerable.Repeat<object?>(MissingValue, _columns.Count).ToArray();
            Array.Copy(values, padded, values.Length);
            values = padded;
        }
        JetFormat format = Format;
        PageChannel pageChannel = _database.PageChannel;

        pageChannel.StartWrite();
        var complexWrites = new List<ComplexWrite>();
        var newLongValuePages = new HashSet<int>();
        HashSet<int> existingLongValuePages = new();
        int previousAutoNumber = _lastLongAutoNumber;
        int newlyAllocatedDataPage = PageChannelImpl.InvalidPageNumber;
        bool rowCommitted = false;
        object?[]? committedValues = null;
        try
        {
            values = NormalizeValues(values);

            HandleDefaultValues(values);
            HandleAutoNumbers(values, preserveAutoNumbers);
            ValidateRequiredValues(values);
            // Resolve omitted/defaulted values before complex-field handling.  The
            // MissingValue marker is an internal SQL-layer sentinel and must never
            // be interpreted as a CLR complex value.
            complexWrites = PrepareComplexValues(values, null, null, null);
            if (HasLongValueColumns)
            {
                EnsureLongValuePageReferences();
                existingLongValuePages = new HashSet<int>(_longValuePages);
            }

            byte[] rowData = CreateRow(values);
            if (rowData.Length > format.MaxRowSize)
            {
                throw new DatabaseException($"Row size {rowData.Length} is too large ({format.MaxRowSize})");
            }

            CollectRowLongValuePages(rowData, 0, rowData.Length, newLongValuePages);

            try
            {
                // Validate referential constraints before allocating a data page.  This
                // keeps a rejected insert from leaving an empty page behind.
                FkEnforcer.AddRow(this, values);

                int pageNumber = FindFreeRowSpace(rowData.Length, out bool allocatedNewPage);
                if (allocatedNewPage)
                {
                    newlyAllocatedDataPage = pageNumber;
                }
                byte[] dataPage = _addRowBuffer;
                int rowNum = GetRowsOnDataPage(dataPage, format);
                var rowId = new RowId(pageNumber, rowNum);

                // before we actually write the row data, we verify all the database constraints
                IndexData.PendingChange? idxChange = null;
                try
                {
                    // prepare index updates
                    foreach (IndexData indexData in _indexDatas)
                    {
                        idxChange = indexData.PrepareAddRow(values, rowId, idxChange);
                    }

                    // complete index updates
                    IndexData.CommitAll(idxChange);
                }
                catch (Exception)
                {
                    try
                    {
                        IndexData.RollbackAll(idxChange);
                    }
                    catch
                    {
                        // Preserve the original constraint or index exception.
                    }
                    throw;
                }

                var (_, rowLocation) = AddDataPageRow(dataPage, rowData.Length, format, 0);
                Array.Copy(rowData, 0, dataPage, rowLocation, rowData.Length);
                pageChannel.WritePage(dataPage, pageNumber);

                UpdateTableDefinition(1);
                UpdateLongValuePageReferences(Array.Empty<int>(), newLongValuePages);
                newlyAllocatedDataPage = PageChannelImpl.InvalidPageNumber;
                rowCommitted = true;
                committedValues = values;
            }
            catch
            {
                if (newlyAllocatedDataPage != PageChannelImpl.InvalidPageNumber)
                {
                    try
                    {
                        ReleaseEmptyDataPage(newlyAllocatedDataPage);
                    }
                    catch
                    {
                        // Preserve the original insert exception.
                    }
                }
                try
                {
                    var candidates = new HashSet<int>(newLongValuePages);
                    candidates.UnionWith(_longValuePages.Except(existingLongValuePages));
                    ReleaseAllocatedLongValuePages(candidates);
                }
                catch
                {
                    // Preserve the operation's original exception.
                }
                throw;
            }
        }
        finally
        {
            if (!rowCommitted)
            {
                _lastLongAutoNumber = previousAutoNumber;
            }
            pageChannel.FinishWrite();
        }

        if (rowCommitted && complexWrites.Count > 0)
        {
            WriteComplexChildren(complexWrites, replaceExisting: false);
        }
        return committedValues ?? values;
    }

    /// <summary>
    /// Preserves a previously allocated AutoNumber counter when a rebuilt table
    /// contains gaps or deleted rows and therefore cannot infer the old counter
    /// from the values that were replayed.
    /// </summary>
    internal void PreserveLastLongAutoNumber(int value)
    {
        if (value <= _lastLongAutoNumber)
        {
            return;
        }
        if (_database.IsReadOnly)
        {
            throw new DatabaseException("The database was opened read-only.");
        }

        PageChannel pageChannel = _database.PageChannel;
        pageChannel.StartWrite();
        try
        {
            _lastLongAutoNumber = value;
            UpdateTableDefinition(0);
        }
        finally
        {
            pageChannel.FinishWrite();
        }
    }

    /// <summary>
    /// Retargets foreign-key index metadata which points at a replaced parent
    /// table-definition page.  The logical index block may span the table
    /// definition page chain, so the update is mapped back to its physical pages
    /// instead of assuming the definition fits on page one.
    /// </summary>
    internal bool RetargetForeignKeyIndexes(int oldParentPage, int newParentPage,
        IReadOnlyDictionary<int, int>? parentIndexMap = null)
    {
        var indexes = _indexes
            .Where(index => index.IsForeignKey
                && index.RelatedTablePageNumber == oldParentPage)
            .ToArray();
        if (indexes.Length == 0)
        {
            return false;
        }

        var definitionPages = new List<int> { TableDefPageNumber };
        int nextPage = ByteUtil.GetIntLittleEndian(_tableDefPage, Format.OffsetNextTableDefPage);
        while (nextPage != 0)
        {
            definitionPages.Add(nextPage);
            byte[] page = new byte[Format.PageSize];
            _database.PageChannel.ReadPage(page, nextPage);
            nextPage = ByteUtil.GetIntLittleEndian(page, Format.OffsetNextTableDefPage);
        }

        var pageBuffers = new Dictionary<int, byte[]>();

        void WriteDefinitionInt(int offset, int value)
        {
            for (int byteIndex = 0; byteIndex < 4; byteIndex++)
            {
                int absoluteOffset = offset + byteIndex;
                int pageIndex;
                int pageOffset;
                if (absoluteOffset < Format.PageSize)
                {
                    pageIndex = 0;
                    pageOffset = absoluteOffset;
                }
                else
                {
                    int relative = absoluteOffset - Format.PageSize;
                    int payloadSize = Format.PageSize - 8;
                    pageIndex = 1 + relative / payloadSize;
                    pageOffset = 8 + relative % payloadSize;
                }
                if (pageIndex >= definitionPages.Count)
                {
                    throw new DatabaseException("Foreign-key metadata points outside the table definition.");
                }
                int pageNumber = definitionPages[pageIndex];
                if (!pageBuffers.TryGetValue(pageNumber, out byte[]? pageBuffer))
                {
                    pageBuffer = new byte[Format.PageSize];
                    _database.PageChannel.ReadPage(pageBuffer, pageNumber);
                    pageBuffers.Add(pageNumber, pageBuffer);
                }
                pageBuffer[pageOffset] = (byte)(value >> (8 * byteIndex));

                if (absoluteOffset < _tableDef.Length)
                {
                    _tableDef[absoluteOffset] = pageBuffer[pageOffset];
                }
            }
        }

        foreach (IndexImpl index in indexes)
        {
            WriteDefinitionInt(index.RelatedTablePageNumberOffset, newParentPage);
            index.RetargetRelatedTablePage(oldParentPage, newParentPage);

            if (parentIndexMap != null
                && parentIndexMap.TryGetValue(index.RelatedIndexNumber, out int newIndexNumber)
                && newIndexNumber != index.RelatedIndexNumber)
            {
                WriteDefinitionInt(index.RelatedIndexNumberOffset, newIndexNumber);
                index.RetargetRelatedIndex(index.RelatedIndexNumber, newIndexNumber);
            }
        }

        foreach ((int pageNumber, byte[] pageBuffer) in pageBuffers)
        {
            _database.PageChannel.WritePage(pageBuffer, pageNumber);
        }
        if (pageBuffers.TryGetValue(TableDefPageNumber, out byte[]? firstPage))
        {
            Array.Copy(firstPage, _tableDefPage, Format.PageSize);
        }
        return true;
    }

    /// <summary>
    /// Deletes the given row (marks it deleted and decrements the row count).
    /// </summary>
    public void DeleteRow(int pageNumber, int rowNumber)
    {
        if (_database.IsReadOnly)
        {
            throw new DatabaseException("The database was opened read-only.");
        }
        InvalidateAddRowPage();
        JetFormat format = Format;
        PageChannel pageChannel = _database.PageChannel;
        var complexDeletes = new List<(ComplexColumnInfo Info, Column Column, int Key)>();

        pageChannel.StartWrite();
        try
        {
            var (status, page, _) = PositionAtRowHeader(pageNumber, rowNumber);
            if (status is RowStatus.InvalidPage or RowStatus.InvalidRow or RowStatus.Deleted)
            {
                throw new DatabaseException($"Row ({pageNumber}, {rowNumber}) is not a normal row.");
            }

            // capture the row values before it is marked deleted
            object?[] oldRowValues = GetRow(pageNumber, rowNumber)?.ToArray() ?? Array.Empty<object?>();
            var oldLongValuePages = new HashSet<int>();
            var positioned = PositionAtRowData(pageNumber, rowNumber)
                ?? throw new DatabaseException($"Row ({pageNumber}, {rowNumber}) is invalid or deleted.");
            EnsureComplexColumns();
            foreach (Column complexColumn in _columns.Where(c => c.Type == DataType.ComplexType))
            {
                if (_complexColumns.TryGetValue(complexColumn.ColumnIndex, out ComplexColumnInfo? info)
                    && TryInt(ReadColumnValue(positioned.page, positioned.rowStart,
                        positioned.rowEnd, complexColumn), out int key))
                {
                    oldRowValues[complexColumn.ColumnIndex] = key;
                    complexDeletes.Add((info, complexColumn, key));
                }
            }
            CollectRowLongValuePages(positioned.page, positioned.rowStart,
                positioned.rowEnd, oldLongValuePages);
            if (oldLongValuePages.Count > 0)
            {
                EnsureLongValuePageReferences();
            }

            // A relocated row has a physical overflow record in addition to its
            // header record.  Mark that physical record deleted as well so it is
            // not enumerated or retained forever after the logical delete.
            if (positioned.pageNumber != pageNumber || positioned.rowNumber != rowNumber)
            {
                byte[] dataPage = positioned.pageNumber == pageNumber ? page : positioned.page;
                int dataRowOffset = GetRowStartOffset(positioned.rowNumber, format);
                PutShort(dataPage, dataRowOffset,
                    (short)(ReadShort(dataPage, dataRowOffset) | DeletedRowMask));
                if (positioned.pageNumber != pageNumber)
                {
                    pageChannel.WritePage(dataPage, positioned.pageNumber);
                }
            }

            // enforce foreign-key constraints (may cascade)
            FkEnforcer.DeleteRow(this, oldRowValues);

            int rowStartIndex = GetRowStartOffset(rowNumber, format);
            PutShort(page, rowStartIndex, (short)(ReadShort(page, rowStartIndex) | DeletedRowMask));
            pageChannel.WritePage(page, pageNumber);

            // remove the row from any indexes
            var rowId = new RowId(pageNumber, rowNumber);
            foreach (IndexData indexData in _indexDatas)
            {
                indexData.DeleteRow(oldRowValues, rowId);
            }

            UpdateTableDefinition(-1);
            UpdateLongValuePageReferences(oldLongValuePages, Array.Empty<int>());
        }
        finally
        {
            pageChannel.FinishWrite();
        }

        DeleteComplexChildren(complexDeletes);
    }

    /// <summary>
    /// Updates the given row with new values (port of Jackcess <c>updateRow</c>,
    /// without index maintenance).
    /// </summary>
    public object?[] UpdateRow(int pageNumber, int rowNumber, object?[] values)
        => UpdateRow(pageNumber, rowNumber, values, skipForeignKeyValidation: false);

    internal object?[] UpdateRow(int pageNumber, int rowNumber, object?[] values,
        bool skipForeignKeyValidation)
    {
        if (_database.IsReadOnly)
        {
            throw new DatabaseException("The database was opened read-only.");
        }
        InvalidateAddRowPage();
        JetFormat format = Format;
        PageChannel pageChannel = _database.PageChannel;

        pageChannel.StartWrite();
        var newLongValuePages = new HashSet<int>();
        HashSet<int> existingLongValuePages = new();
        bool committed = false;
        object?[]? committedValues = null;
        List<ComplexWrite> complexWrites = new();
        int newlyAllocatedDataPage = PageChannelImpl.InvalidPageNumber;
        try
        {
            var oldPositioned = PositionAtRowData(pageNumber, rowNumber)
                ?? throw new DatabaseException($"Row ({pageNumber}, {rowNumber}) is invalid or deleted.");
            var (headerStatus, headerPage, _) = PositionAtRowHeader(pageNumber, rowNumber);
            if (headerStatus is RowStatus.InvalidPage or RowStatus.InvalidRow or RowStatus.Deleted)
            {
                throw new DatabaseException($"Row ({pageNumber}, {rowNumber}) is invalid or deleted.");
            }
            int headerRowStart = CleanRowStart(ReadShort(headerPage, GetRowStartOffset(rowNumber, format)));
            int headerRowEnd = FindRowEnd(headerPage, rowNumber, format);
            int headerPageNumber = pageNumber;
            var (oldPage, oldPageNumber, oldDataRowNumber, oldRowStart, oldRowEnd) = oldPositioned;
            int oldRowSize = oldRowEnd - oldRowStart;
            var oldLongValuePages = new HashSet<int>();
            CollectRowLongValuePages(oldPage, oldRowStart, oldRowEnd, oldLongValuePages);
            if (HasLongValueColumns)
            {
                EnsureLongValuePageReferences();
                existingLongValuePages = new HashSet<int>(_longValuePages);
            }

            if (values.Length < _columns.Count)
            {
                var padded = new object?[_columns.Count];
                Array.Copy(values, padded, values.Length);
                values = padded;
            }

            // auto-number columns keep their existing value when null is passed
            foreach (Column column in _columns)
            {
                if (column.AutoNumber && IsNullValue(values[column.ColumnIndex]))
                {
                    values[column.ColumnIndex] = ReadColumnValue(oldPage, oldRowStart, oldRowEnd, column);
                }
            }

            complexWrites = PrepareComplexValues(values, oldPage, oldRowStart, oldRowEnd);
            values = NormalizeValues(values);

            ValidateRequiredValues(values);

            byte[] newRowData = CreateRow(values, oldRowSize);
            if (newRowData.Length > format.MaxRowSize)
            {
                throw new DatabaseException($"Row size {newRowData.Length} is too large ({format.MaxRowSize})");
            }
            CollectRowLongValuePages(newRowData, 0, newRowData.Length, newLongValuePages);

            // verify all the database constraints and update the indexes
            object?[] oldRowValues = ReadRow(oldPage, oldRowStart, oldRowEnd).ToArray();
            foreach (Column complexColumn in _columns.Where(c => c.Type == DataType.ComplexType))
            {
                // ReadRow exposes the friendly CLR array for complex fields;
                // indexes and the parent row itself contain the scalar pointer.
                oldRowValues[complexColumn.ColumnIndex] =
                    ReadColumnValue(oldPage, oldRowStart, oldRowEnd, complexColumn);
            }
            var rowId = new RowId(pageNumber, rowNumber);
            FkEnforcer.UpdateRow(this, oldRowValues, values, skipForeignKeyValidation);
            IndexData.PendingChange? idxChange = null;
            try
            {
                foreach (IndexData indexData in _indexDatas)
                {
                    idxChange = indexData.PrepareUpdateRow(oldRowValues, rowId, values, idxChange);
                }
                IndexData.CommitAll(idxChange);
            }
            catch (Exception)
            {
                try
                {
                    IndexData.RollbackAll(idxChange);
                }
                catch
                {
                    // Preserve the original constraint or index exception.
                }
                throw;
            }

            if (oldRowSize >= newRowData.Length)
            {
                // fits in the existing space: overwrite in place
                Array.Copy(newRowData, 0, oldPage, oldRowStart, newRowData.Length);
                pageChannel.WritePage(oldPage, oldPageNumber);
            }
            else
            {
                // relocate to a new row location; the header row becomes an overflow pointer
                int newPageNumber = FindFreeRowSpace(newRowData.Length, out bool allocatedNewPage);
                if (allocatedNewPage)
                {
                    newlyAllocatedDataPage = newPageNumber;
                }
                byte[] newPage = _addRowBuffer;
                bool samePage = newPageNumber == headerPageNumber;
                if (samePage)
                {
                    // The new row shares the page with the original row header.
                    newPage = headerPage;
                }
                else if (oldPageNumber == newPageNumber)
                {
                    // FindFreeRowSpace may reuse the page containing the old
                    // overflow row.  Keep one buffer for both operations; writing
                    // the stale buffer returned by PositionAtRowData afterwards
                    // would otherwise erase the newly appended row.
                    newPage = oldPage;
                }
                var (newRowNum, newRowLocation) = AddDataPageRow(newPage, newRowData.Length, format, DeletedRowMask);
                Array.Copy(newRowData, 0, newPage, newRowLocation, newRowData.Length);

                // write the overflow pointer into the header row and clear the rest
                headerPage[headerRowStart] = (byte)newRowNum;
                headerPage[headerRowStart + 1] = (byte)newPageNumber;
                headerPage[headerRowStart + 2] = (byte)(newPageNumber >> 8);
                headerPage[headerRowStart + 3] = (byte)(newPageNumber >> 16);
                for (int i = headerRowStart + 4; i < headerRowEnd; i++)
                {
                    headerPage[i] = 0;
                }

                // set the overflow flag on the header row
                int headerRowIndex = GetRowStartOffset(rowNumber, format);
                PutShort(headerPage, headerRowIndex, (short)(ReadShort(headerPage, headerRowIndex) | OverflowRowMask));

                bool oldRowIsSeparate = oldPageNumber != headerPageNumber || oldDataRowNumber != rowNumber;
                if (oldRowIsSeparate && oldPageNumber == newPageNumber)
                {
                    int oldRowOffset = GetRowStartOffset(oldDataRowNumber, format);
                    PutShort(newPage, oldRowOffset,
                        (short)(ReadShort(newPage, oldRowOffset) | DeletedRowMask));
                }

                if (!samePage)
                {
                    pageChannel.WritePage(newPage, newPageNumber);
                }
                pageChannel.WritePage(headerPage, headerPageNumber);

                if (oldRowIsSeparate && oldPageNumber != headerPageNumber && oldPageNumber != newPageNumber)
                {
                    int oldRowOffset = GetRowStartOffset(oldDataRowNumber, format);
                    PutShort(oldPage, oldRowOffset,
                        (short)(ReadShort(oldPage, oldRowOffset) | DeletedRowMask));
                    if (oldPageNumber != headerPageNumber)
                    {
                        pageChannel.WritePage(oldPage, oldPageNumber);
                    }
                }
                newlyAllocatedDataPage = PageChannelImpl.InvalidPageNumber;
            }

            UpdateTableDefinition(0);
            UpdateLongValuePageReferences(oldLongValuePages, newLongValuePages);
            committed = true;
            committedValues = values;
        }
        catch
        {
            if (!committed)
            {
                if (newlyAllocatedDataPage != PageChannelImpl.InvalidPageNumber)
                {
                    try
                    {
                        ReleaseEmptyDataPage(newlyAllocatedDataPage);
                    }
                    catch
                    {
                        // Preserve the original update exception.
                    }
                }
                try
                {
                    var candidates = new HashSet<int>(newLongValuePages);
                    candidates.UnionWith(_longValuePages.Except(existingLongValuePages));
                    ReleaseAllocatedLongValuePages(candidates);
                }
                catch
                {
                    // Preserve the operation's original exception.
                }
            }
            throw;
        }
        finally
        {
            pageChannel.FinishWrite();
        }

        if (committed && complexWrites.Count > 0)
        {
            WriteComplexChildren(complexWrites, replaceExisting: true);
        }
        return committedValues ?? values;
    }

    private object? ReadColumnValue(byte[] page, int rowStart, int rowEnd, Column column)
    {
        // minimal single-column read used by UpdateRow
        NullMask nullMask = ReadRowNullMask(page, rowStart, rowEnd);

        if (nullMask.IsNull(column))
        {
            return null;
        }

        int colDataPos;
        int colDataLen;
        if (!column.VariableLength)
        {
            int dataStart = rowStart + Format.OffsetColumnFixedDataRowOffset;
            colDataPos = dataStart + column.FixedDataOffset;
            colDataLen = column.FixedDataSize;
        }
        else
        {
            (colDataPos, int colDataEnd) = GetVariableColumnBounds(page, rowStart, rowEnd, nullMask, column);
            colDataLen = colDataEnd - colDataPos;
        }
        ValidateColumnBounds(colDataPos, colDataLen, rowStart, rowEnd);
        return column.Read(page, colDataPos, colDataLen);
    }

    internal void CollectRowLongValuePages(byte[] page, int rowStart, int rowEnd, HashSet<int> pages)
    {
        NullMask nullMask = ReadRowNullMask(page, rowStart, rowEnd);
        foreach (Column column in _varColumns)
        {
            if (column.Type is not (DataType.Memo or DataType.Ole) || nullMask.IsNull(column))
            {
                continue;
            }
            (int start, int end) = GetVariableColumnBounds(page, rowStart, rowEnd, nullMask, column);
            ValidateColumnBounds(start, end - start, rowStart, rowEnd);
            column.CollectLongValuePages(page.AsSpan(start, end - start), pages);
        }
    }

    private (int Start, int End) GetVariableColumnBounds(byte[] page, int rowStart, int rowEnd,
        NullMask nullMask, Column column)
    {
        if (Format.SizeRowVarColOffset == 2)
        {
            int offsetPosition = rowEnd - nullMask.ByteSize - 4 - column.VarLenTableIndex * 2;
            if (offsetPosition - 1 < rowStart || offsetPosition + 1 >= rowEnd)
            {
                throw new DatabaseException("Invalid variable-length column offset position.");
            }
            int start = ReadShort(page, offsetPosition);
            int end = ReadShort(page, offsetPosition - 2);
            if (start < 0 || end < start || rowStart + end > rowEnd)
            {
                throw new DatabaseException("Invalid variable-length column offsets.");
            }
            return (rowStart + start, rowStart + end);
        }

        short[] offsets = ReadJumpTableVarColOffsets(page, rowStart, rowEnd, nullMask);
        if (column.VarLenTableIndex < 0 || column.VarLenTableIndex + 1 >= offsets.Length)
        {
            throw new DatabaseException("Invalid variable-length column index.");
        }
        int varStart = offsets[column.VarLenTableIndex];
        int varEnd = offsets[column.VarLenTableIndex + 1];
        if (varStart < 0 || varEnd < varStart || rowStart + varEnd > rowEnd)
        {
            throw new DatabaseException("Invalid Jet 3 variable-length column offsets.");
        }
        return (rowStart + varStart, rowStart + varEnd);
    }

    private byte[] _addRowBuffer = Array.Empty<byte>();
    private int _addRowPageNumber = PageChannelImpl.InvalidPageNumber;

    private bool HasLongValueColumns
        => _varColumns.Any(column => column.Type is DataType.Memo or DataType.Ole);

    /// <summary>
    /// Fills in all auto-number column values for add.
    /// </summary>
    private void HandleAutoNumbers(object?[] row, bool preserveAutoNumbers)
    {
        if (!_columns.Any(c => c.AutoNumber))
        {
            return;
        }
        bool allowAutoNumberInsert = _database.AllowAutoNumberInsert(Name);
        foreach (Column column in _columns)
        {
            if (!column.AutoNumber)
            {
                continue;
            }
            if (column.Type == DataType.Long)
            {
                bool keepSupplied = preserveAutoNumbers || allowAutoNumberInsert;
                if (keepSupplied && !IsNullValue(row[column.ColumnIndex]))
                {
                    int supplied = Convert.ToInt32(row[column.ColumnIndex], System.Globalization.CultureInfo.InvariantCulture);
                    _lastLongAutoNumber = Math.Max(_lastLongAutoNumber, supplied);
                    row[column.ColumnIndex] = supplied;
                }
                else if (allowAutoNumberInsert && !preserveAutoNumbers)
                {
                    throw new DatabaseException(
                        $"AutoNumber column '{column.Name}' requires an explicit value while AUTOINCREMENT is disabled.");
                }
                else
                {
                    row[column.ColumnIndex] = ++_lastLongAutoNumber;
                }
            }
            else if (column.Type == DataType.Guid)
            {
                if (!preserveAutoNumbers || IsNullValue(row[column.ColumnIndex]))
                {
                    row[column.ColumnIndex] = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
                }
            }
        }
    }

    private void HandleDefaultValues(object?[] row)
    {
        foreach (Column column in _columns)
        {
            if (!ReferenceEquals(row[column.ColumnIndex], MissingValue))
            {
                continue;
            }
            row[column.ColumnIndex] = column.DefaultValue == null
                ? null
                : DefaultValueEvaluator.Evaluate(column.DefaultValue);
        }
    }

    private object?[] NormalizeValues(object?[] values)
    {
        for (int i = 0; i < _columns.Count; i++)
        {
            if (values[i] is DBNull)
            {
                values[i] = null;
            }
        }
        return values;
    }

    private void ValidateRequiredValues(object?[] values)
    {
        foreach (Column column in _columns)
        {
            if (column.Required && IsNullValue(values[column.ColumnIndex]))
            {
                throw new DatabaseException(
                    $"Column '{column.Name}' does not allow NULL values.");
            }
        }
    }

    private static bool IsNullValue(object? value)
        => value is null or DBNull;

    /// <summary>
    /// Serializes a row of values into its byte representation (Jackcess <c>createRow</c>).
    /// </summary>
    private byte[] CreateRow(object?[] rowArray, int minRowSize = 0)
    {
        JetFormat format = Format;
        var buffer = new byte[format.MaxRowSize];
        int pos = 0;

        PutShort(buffer, ref pos, MaxColumnCount);
        var nullMask = new NullMask(MaxColumnCount);

        int fixedDataStart = pos;
        int fixedDataEnd = pos;
        foreach (Column col in _columns)
        {
            if (col.VariableLength)
            {
                continue;
            }

            object? rowValue = rowArray[col.ColumnIndex];

            if (col.StoreInNullMask)
            {
                if (rowValue is true)
                {
                    nullMask.MarkNotNull(col);
                }
                rowValue = null;
            }

            if (rowValue != null)
            {
                nullMask.MarkNotNull(col);
                byte[] data = col.Write(rowValue, 0);
                Array.Copy(data, 0, buffer, fixedDataStart + col.FixedDataOffset, data.Length);
            }

            int colEnd = fixedDataStart + col.FixedDataOffset + col.FixedDataSize;
            if (colEnd > fixedDataEnd)
            {
                fixedDataEnd = colEnd;
            }
        }
        pos = fixedDataEnd;

        if (MaxVarColumnCount > 0)
        {
            int maxRowSize = format.MaxRowSize - pos;
            int trailerSize = nullMask.ByteSize + 4 + MaxVarColumnCount * 2;
            maxRowSize -= trailerSize;

            foreach (Column varCol in _varColumns)
            {
                if (varCol.Type is DataType.Memo or DataType.Ole && rowArray[varCol.ColumnIndex] != null)
                {
                    maxRowSize -= format.SizeLongValueDef;
                }
            }

            var varColumnOffsets = new short[MaxVarColumnCount];
            int varOffsetIdx = 0;
            foreach (Column varCol in _varColumns)
            {
                short offset = (short)pos;
                object? rowValue = rowArray[varCol.ColumnIndex];
                if (rowValue != null)
                {
                    nullMask.MarkNotNull(varCol);
                    byte[] data = varCol.Write(rowValue, maxRowSize);
                    maxRowSize -= data.Length;
                    if (varCol.Type is DataType.Memo or DataType.Ole)
                    {
                        maxRowSize += format.SizeLongValueDef;
                    }
                    Array.Copy(data, 0, buffer, pos, data.Length);
                    pos += data.Length;
                }
                while (varOffsetIdx <= varCol.VarLenTableIndex)
                {
                    varColumnOffsets[varOffsetIdx++] = offset;
                }
            }
            while (varOffsetIdx < varColumnOffsets.Length)
            {
                varColumnOffsets[varOffsetIdx++] = (short)pos;
            }

            int eod = pos;
            if (format.SizeRowVarColOffset == 1)
            {
                // Jet 3: variable length columns use a jump-table based trailer
                WriteJet3VarTrailer(buffer, ref pos, varColumnOffsets, eod, nullMask, minRowSize);
            }
            else
            {
                if (pos + trailerSize < minRowSize)
                {
                    // pad the row to get to the min byte size
                    pos = minRowSize - trailerSize;
                }
                PutShort(buffer, ref pos, (short)eod);
                for (int i = MaxVarColumnCount - 1; i >= 0; i--)
                {
                    PutShort(buffer, ref pos, varColumnOffsets[i]);
                }
                PutShort(buffer, ref pos, MaxVarColumnCount);
            }
        }
        else if (pos + nullMask.ByteSize < minRowSize)
        {
            pos = minRowSize - nullMask.ByteSize;
        }

        int nullMaskPos = pos;
        nullMask.WriteTo(buffer, nullMaskPos);
        pos += nullMask.ByteSize;

        var result = new byte[pos];
        Array.Copy(buffer, result, pos);
        return result;
    }

    /// <summary>
    /// Finds (or creates) a data page with room for the given row size.
    /// </summary>
    private int FindFreeRowSpace(int rowSize, out bool allocatedNewPage)
    {
        JetFormat format = Format;
        PageChannel pageChannel = _database.PageChannel;
        allocatedNewPage = false;

        if (_addRowPageNumber != PageChannelImpl.InvalidPageNumber
            && RowFitsOnDataPage(rowSize, _addRowBuffer, format))
        {
            return _addRowPageNumber;
        }
        if (_addRowPageNumber != PageChannelImpl.InvalidPageNumber)
        {
            _freeSpacePages.RemovePageNumber(_addRowPageNumber);
            InvalidateAddRowPage();
        }

        // collect owned pages in order
        var owned = new List<int>();
        var cursor = _ownedPages.Cursor();
        while (true)
        {
            int page = cursor.GetNextPage();
            if (page == PageChannelImpl.InvalidPageNumber)
            {
                break;
            }
            owned.Add(page);
        }

        // walk the owned pages backwards; only pages listed in the free space map qualify
        for (int i = owned.Count - 1; i >= 0; i--)
        {
            int pageNumber = owned[i];
            if (!_freeSpacePages.ContainsPageNumber(pageNumber))
            {
                continue;
            }
            var dataPage = new byte[format.PageSize];
            pageChannel.ReadPage(dataPage, pageNumber);
            if (dataPage[0] != PageTypes.Data)
            {
                continue;
            }
            if (RowFitsOnDataPage(rowSize, dataPage, format))
            {
                _addRowBuffer = dataPage;
                _addRowPageNumber = pageNumber;
                return pageNumber;
            }
            // page is full; remove it from the free space map
            _freeSpacePages.RemovePageNumber(pageNumber);
        }

        allocatedNewPage = true;
        return NewDataPage();
    }

    private void ReleaseEmptyDataPage(int pageNumber)
    {
        if (pageNumber == PageChannelImpl.InvalidPageNumber)
        {
            return;
        }
        _ownedPages.RemovePageNumber(pageNumber);
        _freeSpacePages.RemovePageNumber(pageNumber);
        _database.PageChannel.DeallocatePage(pageNumber);
        if (_addRowPageNumber == pageNumber)
        {
            InvalidateAddRowPage();
        }
    }

    private void InvalidateAddRowPage()
    {
        _addRowPageNumber = PageChannelImpl.InvalidPageNumber;
        _addRowBuffer = Array.Empty<byte>();
    }

    private int NewDataPage()
    {
        JetFormat format = Format;
        PageChannel pageChannel = _database.PageChannel;

        int pageNumber = pageChannel.AllocateNewPage();
        var dataPage = new byte[format.PageSize];
        dataPage[0] = PageTypes.Data;
        dataPage[1] = 1;
        PutShort(dataPage, format.OffsetFreeSpace, (short)format.DataPageInitialFreeSpace);
        PutInt(dataPage, 4, TableDefPageNumber);           // page pointer to table definition
        PutInt(dataPage, 8, 0);                            // unknown
        PutShort(dataPage, format.OffsetNumRowsOnDataPage, 0);

        pageChannel.WritePage(dataPage, pageNumber);
        _ownedPages.AddPageNumber(pageNumber);
        _freeSpacePages.AddPageNumber(pageNumber);
        _addRowBuffer = dataPage;
        _addRowPageNumber = pageNumber;
        return pageNumber;
    }

    internal static bool RowFitsOnDataPage(int rowLength, byte[] dataPage, JetFormat format)
    {
        int rowSpaceUsage = GetRowSpaceUsage(rowLength, format);
        short freeSpaceInPage = ReadShort(dataPage, format.OffsetFreeSpace);
        int rowsOnPage = GetRowsOnDataPage(dataPage, format);
        return rowSpaceUsage <= freeSpaceInPage && rowsOnPage < format.MaxNumRowsOnDataPage;
    }

    internal static int GetRowSpaceUsage(int rowSize, JetFormat format)
        => rowSize + format.SizeRowLocation;

    /// <summary>
    /// Appends a row to the given data page, updating free space and row count.
    /// Returns the row number and the location at which the row bytes must be written.
    /// </summary>
    internal static (int RowNumber, int RowLocation) AddDataPageRow(byte[] dataPage, int rowSize, JetFormat format, int rowFlags)
    {
        int rowSpaceUsage = GetRowSpaceUsage(rowSize, format);

        short freeSpaceInPage = ReadShort(dataPage, format.OffsetFreeSpace);
        PutShort(dataPage, format.OffsetFreeSpace, (short)(freeSpaceInPage - rowSpaceUsage));

        short rowCount = ReadShort(dataPage, format.OffsetNumRowsOnDataPage);
        PutShort(dataPage, format.OffsetNumRowsOnDataPage, (short)(rowCount + 1));

        int rowLocation = FindRowEnd(dataPage, rowCount, format) - rowSize;
        PutShort(dataPage, GetRowStartOffset(rowCount, format), (short)(rowLocation | rowFlags));

        return (rowCount, rowLocation);
    }

    /// <summary>
    /// Updates the table definition page after rows are modified.
    /// </summary>
    private void UpdateTableDefinition(int rowCountInc)
    {
        JetFormat format = Format;
        byte[] tdefPage = _tableDefPage;

        _rowCount += rowCountInc;
        PutInt(tdefPage, format.OffsetNumRows, _rowCount);
        PutInt(tdefPage, format.OffsetNextAutoNumber, _lastLongAutoNumber);

        // write any index changes
        foreach (IndexData indexData in _indexDatas)
        {
            // write the unique entry count for the index to the table definition page
            PutInt(tdefPage, indexData.UniqueEntryCountOffset, indexData.UniqueEntryCount);
            // write the entry pages for the index
            indexData.Update();
        }

        _database.PageChannel.WritePage(tdefPage, TableDefPageNumber);
    }

    private static void PutShort(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static void PutShort(byte[] buffer, ref int pos, short value)
    {
        PutShort(buffer, pos, value);
        pos += 2;
    }

    private static void PutInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    /// <summary>
    /// Writes the Jet 3 (Access 97) variable-length column trailer, which uses a
    /// jump-table layout (mirror of the reader in <see cref="ReadJumpTableVarColOffsets"/>).
    /// </summary>
    private void WriteJet3VarTrailer(byte[] buffer, ref int pos, short[] varColumnOffsets, int eod, NullMask nullMask, int minRowSize)
    {
        int numVarCols = varColumnOffsets.Length;
        int nullMaskSize = nullMask.ByteSize;
        int varDataEnd = eod;

        // the number of jump bytes depends on the total row length; solve iteratively
        int numJumps = 0;
        while (true)
        {
            int trailerLen = (numVarCols + 1) + numJumps + 1 + nullMaskSize;
            int rowLen = varDataEnd + trailerLen;
            int computed = (rowLen - 1) / MaxByte;

            // pad the var data region so the full row reaches minRowSize
            int paddedEnd = varDataEnd;
            if (minRowSize > 0 && varDataEnd + trailerLen < minRowSize)
            {
                paddedEnd = minRowSize - trailerLen;
            }

            if (computed == numJumps && paddedEnd == varDataEnd)
            {
                break;
            }
            numJumps = computed;
            varDataEnd = paddedEnd;
        }

        // apply any padding to the var data region
        for (int i = pos; i < varDataEnd; i++)
        {
            buffer[i] = 0;
        }
        pos = varDataEnd;

        int rowEnd = varDataEnd + (numVarCols + 1) + numJumps + 1 + nullMaskSize - 1; // inclusive last byte
        int colOffset = rowEnd - nullMaskSize - numJumps - 1;

        // the reader drops a trailing "dummy" jump byte when the offsets region is too small
        int effectiveJumps = numJumps;
        if ((colOffset - numVarCols) / MaxByte < numJumps)
        {
            effectiveJumps = numJumps - 1;
        }

        // offsets: numVarCols + 1 values (the last is the end-of-data offset)
        int[] offsets = new int[numVarCols + 1];
        for (int i = 0; i < numVarCols; i++)
        {
            offsets[i] = varColumnOffsets[i];
        }
        offsets[numVarCols] = varDataEnd;

        // jump bytes: byte k holds the first offset index that is >= (k+1) * 256
        for (int k = 0; k < effectiveJumps; k++)
        {
            int target = (k + 1) * MaxByte;
            int first = numVarCols + 1;
            for (int i = 0; i < numVarCols + 1; i++)
            {
                if (offsets[i] >= target)
                {
                    first = i;
                    break;
                }
            }
            buffer[rowEnd - nullMaskSize - k - 1] = (byte)first;
        }

        // offset bytes, stored in reverse order
        for (int i = 0; i < numVarCols + 1; i++)
        {
            buffer[colOffset - i] = (byte)(offsets[i] % MaxByte);
        }

        // number of var length columns (1 byte)
        buffer[rowEnd - nullMaskSize] = (byte)numVarCols;

        pos = rowEnd + 1 - nullMaskSize;
    }

    private static short ReadShort(byte[] buffer, int offset)
        => (short)(buffer[offset] | (buffer[offset + 1] << 8));

    /// <summary>
    /// Returns a single byte array which contains the entire table definition
    /// (which may span multiple database pages).
    /// </summary>
    private byte[] LoadCompleteTableDefinitionBuffer(byte[] tableBuffer)
    {
        JetFormat format = Format;
        int nextPage = ByteUtil.GetIntLittleEndian(tableBuffer, format.OffsetNextTableDefPage);
        while (nextPage != 0)
        {
            byte[] nextPageBuffer = new byte[format.PageSize];
            _database.PageChannel.ReadPage(nextPageBuffer, nextPage);
            nextPage = ByteUtil.GetIntLittleEndian(nextPageBuffer, format.OffsetNextTableDefPage);

            byte[] expanded = new byte[tableBuffer.Length + format.PageSize - 8];
            Array.Copy(tableBuffer, expanded, tableBuffer.Length);
            Array.Copy(nextPageBuffer, 8, expanded, tableBuffer.Length, format.PageSize - 8);
            tableBuffer = expanded;
        }
        return tableBuffer;
    }

    private void ReadColumnDefinitions(byte[] tableBuffer, short columnCount)
    {
        JetFormat format = Format;
        int colOffset = format.OffsetIndexDefBlock + IndexCount * format.SizeIndexDefinition;

        // read column names
        int namePos = colOffset + columnCount * format.SizeColumnHeader;
        var colNames = new List<string>(columnCount);
        for (int i = 0; i < columnCount; i++)
        {
            colNames.Add(ReadName(tableBuffer, ref namePos));
        }
        // the index definitions (column blocks, logical info, names) follow the column names
        _indexBlockStart = namePos;

        int displayIndex = 0;
        for (int i = 0; i < columnCount; i++)
        {
            int offset = colOffset + i * format.SizeColumnHeader;
            byte colType = tableBuffer[offset + format.OffsetColumnType];
            var type = DataTypeInfo.FromByte(colType);
            var column = new Column(this, tableBuffer, offset, colNames[i], type, displayIndex++);
            _columns.Add(column);
            if (column.VariableLength)
            {
                _varColumns.Add(column);
            }
        }

        _columns.Sort((a, b) => a.ColumnNumber.CompareTo(b.ColumnNumber));
        for (int i = 0; i < _columns.Count; i++)
        {
            _columns[i].ColumnIndex = i;
            _columnIndexes[_columns[i].Name] = i;
        }

        // variable length columns are written in var-len-table-index order
        _varColumns.Sort((a, b) => a.VarLenTableIndex.CompareTo(b.VarLenTableIndex));
    }

    private string ReadName(byte[] buffer, ref int pos)
    {
        int nameLength = (int)ReadUnsignedVarInt(buffer, pos, Format.SizeNameLength);
        pos += Format.SizeNameLength;
        string name;
        if (Format.Charset is Encoding charset)
        {
            name = charset.GetString(buffer, pos, nameLength);
        }
        else
        {
            name = _database.TextEncoding.GetString(buffer, pos, nameLength);
        }
        pos += nameLength;
        return name;
    }

    /// <summary>
    /// Reads the index definitions (physical column blocks, logical index info and
    /// index names) from the table-definition buffer (port of Jackcess
    /// <c>TableImpl.readIndexDefinitions</c>).
    /// </summary>
    private void ReadIndexDefinitions()
    {
        JetFormat format = Format;
        int pos = _indexBlockStart;
        for (int i = 0; i < IndexCount; i++)
        {
            IndexData idxData = IndexData.Create(this, _tableDef, i, format);
            idxData.Read(_tableDef, _pageBuffers, ref pos);
            _indexDatas.Add(idxData);
        }
        for (int i = 0; i < LogicalIndexCount; i++)
        {
            _indexes.Add(new IndexImpl(_tableDef, _indexDatas, format, ref pos));
        }
        for (int i = 0; i < LogicalIndexCount; i++)
        {
            _indexes[i].Name = ReadName(_tableDef, ref pos);
        }
        _indexes.Sort((a, b) => a.IndexNumber.CompareTo(b.IndexNumber));
    }

    private static uint ReadUnsignedVarInt(byte[] buffer, int offset, int numBytes)
        => ByteUtil.GetUnsignedVarInt(buffer, offset, numBytes);

    // ---------------------------------------------------------------------
    // Row location helpers
    // ---------------------------------------------------------------------

    internal static bool IsDeletedRow(short rowStart) => (rowStart & DeletedRowMask) != 0;

    internal static bool IsOverflowRow(short rowStart) => (rowStart & OverflowRowMask) != 0;

    internal static short CleanRowStart(short rowStart) => (short)(rowStart & OffsetMask);

    internal static short FindRowStart(byte[] buffer, int rowNum, JetFormat format)
        => CleanRowStart(ReadShort(buffer, GetRowStartOffset(rowNum, format)));

    internal static int GetRowStartOffset(int rowNum, JetFormat format)
        => format.OffsetRowStart + format.SizeRowLocation * rowNum;

    internal static short FindRowEnd(byte[] buffer, int rowNum, JetFormat format)
        => rowNum == 0
            ? (short)format.PageSize
            : CleanRowStart(ReadShort(buffer, GetRowEndOffset(rowNum, format)));

    internal static int GetRowEndOffset(int rowNum, JetFormat format)
        => format.OffsetRowStart + format.SizeRowLocation * (rowNum - 1);

    internal static int GetRowsOnDataPage(byte[] buffer, JetFormat format)
        => buffer[0] == PageTypes.Data ? ReadShort(buffer, format.OffsetNumRowsOnDataPage) : 0;

    /// <summary>
    /// Positions at the given row's header; determines deleted/overflow status.
    /// </summary>
    private (RowStatus status, byte[] page, int rowsOnPage) PositionAtRowHeader(
        int pageNumber, int rowNumber, byte[]? page = null)
    {
        if (pageNumber < 0 || !_ownedPages.ContainsPageNumber(pageNumber))
        {
            return (RowStatus.InvalidPage, null!, 0);
        }

        if (page == null)
        {
            page = new byte[Format.PageSize];
            _database.PageChannel.ReadPage(page, pageNumber);
        }
        int rowsOnPage = GetRowsOnDataPage(page, Format);

        if (rowNumber < 0 || rowNumber >= rowsOnPage)
        {
            return (RowStatus.InvalidRow, page, rowsOnPage);
        }

        short rowStart = ReadShort(page, GetRowStartOffset(rowNumber, Format));
        if (IsDeletedRow(rowStart))
        {
            return (RowStatus.Deleted, page, rowsOnPage);
        }
        if (IsOverflowRow(rowStart))
        {
            return (RowStatus.Overflow, page, rowsOnPage);
        }
        return (RowStatus.Normal, page, rowsOnPage);
    }

    private enum RowStatus
    {
        InvalidPage,
        InvalidRow,
        Deleted,
        Overflow,
        Normal,
    }

    /// <summary>
    /// Returns the page/rowStart/rowEnd for the given row, following overflow pointers as necessary.
    /// Returns null if the row is invalid or deleted.
    /// </summary>
    private (byte[] page, int pageNumber, int rowNumber, int rowStart, int rowEnd)? PositionAtRowData(
        int pageNumber, int rowNumber, byte[]? page = null)
    {
        var (status, headerPage, _) = PositionAtRowHeader(pageNumber, rowNumber, page);
        if (status == RowStatus.InvalidPage || status == RowStatus.InvalidRow || status == RowStatus.Deleted)
        {
            return null;
        }

        JetFormat format = Format;
        page = headerPage;
        HashSet<(int Page, int Row)>? visitedRows = null;
        while (true)
        {
            if (pageNumber < 0 || !_ownedPages.ContainsPageNumber(pageNumber)
                || page[0] != PageTypes.Data)
            {
                throw new DatabaseException($"Overflow pointer targets a non-data page {pageNumber}.");
            }
            int rowsOnPage = GetRowsOnDataPage(page, format);
            if (rowNumber < 0 || rowNumber >= rowsOnPage)
            {
                throw new DatabaseException($"Overflow pointer targets invalid row {pageNumber}:{rowNumber}.");
            }
            short rowStartShort = ReadShort(page, GetRowStartOffset(rowNumber, format));
            short rowEnd = FindRowEnd(page, rowNumber, format);

            bool overflowRow = IsOverflowRow(rowStartShort);
            rowStartShort = CleanRowStart(rowStartShort);

            if (overflowRow)
            {
                visitedRows ??= new HashSet<(int Page, int Row)>();
                if (!visitedRows.Add((pageNumber, rowNumber)))
                {
                    throw new DatabaseException($"Cyclic overflow pointer detected at page {pageNumber}, row {rowNumber}.");
                }
                if (rowEnd - rowStartShort < 4)
                {
                    throw new DatabaseException("invalid overflow row info");
                }
                int overflowRowNum = page[rowStartShort];
                int overflowPageNum = ByteUtil.Get3ByteInt(page, rowStartShort + 1);
                if (overflowPageNum == pageNumber && overflowRowNum == rowNumber)
                {
                    throw new DatabaseException("Overflow row points to itself.");
                }
                page = new byte[format.PageSize];
                _database.PageChannel.ReadPage(page, overflowPageNum);
                pageNumber = overflowPageNum;
                rowNumber = overflowRowNum;
            }
            else
            {
                return (page, pageNumber, rowNumber, rowStartShort, rowEnd);
            }
        }
    }

    /// <summary>
    /// Enumerates all non-deleted rows in the table in physical (data page) order.
    /// </summary>
    public IEnumerable<Row> Rows()
    {
        foreach (RowLocation location in RowLocations())
        {
            yield return location.Row;
        }
    }

    /// <summary>a row together with its physical location (page and row number)</summary>
    public readonly record struct RowLocation(int PageNumber, int RowNumber, Row Row);

    /// <summary>
    /// Enumerates every row of the table together with its physical location
    /// (the page/row numbers used by <see cref="UpdateRow"/> and <see cref="DeleteRow"/>).
    /// </summary>
    public IEnumerable<RowLocation> RowLocations()
    {
        var ownedPages = _ownedPages.Cursor();
        while (true)
        {
            int pageNumber = ownedPages.GetNextPage();
            if (pageNumber == PageChannelImpl.InvalidPageNumber)
            {
                yield break;
            }

            byte[] page = new byte[Format.PageSize];
            _database.PageChannel.ReadPage(page, pageNumber);
            int rowsOnPage = GetRowsOnDataPage(page, Format);
            for (int rowNumber = 0; rowNumber < rowsOnPage; rowNumber++)
            {
                var positioned = PositionAtRowData(pageNumber, rowNumber, page);
                if (positioned == null)
                {
                    continue;
                }
                yield return new RowLocation(pageNumber, rowNumber,
                    ReadRow(positioned.Value.page, positioned.Value.rowStart, positioned.Value.rowEnd));
            }
        }
    }

    private void EnsureLongValuePageReferences()
    {
        if (_longValuePageReferencesInitialized)
        {
            return;
        }

        var references = new Dictionary<int, int>();
        var ownedPages = _ownedPages.Cursor();
        while (true)
        {
            int pageNumber = ownedPages.GetNextPage();
            if (pageNumber == PageChannelImpl.InvalidPageNumber)
            {
                break;
            }

            byte[] page = new byte[Format.PageSize];
            _database.PageChannel.ReadPage(page, pageNumber);
            int rowsOnPage = GetRowsOnDataPage(page, Format);
            for (int rowNumber = 0; rowNumber < rowsOnPage; rowNumber++)
            {
                    var positioned = PositionAtRowData(pageNumber, rowNumber, page);
                if (positioned != null)
                {
                    var rowPages = new HashSet<int>();
                    CollectRowLongValuePages(positioned.Value.page, positioned.Value.rowStart,
                        positioned.Value.rowEnd, rowPages);
                    foreach (int rowPage in rowPages)
                    {
                        references.TryGetValue(rowPage, out int count);
                        references[rowPage] = count + 1;
                    }
                }
            }
        }

        foreach ((int pageNumber, int count) in references)
        {
            _longValuePages.Add(pageNumber);
            _longValuePageReferences[pageNumber] = count;
        }
        _longValuePageReferencesInitialized = true;
    }

    public Row? GetRow(int pageNumber, int rowNumber)
    {
        var positioned = PositionAtRowData(pageNumber, rowNumber);
        if (positioned == null)
        {
            return null;
        }
        return ReadRow(positioned.Value.page, positioned.Value.rowStart, positioned.Value.rowEnd);
    }

    /// <summary>
    /// Enumerates the rows of this table in the order of the given index
    /// (port of the Jackcess <c>IndexCursor</c>).
    /// </summary>
    public IEnumerable<Row> RowsInIndexOrder(string indexName)
        => RowsInIndexRange(indexName, null, true, null, true);

    /// <summary>
    /// Enumerates the rows of this table whose index entry falls within the given range,
    /// in index order (port of the Jackcess range <c>IndexCursor</c>).
    /// <paramref name="startRow"/> and <paramref name="endRow"/> are sparse arrays of
    /// values indexed by column position (only the indexed columns need values).
    /// </summary>
    public IEnumerable<Row> RowsInIndexRange(string indexName, object?[]? startRow, bool startInclusive, object?[]? endRow, bool endInclusive)
    {
        IndexImpl? index = _indexes.FirstOrDefault(i => i.Name is not null && i.Name.Equals(indexName, StringComparison.OrdinalIgnoreCase));
        if (index == null)
        {
            throw new ArgumentException($"Index '{indexName}' does not exist on table '{Name}'.");
        }

        IndexData.EntryCursor cursor = IndexData.EntryCursor.Create(index!.IndexData, startRow, startInclusive, endRow, endInclusive);
        IndexData.Entry endEntry = cursor.LastEntry;
        while (true)
        {
            IndexData.Entry entry = cursor.GetNextEntry();
            if (entry.Equals(endEntry))
            {
                yield break;
            }
            Row? row = GetRow(entry.RowId.PageNumber, entry.RowId.RowNumber);
            if (row != null)
            {
                yield return row;
            }
        }
    }

    /// <summary>
    /// Reads all column values from the given row data.
    /// </summary>
    private Row ReadRow(byte[] page, int rowStart, int rowEnd)
    {
        JetFormat format = Format;

        if (page.Length < format.PageSize || rowStart < 0 || rowEnd < rowStart || rowEnd > format.PageSize)
        {
            throw new DatabaseException("Invalid row boundaries.");
        }

        NullMask nullMask = ReadRowNullMask(page, rowStart, rowEnd);

        var values = new object?[_columns.Count];
        for (int i = 0; i < _columns.Count; i++)
        {
            Column column = _columns[i];
            bool isNull = nullMask.IsNull(column);
            if (column.StoreInNullMask)
            {
                // Boolean values are stored in the null mask
                values[i] = column.ReadFromNullMask(isNull);
                continue;
            }
            if (isNull)
            {
                values[i] = null;
                continue;
            }

            int colDataPos;
            int colDataLen;
            if (!column.VariableLength)
            {
                int dataStart = rowStart + format.OffsetColumnFixedDataRowOffset;
                colDataPos = dataStart + column.FixedDataOffset;
                colDataLen = column.FixedDataSize;
            }
            else if (format.SizeRowVarColOffset == 2)
            {
                // simple var length value
                (colDataPos, int colDataEnd) = GetVariableColumnBounds(page, rowStart, rowEnd, nullMask, column);
                colDataLen = colDataEnd - colDataPos;
            }
            else
            {
                // jump-table based var length values
                short[] varColumnOffsets = ReadJumpTableVarColOffsets(page, rowStart, rowEnd, nullMask);
                int varDataStart = varColumnOffsets[column.VarLenTableIndex];
                int varDataEnd = varColumnOffsets[column.VarLenTableIndex + 1];
                if (varDataStart < 0 || varDataEnd < varDataStart || rowStart + varDataEnd > rowEnd)
                {
                    throw new DatabaseException("Invalid Jet 3 variable-length column offsets.");
                }
                colDataPos = rowStart + varDataStart;
                colDataLen = varDataEnd - varDataStart;
            }

            ValidateColumnBounds(colDataPos, colDataLen, rowStart, rowEnd);
            object? rawValue = column.Read(page, colDataPos, colDataLen);
            values[i] = column.Type == DataType.ComplexType
                ? ResolveComplexValue(column, rawValue)
                : rawValue;
        }

        return new Row(this, values);
    }

    private sealed record ComplexColumnInfo(int ComplexTypeObjectId, int FlatTableId, Table FlatTable);

    private sealed record ComplexWrite(ComplexColumnInfo Info, Column Column, int Key,
        object?[] Children, int? PreviousKey);

    private void EnsureComplexColumns()
    {
        if (_complexColumnsLoaded)
        {
            return;
        }
        _complexColumnsLoaded = true;

        Table? metadata = _database.GetSystemTable("MSysComplexColumns");
        if (metadata == null)
        {
            return;
        }

        foreach (Row row in metadata.Rows())
        {
            if (!row.TryGetValue("ConceptualTableID", out object? tableId)
                || !TryInt(tableId, out int conceptualTableId)
                || conceptualTableId != TableDefPageNumber
                || !row.TryGetValue("ColumnName", out object? columnName)
                || columnName is not string name)
            {
                continue;
            }
            Column? column = _columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (column == null
                || !row.TryGetValue("FlatTableID", out object? flatValue)
                || !TryInt(flatValue, out int flatTableId)
                || !row.TryGetValue("ComplexTypeObjectID", out object? typeValue)
                || !TryInt(typeValue, out int typeId))
            {
                continue;
            }
            Table? flatTable = _database.GetTableByPageNumber(flatTableId);
            if (flatTable != null)
            {
                _complexColumns[column.ColumnIndex] = new ComplexColumnInfo(typeId, flatTableId, flatTable);
            }
        }
    }

    private object? ResolveComplexValue(Column column, object? rawValue)
    {
        if (rawValue is null or DBNull)
        {
            return null;
        }
        EnsureComplexColumns();
        if (!_complexColumns.TryGetValue(column.ColumnIndex, out ComplexColumnInfo? info)
            || !TryInt(rawValue, out int complexKey))
        {
            return rawValue;
        }

        string foreignKeyName = "_" + column.Name;
        Column? foreignColumn = info.FlatTable.Columns.FirstOrDefault(c =>
            c.Name.Equals(foreignKeyName, StringComparison.OrdinalIgnoreCase));
        if (foreignColumn == null)
        {
            foreignColumn = info.FlatTable.Columns.LastOrDefault(c => c.Type is DataType.Long or DataType.Int);
        }
        if (foreignColumn == null)
        {
            return Array.Empty<AccessSingleValue>();
        }

        var children = info.FlatTable.Rows()
            .Where(row => TryInt(row[foreignColumn.ColumnIndex], out int key) && key == complexKey)
            .ToList();
        if (info.ComplexTypeObjectId == 39
            || info.FlatTable.Columns.Any(c => c.Name.Equals("FileData", StringComparison.OrdinalIgnoreCase)))
        {
            return children.Select(row => new AccessAttachment(
                GetBytes(row, "FileData"),
                GetNullableInt(row, "FileFlags"),
                GetNullableString(row, "FileName"),
                GetNullableDate(row, "FileTimeStamp"),
                GetNullableString(row, "FileType"),
                GetNullableString(row, "FileURL"))).ToArray();
        }

        if (info.FlatTable.Columns.Any(c => c.Name.Equals("Modified", StringComparison.OrdinalIgnoreCase))
            || info.FlatTable.Columns.Any(c => c.Name.Equals("Version", StringComparison.OrdinalIgnoreCase)))
        {
            return children.Select(row => new AccessVersion(
                row.TryGetValue("Value", out object? value) ? value : null,
                GetNullableDate(row, "Modified"))).ToArray();
        }

        return children.Select(row => new AccessSingleValue(
            row.TryGetValue("Value", out object? value) ? value : null)).ToArray();
    }

    /// <summary>
    /// Replaces the scalar pointer stored in a complex column with a new
    /// complex-object key and builds the corresponding flat-table rows.  The
    /// Access file format stores the parent row and the child values separately;
    /// serializing the CLR array directly would create an unreadable database.
    /// </summary>
    private List<ComplexWrite> PrepareComplexValues(object?[] values,
        byte[]? oldPage, int? oldRowStart, int? oldRowEnd)
    {
        EnsureComplexColumns();
        var writes = new List<ComplexWrite>();
        foreach (Column column in _columns.Where(c => c.Type == DataType.ComplexType))
        {
            object? value = values[column.ColumnIndex];
            if (value is null or DBNull)
            {
                continue;
            }
            if (!_complexColumns.TryGetValue(column.ColumnIndex, out ComplexColumnInfo? info))
            {
                throw new DatabaseException(
                    $"Complex column '{column.Name}' has no MSysComplexColumns metadata.");
            }

            int? previousKey = null;
            if (oldPage != null && oldRowStart.HasValue && oldRowEnd.HasValue
                && TryInt(ReadColumnValue(oldPage, oldRowStart.Value, oldRowEnd.Value, column), out int oldKey))
            {
                previousKey = oldKey;
            }
            int key = previousKey ?? NextComplexKey(info);
            object?[] children = BuildComplexChildren(info, column, key, value);
            values[column.ColumnIndex] = key;
            writes.Add(new ComplexWrite(info, column, key, children, previousKey));
        }
        return writes;
    }

    private int NextComplexKey(ComplexColumnInfo info)
    {
        string foreignKeyName = "_" + info.FlatTable.Name[(info.FlatTable.Name.LastIndexOf('_') + 1)..];
        Column? foreignColumn = info.FlatTable.Columns.FirstOrDefault(c =>
            c.Name.Equals(foreignKeyName, StringComparison.OrdinalIgnoreCase));
        foreignColumn ??= info.FlatTable.Columns.LastOrDefault(c => c.Type is DataType.Long or DataType.Int);
        int maximum = 0;
        if (foreignColumn != null)
        {
            foreach (Row row in info.FlatTable.Rows())
            {
                if (TryInt(row[foreignColumn.ColumnIndex], out int key))
                {
                    maximum = Math.Max(maximum, key);
                }
            }
        }
        return Math.Max(maximum, _lastComplexTypeAutoNumber) + 1;
    }

    private static object?[] BuildComplexChildren(ComplexColumnInfo info, Column column, int key, object value)
    {
        string foreignKeyName = "_" + column.Name;
        Column? foreignColumn = info.FlatTable.Columns.FirstOrDefault(c =>
            c.Name.Equals(foreignKeyName, StringComparison.OrdinalIgnoreCase));
        foreignColumn ??= info.FlatTable.Columns.LastOrDefault(c => c.Type is DataType.Long or DataType.Int);
        if (foreignColumn == null)
        {
            throw new DatabaseException(
                $"Flat table '{info.FlatTable.Name}' has no complex-value foreign-key column.");
        }

        IEnumerable<object> source = value switch
        {
            AccessSingleValue[] values => values,
            AccessAttachment[] values => values,
            AccessVersion[] values => values,
            _ => throw new DatabaseException(
                $"Complex column '{column.Name}' accepts AccessSingleValue[], AccessAttachment[] or AccessVersion[]."),
        };
        var rows = new List<object?[]>();
        foreach (object item in source)
        {
            var row = new object?[info.FlatTable.Columns.Count];
            foreach (Column childColumn in info.FlatTable.Columns)
            {
                if (childColumn.ColumnIndex == foreignColumn.ColumnIndex)
                {
                    row[childColumn.ColumnIndex] = key;
                    continue;
                }
                if (childColumn.AutoNumber)
                {
                    continue;
                }
                row[childColumn.ColumnIndex] = childColumn.Name.ToLowerInvariant() switch
                {
                    "value" => item switch
                    {
                        AccessSingleValue single => single.Value,
                        AccessVersion version => version.Value,
                        _ => null,
                    },
                    "modified" => item is AccessVersion version ? version.Modified : null,
                    "filedata" => item is AccessAttachment attachment ? attachment.FileData : null,
                    "fileflags" => item is AccessAttachment attachment ? attachment.FileFlags : null,
                    "filename" => item is AccessAttachment attachment ? attachment.FileName : null,
                    "filetimestamp" => item is AccessAttachment attachment ? attachment.FileTimeStamp : null,
                    "filetype" => item is AccessAttachment attachment ? attachment.FileType : null,
                    "fileurl" => item is AccessAttachment attachment ? attachment.FileURL : null,
                    _ => null,
                };
            }
            rows.Add(row);
        }
        return rows.SelectMany(row => row).ToArray();
    }

    private void WriteComplexChildren(IReadOnlyList<ComplexWrite> writes, bool replaceExisting)
    {
        foreach (ComplexWrite write in writes)
        {
            Column? foreignColumn = write.Info.FlatTable.Columns.FirstOrDefault(c =>
                c.Name.Equals("_" + write.Column.Name, StringComparison.OrdinalIgnoreCase));
            foreignColumn ??= write.Info.FlatTable.Columns.LastOrDefault(c => c.Type is DataType.Long or DataType.Int);
            if (foreignColumn == null)
            {
                throw new DatabaseException(
                    $"Flat table '{write.Info.FlatTable.Name}' has no complex-value foreign-key column.");
            }

            if (replaceExisting && write.PreviousKey.HasValue)
            {
                var locations = write.Info.FlatTable.RowLocations()
                    .Where(location => TryInt(location.Row[foreignColumn.ColumnIndex], out int key)
                        && key == write.PreviousKey.Value)
                    .ToList();
                foreach (RowLocation location in locations)
                {
                    write.Info.FlatTable.DeleteRow(location.PageNumber, location.RowNumber);
                }
            }

            int width = write.Info.FlatTable.Columns.Count;
            for (int offset = 0; offset < write.Children.Length; offset += width)
            {
                write.Info.FlatTable.AddRow(write.Children.Skip(offset).Take(width).ToArray());
            }
        }
    }

    private static void DeleteComplexChildren(
        IReadOnlyList<(ComplexColumnInfo Info, Column Column, int Key)> deletes)
    {
        foreach ((ComplexColumnInfo info, Column column, int key) in deletes)
        {
            Column? foreignColumn = info.FlatTable.Columns.FirstOrDefault(c =>
                c.Name.Equals("_" + column.Name, StringComparison.OrdinalIgnoreCase));
            foreignColumn ??= info.FlatTable.Columns.LastOrDefault(c => c.Type is DataType.Long or DataType.Int);
            if (foreignColumn == null)
            {
                continue;
            }
            var locations = info.FlatTable.RowLocations()
                .Where(location => TryInt(location.Row[foreignColumn.ColumnIndex], out int childKey)
                    && childKey == key)
                .ToList();
            foreach (RowLocation location in locations)
            {
                info.FlatTable.DeleteRow(location.PageNumber, location.RowNumber);
            }
        }
    }

    private static bool TryInt(object? value, out int result)
    {
        try
        {
            result = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            result = 0;
            return false;
        }
    }

    private static string? GetNullableString(Row row, string name)
        => row.TryGetValue(name, out object? value) && value is not null ? value.ToString() : null;

    private static int? GetNullableInt(Row row, string name)
        => row.TryGetValue(name, out object? value) && TryInt(value, out int result) ? result : null;

    private static DateTime? GetNullableDate(Row row, string name)
        => row.TryGetValue(name, out object? value) && value is DateTime date ? date : null;

    private static byte[]? GetBytes(Row row, string name)
        => row.TryGetValue(name, out object? value) && value is byte[] bytes ? bytes : null;

    private short[] ReadJumpTableVarColOffsets(byte[] page, int rowStart, int rowEnd, NullMask nullMask)
    {
        int nullMaskSize = nullMask.ByteSize;
        if (rowStart < 0 || rowEnd <= rowStart || rowEnd > page.Length
            || rowEnd - rowStart < nullMaskSize + 2)
        {
            throw new DatabaseException("Invalid Jet 3 variable-length row bounds.");
        }
        int rowEndExclusive = rowStart + (rowEnd - rowStart) - 1;
        int numVarCols = page[rowEndExclusive - nullMaskSize];
        if (numVarCols > MaxVarColumnCount)
        {
            throw new DatabaseException(
                $"Row declares {numVarCols} variable columns, table has {MaxVarColumnCount}.");
        }
        var varColOffsets = new short[numVarCols + 1];

        int rowLen = rowEnd - rowStart + 1;
        int numJumps = (rowLen - 1) / MaxByte;
        int colOffset = rowEndExclusive - nullMaskSize - numJumps - 1;
        if (colOffset - numVarCols < rowStart)
        {
            throw new DatabaseException("Invalid Jet 3 variable-length offset table.");
        }

        // If last jump is a dummy value, ignore it
        if ((colOffset - rowStart - numVarCols) / MaxByte < numJumps)
        {
            numJumps--;
        }

        int jumpsUsed = 0;
        for (int i = 0; i < numVarCols + 1; i++)
        {
            while (jumpsUsed < numJumps)
            {
                int jumpPosition = rowEndExclusive - nullMaskSize - jumpsUsed - 1;
                if (jumpPosition < rowStart)
                {
                    throw new DatabaseException("Invalid Jet 3 variable-length jump table.");
                }
                if (i != page[jumpPosition])
                {
                    break;
                }
                jumpsUsed++;
            }
            if (colOffset - i < rowStart || colOffset - i >= rowEnd)
            {
                throw new DatabaseException("Invalid Jet 3 variable-length offset table.");
            }
            varColOffsets[i] = (short)(page[colOffset - i] + jumpsUsed * MaxByte);
        }

        return varColOffsets;
    }

    private static void ValidateColumnBounds(int offset, int length, int rowStart, int rowEnd)
    {
        if (offset < rowStart || length < 0 || offset > rowEnd - length)
        {
            throw new DatabaseException("Column data falls outside the row bounds.");
        }
    }

    private NullMask ReadRowNullMask(byte[] page, int rowStart, int rowEnd)
    {
        JetFormat format = Format;
        if (rowStart < 0 || rowEnd <= rowStart || rowEnd > format.PageSize || rowEnd > page.Length
            || rowStart + format.SizeRowColumnCount > rowEnd)
        {
            throw new DatabaseException("Invalid row bounds.");
        }
        uint columnCount = ReadUnsignedVarInt(page, rowStart, format.SizeRowColumnCount);
        if (columnCount > (uint)MaxColumnCount)
        {
            throw new DatabaseException($"Row declares {columnCount} columns, table has {MaxColumnCount}.");
        }
        var nullMask = new NullMask((int)columnCount);
        int maskStart = rowEnd - nullMask.ByteSize;
        if (maskStart < rowStart + format.SizeRowColumnCount)
        {
            throw new DatabaseException("Row is too short for its null mask.");
        }
        nullMask.Read(page, maskStart);
        return nullMask;
    }
}
