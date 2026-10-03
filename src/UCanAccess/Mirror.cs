using System.Collections.Concurrent;
using System.Globalization;
using System.Data;
using Microsoft.Data.Sqlite;
using UCanAccess.File;

namespace UCanAccess;

/// <summary>
/// Helpers for quoting SQLite identifiers.
/// </summary>
internal static class SqlNames
{
    public static string Quote(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}

/// <summary>
/// Mirrors an MS Access database (schema + data) into an in-memory SQLite database.
/// SQLite acts as the query engine and is refreshed after writes to the Access file.
/// </summary>
public sealed class Mirror : IDisposable
{
    private readonly File.Database _accessDb;
    private readonly SqliteConnection _connection;
    private readonly bool _includeSystem;
    private readonly bool _displayOrder;
    private readonly string? _storagePath;
    private readonly bool _deleteStorageOnDispose;
    private readonly HashSet<SqliteCommand> _commands = new();
    private readonly Dictionary<string, string> _tableNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DataType> _columnTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<long, Table.RowLocation>> _rowLocators = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _booleanColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _nonBooleanColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _qualifiedBooleanColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _moneyColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _nonMoneyColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _qualifiedMoneyColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _exactDecimalColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _nonExactDecimalColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dateColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _nonDateColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _qualifiedDateColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _loadedTables = new();
    private readonly List<string> _viewNames = new();
    private readonly List<string> _diagnostics = new();
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<TranslationKey, TranslationResult> _translationCache = new();
    private bool _disposed;

