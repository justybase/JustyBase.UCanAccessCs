using System.Text;

namespace UCanAccess.File;

/// <summary>
/// Metadata for a table discovered in the system catalog (port of Jackcess <c>TableInfo</c> / <c>TableMetaData</c>).
/// </summary>
public sealed class TableMetaData
{
    internal TableMetaData(string name, int pageNumber, int flags, byte type, string? linkedDbName, string? linkedTableName, string? connectName)
    {
        Name = name;
        PageNumber = pageNumber;
        Flags = flags;
        Type = type;
        LinkedDbName = linkedDbName;
        LinkedTableName = linkedTableName;
        ConnectName = connectName;
    }

    public string Name { get; }

    public int PageNumber { get; }

    public int Flags { get; }

    public byte Type { get; }

    public string? LinkedDbName { get; }

    public string? LinkedTableName { get; }

    public string? ConnectName { get; }

    public bool IsSystem => (Flags & Database.SystemObjectFlags) != 0;

    public bool IsLinked => Type == Database.TypeLinkedTable || Type == Database.TypeLinkedOdbcTable;
}

/// <summary>
/// An MS Access database file: opens the file, reads the system catalog and provides
/// access to tables (port of Jackcess <c>DatabaseImpl</c>).
/// </summary>
public sealed class Database : IDisposable
{
    internal const int SystemObjectFlag = unchecked((int)0x80000000);
    internal const int AltSystemObjectFlag = 0x02;
    internal const int SystemObjectFlags = SystemObjectFlag | AltSystemObjectFlag;

    internal const byte TypeTable = 1;
    internal const byte TypeLinkedOdbcTable = 4;
    internal const byte TypeQuery = 5;
    internal const byte TypeLinkedTable = 6;
    internal const byte TypeRelationship = 8;

    /// <summary>system catalog always lives on page 2</summary>
    private const int PageSystemCatalog = 2;

    /// <summary>name of the system catalog</summary>
    private const string TableSystemCatalog = "MSysObjects";

    private const int DbParentId = 0xF000000;

    private readonly Stream _stream;
    private readonly PageChannel _pageChannel;
    private readonly JetFormat _format;
    private readonly bool _isReadOnly;
    private readonly bool _allowExternalLinks;
    private readonly Table _systemCatalog;
    private readonly int _tableParentId;
    private readonly List<TableMetaData> _tableInfos = new();
    private Encoding _textEncoding;
    private bool _disposed;
    private bool _enforceForeignKeys = true;
    private string? _path;
    private readonly Dictionary<string, Database> _linkedDatabaseByPath = new(StringComparer.OrdinalIgnoreCase);
    private FileStream? _lockStream;
    private string? _lockPath;

    /// <summary>whether foreign-key relationships are enforced on row writes (default true)</summary>
    public bool EnforceForeignKeys
    {
        get => _enforceForeignKeys;
        set => _enforceForeignKeys = value;
    }

    private Database(Stream stream, bool closeChannel, Encoding? encoding, bool readOnly,
        bool allowExternalLinks, IAccessPageCodec? codec = null)
    {
        _stream = stream;
        _isReadOnly = readOnly;
        _allowExternalLinks = allowExternalLinks;
        _format = JetFormat.GetFormat(ReadHeader(stream));

        _pageChannel = new PageChannel(stream, _format, closeChannel, codec);
        _textEncoding = ResolveEncoding(encoding);

        _systemCatalog = LoadTable(TableSystemCatalog, PageSystemCatalog, SystemObjectFlag, TypeTable);

        // discover tables (full catalog scan, like Jackcess' FallbackTableFinder)
        int tablesParentId = -1;
        foreach (Row row in _systemCatalog.Rows())
        {
            if (TryGetString(row, "Name", out string? name)
                && string.Equals(name, "Tables", StringComparison.OrdinalIgnoreCase)
                && TryGetInt(row, "ParentId", out int parentId)
                && parentId == DbParentId)
            {
                if (TryGetInt(row, "Id", out int id))
                {
                    tablesParentId = id;
                }
                break;
            }
        }
        _tableParentId = tablesParentId;

        if (_tableParentId <= 0)
        {
            throw new DatabaseException("Did not find required parent table id");
        }

        foreach (Row row in _systemCatalog.Rows())
        {
            if (!TryGetInt(row, "ParentId", out int parentId) || parentId != _tableParentId)
            {
                continue;
            }
            byte type = GetTypeValue(row, "Type");
            if (!IsTableType(type))
            {
                continue;
            }
            string tableName = GetString(row, "Name") ?? "";
            int id = GetInt(row, "Id");
            int flags = GetInt(row, "Flags");
            _tableInfos.Add(new TableMetaData(
                tableName,
                id,
                flags,
                type,
                TryGetString(row, "Database", out var db) ? db : null,
                TryGetString(row, "ForeignName", out var foreign) ? foreign : null,
                TryGetString(row, "Connect", out var connect) ? connect : null));
        }
    }

