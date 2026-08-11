using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using UCanAccess.File;

namespace UCanAccess;

/// <summary>
/// ADO.NET connection for MS Access databases.
/// </summary>
public sealed class UCanAccessConnection : DbConnection
{
    private readonly List<RegisteredFunction> _customFunctions = new();
    private UCanAccessConnectionString? _connStr;
    private File.Database? _database;
    private Mirror? _mirror;
    private ConnectionState _state = ConnectionState.Closed;
    private int _openCount;
    private IAccessDatabaseOpener? _databaseOpener;
    private long _sourceLength = -1;
    private long _sourceWriteTicks;

    /// <summary>
    /// Optional per-connection opener for password-protected/encrypted files.
    /// The default provider path opens unencrypted Access files directly.
    /// </summary>
    public IAccessDatabaseOpener? DatabaseOpener
    {
        get => _databaseOpener;
        set
        {
            if (_state != ConnectionState.Closed)
            {
                throw new InvalidOperationException("DatabaseOpener cannot be changed while the connection is open.");
            }
            _databaseOpener = value;
        }
    }

    public UCanAccessConnection()
    {
    }

    public UCanAccessConnection(string connectionString)
    {
        ConnectionString = connectionString;
    }

    /// <summary>
    /// Registers a connection-local scalar function that can be called from
    /// Access SQL. Register functions before opening the connection.
    /// </summary>
    /// <param name="name">SQL function name.</param>
    /// <param name="arity">Number of arguments, or -1 for a variable number.</param>
    /// <param name="function">Implementation receiving arguments in SQL order.</param>
    /// <param name="deterministic">Whether the result depends only on its arguments.</param>
    public void RegisterFunction(string name, int arity, Func<IReadOnlyList<object?>, object?> function,
        bool deterministic = false)
    {
        if (_state != ConnectionState.Closed)
        {
            throw new InvalidOperationException("Register functions before opening the connection.");
        }
        ArgumentNullException.ThrowIfNull(function);
        // Validate through the same public helper used when a mirror is built.
        // A short-lived SQLite connection is unnecessary: the common validation
        // is repeated by AccessFunctions when the registration is applied.
        if (arity < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(arity), arity, "Arity must be -1 or greater.");
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Function name is required.", nameof(name));
        }
        _customFunctions.Add(new RegisteredFunction(name, arity, function, deterministic));
    }

    [AllowNull]
    public override string ConnectionString
    {
        get => _connStr?.ToString() ?? string.Empty;
        set
        {
            if (_state != ConnectionState.Closed)
            {
                throw new InvalidOperationException("Cannot change ConnectionString while the connection is open.");
            }
            _connStr = value == null ? null : new UCanAccessConnectionString(value);
        }
    }

    /// <summary>The underlying file database (only valid while open).</summary>
    public File.Database AccessDatabase
        => _database ?? throw new InvalidOperationException("The connection is not open.");

    /// <summary>
    /// The SQLite mirror (created on first use). SQL queries run against the mirror.
    /// </summary>
    public Mirror Mirror
    {
        get
        {
            if (_state != ConnectionState.Open)
            {
                throw new InvalidOperationException("The connection is not open.");
            }
            EnsureDatabaseCurrent();
            if (_mirror == null)
            {
                _mirror = CreateMirrorFor(_database!);
            }
            return _mirror;
        }
    }

    internal Mirror CreateMirrorFor(File.Database database, bool useConfiguredStorage = true)
    {
        bool displayOrder = string.Equals(_connStr?.ColumnOrder, "display", StringComparison.OrdinalIgnoreCase);
        string? mirrorPath = useConfiguredStorage ? ResolveMirrorPath(database) : null;
        var mirror = new Mirror(database, _connStr?.ShowSchema ?? false, displayOrder,
            buildSavedQueries: false, storagePath: mirrorPath,
            deleteStorageOnDispose: mirrorPath != null && _connStr?.MirrorPath == null);
        AccessFunctions.Register(mirror.Connection, mirror.DomainConnection,
            mirror.IsMoneyColumn, mirror.IsExactDecimalColumn, mirror.IsDateColumn);
        foreach (RegisteredFunction registration in _customFunctions)
        {
            AccessFunctions.RegisterFunction(mirror.Connection, registration.Name, registration.Arity,
                registration.Function, registration.Deterministic);
            AccessFunctions.RegisterFunction(mirror.DomainConnection, registration.Name, registration.Arity,
                registration.Function, registration.Deterministic);
        }
        mirror.BuildSavedQueryViews();
        return mirror;
    }

    private string? ResolveMirrorPath(File.Database database)
    {
        string mode = _connStr?.MirrorMode ?? "memory";
        if (!mode.Equals("memory", StringComparison.OrdinalIgnoreCase)
            && !mode.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid Mirror Mode '{mode}'. Expected 'memory' or 'file'.");
        }
        if (mode.Equals("memory", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? sourcePath = database.Path;
        string path = _connStr?.MirrorPath
            ?? System.IO.Path.Combine(
                _connStr?.MirrorFolder
                    ?? (sourcePath == null
                        ? System.IO.Path.GetTempPath()
                        : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(sourcePath))
                            ?? System.IO.Path.GetTempPath()),
                System.IO.Path.GetFileNameWithoutExtension(sourcePath ?? "ucanaccess")
                    + "-ucanaccess-mirror-" + Guid.NewGuid().ToString("N") + ".sqlite");
        string fullPath = System.IO.Path.GetFullPath(path);
        if (sourcePath != null
            && string.Equals(fullPath, System.IO.Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Mirror Path must not be the Access database path.");
        }
        return fullPath;
    }

    private sealed record RegisteredFunction(string Name, int Arity,
        Func<IReadOnlyList<object?>, object?> Function, bool Deterministic);

    internal bool KeepMirror => _connStr?.KeepMirror ?? true;

    internal File.Database OpenDatabaseFile(string path, bool readOnly)
    {
        string? password = _connStr?.Password;
        if (password is { Length: > 0 } && _databaseOpener == null)
        {
            throw new NotSupportedException(
                "Password-protected/encrypted Access files require an IAccessDatabaseOpener codec adapter.");
        }
        if (_databaseOpener != null)
        {
            return _databaseOpener.Open(new AccessDatabaseOpenRequest(path, readOnly,
                _connStr?.ResolveEncoding(), _connStr?.AllowExternalLinks ?? false, password));
        }
        return File.Database.Open(path, _connStr?.ResolveEncoding(), readOnly,
            _connStr?.AllowExternalLinks ?? false);
    }

    public override string Database => _connStr?.DataSource ?? string.Empty;

    public override string DataSource => _connStr?.DataSource ?? string.Empty;

    /// <summary>
    /// The numeric Access AutoNumber generated by the most recent successful
    /// INSERT on this connection. It is null when the statement did not insert
    /// a numeric AutoNumber or when no INSERT has succeeded yet.
    /// </summary>
    public long? LastInsertedId { get; private set; }

    internal void ClearLastInsertedId() => LastInsertedId = null;

    internal void MarkDatabaseCurrent() => CaptureSourceFingerprint(AccessDatabase);

    internal void ThrowIfActiveReaders()
    {
        _mirror?.ThrowIfActiveReaders();
    }

    public override string ServerVersion
    {
        get
        {
            Version? v = typeof(UCanAccessConnection).Assembly.GetName().Version;
            string label = v == null ? "1.0" : $"{v.Major}.{v.Minor}";
            return $"UCanAccess-csharp {label} (Jet 3/4/12/14/16 read/write)";
        }
    }

    public override ConnectionState State => _state;

    /// <summary>the mirror if it has already been created, otherwise null</summary>
    internal Mirror? MirrorIfCreated => _mirror;

    internal void EnsureDatabaseCurrent()
    {
        if (_state != ConnectionState.Open || ActiveTransaction != null || _database == null)
        {
            return;
        }
        // Linked databases own their own file handles and can legitimately
        // update a different file. Reopening the main handle opportunistically
        // here would leave a second link resolver alive on Windows; linked-file
        // freshness is handled by the explicit mirror refresh path instead.
        if (_database.GetTableMetaData().Any(meta => meta.IsLinked))
        {
            return;
        }
        string path = _database.Path;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            return;
        }

        DateTime writeTime = System.IO.File.GetLastWriteTimeUtc(path);
        long length = new System.IO.FileInfo(path).Length;
        if (writeTime.Ticks == _sourceWriteTicks && length == _sourceLength)
        {
            return;
        }
        if (_mirror?.HasActiveReaders == true)
        {
            throw new InvalidOperationException(
                "The Access file changed while a mirror reader was active; close the reader before reloading.");
        }

        _mirror?.Dispose();
        _mirror = null;
        _database.Dispose();
        _database = OpenDatabaseFile(path, _connStr?.ReadOnly ?? true);
        CaptureSourceFingerprint(_database);
    }

    /// <summary>
    /// Diagnostics collected while saved Access queries are materialized in the
    /// mirror. The collection is empty until the mirror has been created.
    /// </summary>
    public IReadOnlyList<string> MirrorDiagnostics
        => _mirror?.Diagnostics ?? Array.Empty<string>();

    protected override DbProviderFactory DbProviderFactory => UCanAccessFactory.Instance;

    public override void ChangeDatabase(string databaseName)
        => throw new NotSupportedException();

    public override void Open()
    {
        if (_connStr == null)
        {
            throw new InvalidOperationException("ConnectionString is not set.");
        }
        if (string.IsNullOrEmpty(_connStr.DataSource))
        {
            throw new InvalidOperationException("A Data Source (database file path) is required.");
        }
        if (_state == ConnectionState.Open)
        {
            _openCount++;
            return;
        }
        string columnOrder = _connStr.ColumnOrder.Trim();
        if (!columnOrder.Equals("natural", StringComparison.OrdinalIgnoreCase)
            && !columnOrder.Equals("display", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Invalid Column Order '{_connStr.ColumnOrder}'. Expected 'natural' or 'display'.");
        }
        string mirrorMode = _connStr.MirrorMode.Trim();
        if (!mirrorMode.Equals("memory", StringComparison.OrdinalIgnoreCase)
            && !mirrorMode.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Invalid Mirror Mode '{_connStr.MirrorMode}'. Expected 'memory' or 'file'.");
        }

        try
        {
            if (!_connStr.ReadOnly && !System.IO.File.Exists(_connStr.DataSource)
                && _connStr.NewDatabaseVersion != null)
            {
                // create a fresh database of the requested version when the target is missing
                _database = File.Database.Create(_connStr.DataSource, _connStr.ResolveEncoding(), _connStr.NewDatabaseVersion,
                    _connStr.AllowExternalLinks);
            }
            else
            {
                _database = OpenDatabaseFile(_connStr.DataSource, _connStr.ReadOnly);
            }
            _state = ConnectionState.Open;
            _openCount = 1;
            CaptureSourceFingerprint(_database);
            if (!_connStr.LazyLoad && _connStr.KeepMirror)
            {
                _mirror = CreateMirrorFor(_database);
            }
        }
        catch
        {
            _mirror?.Dispose();
            _mirror = null;
            _database?.Dispose();
            _database = null;
            _sourceLength = -1;
            _sourceWriteTicks = 0;
            _state = ConnectionState.Closed;
            _openCount = 0;
            throw;
        }
    }

    public override void Close()
    {
        if (_state == ConnectionState.Open && --_openCount <= 0)
        {
            ActiveTransaction?.Rollback();
            _mirror?.Dispose();
            _mirror = null;
            _database?.Dispose();
            _database = null;
            _sourceLength = -1;
            _sourceWriteTicks = 0;
            _state = ConnectionState.Closed;
            _openCount = 0;
        }
    }

    protected override DbCommand CreateDbCommand()
        => new UCanAccessCommand(this);

    /// <summary>the active transaction, if any</summary>
    internal UCanAccessTransaction? ActiveTransaction { get; private set; }

    internal void ClearTransaction(UCanAccessTransaction transaction)
    {
        if (ReferenceEquals(ActiveTransaction, transaction))
        {
            ActiveTransaction = null;
        }
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        if (_state != ConnectionState.Open)
        {
            throw new InvalidOperationException("The connection is not open.");
        }
        EnsureDatabaseCurrent();
        if (AccessDatabase.IsReadOnly)
        {
            throw new InvalidOperationException("Cannot begin a write transaction on a read-only database.");
        }
        if (ActiveTransaction != null)
        {
            throw new InvalidOperationException("A transaction is already active on this connection.");
        }
        var transaction = new UCanAccessTransaction(this, isolationLevel);
        ActiveTransaction = transaction;
        return transaction;
    }

    /// <summary>
    /// Atomically installs a fully prepared database copy and reopens the connection.
    /// The caller must have already applied and validated all pending changes to the
    /// prepared file.
    /// </summary>
    internal void ReplaceDatabaseFile(string preparedPath)
    {
        string sourcePath = AccessDatabase.Path;
        if (string.IsNullOrEmpty(sourcePath))
        {
            throw new InvalidOperationException("The connection has no file-backed database.");
        }

        _mirror?.Dispose();
        _mirror = null;
        _database?.Dispose();
        _database = null;
        _state = ConnectionState.Closed;
        _openCount = 0;

        string backupPath = sourcePath + ".ucanaccess-backup-" + Guid.NewGuid().ToString("N");
        try
        {
            System.IO.File.Replace(preparedPath, sourcePath, backupPath, ignoreMetadataErrors: true);
            try
            {
                System.IO.File.Delete(backupPath);
            }
            catch
            {
                // A backup is recoverable and does not affect the newly installed file.
            }
        }
        catch
        {
            // Reopen the original file so the connection remains usable after a failed
            // replacement.  The prepared file is deliberately left for diagnostics.
            Open();
            throw;
        }

        Open();
    }

    private void CaptureSourceFingerprint(File.Database database)
    {
        string path = database.Path;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            _sourceLength = -1;
            _sourceWriteTicks = 0;
            return;
        }
        _sourceLength = new System.IO.FileInfo(path).Length;
        _sourceWriteTicks = System.IO.File.GetLastWriteTimeUtc(path).Ticks;
    }

    internal (long Length, long WriteTicks) GetSourceFingerprint()
    {
        string path = AccessDatabase.Path;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            return (-1, 0);
        }

        FileInfo info = new(path);
        return (info.Length, info.LastWriteTimeUtc.Ticks);
    }

    internal void EnsureSourceFingerprint(long expectedLength, long expectedWriteTicks)
    {
        (long length, long writeTicks) = GetSourceFingerprint();
        if (length != expectedLength || writeTicks != expectedWriteTicks)
        {
            throw new IOException(
                "The Access file changed while the atomic operation was being prepared.");
        }
    }

    /// <summary>
    /// Executes one autocommit DML statement on a private database copy and
    /// installs that copy only after the complete statement has succeeded.
    /// Native linked tables remain on the direct path because their target file
    /// is external to the staged database.
    /// </summary>
    internal int ExecuteDmlAtomically(string sql, IReadOnlyList<object?>? parameters)
        => ExecuteDmlBatchAtomically(new[] { (sql, parameters) });

    internal int ExecuteDmlBatchAtomically(
        IReadOnlyList<(string Sql, IReadOnlyList<object?>? Parameters)> statements)
    {
        if (statements.Count == 0)
        {
            return 0;
        }
        if (AccessDatabase.IsReadOnly)
        {
            throw new DatabaseException("The database was opened read-only.");
        }
        ThrowIfActiveReaders();
        LastInsertedId = null;
        string sourcePath = AccessDatabase.Path;
        if (string.IsNullOrEmpty(sourcePath) || !System.IO.File.Exists(sourcePath))
        {
            throw new InvalidOperationException("Atomic DML requires a file-backed database.");
        }
        if (AccessDatabase.GetTableMetaData().Any(meta => meta.IsLinked))
        {
            int directTotal = 0;
            foreach ((string sql, IReadOnlyList<object?>? parameters) in statements)
            {
                directTotal += ExecuteDmlDirect(sql, parameters);
            }
            return directTotal;
        }

        string directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(sourcePath))
            ?? throw new InvalidOperationException("Could not determine the database directory.");
        string stagedPath = System.IO.Path.Combine(directory,
            "." + System.IO.Path.GetFileNameWithoutExtension(sourcePath)
            + ".ucanaccess-dml-" + Guid.NewGuid().ToString("N")
            + System.IO.Path.GetExtension(sourcePath));
        FileInfo sourceInfo = new(sourcePath);
        long originalLength = sourceInfo.Length;
        long originalWriteTicks = sourceInfo.LastWriteTimeUtc.Ticks;
        File.Database? stagedDatabase = null;
        Mirror? stagedMirror = null;
        Table? insertedTable = null;
        object?[]? insertedValues = null;
        try
        {
            System.IO.File.Copy(sourcePath, stagedPath, true);
            stagedDatabase = OpenDatabaseFile(stagedPath, readOnly: false);
            stagedMirror = CreateMirrorFor(stagedDatabase, useConfiguredStorage: false);
            int affected = 0;
            foreach ((string sql, IReadOnlyList<object?>? parameters) in statements)
            {
                affected += AccessDml.Execute(stagedDatabase, stagedMirror, sql, parameters,
                    onInsertedRow: (table, values) =>
                    {
                        insertedTable = table;
                        insertedValues = values;
                    });
            }
            stagedMirror.Dispose();
            stagedMirror = null;
            stagedDatabase.Dispose();
            stagedDatabase = null;
            EnsureSourceFingerprint(originalLength, originalWriteTicks);
            ReplaceDatabaseFile(stagedPath);
            stagedPath = string.Empty;
            SetLastInsertedId(insertedTable, insertedValues);
            return affected;
        }
        finally
        {
            stagedMirror?.Dispose();
            stagedDatabase?.Dispose();
            if (stagedPath.Length > 0)
            {
                try
                {
                    System.IO.File.Delete(stagedPath);
                }
                catch
                {
                    // Leave the staged file available for recovery diagnostics.
                }
            }
        }
    }

    private int ExecuteDmlDirect(string sql, IReadOnlyList<object?>? parameters)
    {
        LastInsertedId = null;
        Mirror? transientMirror = null;
        Table? insertedTable = null;
        object?[]? insertedValues = null;
        try
        {
            Mirror mirror = KeepMirror
                ? Mirror
                : transientMirror = CreateMirrorFor(AccessDatabase);
            int affected = AccessDml.Execute(AccessDatabase, mirror, sql, parameters,
                onInsertedRow: (table, values) =>
                {
                    insertedTable = table;
                    insertedValues = values;
                });
            SetLastInsertedId(insertedTable, insertedValues);
            CaptureSourceFingerprint(AccessDatabase);
            return affected;
        }
        finally
        {
            transientMirror?.Dispose();
        }
    }

    internal void SetLastInsertedId(Table? table, object?[]? values)
    {
        Column? autoNumber = table?.Columns.FirstOrDefault(column => column.AutoNumber);
        if (autoNumber == null || values == null || autoNumber.ColumnIndex >= values.Length)
        {
            LastInsertedId = null;
            return;
        }
        LastInsertedId = long.TryParse(
            Convert.ToString(values[autoNumber.ColumnIndex], System.Globalization.CultureInfo.InvariantCulture),
            out long id) ? id : null;
    }

    /// <summary>
    /// Executes CREATE/DROP INDEX on a same-directory staging copy and installs the
    /// result only after the file has been closed cleanly.  This keeps a failed index
    /// validation or B-tree build from changing the caller's original file.
    /// </summary>
    internal int ExecuteIndexDdlAtomically(string sql)
    {
        ThrowIfActiveReaders();
        string sourcePath = AccessDatabase.Path;
        if (string.IsNullOrEmpty(sourcePath) || !System.IO.File.Exists(sourcePath))
        {
            throw new InvalidOperationException("Index DDL requires a file-backed database.");
        }
        if (AccessDatabase.GetTableMetaData().Any(meta => meta.IsLinked))
        {
            throw new NotSupportedException(
                "Index DDL involving native linked tables is not supported atomically.");
        }

        string directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(sourcePath))
            ?? throw new InvalidOperationException("Could not determine the database directory.");
        string extension = System.IO.Path.GetExtension(sourcePath);
        string stagedPath = System.IO.Path.Combine(directory,
            "." + System.IO.Path.GetFileNameWithoutExtension(sourcePath)
            + ".ucanaccess-index-" + Guid.NewGuid().ToString("N") + extension);

        FileInfo sourceInfo = new(sourcePath);
        long originalLength = sourceInfo.Length;
        long originalWriteTicks = sourceInfo.LastWriteTimeUtc.Ticks;
        System.IO.File.Copy(sourcePath, stagedPath, true);
        File.Database? stagedDatabase = null;
        Mirror? stagedMirror = null;
        try
        {
            stagedDatabase = OpenDatabaseFile(stagedPath, readOnly: false);
            stagedMirror = CreateMirrorFor(stagedDatabase, useConfiguredStorage: false);
            int result = AccessDdl.Execute(stagedDatabase, stagedMirror, sql);
            stagedMirror.Dispose();
            stagedMirror = null;
            stagedDatabase.Dispose();
            stagedDatabase = null;

            EnsureSourceFingerprint(originalLength, originalWriteTicks);
            ReplaceDatabaseFile(stagedPath);
            stagedPath = string.Empty;
            return result;
        }
        finally
        {
            stagedMirror?.Dispose();
            stagedDatabase?.Dispose();
            if (stagedPath.Length > 0)
            {
                try
                {
                    System.IO.File.Delete(stagedPath);
                }
                catch
                {
                    // Preserve the original error; leave the staging file for recovery.
                }
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
        }
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------
    // metadata (GetSchema)
    // ------------------------------------------------------------------

    public override System.Data.DataTable GetSchema()
        => GetSchema("MetaDataCollections", null);

    public override System.Data.DataTable GetSchema(string collectionName)
        => GetSchema(collectionName, null);

    public override System.Data.DataTable GetSchema(string collectionName, string?[]? restrictionValues)
    {
        if (_state != ConnectionState.Open)
        {
            throw new InvalidOperationException("The connection is not open.");
        }
        EnsureDatabaseCurrent();
        if (string.IsNullOrEmpty(collectionName))
        {
            throw new ArgumentException("collectionName is required", nameof(collectionName));
        }

        return collectionName.ToUpperInvariant() switch
        {
            "METADATACOLLECTIONS" => GetMetaDataCollections(),
            "TABLES" => GetTablesSchema(restrictionValues),
            "COLUMNS" or "COLUMN" => GetColumnsSchema(restrictionValues),
            "INDEXES" => GetIndexesSchema(restrictionValues),
            "INDEXCOLUMNS" => GetIndexColumnsSchema(restrictionValues),
            "PRIMARYKEYS" => GetPrimaryKeysSchema(restrictionValues),
            "FOREIGNKEYS" => GetForeignKeysSchema(restrictionValues),
            "VIEWS" => GetViewsSchema(restrictionValues),
            _ => throw new ArgumentException($"Unsupported metadata collection '{collectionName}'."),
        };
    }

    private static System.Data.DataTable NewTable(params string[] columns)
    {
        var table = new System.Data.DataTable();
        foreach (string column in columns)
        {
            // untyped (object) columns preserve the actual CLR value type
            table.Columns.Add(column, typeof(object));
        }
        return table;
    }

    private static void AddRow(System.Data.DataTable table, params object?[] values)
    {
        object?[] row = new object?[table.Columns.Count];
        Array.Copy(values, row, Math.Min(values.Length, row.Length));
        table.Rows.Add(row);
    }

    private System.Data.DataTable GetMetaDataCollections()
    {
        var table = NewTable("CollectionName", "NumberOfRestrictions", "NumberOfIdentifierParts");
        foreach ((string name, int restrictions) in new[]
        {
            ("MetaDataCollections", 0),
            ("Tables", 4),
            ("Columns", 4),
            ("Indexes", 4),
            ("IndexColumns", 5),
            ("PrimaryKeys", 3),
            ("ForeignKeys", 3),
            ("Views", 3),
        })
        {
            AddRow(table, name, restrictions, 0);
        }
        return table;
    }

    private System.Data.DataTable GetTablesSchema(string?[]? restrictions)
    {
        var table = NewTable(
            "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "TABLE_TYPE",
            "TABLE_GUID", "DESCRIPTION", "TABLE_PROPID", "DATE_CREATED", "DATE_MODIFIED");
        foreach (TableMetaData meta in _database!.GetTableMetaData())
        {
            if (Mismatches(restrictions, 2, meta.Name))
            {
                continue;
            }
            if (meta.IsSystem && !(_connStr?.ShowSchema ?? false))
            {
                continue;
            }
            string type = meta.IsLinked ? "LINKED TABLE" : meta.IsSystem ? "SYSTEM TABLE" : "TABLE";
            AddRow(table, Database, null, meta.Name, type, null, null, null, null, null);
        }
        foreach (QueryDef query in _database.GetQueries())
        {
            if (Mismatches(restrictions, 2, query.Name))
            {
                continue;
            }
            AddRow(table, Database, null, query.Name, "VIEW", null, null, null, null, null);
        }
        return table;
    }

    private static bool Mismatches(string?[]? restrictions, int index, string name)
    {
        if (restrictions == null || index >= restrictions.Length || restrictions[index] == null)
        {
            return false;
        }
        return !string.Equals(restrictions[index], name, StringComparison.OrdinalIgnoreCase);
    }

    private System.Data.DataTable GetColumnsSchema(string?[]? restrictions)
    {
        var table = NewTable(
            "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "COLUMN_NAME",
            "ORDINAL_POSITION", "COLUMN_DEFAULT", "IS_NULLABLE", "DATA_TYPE",
            "CHARACTER_MAXIMUM_LENGTH", "NUMERIC_PRECISION", "NUMERIC_SCALE",
            "COLUMN_FLAGS", "IS_IDENTITY", "IS_LONG", "DATA_TYPE_NAME");

        foreach (TableMetaData meta in _database!.GetTableMetaData())
        {
            if (meta.IsSystem || meta.IsLinked)
            {
                continue;
            }
            if (restrictions != null && restrictions.Length > 2 && restrictions[2] != null
                && !string.Equals(restrictions[2], meta.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Table? tableDef = _database.GetTable(meta.Name);
            if (tableDef == null)
            {
                continue;
            }
            IReadOnlyList<IndexInfo> indexInfo = _database.GetIndexInfo(meta.Name);
            int ordinal = 1;
            foreach (Column column in tableDef.Columns)
            {
                if (restrictions != null && restrictions.Length > 3 && restrictions[3] != null
                    && !string.Equals(restrictions[3], column.Name, StringComparison.OrdinalIgnoreCase))
                {
                    ordinal++;
                    continue;
                }
                string clrType = column.Type switch
                {
                    DataType.Boolean => "System.Boolean",
                    DataType.Byte => "System.Byte",
                    DataType.Int => "System.Int16",
                    DataType.Long => "System.Int32",
                    DataType.BigInt => "System.Int64",
                    DataType.Money or DataType.Numeric => "System.Decimal",
                    DataType.Float => "System.Single",
                    DataType.Double => "System.Double",
                    DataType.ShortDateTime or DataType.ExtDateTime => "System.DateTime",
                    DataType.Guid => "System.Guid",
                    DataType.Binary or DataType.Ole => "System.Byte[]",
                    _ => "System.String",
                };
                bool required = column.Required || indexInfo.Any(index => (index.PrimaryKey || index.Required)
                    && index.Columns.Any(indexColumn => indexColumn.Name.Equals(column.Name, StringComparison.OrdinalIgnoreCase)));
                int? characterLength = column.Type is DataType.Text or DataType.Memo
                    ? column.ColumnLength / _database.Format.SizeTextFieldUnit
                    : column.VariableLength ? column.ColumnLength : null;
                AddRow(table, Database, null, meta.Name, column.Name, ordinal, null,
                    required ? "NO" : "YES", clrType,
                    characterLength,
                    column.Type is DataType.Numeric ? column.Precision : null,
                    column.Type is DataType.Numeric ? column.Scale : null,
                    null, column.AutoNumber, column.Type is DataType.Memo or DataType.Ole, column.Type.ToString());
                ordinal++;
            }
        }
        return table;
    }

    private System.Data.DataTable GetIndexesSchema(string?[]? restrictions)
    {
        var table = NewTable(
            "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "INDEX_CATALOG", "INDEX_SCHEMA",
            "INDEX_NAME", "PRIMARY_KEY", "UNIQUE", "CLUSTERED", "TYPE", "ORDINAL_POSITION",
            "COLUMN_NAME", "COLLATION", "CARDINALITY", "PAGES", "FILTER_CONDITION", "NULLS");
        foreach (TableMetaData meta in _database!.GetTableMetaData())
        {
            if (meta.IsSystem || meta.IsLinked)
            {
                continue;
            }
            if (restrictions != null && restrictions.Length > 2 && restrictions[2] != null
                && !string.Equals(restrictions[2], meta.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (IndexInfo index in _database.GetIndexInfo(meta.Name))
            {
                if (restrictions != null && restrictions.Length > 5 && restrictions[5] != null
                    && !string.Equals(restrictions[5], index.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                int pos = 1;
                foreach (IndexColumnInfo column in index.Columns)
                {
                    AddRow(table, Database, null, meta.Name, null, null, index.Name,
                        index.PrimaryKey, index.Unique, false, 3, pos, column.Name,
                        column.Ascending ? "A" : "D", null, null, null, null);
                    pos++;
                }
            }
        }
        return table;
    }

    private System.Data.DataTable GetIndexColumnsSchema(string?[]? restrictions)
    {
        var table = NewTable(
            "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "INDEX_NAME", "COLUMN_NAME",
            "ORDINAL_POSITION", "SORT_ORDER");
        foreach (TableMetaData meta in _database!.GetTableMetaData())
        {
            if (meta.IsSystem || meta.IsLinked)
            {
                continue;
            }
            if (restrictions != null && restrictions.Length > 2 && restrictions[2] != null
                && !string.Equals(restrictions[2], meta.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (IndexInfo index in _database.GetIndexInfo(meta.Name))
            {
                if (restrictions != null && restrictions.Length > 3 && restrictions[3] != null
                    && !string.Equals(restrictions[3], index.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                int pos = 1;
                foreach (IndexColumnInfo column in index.Columns)
                {
                    if (restrictions != null && restrictions.Length > 4 && restrictions[4] != null
                        && !string.Equals(restrictions[4], column.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        pos++;
                        continue;
                    }
                    AddRow(table, Database, null, meta.Name, index.Name, column.Name, pos,
                        column.Ascending ? "ASC" : "DESC");
                    pos++;
                }
            }
        }
        return table;
    }

    private System.Data.DataTable GetPrimaryKeysSchema(string?[]? restrictions)
    {
        var table = NewTable(
            "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "COLUMN_NAME",
            "COLUMN_GUID", "COLUMN_PROPID", "ORDINAL", "PK_NAME");
        foreach (TableMetaData meta in _database!.GetTableMetaData())
        {
            if (meta.IsSystem || meta.IsLinked)
            {
                continue;
            }
            if (restrictions != null && restrictions.Length > 2 && restrictions[2] != null
                && !string.Equals(restrictions[2], meta.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (IndexInfo index in _database.GetIndexInfo(meta.Name))
            {
                if (!index.PrimaryKey)
                {
                    continue;
                }
                int pos = 1;
                foreach (IndexColumnInfo column in index.Columns)
                {
                    AddRow(table, Database, null, meta.Name, column.Name, null, null, pos, index.Name);
                    pos++;
                }
            }
        }
        return table;
    }

    private System.Data.DataTable GetForeignKeysSchema(string?[]? restrictions)
    {
        var table = NewTable(
            "CONSTRAINT_CATALOG", "CONSTRAINT_SCHEMA", "CONSTRAINT_NAME",
            "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME",
            "CONSTRAINT_TYPE", "IS_DEFERRABLE", "INITIALLY_DEFERRED");
        foreach (Relationship relationship in _database!.GetRelationships())
        {
            if (restrictions != null && restrictions.Length > 2 && restrictions[2] != null
                && !string.Equals(restrictions[2], relationship.FromTable.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (restrictions != null && restrictions.Length > 5 && restrictions[5] != null
                && !string.Equals(restrictions[5], relationship.ToTable.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            AddRow(table, Database, null, relationship.Name,
                Database, null, relationship.ToTable.Name,
                "FOREIGN KEY", "NO", "NO");
        }
        return table;
    }

    private System.Data.DataTable GetViewsSchema(string?[]? restrictions)
    {
        var table = NewTable(
            "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "VIEW_DEFINITION",
            "CHECK_OPTION", "IS_UPDATABLE");
        foreach (QueryDef query in _database!.GetQueries())
        {
            if (query.Type != QueryType.Select || query.Sql == null)
            {
                continue;
            }
            if (restrictions != null && restrictions.Length > 2 && restrictions[2] != null
                && !string.Equals(restrictions[2], query.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            AddRow(table, Database, null, query.Name, query.Sql, "NONE", "NO");
        }
        return table;
    }
}