    /// <summary>
    /// Creates the mirror: builds the SQLite schema from the Access schema and loads all data.
    /// </summary>
    /// <param name="displayOrder">order columns by their Access display order instead of natural file order</param>
    /// <param name="skipSimpleIndexes">upstream skipIndexes: skip simple non-constraint indexes (mirror carries none; accepted for compatibility)</param>
    /// <param name="ignoreCase">upstream ignoreCase: case-insensitive text comparison (default true)</param>
    /// <param name="concatNulls">upstream concatNulls: NULL &amp; 'x' yields NULL when true (default false maps NULL to '')</param>
    public Mirror(File.Database accessDb, bool includeSystem = false, bool displayOrder = false,
        bool buildSavedQueries = true, string? storagePath = null, bool deleteStorageOnDispose = false,
        bool skipSimpleIndexes = false, bool ignoreCase = true, bool concatNulls = false)
    {
        _accessDb = accessDb;
        _includeSystem = includeSystem;
        _displayOrder = displayOrder;
        SkipSimpleIndexes = skipSimpleIndexes;
        IgnoreCase = ignoreCase;
        ConcatNulls = concatNulls;
        _storagePath = storagePath;
        _deleteStorageOnDispose = deleteStorageOnDispose;
        // The Access domain functions (DCount/DLookup/...) run their own
        // subqueries while the outer query's reader is still active. SQLite
        // supports concurrent read statements on one connection, so a single
        // connection is sufficient (a second shared-cache connection would
        // deadlock in sqlite3_prepare_v2 on Linux).
        if (storagePath != null)
        {
            string? directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(storagePath));
            if (directory != null)
            {
                Directory.CreateDirectory(directory);
            }
        }
        string sqliteConnectionString = storagePath == null
            ? "Data Source=:memory:"
            // Pooling would keep a stale handle on the mirror file open after dispose.
            : $"Data Source={storagePath};Pooling=False";
        _connection = new SqliteConnection(sqliteConnectionString);
        _connection.Open();
        if (storagePath != null)
        {
            ClearFileStorage();
        }
        // the original UCanAccess compares text case-insensitively by default
        // (HSQLDB SQL_TEXT_UCC collation); match that for text columns
        _connection.CreateCollation(CaseInsensitiveCollation,
            (x, y) => string.Compare(x, y, StringComparison.OrdinalIgnoreCase));
        _connection.CreateCollation(ExactDecimalSql.CollationName,
            (x, y) => ExactDecimalSql.CompareTextForCollation(x, y));
        try
        {
            BuildSchemaAndLoad(buildSavedQueries);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public SqliteConnection Connection => _connection;

    /// <summary>whether simple non-constraint indexes were skipped (upstream skipIndexes).</summary>
    public bool SkipSimpleIndexes { get; }

    /// <summary>whether text comparison is case-insensitive (upstream ignoreCase).</summary>
    public bool IgnoreCase { get; }

    /// <summary>whether &amp;/|| with NULL yields NULL (upstream concatNulls).</summary>
    public bool ConcatNulls { get; }

    /// <summary>the mirrored (non-system, non-linked) table names</summary>
    public IReadOnlyCollection<string> TableNames => _tableNames.Keys;

    /// <summary>
    /// Non-fatal diagnostics collected while reconstructing saved Access queries.
    /// A query that cannot be translated is not silently indistinguishable from
    /// a query that does not exist.
    /// </summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    public bool ContainsTable(string name) => _tableNames.ContainsKey(name);

    internal bool HasActiveReaders
    {
        get
        {
            lock (_sync)
            {
                return _commands.Count != 0;
            }
        }
    }

    internal void ThrowIfActiveReaders()
    {
        lock (_sync)
        {
            if (_commands.Count != 0)
            {
                throw new InvalidOperationException(
                    "Cannot modify the Access database while a data reader is active on the connection.");
            }
        }
    }

    internal bool TryGetRowLocation(string tableName, long sqliteRowId, out Table.RowLocation location)
    {
        if (_rowLocators.TryGetValue(tableName, out Dictionary<long, Table.RowLocation>? rows)
            && rows.TryGetValue(sqliteRowId, out location))
        {
            return true;
        }
        location = default;
        return false;
    }

    /// <summary>whether a column reference is a MONEY column (for '&amp;' concatenation scale)</summary>
    public bool IsMoneyColumn(string name)
    {
        string normalized = name.Trim().Trim('"', '[', ']');
        if (normalized.Contains('.', StringComparison.Ordinal))
        {
            return _qualifiedMoneyColumns.Contains(normalized);
        }
        return _moneyColumns.Contains(normalized) && !_nonMoneyColumns.Contains(normalized);
    }

    internal bool IsBooleanColumn(string tableName, string columnName)
        => _qualifiedBooleanColumns.Contains($"{tableName}.{columnName}")
            || (_booleanColumns.Contains(columnName) && !_nonBooleanColumns.Contains(columnName));

    internal DataType? GetColumnType(string tableName, string columnName)
        => _columnTypes.TryGetValue($"{tableName}.{columnName}", out DataType type) ? type : null;

    internal bool IsExactDecimalColumn(string name)
    {
        string normalized = name.Trim().Trim('"', '[', ']');
        if (normalized.Contains('.', StringComparison.Ordinal))
        {
            return _columnTypes.TryGetValue(normalized, out DataType qualifiedType)
                && qualifiedType is DataType.Money or DataType.Numeric;
        }
        // O(1) unqualified lookup; the previous EndsWith scan over all columns
        // ran per operand of every arithmetic/comparison rewrite. Matches the
        // IsMoneyColumn contract: true only when every same-named column is
        // exact-decimal, so a TEXT/decimal name collision is not miswrapped.
        return _exactDecimalColumns.Contains(normalized)
            && !_nonExactDecimalColumns.Contains(normalized);
    }

    internal bool IsDateColumn(string name)
    {
        string normalized = name.Trim().Trim('"', '[', ']');
        if (normalized.Contains('.', StringComparison.Ordinal))
        {
            return _qualifiedDateColumns.Contains(normalized);
        }
        return _dateColumns.Contains(normalized) && !_nonDateColumns.Contains(normalized);
    }

    /// <summary>whether system (MSys*) tables are exposed in the mirror</summary>
    private bool AllowSystem(TableMetaData meta) => _includeSystem;

    public SqliteCommand CreateCommand() => _connection.CreateCommand();

    private readonly record struct TranslationKey(string AccessSql, bool ConcatNulls);

    private sealed record TranslationResult(string Sql, int ParameterCount, string[]? NamedParameters);

    private const int MaxTranslationCacheEntries = 512;

    /// <summary>
    /// Translates Access SQL with this mirror's column classifications, memoizing
    /// by statement text. Repeated commands (parameterized OLTP loops) skip the
    /// lexer/parser entirely on a hit. The cache is cleared whenever column
    /// classifications can change (see <see cref="ClearTranslationCache"/>).
    /// </summary>
    internal string TranslateQuery(string accessSql, out int parameterCount,
        out IReadOnlyList<string>? namedParameters)
    {
        var key = new TranslationKey(accessSql, ConcatNulls);
        if (_translationCache.TryGetValue(key, out TranslationResult? cached))
        {
            parameterCount = cached.ParameterCount;
            // Defensive copy: the cached array must not escape mutably.
            namedParameters = cached.NamedParameters?.ToArray();
            return cached.Sql;
        }
        string translated = AccessSqlTranslator.Translate(accessSql, out parameterCount,
            out namedParameters, IsMoneyColumn, IsExactDecimalColumn, IsDateColumn, ConcatNulls);
        if (_translationCache.Count >= MaxTranslationCacheEntries)
        {
            _translationCache.Clear();
        }
        _translationCache.TryAdd(key, new TranslationResult(translated, parameterCount,
            namedParameters?.ToArray()));
        return translated;
    }

    private void ClearTranslationCache() => _translationCache.Clear();

    /// <summary>
    /// Number of cached translations (test seam: proves repeated commands hit
    /// the cache instead of re-running the lexer/parser).
    /// </summary>
    internal int TranslationCacheCount => _translationCache.Count;

    private void BuildSchemaAndLoad(bool buildSavedQueries)
    {
        foreach (TableMetaData meta in _accessDb.GetTableMetaData())
        {
            if (!AllowSystem(meta) && meta.IsSystem)
            {
                continue;
            }

            // linked tables are resolved through the link (their data lives in another file)
            Table? table = meta.IsLinked ? _accessDb.GetLinkedTable(meta.Name) : _accessDb.GetTable(meta.Name);
            if (table == null)
            {
                throw new InvalidOperationException(
                    $"Access table '{meta.Name}' disappeared while the mirror was being built.");
            }

            try
            {
                LoadTableIntoMirror(meta.Name, table);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not mirror Access table '{meta.Name}'.", ex);
            }
        }

        if (buildSavedQueries)
        {
            BuildSavedQueries();
        }
    }

    internal void BuildSavedQueryViews()
    {
        lock (_sync)
        {
            BuildSavedQueries();
        }
    }

    private void BuildSavedQueries()
    {
        // expose saved SELECT queries as views so they can be queried like tables
        foreach (QueryDef query in _accessDb.GetQueries())
        {
            if (query.Type != QueryType.Select || query.HasParameters)
            {
                // Parameterized QueryDefs cannot be represented by a SQLite
                // view because their values are supplied by the outer
                // ADO.NET command.  UCanAccessCommand expands those QueryDefs
                // into a derived table at execution time instead.
                continue;
            }
            try
            {
                string? querySql = query.Sql;
                if (querySql == null)
                {
                    continue;
                }
                if (CrosstabTranslator.TryBuildDynamicValueQuery(querySql, out string valueQuery))
                {
                    string translatedValues = TranslateQuery(valueQuery,
                        out int valueParameterCount, out _);
                    if (valueParameterCount != 0)
                    {
                        throw new NotSupportedException(
                            $"Saved dynamic crosstab '{query.Name}' contains parameters and cannot be materialized as a view.");
                    }
                    var values = new List<object?>();
                    using (var valueCommand = _connection.CreateCommand())
                    {
                        valueCommand.CommandText = translatedValues;
                        using var valueReader = valueCommand.ExecuteReader();
                        while (valueReader.Read())
                        {
                            if (!valueReader.IsDBNull(0))
                            {
                                values.Add(valueReader.GetValue(0));
                            }
                        }
                    }
                    querySql = CrosstabTranslator.AddPivotValues(querySql, values);
                }
                string translated = TranslateQuery(querySql, out _, out _);
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = $"CREATE VIEW {SqlNames.Quote(query.Name)} AS {translated}";
                cmd.ExecuteNonQuery();
                _viewNames.Add(query.Name);
            }
            catch (Exception)
            {
                _diagnostics.Add($"Saved query '{query.Name}' could not be materialized in the SQLite mirror.");
            }
        }
    }

    /// <summary>mirrors one Access table (regular or linked) into SQLite under the given name</summary>
    private void LoadTableIntoMirror(string accessName, Table table, SqliteTransaction? transaction = null)
    {
        string sqlName = SqlNames.Quote(accessName);
        CreateTable(sqlName, table, transaction);
        LoadData(sqlName, table, accessName, transaction);
        _tableNames[accessName] = sqlName;
        RegisterTableMetadata(accessName, table);
        _loadedTables.Add(accessName);
    }

    private void RegisterTableMetadata(string accessName, Table table)
    {
        foreach (Column col in table.Columns)
        {
            string qualified = $"{accessName}.{col.Name}";
            if (col.Type == DataType.Boolean)
            {
                _booleanColumns.Add(col.Name);
                _qualifiedBooleanColumns.Add(qualified);
            }
            else
            {
                _nonBooleanColumns.Add(col.Name);
            }
            if (col.Type == DataType.Money)
            {
                _moneyColumns.Add(col.Name);
                _qualifiedMoneyColumns.Add(qualified);
            }
            else
            {
                _nonMoneyColumns.Add(col.Name);
            }
            if (col.Type is DataType.Money or DataType.Numeric)
            {
                _exactDecimalColumns.Add(col.Name);
            }
            else
            {
                _nonExactDecimalColumns.Add(col.Name);
            }
            if (col.Type is DataType.ShortDateTime or DataType.ExtDateTime)
            {
                _dateColumns.Add(col.Name);
                _qualifiedDateColumns.Add(qualified);
            }
            else
            {
                _nonDateColumns.Add(col.Name);
            }
            _columnTypes[qualified] = col.Type;
        }
    }

    private const string CaseInsensitiveCollation = "UCA_IGNORE_CASE";

    private string SqliteType(Column column) => column.Type switch
    {
        DataType.Boolean => "INTEGER",
        DataType.Byte => "INTEGER",
        DataType.Int => "INTEGER",
        DataType.Long => "INTEGER",
        DataType.BigInt => "INTEGER",
        DataType.Float => "REAL",
        DataType.Double => "REAL",
        // SQLite NUMERIC affinity may coerce long decimal text to REAL. Keep
        // Access decimal values as text and use the exact-decimal collation and
        // translator functions for comparisons/arithmetic.
        DataType.Money => $"TEXT COLLATE {ExactDecimalSql.CollationName}",
        DataType.Numeric => $"TEXT COLLATE {ExactDecimalSql.CollationName}",
        DataType.ShortDateTime => "TEXT",
        DataType.ExtDateTime => "TEXT",
        DataType.Text or DataType.Memo => IgnoreCase
            ? $"TEXT COLLATE {CaseInsensitiveCollation}"
            : "TEXT COLLATE BINARY",
        DataType.Guid => "TEXT",
        DataType.ComplexType => "TEXT",
        _ => "BLOB",
    };

    /// <summary>
    /// The table columns in the order they should appear in the mirror
    /// (natural file order, or Access display order), paired with their file index.
    /// </summary>
    private (Column Column, int FileIndex)[] OrderedColumns(Table table)
    {
        if (!_displayOrder)
        {
            return table.Columns.Select(c => (c, c.ColumnIndex)).ToArray();
        }
        return table.Columns.OrderBy(c => c.DisplayIndex).Select(c => (c, c.ColumnIndex)).ToArray();
    }

    private void CreateTable(string sqlName, Table table, SqliteTransaction? transaction = null)
    {
        var cols = new List<string>();
        foreach ((Column col, _) in OrderedColumns(table))
        {
            cols.Add($"{SqlNames.Quote(col.Name)} {SqliteType(col)}{(col.Required ? " NOT NULL" : string.Empty)}");
        }

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"CREATE TABLE {sqlName} ({string.Join(", ", cols)})";
        cmd.ExecuteNonQuery();
    }

    private void LoadData(string sqlName, Table table, string? locatorName = null,
        SqliteTransaction? transaction = null)
    {
        locatorName ??= sqlName.Trim('"');
        var locators = new Dictionary<long, Table.RowLocation>(table.RowCount);
        (Column Column, int FileIndex)[] ordered = OrderedColumns(table);
        var insertColumns = string.Join(", ", ordered.Select(o => SqlNames.Quote(o.Column.Name)));
        var placeholders = string.Join(", ", ordered.Select((_, i) => $"$p{i}"));

        using SqliteTransaction? ownedTransaction = transaction == null ? _connection.BeginTransaction() : null;
        SqliteTransaction activeTransaction = transaction ?? ownedTransaction!;

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = activeTransaction;
            cmd.CommandText = $"INSERT INTO {sqlName} ({insertColumns}) VALUES ({placeholders})";

            var parameters = ordered.Select((_, i) =>
            {
                var p = cmd.CreateParameter();
                p.ParameterName = $"$p{i}";
                cmd.Parameters.Add(p);
                return p;
            }).ToArray();

            // One reused rowid command instead of allocating a command per row.
            using var idCommand = _connection.CreateCommand();
            idCommand.Transaction = activeTransaction;
            idCommand.CommandText = "SELECT last_insert_rowid()";
            foreach (Table.RowLocation location in table.RowLocations())
            {
                for (int i = 0; i < ordered.Length; i++)
                {
                    parameters[i].Value = ToSqliteValue(location.Row[ordered[i].FileIndex], ordered[i].Column);
                }
                cmd.ExecuteNonQuery();
                long sqliteRowId = Convert.ToInt64(idCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
                locators[sqliteRowId] = location;
            }
        }

        ownedTransaction?.Commit();
        _rowLocators[locatorName] = locators;
    }

    private static object? ToSqliteValue(object? value, Column? column = null)
        => AccessValueCodec.ToSqlite(value, column);

    internal static object? ToSqliteParameterValue(object? value)
        => AccessValueCodec.ToSqliteParameter(value);

    /// <summary>
    /// Executes the given (already translated) SQL and returns a reader.
    /// The reader stays valid until it is disposed; the backing command is disposed
    /// together with the mirror.
    /// </summary>
    public MirrorReader ExecuteReader(string sql)
        => ExecuteReader(sql, null, 0, null, CommandBehavior.Default);

    /// <summary>
    /// Executes the given (already translated) SQL with positional parameters and returns a reader.
    /// </summary>
    public MirrorReader ExecuteReader(string sql, IReadOnlyList<object?>? parameters, int commandTimeout = 0,
        Action? onDispose = null, CommandBehavior behavior = CommandBehavior.Default)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(Mirror));
            }

            SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = commandTimeout;
            if (parameters != null)
            {
                for (int i = 0; i < parameters.Count; i++)
                {
                    SqliteParameter p = cmd.CreateParameter();
                    p.ParameterName = $"@p{i}";
                    p.Value = ToSqliteParameterValue(parameters[i]) ?? DBNull.Value;
                    cmd.Parameters.Add(p);
                }
            }
            _commands.Add(cmd);
            try
            {
                // CloseConnection belongs to the outer ADO.NET connection, not the
                // private SQLite connection owned by the mirror.
                CommandBehavior sqliteBehavior = behavior & ~CommandBehavior.CloseConnection;
                SqliteDataReader reader = cmd.ExecuteReader(sqliteBehavior);
                return new MirrorReader(reader, cmd, IsBooleanColumn, GetColumnType,
                    ordinal => IsExactDecimalProjection(sql, ordinal), ReleaseCommand, onDispose);
            }
            catch
            {
                _commands.Remove(cmd);
                cmd.Dispose();
                throw;
            }
        }
    }

    internal void CancelActiveCommands()
    {
        lock (_sync)
        {
            foreach (SqliteCommand command in _commands.ToArray())
            {
                command.Cancel();
            }
        }
    }

    private void ReleaseCommand(SqliteCommand command)
    {
        lock (_sync)
        {
            _commands.Remove(command);
        }
        command.Dispose();
    }

    private bool IsExactDecimalProjection(string sql, int ordinal)
    {
        List<AccessTokenizer.Token> tokens;
        try
        {
            tokens = AccessTokenizer.Tokenize(sql);
        }
        catch
        {
            return false;
        }

        int select = tokens.FindIndex(t => t.Text.Equals("select", StringComparison.OrdinalIgnoreCase));
        if (select < 0)
        {
            return false;
        }

        int depth = 0;
        int from = -1;
        for (int i = select + 1; i < tokens.Count; i++)
        {
            if (tokens[i].Text == "(") depth++;
            else if (tokens[i].Text == ")") depth = Math.Max(0, depth - 1);
            else if (depth == 0 && tokens[i].Text.Equals("from", StringComparison.OrdinalIgnoreCase))
            {
                from = i;
                break;
            }
        }
        if (from < 0)
        {
            return false;
        }

        var projections = new List<List<AccessTokenizer.Token>>();
        var current = new List<AccessTokenizer.Token>();
        depth = 0;
        for (int i = select + 1; i < from; i++)
        {
            AccessTokenizer.Token token = tokens[i];
            if (token.Text == "(") depth++;
            else if (token.Text == ")") depth = Math.Max(0, depth - 1);
            if (depth == 0 && token.Text == ",")
            {
                projections.Add(current);
                current = new List<AccessTokenizer.Token>();
            }
            else
            {
                current.Add(token);
            }
        }
        projections.Add(current);
        if (ordinal < 0 || ordinal >= projections.Count)
        {
            return false;
        }

        List<AccessTokenizer.Token> projection = projections[ordinal];
        // Only a projection whose outer expression is an exact-decimal
        // operation should be converted to CLR decimal.  Looking for the
        // function name anywhere in the expression would misclassify e.g.
        // IIf(uca_decimal_cmp(amount, 0) < 0, 'neg', 'pos') as numeric.
        if (projection.Count > 1
            && projection[0].Kind == AccessTokenizer.Kind.Word
            && projection[0].Text.StartsWith("uca_decimal_", StringComparison.OrdinalIgnoreCase)
            && projection[1].Text == "(")
        {
            string function = projection[0].Text.ToLowerInvariant();
            return function is "uca_decimal_add" or "uca_decimal_subtract"
                or "uca_decimal_multiply" or "uca_decimal_divide"
                or "uca_decimal_sum" or "uca_decimal_min" or "uca_decimal_max"
                or "uca_decimal_avg";
        }

        for (int i = 0; i + 1 < projection.Count; i++)
        {
            string function = projection[i].Text.ToLowerInvariant();
            if (function is not ("sum" or "min" or "max"))
            {
                continue;
            }
            int open = i + 1;
            if (projection[open].Text != "(" || open + 2 >= projection.Count)
            {
                continue;
            }
            AccessTokenizer.Token argument = projection[open + 1];
            string name = argument.Text.Trim('"', '[', ']');
            if (argument.Kind is AccessTokenizer.Kind.Word or AccessTokenizer.Kind.Ident
                && IsExactDecimalColumn(name))
            {
                return true;
            }
        }
        return false;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (SqliteCommand cmd in _commands.ToArray())
            {
                cmd.Dispose();
            }
            _commands.Clear();
            _connection.Dispose();
            if (_deleteStorageOnDispose && _storagePath != null)
            {
                try
                {
                    System.IO.File.Delete(_storagePath);
                }
                catch
                {
                    // A stale mirror file is harmless and can be cleaned by the owner.
                }
            }
        }
    }

    private void ClearFileStorage()
    {
        var objects = new List<(string Name, string Type)>();
        using (var list = _connection.CreateCommand())
        {
            list.CommandText = "SELECT name, type FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";
            using var reader = list.ExecuteReader();
            while (reader.Read())
            {
                objects.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach ((string name, string type) in objects.Where(item =>
                     item.Type.Equals("view", StringComparison.OrdinalIgnoreCase)))
        {
            using var drop = _connection.CreateCommand();
            drop.CommandText = $"DROP VIEW IF EXISTS {SqlNames.Quote(name)}";
            drop.ExecuteNonQuery();
        }
        foreach ((string name, string type) in objects.Where(item =>
                     !item.Type.Equals("view", StringComparison.OrdinalIgnoreCase)))
        {
            using var drop = _connection.CreateCommand();
            drop.CommandText = $"DROP TABLE IF EXISTS {SqlNames.Quote(name)}";
            drop.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Reloads every mirrored table from the Access database (used after the MDB has
    /// been modified through the write API, so subsequent queries see the new data).
    /// </summary>
    public void RefreshAll()
    {
        lock (_sync)
        {
            RefreshAllCore();
        }
    }

    private void RefreshAllCore()
    {
        if (_commands.Count != 0)
        {
            throw new InvalidOperationException(
                "Cannot refresh the mirror while a data reader is active on the connection.");
        }
        MirrorStateSnapshot previous = CaptureState();
        using SqliteTransaction transaction = _connection.BeginTransaction();
        try
        {
            foreach (string viewName in _viewNames.ToArray())
            {
                using var dropView = _connection.CreateCommand();
                dropView.Transaction = transaction;
                dropView.CommandText = $"DROP VIEW IF EXISTS {SqlNames.Quote(viewName)}";
                dropView.ExecuteNonQuery();
            }
            _viewNames.Clear();

            var names = _tableNames.Keys.ToList();
            foreach (string accessName in names)
            {
                if (_tableNames.TryGetValue(accessName, out string? sqlName))
                {
                    using var drop = _connection.CreateCommand();
                    drop.Transaction = transaction;
                    drop.CommandText = $"DROP TABLE {sqlName}";
                    drop.ExecuteNonQuery();
                }
            }
            _tableNames.Clear();
            _loadedTables.Clear();
            _diagnostics.Clear();
            _rowLocators.Clear();
            ClearMetadata();

            foreach (TableMetaData meta in _accessDb.GetTableMetaData())
            {
                if (!AllowSystem(meta) && meta.IsSystem)
                {
                    continue;
                }
                Table? table = meta.IsLinked ? _accessDb.GetLinkedTable(meta.Name) : _accessDb.GetTable(meta.Name);
                if (table == null)
                {
                    throw new InvalidOperationException(
                        $"Access table '{meta.Name}' disappeared while the mirror was being refreshed.");
                }
                try
                {
                    LoadTableIntoMirror(meta.Name, table, transaction);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Could not refresh Access table '{meta.Name}'.", ex);
                }
            }
            transaction.Commit();
        }
        catch
        {
            previous.Restore(this);
            throw;
        }

        _diagnostics.Clear();
        BuildSavedQueries();
    }

    /// <summary>
    /// Reloads only the named tables. New data is loaded into temporary SQLite
    /// tables first and swapped into the mirror in one transaction, so a load
    /// failure leaves the previous table contents available.
    /// </summary>
    public void RefreshTables(IEnumerable<string> accessTableNames)
    {
        ArgumentNullException.ThrowIfNull(accessTableNames);
        string[] names = accessTableNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(Mirror));
            }
            if (_commands.Count != 0)
            {
                throw new InvalidOperationException(
                    "Cannot refresh the mirror while a data reader is active on the connection.");
            }

            var temporaryTables = new List<(string AccessName, string TemporaryName, Table Table)>();
            var previousLocators = names
                .Where(name => _rowLocators.ContainsKey(name))
                .ToDictionary(name => name,
                    name => new Dictionary<long, Table.RowLocation>(_rowLocators[name]),
                    StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string accessName in names)
                {
                    TableMetaData? meta = _accessDb.GetTableMetaData()
                        .FirstOrDefault(item => item.Name.Equals(accessName, StringComparison.OrdinalIgnoreCase));
                    if (meta == null)
                    {
                        throw new InvalidOperationException($"Access table '{accessName}' does not exist.");
                    }
                    Table? table = meta.IsLinked ? _accessDb.GetLinkedTable(meta.Name) : _accessDb.GetTable(meta.Name);
                    if (table == null)
                    {
                        throw new InvalidOperationException(
                            $"Access table '{meta.Name}' disappeared while the mirror was being refreshed.");
                    }

                    string temporaryName = "__uca_refresh_" + Guid.NewGuid().ToString("N");
                    temporaryTables.Add((meta.Name, temporaryName, table));
                    CreateTable(SqlNames.Quote(temporaryName), table);
                    LoadData(SqlNames.Quote(temporaryName), table, meta.Name);
                }

                using var tx = _connection.BeginTransaction();
                foreach ((string accessName, string temporaryName, _) in temporaryTables)
                {
                    string sqlName = SqlNames.Quote(accessName);
                    using var drop = _connection.CreateCommand();
                    drop.Transaction = tx;
                    drop.CommandText = $"DROP TABLE IF EXISTS {sqlName}";
                    drop.ExecuteNonQuery();

                    using var rename = _connection.CreateCommand();
                    rename.Transaction = tx;
                    rename.CommandText = $"ALTER TABLE {SqlNames.Quote(temporaryName)} RENAME TO {sqlName}";
                    rename.ExecuteNonQuery();
                }
                tx.Commit();

                foreach ((string accessName, string _, Table table) in temporaryTables)
                {
                    _tableNames[accessName] = SqlNames.Quote(accessName);
                    if (!_loadedTables.Contains(accessName, StringComparer.OrdinalIgnoreCase))
                    {
                        _loadedTables.Add(accessName);
                    }
                }
                RebuildColumnClassifications();
            }
            catch
            {
                foreach (string name in names)
                {
                    _rowLocators.Remove(name);
                }
                foreach ((string name, Dictionary<long, Table.RowLocation> locators) in previousLocators)
                {
                    _rowLocators[name] = locators;
                }
                foreach ((_, string temporaryName, _) in temporaryTables)
                {
                    try
                    {
                        using var drop = _connection.CreateCommand();
                        drop.CommandText = $"DROP TABLE IF EXISTS {SqlNames.Quote(temporaryName)}";
                        drop.ExecuteNonQuery();
                    }
                    catch
                    {
                        // Preserve the original refresh exception.
                    }
                }
                throw;
            }
            try
            {
                RebuildSavedQueries();
            }
            catch (Exception ex)
            {
                _diagnostics.Add($"Saved queries could not be rebuilt after a table refresh: {ex.Message}");
            }
        }
    }

    private void RebuildSavedQueries()
    {
        foreach (string viewName in _viewNames.ToArray())
        {
            using var drop = _connection.CreateCommand();
            drop.CommandText = $"DROP VIEW IF EXISTS {SqlNames.Quote(viewName)}";
            drop.ExecuteNonQuery();
        }
        _viewNames.Clear();
        _diagnostics.Clear();
        BuildSavedQueries();
    }

    private void RebuildColumnClassifications()
    {
        ClearTranslationCache();
        _booleanColumns.Clear();
        _nonBooleanColumns.Clear();
        _qualifiedBooleanColumns.Clear();
        _moneyColumns.Clear();
        _nonMoneyColumns.Clear();
        _qualifiedMoneyColumns.Clear();
        _exactDecimalColumns.Clear();
        _nonExactDecimalColumns.Clear();
        _dateColumns.Clear();
        _nonDateColumns.Clear();
        _qualifiedDateColumns.Clear();
        foreach ((string qualified, DataType type) in _columnTypes)
        {
            int separator = qualified.IndexOf('.', StringComparison.Ordinal);
            if (separator <= 0 || separator + 1 >= qualified.Length)
            {
                continue;
            }
            string columnName = qualified[(separator + 1)..];
            if (type == DataType.Boolean)
            {
                _booleanColumns.Add(columnName);
                _qualifiedBooleanColumns.Add(qualified);
            }
            else
            {
                _nonBooleanColumns.Add(columnName);
            }
            if (type == DataType.Money)
            {
                _moneyColumns.Add(columnName);
                _qualifiedMoneyColumns.Add(qualified);
            }
            else
            {
                _nonMoneyColumns.Add(columnName);
            }
            if (type is DataType.Money or DataType.Numeric)
            {
                _exactDecimalColumns.Add(columnName);
            }
            else
            {
                _nonExactDecimalColumns.Add(columnName);
            }
            if (type is DataType.ShortDateTime or DataType.ExtDateTime)
            {
                _dateColumns.Add(columnName);
                _qualifiedDateColumns.Add(qualified);
            }
            else
            {
                _nonDateColumns.Add(columnName);
            }
        }
    }

    private void ClearMetadata()
    {
        ClearTranslationCache();
        _booleanColumns.Clear();
        _nonBooleanColumns.Clear();
        _qualifiedBooleanColumns.Clear();
        _moneyColumns.Clear();
        _nonMoneyColumns.Clear();
        _qualifiedMoneyColumns.Clear();
        _exactDecimalColumns.Clear();
        _nonExactDecimalColumns.Clear();
        _dateColumns.Clear();
        _nonDateColumns.Clear();
        _qualifiedDateColumns.Clear();
        _columnTypes.Clear();
    }

    private MirrorStateSnapshot CaptureState()
        => new()
        {
            TableNames = new Dictionary<string, string>(_tableNames, StringComparer.OrdinalIgnoreCase),
            LoadedTables = _loadedTables.ToList(),
            ViewNames = _viewNames.ToList(),
            Diagnostics = _diagnostics.ToList(),
            RowLocators = _rowLocators.ToDictionary(
                pair => pair.Key,
                pair => new Dictionary<long, Table.RowLocation>(pair.Value),
                StringComparer.OrdinalIgnoreCase),
            ColumnTypes = new Dictionary<string, DataType>(_columnTypes, StringComparer.OrdinalIgnoreCase),
            BooleanColumns = new HashSet<string>(_booleanColumns, StringComparer.OrdinalIgnoreCase),
            NonBooleanColumns = new HashSet<string>(_nonBooleanColumns, StringComparer.OrdinalIgnoreCase),
            QualifiedBooleanColumns = new HashSet<string>(_qualifiedBooleanColumns, StringComparer.OrdinalIgnoreCase),
            MoneyColumns = new HashSet<string>(_moneyColumns, StringComparer.OrdinalIgnoreCase),
            NonMoneyColumns = new HashSet<string>(_nonMoneyColumns, StringComparer.OrdinalIgnoreCase),
            QualifiedMoneyColumns = new HashSet<string>(_qualifiedMoneyColumns, StringComparer.OrdinalIgnoreCase),
            ExactDecimalColumns = new HashSet<string>(_exactDecimalColumns, StringComparer.OrdinalIgnoreCase),
            NonExactDecimalColumns = new HashSet<string>(_nonExactDecimalColumns, StringComparer.OrdinalIgnoreCase),
            DateColumns = new HashSet<string>(_dateColumns, StringComparer.OrdinalIgnoreCase),
            NonDateColumns = new HashSet<string>(_nonDateColumns, StringComparer.OrdinalIgnoreCase),
            QualifiedDateColumns = new HashSet<string>(_qualifiedDateColumns, StringComparer.OrdinalIgnoreCase),
        };

    private sealed class MirrorStateSnapshot
    {
        public Dictionary<string, string> TableNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> LoadedTables { get; init; } = new();
        public List<string> ViewNames { get; init; } = new();
        public List<string> Diagnostics { get; init; } = new();
        public Dictionary<string, Dictionary<long, Table.RowLocation>> RowLocators { get; init; }
            = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DataType> ColumnTypes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> BooleanColumns { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> NonBooleanColumns { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> QualifiedBooleanColumns { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> MoneyColumns { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> NonMoneyColumns { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> QualifiedMoneyColumns { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExactDecimalColumns { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> NonExactDecimalColumns { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DateColumns { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> NonDateColumns { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> QualifiedDateColumns { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public void Restore(Mirror mirror)
        {
            mirror.ClearTranslationCache();
            mirror._tableNames.Clear();
            foreach ((string name, string sqlName) in TableNames) mirror._tableNames[name] = sqlName;
            mirror._loadedTables.Clear();
            mirror._loadedTables.AddRange(LoadedTables);
            mirror._viewNames.Clear();
            mirror._viewNames.AddRange(ViewNames);
            mirror._diagnostics.Clear();
            mirror._diagnostics.AddRange(Diagnostics);
            mirror._rowLocators.Clear();
            foreach ((string name, Dictionary<long, Table.RowLocation> rows) in RowLocators)
            {
                mirror._rowLocators[name] = new Dictionary<long, Table.RowLocation>(rows);
            }
            mirror._columnTypes.Clear();
            foreach ((string name, DataType type) in ColumnTypes) mirror._columnTypes[name] = type;
            RestoreSet(mirror._booleanColumns, BooleanColumns);
            RestoreSet(mirror._nonBooleanColumns, NonBooleanColumns);
            RestoreSet(mirror._qualifiedBooleanColumns, QualifiedBooleanColumns);
            RestoreSet(mirror._moneyColumns, MoneyColumns);
            RestoreSet(mirror._nonMoneyColumns, NonMoneyColumns);
            RestoreSet(mirror._qualifiedMoneyColumns, QualifiedMoneyColumns);
            RestoreSet(mirror._exactDecimalColumns, ExactDecimalColumns);
            RestoreSet(mirror._nonExactDecimalColumns, NonExactDecimalColumns);
            RestoreSet(mirror._dateColumns, DateColumns);
            RestoreSet(mirror._nonDateColumns, NonDateColumns);
            RestoreSet(mirror._qualifiedDateColumns, QualifiedDateColumns);
        }

        private static void RestoreSet(HashSet<string> target, IEnumerable<string> values)
        {
            target.Clear();
            target.UnionWith(values);
        }
    }
}