    /// <summary>
    /// Creates a new (empty) Access database file, seeded from the bundled template.
    /// </summary>
    /// <param name="path">path of the new .mdb/.accdb file (created/truncated)</param>
    /// <param name="encoding">optional text encoding override</param>
    /// <param name="version">"2000", "2002" or "2003" (Jet 4 .mdb, default), or "2007", "2010" or "2016" (.accdb)</param>
    public static Database Create(string path, Encoding? encoding = null, string version = "2003", bool allowExternalLinks = false)
    {
        string normalizedVersion = version.Trim();
        string templateName = normalizedVersion.ToUpperInvariant() switch
        {
            "2000" or "2002" or "2003" => "UCanAccess.File.Resources.empty.mdb",
            "2007" => "UCanAccess.File.Resources.empty2007.accdb",
            "2010" => "UCanAccess.File.Resources.empty2010.accdb",
            "2016" => "UCanAccess.File.Resources.empty2016.accdb",
            _ => throw new ArgumentException($"Unsupported database version '{version}'. Expected 2000, 2002, 2003, 2007, 2010 or 2016.", nameof(version)),
        };
        using Stream? template = typeof(Database).Assembly.GetManifestResourceStream(templateName)
            ?? throw new InvalidOperationException($"Missing embedded empty database template '{templateName}'.");
        var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
        bool ok = false;
        try
        {
            template.CopyTo(fs);
            fs.Flush();
            fs.Position = 0;
            var db = new Database(fs, true, encoding, false, allowExternalLinks); db._path = path;
            db.AcquireLock();
            ok = true;
            return db;
        }
        finally
        {
            if (!ok)
            {
                fs.Dispose();
            }
        }
    }

    /// <summary>
    /// Opens an existing Access database file.
    /// </summary>
    /// <param name="path">path to the .mdb/.accdb file</param>
    /// <param name="encoding">optional text encoding override (only relevant for Jet 3 databases)</param>
    /// <param name="readOnly">whether to open without write intent (default true)</param>
    /// <param name="codecFactory">optional page codec factory for an encrypted file</param>
    public static Database Open(string path, Encoding? encoding = null, bool readOnly = true,
        bool allowExternalLinks = false, IAccessPageCodecFactory? codecFactory = null)
    {
        if (readOnly)
        {
            var roStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
            bool success = false;
            IAccessPageCodec? codec = null;
            try
            {
                JetFormat format = JetFormat.GetFormat(ReadHeader(roStream));
                if (codecFactory != null)
                {
                    byte[] root = ReadRootPage(roStream, format);
                    codec = codecFactory.Create(new AccessPageCodecContext(
                        path, format, true, root));
                }
                var db = new Database(roStream, true, encoding, true, allowExternalLinks, codec); db._path = path;
                success = true;
                return db;
            }
            finally
            {
                if (!success)
                {
                    codec?.Dispose();
                    roStream.Dispose();
                }
            }
        }

        // read-write: first peek the format version to reject read-only formats
        var rwStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
        bool ok = false;
        IAccessPageCodec? codecForWrite = null;
        try
        {
            JetFormat format = JetFormat.GetFormat(ReadHeader(rwStream));
            if (format.ReadOnly)
            {
                throw new DatabaseException($"The database format {format} does not support writing.");
            }
            if (codecFactory != null)
            {
                byte[] root = ReadRootPage(rwStream, format);
                codecForWrite = codecFactory.Create(new AccessPageCodecContext(
                    path, format, false, root));
            }
            var db = new Database(rwStream, true, encoding, false, allowExternalLinks, codecForWrite); db._path = path;
            db.AcquireLock();
            ok = true;
            return db;
        }
        finally
        {
            if (!ok)
            {
                codecForWrite?.Dispose();
                rwStream.Dispose();
            }
        }
    }

    private static byte[] ReadHeader(Stream stream)
    {
        var header = new byte[21];
        int total = 0;
        while (total < header.Length)
        {
            int read = stream.Read(header, total, header.Length - total);
            if (read == 0)
            {
                throw new DatabaseException("Empty database file");
            }
            total += read;
        }
        stream.Position = 0;
        return header;
    }

    /// <summary>the Jet format detected from the file header</summary>
    public JetFormat Format => _format;

    /// <summary>whether this database was opened without write intent (or the format is read-only)</summary>
    public bool IsReadOnly => _isReadOnly || _format.ReadOnly;

    /// <summary>
    /// Starts a non-atomic write batch. The batch defers the stream flush until
    /// <see cref="WriteBatch.Commit"/> or disposal, but does not provide rollback.
    /// </summary>
    public WriteBatch BeginWriteBatch()
    {
        if (IsReadOnly)
        {
            throw new DatabaseException("The database was opened read-only.");
        }
        return new WriteBatch(this);
    }

