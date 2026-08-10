using System.Text;

namespace UCanAccess.File;

/// <summary>
/// Describes a column for <see cref="Database.CreateTable"/>.
/// </summary>
public sealed class ColumnBuilder
{
    public ColumnBuilder(string name, DataType type)
    {
        Name = name;
        Type = type;
    }

    public string Name { get; }

    public DataType Type { get; }

    /// <summary>the raw stored column length (bytes for Jet 4 text)</summary>
    public int Length { get; private set; }

    public bool AutoNumber { get; private set; }

    /// <summary>whether a row must contain a value for this column</summary>
    public bool Required { get; private set; }

    public byte Precision { get; private set; }

    public byte Scale { get; private set; }

    public bool CompressedUnicode { get; private set; }

    public TextSortOrder? TextSortOrder { get; private set; }

    public ColumnBuilder WithLength(int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Column length cannot be negative.");
        }
        Length = length;
        return this;
    }

    public ColumnBuilder WithAutoNumber(bool autoNumber = true)
    {
        AutoNumber = autoNumber;
        return this;
    }

    public ColumnBuilder WithPrecision(int precision)
    {
        if (precision < 0 || precision > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(precision));
        }
        Precision = (byte)precision;
        return this;
    }

    public ColumnBuilder WithScale(int scale)
    {
        if (scale < 0 || scale > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }
        Scale = (byte)scale;
        return this;
    }

    public ColumnBuilder WithCompressedUnicode(bool compressed = true)
    {
        CompressedUnicode = compressed;
        return this;
    }

    public ColumnBuilder WithRequired(bool required = true)
    {
        Required = required;
        return this;
    }

    public ColumnBuilder WithTextSortOrder(TextSortOrder sortOrder)
    {
        TextSortOrder = sortOrder;
        return this;
    }

    internal bool IsVariableLength => Type is DataType.Text or DataType.Memo or DataType.Ole or DataType.Binary
        or DataType.Unknown0D or DataType.Unknown11 or DataType.UnsupportedVarLen;

    internal bool IsLongValue => Type is DataType.Memo or DataType.Ole or DataType.UnsupportedVarLen;

    internal bool StoreInNullMask => Type == DataType.Boolean;
}

/// <summary>
/// Describes an index for <see cref="Database.CreateTable"/>.
/// </summary>
public sealed class IndexBuilder
{
    public IndexBuilder(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public List<(string Column, bool Ascending)> Columns { get; } = new();

    public bool Unique { get; private set; }

    public bool PrimaryKey { get; private set; }

    public bool Required { get; private set; }

    public bool IgnoreNulls { get; private set; }

    public IndexBuilder WithColumns(params string[] names)
    {
        foreach (string n in names)
        {
            Columns.Add((n, true));
        }
        return this;
    }

    public IndexBuilder WithColumns(bool ascending, params string[] names)
    {
        foreach (string n in names)
        {
            Columns.Add((n, ascending));
        }
        return this;
    }

    public IndexBuilder WithUnique(bool unique = true)
    {
        Unique = unique;
        return this;
    }

    public IndexBuilder WithPrimaryKey()
    {
        PrimaryKey = true;
        Unique = true;
        return this;
    }

    public IndexBuilder WithRequired(bool required = true)
    {
        Required = required;
        return this;
    }

    public IndexBuilder WithIgnoreNulls(bool ignoreNulls = true)
    {
        IgnoreNulls = ignoreNulls;
        return this;
    }
}

/// <summary>
/// Writes a new table definition (table-definition pages, usage maps, indexes) and
/// registers it in the system catalog (port of Jackcess <c>TableCreator</c>).
/// </summary>
internal static class TableCreator
{
    private const int MagicTableNumber = 1625;
    private const int MagicIndexNumber = 1923;
    private const byte TypeUser = 0x4E;

    private const byte UpdatableFlagMask = 0x02;
    private const byte FixedLenFlagMask = 0x01;
    private const byte AutoNumberFlagMask = 0x04;
    private const byte AutoNumberGuidFlagMask = 0x40;
    private const byte CompressedUnicodeExtFlagMask = 0x01;

    private const byte MapTypeInline = 0x0;
    private const byte MapTypeReference = 0x1;

    private const byte PrimaryKeyIndexType = 1;
    private const int InvalidIndexNumber = -1;

    /// <summary>the portions of an existing table definition which can be reused</summary>
    internal sealed class ExistingTableDefinition
    {
        internal required IReadOnlyCollection<int> OwnedPages { get; init; }
        internal required IReadOnlyCollection<int> FreeSpacePages { get; init; }
        internal required IReadOnlyCollection<int> LongValuePages { get; init; }
        internal required IReadOnlyList<ExistingIndexDefinition?> Indexes { get; init; }
        internal int RowCount { get; init; }
        internal int NextAutoNumber { get; init; }
    }