    private static byte[] ReadRootPage(Stream stream, JetFormat format)
    {
        byte[] root = new byte[format.PageSize];
        stream.Position = 0;
        int total = 0;
        while (total < root.Length)
        {
            int read = stream.Read(root, total, root.Length - total);
            if (read == 0)
            {
                throw new DatabaseException(
                    $"Failed attempting to read {format.PageSize} bytes from the root page, only read {total}");
            }
            total += read;
        }
        stream.Position = 0;
        return root;
    }

    internal PageChannel PageChannel => _pageChannel;

    /// <summary>the text encoding used for textual columns (Jet 4 always UTF-16LE; Jet 3 from the header code page).</summary>
    public Encoding TextEncoding => _textEncoding;

    /// <summary>the database file path (empty for in-memory databases)</summary>
    public string Path => _path ?? string.Empty;

    /// <summary>the system catalog (MSysObjects) table</summary>
    public Table SystemCatalog => _systemCatalog;

    private Encoding ResolveEncoding(Encoding? encoding)
    {
        if (encoding != null)
        {
            return encoding;
        }
        if (_format.Charset != null)
        {
            // Jet 4+: UTF-16LE
            return _format.Charset;
        }
        // Jet 3: read the code page from the header
        var buffer = new byte[_format.PageSize];
        _pageChannel.ReadRootPage(buffer);
        int codePage = ReadShort(buffer, _format.OffsetCodePage);
        return JetFormat.GetEncodingForCodePage(codePage);
    }

    private Table LoadTable(string name, int pageNumber, int flags, byte type)
    {
        byte[] buffer = new byte[_format.PageSize];
        _pageChannel.ReadPage(buffer, pageNumber);
        if (buffer[0] != PageTypes.TableDef)
        {
            throw new DatabaseException(
                $"Looking for {name} at page {pageNumber}, but page type is {buffer[0]}");
        }
        return new Table(this, buffer, pageNumber, name, flags);
    }

    /// <summary>
    /// All user (non-system) table names in the database, including linked tables
    /// (matches Jackcess <c>getTableNames()</c> semantics).
    /// </summary>
    public IReadOnlyList<string> GetTableNames()
        => _tableInfos.Where(t => !t.IsSystem).Select(t => t.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// All system table names in the database.
    /// </summary>
    public IReadOnlyList<string> GetSystemTableNames()
        => _tableInfos.Where(t => t.IsSystem).Select(t => t.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Metadata for all tables discovered in the system catalog.
    /// </summary>
    public IReadOnlyList<TableMetaData> GetTableMetaData() => _tableInfos;

    /// <summary>
    /// Gets the given user table, or null if it does not exist. Linked tables throw
    /// <see cref="DatabaseException"/> (their external database is not resolved automatically).
    /// </summary>
    public Table? GetTable(string name)
    {
        foreach (TableMetaData info in _tableInfos)
        {
            if (info.IsSystem)
            {
                continue;
            }
            if (string.Equals(info.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                if (info.IsLinked)
                {
                    throw new DatabaseException($"Cannot open linked table '{name}' (external database not resolved).");
                }
                return LoadTable(info.Name, info.PageNumber, info.Flags, info.Type);
            }
        }
        return null;
    }

    internal Table? GetTableByPageNumber(int pageNumber)
    {
        TableMetaData? info = _tableInfos.FirstOrDefault(t => t.PageNumber == pageNumber);
        return info == null ? null : LoadTable(info.Name, info.PageNumber, info.Flags, info.Type);
    }

    /// <summary>the catalog row id of the virtual "Tables" container object</summary>
    internal int TablesParentId => _tableParentId;

    /// <summary>
    /// Opens a linked (native) table's target: resolves the linkee database relative to
    /// this database's directory, opens it with the same read-only mode, and returns
    /// the linkee table.
    /// The linkee database stays open until this database is disposed.
    /// </summary>
    public Table? GetLinkedTable(string name)
    {
        foreach (TableMetaData info in _tableInfos)
        {
            if (!info.IsLinked || !string.Equals(info.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (info.LinkedDbName == null || info.LinkedTableName == null)
            {
                throw new DatabaseException($"Linked table '{name}' has no link target.");
            }
            string linkeePath = ResolveLinkedPath(info.LinkedDbName);
            if (!System.IO.File.Exists(linkeePath))
            {
                throw new DatabaseException($"Linked database '{info.LinkedDbName}' not found at '{linkeePath}'.");
            }
            if (!_linkedDatabaseByPath.TryGetValue(linkeePath, out Database? linkeeDb))
            {
                linkeeDb = Open(linkeePath, null, readOnly: _isReadOnly, allowExternalLinks: _allowExternalLinks);
                _linkedDatabaseByPath[linkeePath] = linkeeDb;
            }
            return linkeeDb.GetTable(info.LinkedTableName);
        }
        return null;
    }

    private string ResolveLinkedPath(string linkedDbName)
    {
        string baseDirectory = _path is { Length: > 0 }
            ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path)) ?? Environment.CurrentDirectory
            : Environment.CurrentDirectory;
        string candidate = System.IO.Path.IsPathRooted(linkedDbName)
            ? linkedDbName
            : System.IO.Path.Combine(baseDirectory, linkedDbName);
        string fullPath = System.IO.Path.GetFullPath(candidate);

        if (!_allowExternalLinks)
        {
            string relative = System.IO.Path.GetRelativePath(baseDirectory, fullPath);
            if (relative == ".." || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || System.IO.Path.IsPathRooted(relative))
            {
                throw new DatabaseException(
                    $"Linked database '{linkedDbName}' resolves outside '{baseDirectory}'. " +
                    "Set Allow External Links=true only for trusted databases.");
            }
        }

        return fullPath;
    }

    /// <summary>
    /// Creates a new table in this database and returns it.
    /// </summary>
    public Table CreateTable(string name, IEnumerable<ColumnBuilder> columns, IEnumerable<IndexBuilder>? indexes = null)
        => TableCreator.CreateTable(this, name, columns.ToList(), (indexes ?? Enumerable.Empty<IndexBuilder>()).ToList());

    /// <summary>
    /// Deletes a table (its data, index and table-definition pages) and removes it
    /// from the system catalog.
    /// </summary>
    public void DeleteTable(string name)
    {
        Table? table = GetTable(name)
            ?? throw new InvalidOperationException($"Table '{name}' does not exist.");
        if (table.IsSystem)
        {
            throw new InvalidOperationException("Cannot drop a system table.");
        }

        PageChannel pageChannel = PageChannel;
        IReadOnlyCollection<int> longValuePages = table.CollectLongValuePages();
        pageChannel.StartWrite();
        try
        {
            // remove from the system catalog first, while the table pages still
            // exist (foreign-key enforcement may need to resolve the table)
            RemoveFromSystemCatalog(name);
            RemoveRelationshipsFor(name);

            // data pages
            var ownedCursor = table.OwnedPages.Cursor();
            int p;
            while ((p = ownedCursor.GetNextPage()) != PageChannelImpl.InvalidPageNumber)
            {
                pageChannel.DeallocatePage(p);
            }

            // index pages
            foreach (IndexData indexData in table.IndexDatas)
            {
                UsageMap? idxPages = indexData.OwnedPages;
                if (idxPages == null)
                {
                    continue;
                }
                var idxCursor = idxPages.Cursor();
                while ((p = idxCursor.GetNextPage()) != PageChannelImpl.InvalidPageNumber)
                {
                    pageChannel.DeallocatePage(p);
                }
            }

            // LVAL pages are not part of the ordinary row-data usage map, but they
            // are still owned by the table and must be returned to the global map.
            foreach (int lvalPage in longValuePages)
            {
                pageChannel.DeallocatePage(lvalPage);
            }

            // table definition page chain
            int tdef = table.TableDefPageNumber;
            while (tdef != 0)
            {
                byte[] buffer = new byte[Format.PageSize];
                pageChannel.ReadPage(buffer, tdef);
                int next = ByteUtil.GetIntLittleEndian(buffer, Format.OffsetNextTableDefPage);
                pageChannel.DeallocatePage(tdef);
                tdef = next;
            }

            // remove the table from the in-memory table list
            _tableInfos.RemoveAll(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            pageChannel.FinishWrite();
        }
    }

    /// <summary>
    /// Renames a user table in the system catalog and updates relationship metadata
    /// that refers to that table.
    /// </summary>
    public void RenameTable(string fromName, string toName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromName);
        ArgumentException.ThrowIfNullOrWhiteSpace(toName);
        if (fromName.Equals(toName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (GetTable(fromName) == null)
        {
            throw new InvalidOperationException($"Table '{fromName}' does not exist.");
        }
        if (GetTable(toName) != null || GetSystemTable(toName) != null)
        {
            throw new InvalidOperationException($"A table named '{toName}' already exists.");
        }

        bool renamed = false;
        foreach (Table.RowLocation location in _systemCatalog.RowLocations())
        {
            if (TryGetString(location.Row, "Name", out string? catalogName) && catalogName != null
                && catalogName.Equals(fromName, StringComparison.OrdinalIgnoreCase))
            {
                object?[] values = location.Row.ToArray();
                for (int i = 0; i < _systemCatalog.Columns.Count; i++)
                {
                    if (_systemCatalog.Columns[i].Name.Equals("Name", StringComparison.OrdinalIgnoreCase))
                    {
                        values[i] = toName;
                        break;
                    }
                }
                _systemCatalog.UpdateRow(location.PageNumber, location.RowNumber, values);
                renamed = true;
                break;
            }
        }

        if (!renamed)
        {
            throw new InvalidOperationException($"Table '{fromName}' was not found in the system catalog.");
        }

        RenameRelationshipReferences(fromName, toName);

        for (int i = 0; i < _tableInfos.Count; i++)
        {
            if (_tableInfos[i].Name.Equals(fromName, StringComparison.OrdinalIgnoreCase))
            {
                TableMetaData old = _tableInfos[i];
                _tableInfos[i] = new TableMetaData(toName, old.PageNumber, old.Flags, old.Type,
                    old.LinkedDbName, old.LinkedTableName, old.ConnectName);
                return;
            }
        }
    }

    /// <summary>removes saved relationships that reference the given table</summary>
    private void RemoveRelationshipsFor(string tableName)
    {
        Table? relTable = GetSystemTable("MSysRelationships");
        if (relTable == null)
        {
            return;
        }
        var toDelete = new List<Table.RowLocation>();
        foreach (Table.RowLocation location in relTable.RowLocations())
        {
            bool from = TryGetString(location.Row, "szReferencedObject", out string? fromName)
                && fromName != null && fromName.Equals(tableName, StringComparison.OrdinalIgnoreCase);
            bool to = TryGetString(location.Row, "szObject", out string? toName)
                && toName != null && toName.Equals(tableName, StringComparison.OrdinalIgnoreCase);
            if (from || to)
            {
                toDelete.Add(location);
            }
        }
        foreach (Table.RowLocation location in toDelete)
        {
            relTable.DeleteRow(location.PageNumber, location.RowNumber);
        }
    }

    private void RenameRelationshipReferences(string fromName, string toName)
    {
        Table? relTable = GetSystemTable("MSysRelationships");
        if (relTable == null)
        {
            return;
        }

        foreach (Table.RowLocation location in relTable.RowLocations())
        {
            object?[] values = location.Row.ToArray();
            bool changed = false;
            for (int i = 0; i < relTable.Columns.Count; i++)
            {
                string column = relTable.Columns[i].Name;
                if ((column.Equals("szObject", StringComparison.OrdinalIgnoreCase)
                        || column.Equals("szReferencedObject", StringComparison.OrdinalIgnoreCase))
                    && values[i] is string name
                    && name.Equals(fromName, StringComparison.OrdinalIgnoreCase))
                {
                    values[i] = toName;
                    changed = true;
                }
            }
            if (changed)
            {
                relTable.UpdateRow(location.PageNumber, location.RowNumber, values);
            }
        }
    }

    /// <summary>adds an index to an existing table (CREATE INDEX)</summary>
    public void AddIndex(string tableName, IndexBuilder builder)
    {
        Table? table = GetTable(tableName)
            ?? throw new InvalidOperationException($"Table '{tableName}' does not exist.");
        IndexMutator.AddIndex(this, table, builder);
    }

    /// <summary>removes an index from an existing table (DROP INDEX)</summary>
    public void DropIndex(string tableName, string indexName)
    {
        Table? table = GetTable(tableName)
            ?? throw new InvalidOperationException($"Table '{tableName}' does not exist.");
        IndexMutator.DropIndex(this, table, indexName);
    }

    /// <summary>the logical index names of the given table</summary>
    public IReadOnlyList<string> GetIndexNames(string tableName)
    {
        Table? table = GetTable(tableName)
            ?? throw new InvalidOperationException($"Table '{tableName}' does not exist.");
        return table.Indexes.Where(i => i.Name != null).Select(i => i.Name!).ToList();
    }

    /// <summary>the logical indexes of the given table (for metadata APIs)</summary>
    public IReadOnlyList<IndexInfo> GetIndexInfo(string tableName)
    {
        Table? table = GetTable(tableName)
            ?? throw new InvalidOperationException($"Table '{tableName}' does not exist.");
        var result = new List<IndexInfo>();
        foreach (IndexImpl index in table.Indexes)
        {
            var columns = new List<IndexColumnInfo>();
            foreach (IndexData.ColumnDescriptor descriptor in index.IndexData.Columns)
            {
                columns.Add(new IndexColumnInfo(descriptor.Column.Name, descriptor.IsAscending));
            }
            result.Add(new IndexInfo(
                index.Name ?? "idx" + index.IndexNumber,
                columns,
                index.IndexData.IsUnique,
                index.IsPrimaryKey,
                index.IndexData.IsRequired,
                index.IndexData.ShouldIgnoreNulls));
        }
        return result;
    }

    /// <summary>adds a column to an existing table (ALTER TABLE ... ADD COLUMN)</summary>
    public void AddColumn(string tableName, ColumnBuilder column)
    {
        Table? table = GetTable(tableName)
            ?? throw new InvalidOperationException($"Table '{tableName}' does not exist.");
        TableMutator.AddColumn(this, table, column);
    }

    /// <summary>removes a column from an existing table (ALTER TABLE ... DROP COLUMN)</summary>
    public void RemoveColumn(string tableName, string columnName)
    {
        Table? table = GetTable(tableName)
            ?? throw new InvalidOperationException($"Table '{tableName}' does not exist.");
        TableMutator.RemoveColumn(this, table, columnName);
    }

    /// <summary>saves a SELECT query as a view (CREATE VIEW)</summary>
    public void CreateView(string viewName, string selectSql)
        => TableMutator.CreateView(this, viewName, selectSql);

    /// <summary>drops a saved query / view (DROP VIEW)</summary>
    public void DropView(string viewName)
        => TableMutator.DropView(this, viewName);

    private void RemoveFromSystemCatalog(string name)
    {
        foreach (Table.RowLocation location in _systemCatalog.RowLocations())
        {
            if (TryGetString(location.Row, "Name", out string? catalogName) && catalogName != null
                && catalogName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                _systemCatalog.DeleteRow(location.PageNumber, location.RowNumber);
                return;
            }
        }
    }

    /// <summary>registers a new object row in the system catalog</summary>
    internal void AddToSystemCatalog(string name, int objectId, byte type, int pageNumber,
        byte[]? propertyBytes = null)
    {
        byte[] owner = GetObjectOwner();
        var values = new object?[_systemCatalog.Columns.Count];
        for (int i = 0; i < _systemCatalog.Columns.Count; i++)
        {
            string colName = _systemCatalog.Columns[i].Name;
            switch (colName)
            {
                case "Id":
                    values[i] = objectId;
                    break;
                case "Name":
                    values[i] = name;
                    break;
                case "Type":
                    values[i] = type;
                    break;
                case "DateUpdate":
                case "DateCreate":
                    values[i] = DateTime.Now;
                    break;
                case "Owner":
                    // MS Access needs the object's owner SID; Jackcess copies it from the
                    // MSysDb object or falls back to the default sid
                    values[i] = owner;
                    break;
                case "Flags":
                    values[i] = 0;
                    break;
                case "ParentId":
                    values[i] = TablesParentId;
                    break;
                case "LvProp":
                    values[i] = propertyBytes;
                    break;
                default:
                    values[i] = null;
                    break;
            }
        }
        _systemCatalog.AddRow(values);
        AddObjectPermissions(objectId);
    }

    /// <summary>
    /// Reads the minimal column-property map stored on the table's MSysObjects row.
    /// The system catalog itself is constructed before its backing field is assigned,
    /// so the early-load case intentionally reports no custom properties.
    /// </summary>
    internal bool IsColumnRequired(int tableDefPageNumber, string columnName)
    {
        if (_systemCatalog == null)
        {
            return false;
        }

        foreach (Table.RowLocation location in _systemCatalog.RowLocations())
        {
            if (!TryGetInt(location.Row, "Id", out int objectId) || objectId != tableDefPageNumber)
            {
                continue;
            }
            if (location.Row.TryGetValue("LvProp", out object? value) && value is byte[] bytes)
            {
                return PropertyMapCodec.IsRequired(bytes, columnName, TextEncoding);
            }
            return false;
        }
        return false;
    }

    /// <summary>
    /// MS Access derives an object's permissions from its MSysACEs rows (Access Control
    /// Entries). Jackcess copies the database-level ACEs (ObjectId = 0x80000000) to every
    /// new object with FInheritable=false; without them Access reports "no permission to
    /// read the table definition".
    /// </summary>
    private void AddObjectPermissions(int objectId)
    {
        Table? aces = GetSystemTable("MSysACEs");
        if (aces == null)
        {
            return;
        }
        var sids = new List<byte[]>();
        foreach (Table.RowLocation location in aces.RowLocations())
        {
            if (GetInt(location.Row, "ObjectId") == -2147483648
                && location.Row.TryGetValue("SID", out object? sid) && sid is byte[] bytes)
            {
                sids.Add(bytes);
            }
        }
        if (sids.Count == 0)
        {
            return;
        }
        foreach (byte[] sid in sids)
        {
            aces.AddRow(new object?[] { objectId, sid, 0xFFFFF, false });
        }
    }

    /// <summary>the default object owner SID (matches Jackcess SYS_DEFAULT_SID and MSysDb owners)</summary>
    private static readonly byte[] DefaultOwnerSid = { 0xA6, 0x33 };

    private byte[] GetObjectOwner()
    {
        // prefer the owner recorded on the MSysDb object (as Jackcess does), else the default sid
        foreach (Table.RowLocation location in _systemCatalog.RowLocations())
        {
            if (TryGetString(location.Row, "Name", out string? n) && n != null
                && n.Equals("MSysDb", StringComparison.OrdinalIgnoreCase))
            {
                if (location.Row.TryGetValue("Owner", out object? o) && o is byte[] bytes && bytes.Length > 0)
                {
                    return bytes;
                }
            }
        }
        return DefaultOwnerSid;
    }

    /// <summary>adds a table to the in-memory table list (used right after creation)</summary>
    internal void RegisterTableMeta(TableMetaData meta) => _tableInfos.Add(meta);

    /// <summary>moves a table catalog entry to a newly written table-definition page</summary>
    internal void ReplaceTableDefinition(string tableName, int oldPageNumber, int newPageNumber)
    {
        bool catalogUpdated = false;
        foreach (Table.RowLocation location in _systemCatalog.RowLocations())
        {
            if (!TryGetString(location.Row, "Name", out string? name)
                || !string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase)
                || !TryGetInt(location.Row, "Id", out int id)
                || id != oldPageNumber)
            {
                continue;
            }

            object?[] values = location.Row.ToArray();
            for (int i = 0; i < _systemCatalog.Columns.Count; i++)
            {
                if (_systemCatalog.Columns[i].Name.Equals("Id", StringComparison.OrdinalIgnoreCase))
                {
                    values[i] = newPageNumber;
                }
                else if (_systemCatalog.Columns[i].Name.Equals("DateUpdate", StringComparison.OrdinalIgnoreCase))
                {
                    values[i] = DateTime.Now;
                }
            }
            _systemCatalog.UpdateRow(location.PageNumber, location.RowNumber, values);
            catalogUpdated = true;
            break;
        }

        if (!catalogUpdated)
        {
            throw new InvalidOperationException($"Table '{tableName}' is missing from the system catalog.");
        }

        for (int i = 0; i < _tableInfos.Count; i++)
        {
            TableMetaData info = _tableInfos[i];
            if (info.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase)
                && info.PageNumber == oldPageNumber)
            {
                _tableInfos[i] = new TableMetaData(info.Name, newPageNumber, info.Flags, info.Type,
                    info.LinkedDbName, info.LinkedTableName, info.ConnectName);
                return;
            }
        }
    }

    /// <summary>
    /// Finds all the relationships in the database between the given tables.
    /// </summary>
    public IReadOnlyList<Relationship> GetRelationships(string table1, string table2)
    {
        var result = new List<Relationship>();
        foreach (Relationship rel in ReadRelationships())
        {
            bool matches1 = rel.FromTable.Name.Equals(table1, StringComparison.OrdinalIgnoreCase)
                && rel.ToTable.Name.Equals(table2, StringComparison.OrdinalIgnoreCase);
            bool matches2 = rel.FromTable.Name.Equals(table2, StringComparison.OrdinalIgnoreCase)
                && rel.ToTable.Name.Equals(table1, StringComparison.OrdinalIgnoreCase);
            if (matches1 || matches2)
            {
                result.Add(rel);
            }
        }
        return result;
    }

    /// <summary>
    /// Finds all the relationships in the database for the given table.
    /// </summary>
    public IReadOnlyList<Relationship> GetRelationships(string tableName)
    {
        var result = new List<Relationship>();
        foreach (Relationship rel in ReadRelationships())
        {
            if (rel.FromTable.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase)
                || rel.ToTable.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(rel);
            }
        }
        return result;
    }

    /// <summary>
    /// Finds all the relationships in the database in non-system tables.
    /// </summary>
    public IReadOnlyList<Relationship> GetRelationships()
        => ReadRelationships();

    private List<Relationship> ReadRelationships()
    {
        var result = new List<Relationship>();
        Table? relTable = GetSystemTable("MSysRelationships");
        if (relTable == null)
        {
            return result;
        }

        foreach (Row row in relTable.Rows())
        {
            if (!TryGetString(row, "szReferencedObject", out string? fromName) || fromName == null
                || !TryGetString(row, "szObject", out string? toName) || toName == null)
            {
                continue;
            }
            Table? fromTable = GetTable(fromName);
            Table? toTable = GetTable(toName);
            if (fromTable == null || toTable == null)
            {
                continue;
            }
            if (!TryGetString(row, "szRelationship", out string? relName) || relName == null)
            {
                continue;
            }

            Relationship? rel = result.FirstOrDefault(r => r.Name.Equals(relName, StringComparison.OrdinalIgnoreCase));
            if (rel == null)
            {
                int numCols = GetInt(row, "ccolumn");
                int flags = GetInt(row, "grbit");
                rel = new Relationship(relName, fromTable, toTable, flags, numCols);
                result.Add(rel);
            }

            int colIdx = GetInt(row, "icolumn");
            if (colIdx < rel.FromColumns.Length
                && TryGetString(row, "szReferencedColumn", out string? fromColName)
                && TryGetString(row, "szColumn", out string? toColName))
            {
                Column? fromCol = fromTable.Columns.FirstOrDefault(c => c.Name.Equals(fromColName, StringComparison.OrdinalIgnoreCase));
                Column? toCol = toTable.Columns.FirstOrDefault(c => c.Name.Equals(toColName, StringComparison.OrdinalIgnoreCase));
                if (fromCol != null && toCol != null)
                {
                    rel.FromColumns[colIdx] = fromCol;
                    rel.ToColumns[colIdx] = toCol;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Gets the given system table (e.g. "MSysQueries", "MSysRelationships"), or null if it does not exist.
    /// </summary>
    public Table? GetSystemTable(string name)
    {
        foreach (TableMetaData info in _tableInfos)
        {
            if (!info.IsSystem)
            {
                continue;
            }
            if (string.Equals(info.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return LoadTable(info.Name, info.PageNumber, info.Flags, info.Type);
            }
        }
        return null;
    }

    /// <summary>
    /// Enumerates the saved queries (querydefs) of the database, read from MSysObjects + MSysQueries.
    /// </summary>
    public IReadOnlyList<QueryDef> GetQueries()
    {
        var queryInfo = new List<(string? Name, int Id, int Flags)>();
        foreach (Row row in _systemCatalog.Rows())
        {
            if (TryGetString(row, "Name", out string? name)
                && GetTypeValue(row, "Type") == TypeQuery
                && TryGetInt(row, "Id", out int id))
            {
                queryInfo.Add((name, id, GetInt(row, "Flags")));
            }
        }
        if (queryInfo.Count == 0)
        {
            return Array.Empty<QueryDef>();
        }

        Table? queriesTable = GetSystemTable("MSysQueries");
        if (queriesTable == null)
        {
            return Array.Empty<QueryDef>();
        }

        var queryRows = new Dictionary<int, List<QueryRow>>();
        foreach (Row row in queriesTable.Rows())
        {
            if (!TryGetInt(row, "ObjectId", out int objectId))
            {
                continue;
            }
            if (!queryRows.TryGetValue(objectId, out var list))
            {
                list = new List<QueryRow>();
                queryRows[objectId] = list;
            }
            list.Add(new QueryRow
            {
                Attribute = GetTypeValue(row, "Attribute"),
                Expression = GetString(row, "Expression"),
                Flag = GetShortValue(row, "Flag"),
                Extra = GetInt(row, "LvExtra"),
                Name1 = GetString(row, "Name1"),
                Name2 = GetString(row, "Name2"),
                ObjectId = objectId,
                Order = row.TryGetValue("Order", out object? o) && o is byte[] bytes ? bytes : null,
            });
        }

        var result = new List<QueryDef>(queryInfo.Count);
        foreach (var (name, id, flags) in queryInfo)
        {
            if (queryRows.TryGetValue(id, out var rows))
            {
                result.Add(new QueryDef(name!, id, flags, rows));
            }
            else
            {
                result.Add(new QueryDef(name!, id, flags, Array.Empty<QueryRow>()));
            }
        }
        return result;
    }

    private static short GetShortValue(Row row, string name)
    {
        if (row.TryGetValue(name, out object? v))
        {
            return v switch
            {
                byte b => b,
                short s => s,
                int i => (short)i,
                _ => (short)0,
            };
        }
        return 0;
    }

    private static bool TryGetInt(Row row, string name, out int value)
    {
        if (row.TryGetValue(name, out object? v) && v is int i)
        {
            value = i;
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>
    /// Whether the given catalog object type is a table type (user table, linked table or linked ODBC table).
    /// </summary>
    private static bool IsTableType(byte type)
        => type == TypeTable || type == TypeLinkedOdbcTable || type == TypeLinkedTable;

    private static int GetInt(Row row, string name)
        => row.TryGetValue(name, out object? v) && v is int i ? i : 0;

    private static byte GetTypeValue(Row row, string name)
    {
        if (row.TryGetValue(name, out object? v))
        {
            return v switch
            {
                byte b => b,
                short s => (byte)s,
                int i => (byte)i,
                _ => (byte)0,
            };
        }
        return (byte)0;
    }

    private static bool TryGetString(Row row, string name, out string? value)
    {
        if (row.TryGetValue(name, out object? v) && v is string s)
        {
            value = s;
            return true;
        }
        value = null;
        return false;
    }

    private static string? GetString(Row row, string name)
        => row.TryGetValue(name, out object? v) ? v as string : null;

    private static short ReadShort(byte[] buffer, int offset)
        => (short)(buffer[offset] | (buffer[offset + 1] << 8));

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            foreach (Database linked in _linkedDatabaseByPath.Values)
            {
                linked.Dispose();
            }
            _linkedDatabaseByPath.Clear();
            _pageChannel.Dispose();
            ReleaseLock();
        }
    }

    // ------------------------------------------------------------------
    // file locking (.ldb / .laccdb), like MS Access and UCanAccess
    // ------------------------------------------------------------------

    /// <summary>
    /// Creates the database lock file (".ldb" for MDB, ".laccdb" for ACCDB) and holds it
    /// open while the database is writable, preventing another process from writing to
    /// the same file concurrently (as MS Access / UCanAccess do).
    /// </summary>
    private void AcquireLock()
    {
        if (_path == null || _path.Length == 0)
        {
            return;
        }
        bool isAccdb = _format.Name is "VERSION_12" or "VERSION_14" or "VERSION_16";
        string lockExt = isAccdb ? ".laccdb" : ".ldb";
        string lockPath = System.IO.Path.ChangeExtension(_path, lockExt);
        try
        {
            _lockStream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            _lockPath = lockPath;
        }
        catch (IOException)
        {
            throw new DatabaseException(
                $"The database '{_path}' is already open by another process (lock file '{lockPath}' exists).");
        }
    }

    private void ReleaseLock()
    {
        if (_lockStream != null)
        {
            try
            {
                _lockStream.Dispose();
            }
            catch
            {
                // best effort
            }
            _lockStream = null;
        }
        if (_lockPath != null)
        {
            try
            {
                System.IO.File.Delete(_lockPath);
            }
            catch
            {
                // best effort (another process may have taken over)
            }
            _lockPath = null;
        }
    }
}