    internal sealed class ExistingIndexDefinition
    {
        internal required int RootPageNumber { get; init; }
        internal required int UniqueEntryCount { get; init; }
        internal required IReadOnlyCollection<int> OwnedPages { get; init; }
    }

    /// <summary>
    /// Creates a new table in the given database and returns it.
    /// </summary>
    public static Table CreateTable(Database database, string name, List<ColumnBuilder> columns, List<IndexBuilder> indexes)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Table name is required.");
        }
        if (columns.Count == 0)
        {
            throw new ArgumentException("A table must have at least one column.");
        }
        JetFormat format = database.Format;
        ValidateDefinition(name, columns, indexes, format, database.TextEncoding);
        if (database.GetTable(name) != null)
        {
            throw new InvalidOperationException($"Table '{name}' already exists.");
        }

        PageChannel pageChannel = database.PageChannel;

        pageChannel.StartWrite();
        try
        {
            // assign column numbers / var-len indexes / fixed offsets
            var colState = new ColumnState[columns.Count];
            int varOffset = 0;
            int longVarOffset = columns.Count(c => c.IsVariableLength && !c.IsLongValue);
            int fixedOffset = 0;
            for (int i = 0; i < columns.Count; i++)
            {
                ColumnBuilder col = columns[i];
                if (col.IsVariableLength)
                {
                    colState[i] = new ColumnState
                    {
                        VarLenTableIndex = col.IsLongValue ? longVarOffset++ : varOffset++,
                        FixedDataOffset = 0,
                    };
                }
                else
                {
                    // Jackcess writes the running count of variable-length columns for
                    // fixed columns too; MS Access relies on it when reading the definition
                    colState[i] = new ColumnState
                    {
                        VarLenTableIndex = varOffset,
                        FixedDataOffset = col.StoreInNullMask ? 0 : fixedOffset,
                    };
                    fixedOffset += GetFixedDataSize(col, format);
                }
            }

            int tdefPageNumber = pageChannel.AllocateNewPage();
            int umapPageNumber = pageChannel.AllocateNewPage();

            // index data state (one per logical index for simplicity)
            var indexStates = indexes.Select(_ => new IndexState()).ToList();
            var lvalStates = columns.Where(c => c.IsLongValue).Select(_ => new ColumnState()).ToList();

            // usage map rows: 0 = owned, 1 = free, then one per index, then two per long-value column
            CreateUsageMapDefinitionPage(database, umapPageNumber, columns, indexes, indexStates, lvalStates);

            // compute the table definition size
            int idxDataLen = indexes.Count * (format.SizeIndexDefinition + format.SizeIndexColumnBlock)
                + indexes.Count * format.SizeIndexInfoBlock;
            int colUmapLen = columns.Count(c => c.IsLongValue) * 10;
            int totalTableDefSize = format.SizeTdefHeader
                + format.SizeColumnDefBlock * columns.Count
                + idxDataLen
                + colUmapLen
                + format.SizeTdefTrailer;
            foreach (ColumnBuilder col in columns)
            {
                totalTableDefSize += CalculateNameLength(col.Name, format);
            }
            foreach (IndexBuilder idx in indexes)
            {
                totalTableDefSize += CalculateNameLength(idx.Name, format);
            }

            byte[] buffer = new byte[Math.Max(totalTableDefSize, format.PageSize)];

            WriteTableDefinitionHeader(buffer, database, tdefPageNumber, umapPageNumber, columns, indexes);
            int pos = format.SizeTdefHeader;

            // index row counts
            pos += indexes.Count * format.SizeIndexDefinition;

            // column definitions + names
            WriteColumnDefinitions(buffer, ref pos, database, columns, colState, format);

            if (indexes.Count > 0)
            {
                WriteIndexDefinitions(buffer, ref pos, database, columns, indexes, indexStates, tdefPageNumber, format);
                WriteIndexNames(buffer, ref pos, indexes, database, format);
            }

            // column usage map references (for long value columns)
            WriteColumnUsageMapDefinitions(buffer, ref pos, columns, indexes.Count, lvalStates, format);

            // end of tabledef
            buffer[pos++] = 0xFF;
            buffer[pos++] = 0xFF;

            // write the table definition to the database
            WriteTableDefinitionBuffer(buffer, totalTableDefSize, tdefPageNumber, format, pageChannel);

            // register in the system catalog
            // register in the system catalog (the object id of a table is its
            // table-definition page number, which is how the catalog is resolved)
            byte[]? propertyBytes = PropertyMapCodec.WriteRequired(
                columns.Where(column => column.Required).Select(column => column.Name),
                database.TextEncoding,
                database.Format.Charset == null);
            database.AddToSystemCatalog(name, tdefPageNumber, Database.TypeTable, tdefPageNumber,
                propertyBytes);

            // make the table visible to the current open database instance
            database.RegisterTableMeta(new TableMetaData(name, tdefPageNumber, 0, Database.TypeTable, null, null, null));

            return LoadTable(database, name, tdefPageNumber);
        }
        finally
        {
            pageChannel.FinishWrite();
        }
    }

    private static Table LoadTable(Database database, string name, int tdefPageNumber)
    {
        byte[] buffer = new byte[database.Format.PageSize];
        database.PageChannel.ReadPage(buffer, tdefPageNumber);
        return new Table(database, buffer, tdefPageNumber, name, 0);
    }

    internal static int GetFixedDataSize(ColumnBuilder col, JetFormat format) => col.Type switch
    {
        DataType.Byte => 1,
        DataType.Int => 2,
        DataType.Long => 4,
        DataType.BigInt => 8,
        DataType.Float => 4,
        DataType.Double => 8,
        DataType.Money => 8,
        DataType.ShortDateTime => 8,
        DataType.Guid => 16,
        DataType.Numeric => format.NumericFixedSize,
        DataType.Boolean => 1,
        _ => col.Length,
    };

    private static void ValidateDefinition(string tableName, List<ColumnBuilder> columns,
        List<IndexBuilder> indexes, JetFormat format, Encoding encoding)
    {
        if (columns.Count > format.MaxColumnsPerTable)
        {
            throw new ArgumentException(
                $"A table may contain at most {format.MaxColumnsPerTable} columns.");
        }
        if (indexes.Count > format.MaxIndexesPerTable)
        {
            throw new ArgumentException(
                $"A table may contain at most {format.MaxIndexesPerTable} indexes.");
        }
        ValidateName(tableName, format.MaxTableNameLength, format, encoding, "table");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ColumnBuilder column in columns)
        {
            if (string.IsNullOrWhiteSpace(column.Name) || !names.Add(column.Name))
            {
                throw new ArgumentException($"Column name '{column.Name}' is empty or duplicated.");
            }
            ValidateName(column.Name, format.MaxColumnNameLength, format, encoding, "column");
            if (column.Type is DataType.ExtDateTime or DataType.ComplexType
                or DataType.Unknown0D or DataType.Unknown11 or DataType.UnsupportedFixedLen
                or DataType.UnsupportedVarLen)
            {
                throw new NotSupportedException($"Creating columns of type {column.Type} is not supported.");
            }
            if ((column.Type is DataType.Text or DataType.Binary) && column.Length <= 0)
            {
                throw new ArgumentException($"Column '{column.Name}' requires a positive length.");
            }
            if (column.Type is DataType.Text or DataType.Binary && column.Length > short.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(column.Length),
                    $"Column '{column.Name}' is limited to {short.MaxValue} stored bytes.");
            }
            if (column.Type == DataType.Numeric
                && (column.Precision is 0 or > 28 || column.Scale > column.Precision || column.Scale > 28))
            {
                throw new ArgumentException($"Column '{column.Name}' has an invalid numeric precision/scale.");
            }
            if (column.AutoNumber && column.Type is not (DataType.Long or DataType.Guid))
            {
                throw new NotSupportedException(
                    $"AutoNumber is supported only for LONG and GUID columns, not {column.Type}.");
            }
        }

        var indexNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool primaryKeySeen = false;
        foreach (IndexBuilder index in indexes)
        {
            if (string.IsNullOrWhiteSpace(index.Name) || !indexNames.Add(index.Name))
            {
                throw new ArgumentException($"Index name '{index.Name}' is empty or duplicated.");
            }
            ValidateName(index.Name, format.MaxIndexNameLength, format, encoding, "index");
            if (index.Columns.Count == 0)
            {
                throw new ArgumentException($"Index '{index.Name}' must contain at least one column.");
            }
            if (index.PrimaryKey && primaryKeySeen)
            {
                throw new ArgumentException("A table may have only one primary key.");
            }
            primaryKeySeen |= index.PrimaryKey;
            foreach ((string column, _) in index.Columns)
            {
                if (!names.Contains(column))
                {
                    throw new ArgumentException(
                        $"Index '{index.Name}' refers to unknown column '{column}'.");
                }
            }
            if (index.Columns.Count > IndexData.MaxColumns)
            {
                throw new ArgumentException(
                    $"Index '{index.Name}' may contain at most {IndexData.MaxColumns} columns.");
            }
        }
    }

    /// <summary>
    /// Creates only a replacement table-definition and its usage-map declarations.
    /// Existing row pages and retained index pages are referenced in place; no row is
    /// copied and no existing B-tree is rebuilt.  The caller is responsible for
    /// installing the new definition in the system catalog after any new index has
    /// been successfully built.
    /// </summary>
    internal static Table CreateTableDefinitionForExistingData(Database database, string name,
        List<ColumnBuilder> columns, List<IndexBuilder> indexes, ExistingTableDefinition existing)
    {
        if (columns.Count == 0)
        {
            throw new ArgumentException("A table must have at least one column.");
        }
        if (existing.Indexes.Count != indexes.Count)
        {
            throw new ArgumentException("Existing index state does not match the replacement definition.");
        }
        ValidateDefinition(name, columns, indexes, database.Format, database.TextEncoding);

        JetFormat format = database.Format;
        PageChannel pageChannel = database.PageChannel;
        pageChannel.StartWrite();
        try
        {
            ColumnState[] colState = BuildColumnStates(columns, format);
            int tdefPageNumber = pageChannel.AllocateNewPage();
            int umapPageNumber = pageChannel.AllocateNewPage();
            var indexStates = new List<IndexState>(indexes.Count);
            for (int i = 0; i < indexes.Count; i++)
            {
                ExistingIndexDefinition? old = existing.Indexes[i];
                indexStates.Add(old == null
                    ? new IndexState()
                    : new IndexState
                    {
                        Existing = true,
                        RootPageNumber = old.RootPageNumber,
                        ExistingUniqueEntryCount = old.UniqueEntryCount,
                        ExistingPages = old.OwnedPages,
                    });
            }
            var lvalStates = columns.Where(c => c.IsLongValue).Select(_ => new ColumnState()).ToList();

            CreateUsageMapDefinitionPage(database, umapPageNumber, columns, indexes,
                indexStates, lvalStates, existing);

            int totalTableDefSize = CalculateTableDefinitionSize(database, columns, indexes);
            byte[] buffer = new byte[Math.Max(totalTableDefSize, format.PageSize)];
            WriteTableDefinitionHeader(buffer, database, tdefPageNumber, umapPageNumber,
                columns, indexes, existing.RowCount, existing.NextAutoNumber);
            int pos = format.SizeTdefHeader;

            for (int i = 0; i < indexes.Count; i++)
            {
                int countOffset = format.OffsetIndexDefBlock + i * format.SizeIndexDefinition + 4;
                PutInt(buffer, countOffset, indexStates[i].Existing
                    ? indexStates[i].ExistingUniqueEntryCount
                    : 0);
            }
            pos += indexes.Count * format.SizeIndexDefinition;
            WriteColumnDefinitions(buffer, ref pos, database, columns, colState, format);
            if (indexes.Count > 0)
            {
                WriteIndexDefinitions(buffer, ref pos, database, columns, indexes,
                    indexStates, tdefPageNumber, format);
                WriteIndexNames(buffer, ref pos, indexes, database, format);
            }
            WriteColumnUsageMapDefinitions(buffer, ref pos, columns, indexes.Count,
                lvalStates, format);
            buffer[pos++] = 0xFF;
            buffer[pos++] = 0xFF;

            WriteTableDefinitionBuffer(buffer, totalTableDefSize, tdefPageNumber, format, pageChannel);
            return LoadTable(database, name, tdefPageNumber);
        }
        finally
        {
            pageChannel.FinishWrite();
        }
    }

    private static ColumnState[] BuildColumnStates(List<ColumnBuilder> columns, JetFormat format)
    {
        var result = new ColumnState[columns.Count];
        int varOffset = 0;
        int longVarOffset = columns.Count(c => c.IsVariableLength && !c.IsLongValue);
        int fixedOffset = 0;
        for (int i = 0; i < columns.Count; i++)
        {
            ColumnBuilder col = columns[i];
            if (col.IsVariableLength)
            {
                result[i] = new ColumnState
                {
                    VarLenTableIndex = col.IsLongValue ? longVarOffset++ : varOffset,
                    FixedDataOffset = 0,
                };
                if (!col.IsLongValue)
                {
                    varOffset++;
                }
            }
            else
            {
                result[i] = new ColumnState
                {
                    VarLenTableIndex = varOffset,
                    FixedDataOffset = col.StoreInNullMask ? 0 : fixedOffset,
                };
                fixedOffset += GetFixedDataSize(col, format);
            }
        }
        return result;
    }

    private static int CalculateTableDefinitionSize(Database database,
        List<ColumnBuilder> columns, List<IndexBuilder> indexes)
    {
        JetFormat format = database.Format;
        int idxDataLen = indexes.Count * (format.SizeIndexDefinition + format.SizeIndexColumnBlock)
            + indexes.Count * format.SizeIndexInfoBlock;
        int colUmapLen = columns.Count(c => c.IsLongValue) * 10;
        int total = format.SizeTdefHeader + format.SizeColumnDefBlock * columns.Count
            + idxDataLen + colUmapLen + format.SizeTdefTrailer;
        foreach (ColumnBuilder col in columns)
        {
            total += CalculateNameLength(col.Name, format);
        }
        foreach (IndexBuilder idx in indexes)
        {
            total += CalculateNameLength(idx.Name, format);
        }
        return total;
    }

    private static void ValidateName(string name, int maxCharacters, JetFormat format,
        Encoding encoding, string kind)
    {
        if (name.Length > maxCharacters)
        {
            throw new ArgumentException(
                $"The {kind} name '{name}' exceeds the {maxCharacters}-character limit.");
        }
        int byteLength = encoding.GetByteCount(name);
        int maxBytes = format.SizeNameLength == 1 ? byte.MaxValue : ushort.MaxValue;
        if (byteLength > maxBytes)
        {
            throw new ArgumentException(
                $"The {kind} name '{name}' is too long for the database encoding.");
        }
    }

    private static int CalculateNameLength(string name, JetFormat format)
        => name.Length * format.SizeTextFieldUnit + format.SizeNameLength;

    private static void WriteTableDefinitionHeader(byte[] buffer, Database database, int tdefPageNumber,
        int umapPageNumber, List<ColumnBuilder> columns, List<IndexBuilder> indexes,
        int rowCount = 0, int nextAutoNumber = 0)
    {
        JetFormat format = database.Format;
        // page header
        buffer[0] = PageTypes.TableDef;
        buffer[1] = 0x01;
        PutInt(buffer, format.OffsetNextTableDefPage, 0);

        PutInt(buffer, 8, 0); // table def length (patched later)
        PutInt(buffer, 12, MagicTableNumber);
        PutInt(buffer, format.OffsetNumRows, rowCount);
        PutInt(buffer, format.OffsetNextAutoNumber, nextAutoNumber);
        buffer[24] = 1; // makes autonumbering work in access
        Array.Clear(buffer, 25, 15);
        buffer[format.OffsetTableType] = TypeUser;
        PutShort(buffer, format.OffsetMaxCols, (short)columns.Count);
        PutShort(buffer, format.OffsetNumVarCols, (short)columns.Count(c => c.IsVariableLength));
        PutShort(buffer, format.OffsetNumCols, (short)columns.Count);
        PutInt(buffer, format.OffsetNumIndexSlots, indexes.Count);
        PutInt(buffer, format.OffsetNumIndexes, indexes.Count);

        // owned pages map: row 0 on the umap page
        buffer[format.OffsetOwnedPages] = 0;
        Put3ByteInt(buffer, format.OffsetOwnedPages + 1, umapPageNumber);
        // free space pages map: row 1 on the umap page
        buffer[format.OffsetFreeSpacePages] = 1;
        Put3ByteInt(buffer, format.OffsetFreeSpacePages + 1, umapPageNumber);
    }

    private static void WriteColumnDefinitions(byte[] buffer, ref int pos, Database database, List<ColumnBuilder> columns, ColumnState[] colState, JetFormat format)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            ColumnBuilder col = columns[i];
            ColumnState state = colState[i];

            buffer[pos++] = (byte)col.Type;
            PutInt(buffer, ref pos, MagicTableNumber);
            PutShort(buffer, ref pos, (short)i);
            PutShort(buffer, ref pos, (short)state.VarLenTableIndex);
            PutShort(buffer, ref pos, (short)i);

            if (col.Type is DataType.Text or DataType.Memo)
            {
                // sort order: LCID + version (General Legacy for Jet 4)
                TextSortOrder sortOrder = col.TextSortOrder ?? database.Format.DefaultSortOrder;
                PutShort(buffer, ref pos, sortOrder.Value);
                buffer[pos++] = 0;
                buffer[pos++] = (byte)(sortOrder.Version & 0xFF);
            }
            else if (col.Type is DataType.Numeric or DataType.BigInt)
            {
                buffer[pos++] = col.Precision;
                buffer[pos++] = col.Scale;
                PutShort(buffer, ref pos, 0);
            }
            else
            {
                buffer[pos++] = 0;
                buffer[pos++] = 0;
                PutShort(buffer, ref pos, 0);
            }

            // column flags
            byte flags = UpdatableFlagMask;
            if (!col.IsVariableLength)
            {
                flags |= FixedLenFlagMask;
            }
            if (col.AutoNumber)
            {
                flags |= col.Type == DataType.Guid ? AutoNumberGuidFlagMask : AutoNumberFlagMask;
            }
            buffer[pos++] = flags;

            // extended flags
            buffer[pos++] = col.CompressedUnicode && !col.IsVariableLength ? CompressedUnicodeExtFlagMask : (byte)0;

            PutInt(buffer, ref pos, 0); // unknown

            // fixed data offset
            PutShort(buffer, ref pos, (short)(col.IsVariableLength ? 0 : state.FixedDataOffset));

            // column length
            int length = col.IsLongValue ? 0 : ComputeStoredLength(col, format);
            PutShort(buffer, ref pos, (short)length);
        }

        // column names
        foreach (ColumnBuilder col in columns)
        {
            WriteName(buffer, ref pos, col.Name, database.TextEncoding, format);
        }
    }

    internal static int ComputeStoredLength(ColumnBuilder col, JetFormat format)
        => col.IsVariableLength ? col.Length : GetFixedDataSize(col, format);

    internal static void WriteName(byte[] buffer, ref int pos, string name, Encoding charset, JetFormat format)
    {
        byte[] bytes = charset.GetBytes(name);
        if (format.SizeNameLength == 2)
        {
            PutShort(buffer, ref pos, (short)bytes.Length);
        }
        else
        {
            buffer[pos++] = (byte)bytes.Length;
        }
        Array.Copy(bytes, 0, buffer, pos, bytes.Length);
        pos += bytes.Length;
    }

    private static void WriteIndexDefinitions(byte[] buffer, ref int pos, Database database, List<ColumnBuilder> columns, List<IndexBuilder> indexes, List<IndexState> indexStates, int tdefPageNumber, JetFormat format)
    {
        for (int idxIdx = 0; idxIdx < indexes.Count; idxIdx++)
        {
            IndexBuilder idx = indexes[idxIdx];
            IndexState state = indexStates[idxIdx];

            PutInt(buffer, ref pos, MagicIndexNumber);

            // MAX_COLUMNS column slots
            for (int c = 0; c < IndexData.MaxColumns; c++)
            {
                if (c < idx.Columns.Count)
                {
                    (string colName, bool ascending) = idx.Columns[c];
                    int colNumber = FindColumnNumber(columns, colName);
                    if (colNumber < 0)
                    {
                        throw new InvalidOperationException($"Unknown column '{colName}' for index '{idx.Name}'.");
                    }
                    PutShort(buffer, ref pos, (short)colNumber);
                    buffer[pos++] = ascending ? IndexData.AscendingColumnFlag : (byte)0;
                }
                else
                {
                    PutShort(buffer, ref pos, IndexData.ColumnUnused);
                    buffer[pos++] = 0;
                }
            }

            // index usage map reference
            buffer[pos] = (byte)state.UmapRowNumber;
            Put3ByteInt(buffer, pos + 1, state.UmapPageNumber);
            pos += 4;

            // New indexes receive a fresh root page.  Retained indexes keep their
            // complete B-tree; IndexMutator updates the definition pointer in those
            // pages after the replacement definition is written.
            if (!state.Existing)
            {
                byte[] rootPageBuffer = CreateIndexRootPage(tdefPageNumber, format);
                database.PageChannel.WritePage(rootPageBuffer, state.RootPageNumber);
            }
            PutInt(buffer, ref pos, state.RootPageNumber);
            PutInt(buffer, ref pos, 0); // unknown

            // index flags
            byte indexFlags = IndexData.UnknownIndexFlag;
            if (idx.Unique)
            {
                indexFlags |= IndexData.UniqueIndexFlag;
            }
            if (idx.IgnoreNulls)
            {
                indexFlags |= IndexData.IgnoreNullsIndexFlag;
            }
            if (idx.Required)
            {
                indexFlags |= IndexData.RequiredIndexFlag;
            }
            buffer[pos++] = indexFlags;

            pos += 5; // unknown
        }

        // logical index definitions
        for (int idxIdx = 0; idxIdx < indexes.Count; idxIdx++)
        {
            IndexBuilder idx = indexes[idxIdx];

            PutInt(buffer, ref pos, MagicTableNumber);
            PutInt(buffer, ref pos, idxIdx); // index number
            PutInt(buffer, ref pos, idxIdx); // index data number
            buffer[pos++] = 0; // related table type
            PutInt(buffer, ref pos, InvalidIndexNumber); // related index num
            PutInt(buffer, ref pos, 0); // related table page
            buffer[pos++] = 0; // cascade updates
            buffer[pos++] = 0; // cascade deletes
            buffer[pos++] = idx.PrimaryKey ? PrimaryKeyIndexType : (byte)0; // index type
            pos += format.SkipAfterIndexSlot;
        }
    }

    private static void WriteIndexNames(byte[] buffer, ref int pos, List<IndexBuilder> indexes, Database database, JetFormat format)
    {
        foreach (IndexBuilder idx in indexes)
        {
            WriteName(buffer, ref pos, idx.Name, database.TextEncoding, format);
        }
    }

    private static void WriteColumnUsageMapDefinitions(byte[] buffer, ref int pos, List<ColumnBuilder> columns, int indexCount, List<ColumnState> lvalStates, JetFormat format)
    {
        int lvalIdx = 0;
        for (int i = 0; i < columns.Count; i++)
        {
            ColumnBuilder col = columns[i];
            if (!col.IsLongValue)
            {
                continue;
            }
            ColumnState state = lvalStates[lvalIdx];
            PutShort(buffer, ref pos, (short)i); // column number
            buffer[pos++] = (byte)state.UmapOwnedRowNumber;
            Put3ByteInt(buffer, pos, state.UmapPageNumber);
            pos += 3;
            buffer[pos++] = (byte)state.UmapFreeRowNumber;
            Put3ByteInt(buffer, pos, state.UmapPageNumber);
            pos += 3;
            lvalIdx++;
        }
    }

    /// <summary>
    /// Creates the usage-map page(s) holding the table's owned/free space maps (rows 0/1),
    /// one map per index (rows 2+), and two maps per long-value column. Spills onto
    /// additional pages if a single page fills up.
    /// </summary>
    private static void CreateUsageMapDefinitionPage(Database database, int umapPageNumber,
        List<ColumnBuilder> columns, List<IndexBuilder> indexes, List<IndexState> indexStates,
        List<ColumnState> lvalStates, ExistingTableDefinition? existing = null)
    {
        JetFormat format = database.Format;
        PageChannel pageChannel = database.PageChannel;

        int indexUmapEnd = 2 + indexes.Count;
        int umapNum = indexUmapEnd + columns.Count(c => c.IsLongValue) * 2;

        int umapRowLength = format.OffsetUsageMapStart + format.UsageMapTableByteLength;
        int umapSpaceUsage = Table.GetRowSpaceUsage(umapRowLength, format);

        byte[]? page = null;
        int curUmapPage = IndexData.InvalidIndexPageNumber;
        int freeSpace = 0;
        int rowStart = 0;
        int umapRowNum = 0;

        for (int i = 0; i < umapNum; i++)
        {
            if (page == null)
            {
                if (curUmapPage == IndexData.InvalidIndexPageNumber)
                {
                    // the first usage map page has already been allocated
                    curUmapPage = umapPageNumber;
                }
                else
                {
                    curUmapPage = pageChannel.AllocateNewPage();
                }
                freeSpace = format.DataPageInitialFreeSpace;

                page = new byte[format.PageSize];
                page[0] = PageTypes.Data;
                page[1] = 0x01;
                PutShort(page, format.OffsetFreeSpace, (short)freeSpace);
                PutShort(page, format.OffsetNumRowsOnDataPage, 0);

                rowStart = format.PageSize - umapRowLength;
                umapRowNum = 0;
            }

            // record the row start in the offset table
            PutShort(page, Table.GetRowStartOffset(umapRowNum, format), (short)rowStart);

            if (i == 0)
            {
                // table "owned pages" map
                if (existing == null)
                {
                    page[rowStart] = MapTypeReference;
                }
                else
                {
                    WriteReferenceMapRow(page, rowStart, existing.OwnedPages, database);
                }
            }
            else if (i == 1)
            {
                // table "free space pages" map
                if (existing == null)
                {
                    // A growing table can exceed the inline 512-page range.  Use
                    // the same expandable representation as the owned-pages map.
                    page[rowStart] = MapTypeReference;
                }
                else
                {
                    WriteReferenceMapRow(page, rowStart, existing.FreeSpacePages, database);
                }
            }
            else if (i < indexUmapEnd)
            {
                int indexIdx = i - 2;
                indexStates[indexIdx].UmapRowNumber = umapRowNum;
                indexStates[indexIdx].UmapPageNumber = curUmapPage;
                IndexState state = indexStates[indexIdx];
                if (state.Existing)
                {
                    page[rowStart] = MapTypeReference;
                    WriteReferenceMapRow(page, rowStart, state.ExistingPages!, database);
                }
                else
                {
                    // index map: inline, starting at the index root page
                    int rootPageNumber = pageChannel.AllocateNewPage();
                    state.RootPageNumber = rootPageNumber;
                    page[rowStart] = MapTypeInline;
                    PutInt(page, rowStart + 1, rootPageNumber);
                    page[rowStart + 5] = 1; // mark the root page as owned
                }
            }
            else
            {
                // long value column maps (inline); force both maps on the same page
                int lvalIdx = i - indexUmapEnd;
                int umapType = lvalIdx % 2;
                lvalIdx /= 2;
                ColumnState state = lvalStates[lvalIdx];

                if (umapType == 1 && curUmapPage != state.UmapPageNumber)
                {
                    // we want both maps for a column on the same page, so restart this row
                    i--;
                    umapType = 0;
                    lvalIdx = i - indexUmapEnd;
                    lvalIdx /= 2;
                    state = lvalStates[lvalIdx];
                    state.UmapPageNumber = curUmapPage;
                }

                if (umapType == 0)
                {
                    state.UmapOwnedRowNumber = umapRowNum;
                    state.UmapPageNumber = curUmapPage;
                }
                else
                {
                    state.UmapFreeRowNumber = umapRowNum;
                }
                if (existing == null)
                {
                    page[rowStart] = MapTypeInline;
                }
                else
                {
                    page[rowStart] = MapTypeReference;
                    WriteReferenceMapRow(page, rowStart,
                        umapType == 0 ? existing.LongValuePages : Array.Empty<int>(), database);
                }
            }

            rowStart -= umapRowLength;
            freeSpace -= umapSpaceUsage;
            umapRowNum++;

            if (freeSpace <= umapSpaceUsage || i == umapNum - 1)
            {
                // finish current page
                PutShort(page, format.OffsetFreeSpace, (short)freeSpace);
                PutShort(page, format.OffsetNumRowsOnDataPage, (short)umapRowNum);
                pageChannel.WritePage(page, curUmapPage);
                page = null;
            }
        }
    }

    private static void WriteReferenceMapRow(byte[] page, int rowStart,
        IReadOnlyCollection<int> pages, Database database)
    {
        JetFormat format = database.Format;
        int referenceBytes = format.UsageMapTableByteLength - 1;
        Array.Clear(page, rowStart + 1, referenceBytes);
        page[rowStart] = MapTypeReference;

        int maxPagesPerUsageMapPage = (format.PageSize - format.OffsetUsageMapPageData) * 8;
        int maxReferencePages = referenceBytes / 4;
        int maxPage = pages.Where(p => p > 0).DefaultIfEmpty(-1).Max();
        if (maxPage < 0)
        {
            return;
        }
        int numReferencePages = maxPage / maxPagesPerUsageMapPage + 1;
        if (numReferencePages > maxReferencePages)
        {
            throw new DatabaseException("The table usage map reference row is full.");
        }

        var pageBuckets = pages.Where(p => p > 0).ToLookup(p => p / maxPagesPerUsageMapPage);
        for (int bucket = 0; bucket < numReferencePages; bucket++)
        {
            int mapPageNumber = database.PageChannel.AllocateNewPage();
            PutInt(page, rowStart + 1 + bucket * 4, mapPageNumber);
            byte[] mapPage = new byte[format.PageSize];
            mapPage[0] = PageTypes.UsageMap;
            mapPage[1] = 0x01;
            foreach (int pageNumber in pageBuckets[bucket])
            {
                int relative = pageNumber - bucket * maxPagesPerUsageMapPage;
                int offset = format.OffsetUsageMapPageData + relative / 8;
                if (offset < mapPage.Length)
                {
                    mapPage[offset] |= (byte)(1 << (relative % 8));
                }
            }
            database.PageChannel.WritePage(mapPage, mapPageNumber);
        }
    }

    private static byte[] CreateIndexRootPage(int tdefPageNumber, JetFormat format)
    {
        byte[] buffer = new byte[format.PageSize];
        buffer[0] = PageTypes.IndexLeaf;
        buffer[1] = 0x01;
        PutInt(buffer, 4, tdefPageNumber);
        PutShort(buffer, 2, (short)(format.PageSize - (format.OffsetIndexEntryMask + format.SizeIndexEntryMask)));
        return buffer;
    }

    /// <summary>writes the table definition buffer to the (possibly multi-page) tdef</summary>
    internal static void WriteTableDefinitionBuffer(byte[] buffer, int totalTableDefSize, int tdefPageNumber, JetFormat format, PageChannel pageChannel)
    {
        // patch the table definition length
        PutInt(buffer, 8, totalTableDefSize);

        int pos = 0;
        int curPage = tdefPageNumber;
        var page = new byte[format.PageSize];
        int nextPage = 0;

        while (pos < totalTableDefSize)
        {
            Array.Clear(page, 0, page.Length);
            int used;
            if (pos == 0)
            {
                // first page: content already includes the 8-byte page header
                used = Math.Min(format.PageSize, totalTableDefSize);
                Array.Copy(buffer, 0, page, 0, used);
                pos = used;
            }
            else
            {
                page[0] = PageTypes.TableDef;
                page[1] = 0x01;
                used = Math.Min(format.PageSize - 8, totalTableDefSize - pos);
                Array.Copy(buffer, pos, page, 8, used);
                pos += used;
                used += 8;
            }

            if (pos < totalTableDefSize)
            {
                // need a next page
                nextPage = pageChannel.AllocateNewPage();
                PutInt(page, format.OffsetNextTableDefPage, nextPage);
            }

            // update page free space
            int freeSpace = Math.Max(format.PageSize - used - 8, 0);
            PutShort(page, format.OffsetFreeSpace, (short)freeSpace);

            pageChannel.WritePage(page, curPage);
            curPage = nextPage;
        }
    }

    internal static int FindColumnNumber(List<ColumnBuilder> columns, string name)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (columns[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private sealed class ColumnState
    {
        internal int VarLenTableIndex;
        internal int FixedDataOffset;
        internal int UmapOwnedRowNumber;
        internal int UmapFreeRowNumber;
        internal int UmapPageNumber;
    }

    private sealed class IndexState
    {
        internal bool Existing;
        internal int RootPageNumber;
        internal int UmapRowNumber;
        internal int UmapPageNumber;
        internal int ExistingUniqueEntryCount;
        internal IReadOnlyCollection<int>? ExistingPages;
    }

    // ------------------------------------------------------------------
    // byte helpers
    // ------------------------------------------------------------------

    internal static void PutShort(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static void PutShort(byte[] buffer, ref int pos, short value)
    {
        PutShort(buffer, pos, value);
        pos += 2;
    }

    internal static void PutInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static void PutInt(byte[] buffer, ref int pos, int value)
    {
        PutInt(buffer, pos, value);
        pos += 4;
    }

    internal static void Put3ByteInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
    }
}
