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
    private readonly SqliteConnection _domainConnection;
    private readonly bool _includeSystem;
    private readonly bool _displayOrder;
    private readonly HashSet<SqliteCommand> _commands = new();
    private readonly Dictionary<string, string> _tableNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DataType> _columnTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _booleanColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _nonBooleanColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _qualifiedBooleanColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _moneyColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _nonMoneyColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _qualifiedMoneyColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _loadedTables = new();
    private readonly List<string> _viewNames = new();
    private readonly object _sync = new();
    private bool _disposed;

    /// <summary>
    /// Creates the mirror: builds the SQLite schema from the Access schema and loads all data.
    /// </summary>
    /// <param name="displayOrder">order columns by their Access display order instead of natural file order</param>
    public Mirror(File.Database accessDb, bool includeSystem = false, bool displayOrder = false)
    {
        _accessDb = accessDb;
        _includeSystem = includeSystem;
        _displayOrder = displayOrder;
        // A named shared-cache in-memory database lets the Access domain functions
        // (DCount/DLookup/...) run their own subqueries on a second connection
        // while the outer query's reader is still active on the main connection.
        string dbId = $"file:ucanaccess_{Guid.NewGuid():N}?mode=memory&cache=shared";
        _connection = new SqliteConnection($"Data Source={dbId}");
        _domainConnection = new SqliteConnection($"Data Source={dbId}");
        _connection.Open();
        _domainConnection.Open();
        // the original UCanAccess compares text case-insensitively by default
        // (HSQLDB SQL_TEXT_UCC collation); match that for text columns
        _connection.CreateCollation(CaseInsensitiveCollation,
            (x, y) => string.Compare(x, y, StringComparison.OrdinalIgnoreCase));
        _connection.CreateCollation(ExactDecimalSql.CollationName,
            (x, y) => ExactDecimalSql.CompareTextForCollation(x, y));
        try
        {
            BuildSchemaAndLoad();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public SqliteConnection Connection => _connection;

    /// <summary>a separate connection to the same in-memory database (for domain functions)</summary>
    internal SqliteConnection DomainConnection => _domainConnection;

    /// <summary>the mirrored (non-system, non-linked) table names</summary>
    public IReadOnlyCollection<string> TableNames => _tableNames.Keys;

    public bool ContainsTable(string name) => _tableNames.ContainsKey(name);

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
        return _columnTypes.Any(kv => kv.Key.EndsWith("." + normalized, StringComparison.OrdinalIgnoreCase)
            && kv.Value is DataType.Money or DataType.Numeric);
    }

    /// <summary>whether system (MSys*) tables are exposed in the mirror</summary>
    private bool AllowSystem(TableMetaData meta) => _includeSystem;

    public SqliteCommand CreateCommand() => _connection.CreateCommand();

    private void BuildSchemaAndLoad()
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

        BuildSavedQueries();
    }

    private void BuildSavedQueries()
    {
        // expose saved SELECT queries as views so they can be queried like tables
        foreach (QueryDef query in _accessDb.GetQueries())
        {
            if (query.Type != QueryType.Select)
            {
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
                    string translatedValues = AccessSqlTranslator.Translate(valueQuery,
                        out int valueParameterCount, out _, IsMoneyColumn, IsExactDecimalColumn);
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
                string translated = AccessSqlTranslator.Translate(querySql, out _, out _, IsMoneyColumn,
                    IsExactDecimalColumn);
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = $"CREATE VIEW {SqlNames.Quote(query.Name)} AS {translated}";
                cmd.ExecuteNonQuery();
                _viewNames.Add(query.Name);
            }
            catch (Exception)
            {
                // Keep tables and other saved queries usable when this query uses
                // Access syntax that cannot be reconstructed by the provider.
            }
        }
    }

    /// <summary>mirrors one Access table (regular or linked) into SQLite under the given name</summary>
    private void LoadTableIntoMirror(string accessName, Table table)
    {
        string sqlName = SqlNames.Quote(accessName);
        CreateTable(sqlName, table);
        LoadData(sqlName, table);
        _tableNames[accessName] = sqlName;
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
            _columnTypes[qualified] = col.Type;
        }
        _loadedTables.Add(sqlName);
    }

    private const string CaseInsensitiveCollation = "UCA_IGNORE_CASE";

    private static string SqliteType(Column column) => column.Type switch
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
        DataType.Text or DataType.Memo => $"TEXT COLLATE {CaseInsensitiveCollation}",
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

    private void CreateTable(string sqlName, Table table)
    {
        var cols = new List<string>();
        foreach ((Column col, _) in OrderedColumns(table))
        {
            cols.Add($"{SqlNames.Quote(col.Name)} {SqliteType(col)}{(col.Required ? " NOT NULL" : string.Empty)}");
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"CREATE TABLE {sqlName} ({string.Join(", ", cols)})";
        cmd.ExecuteNonQuery();
    }

    private void LoadData(string sqlName, Table table)
    {
        (Column Column, int FileIndex)[] ordered = OrderedColumns(table);
        var insertColumns = string.Join(", ", ordered.Select(o => SqlNames.Quote(o.Column.Name)));
        var placeholders = string.Join(", ", ordered.Select((_, i) => $"$p{i}"));

        using var tx = _connection.BeginTransaction();

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT INTO {sqlName} ({insertColumns}) VALUES ({placeholders})";

            var parameters = ordered.Select((_, i) =>
            {
                var p = cmd.CreateParameter();
                p.ParameterName = $"$p{i}";
                cmd.Parameters.Add(p);
                return p;
            }).ToArray();

            foreach (Row row in table.Rows())
            {
                for (int i = 0; i < ordered.Length; i++)
                {
                    parameters[i].Value = ToSqliteValue(row[ordered[i].FileIndex], ordered[i].Column);
                }
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    private static object? ToSqliteValue(object? value, Column? column = null)
        => value switch
    {
        null => DBNull.Value,
        AccessSingleValue[] or AccessAttachment[] or AccessVersion[] when column?.Type == DataType.ComplexType
            => ComplexValueJson.Serialize(value),
        decimal m when column?.Type == DataType.Money => ExactDecimal.FromDecimal(m).ToFixedString(4),
        decimal m when column?.Type == DataType.Numeric => ExactDecimal.FromDecimal(m).ToFixedString(column!.Scale),
        decimal m => ExactDecimal.FromDecimal(m).ToString(),
        ExactDecimal exact when column?.Type == DataType.Money => exact.ToFixedString(4),
        ExactDecimal exact when column?.Type == DataType.Numeric => exact.ToFixedString(column!.Scale),
        ExactDecimal exact => exact.ToString(),
        bool b => b ? 1L : 0L,
        byte n => (long)n,
        sbyte n => (long)n,
        short n => (long)n,
        ushort n => (long)n,
        int n => (long)n,
        uint n => (long)n,
        long n => n,
        ulong n when n <= long.MaxValue => (long)n,
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        string s when column?.Type == DataType.Money && ExactDecimal.TryParse(s, out ExactDecimal money)
            => money.ToFixedString(4),
        string s when column?.Type == DataType.Numeric && ExactDecimal.TryParse(s, out ExactDecimal numeric)
            => numeric.ToFixedString(column!.Scale),
        string s => s,
        byte[] bytes => bytes,
        _ => value.ToString(),
    };

    internal static object? ToSqliteParameterValue(object? value)
        => value is decimal m ? ExactDecimal.FromDecimal(m).ToString()
            : value is float f ? f.ToString("R", CultureInfo.InvariantCulture)
            : value is double d ? d.ToString("R", CultureInfo.InvariantCulture)
            : value;

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
            _domainConnection.Dispose();
            _connection.Dispose();
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
        foreach (string viewName in _viewNames.ToArray())
        {
            using var dropView = _connection.CreateCommand();
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
                drop.CommandText = $"DROP TABLE {sqlName}";
                drop.ExecuteNonQuery();
            }
        }
        _tableNames.Clear();
        _loadedTables.Clear();
        _booleanColumns.Clear();
        _nonBooleanColumns.Clear();
        _qualifiedBooleanColumns.Clear();
        _moneyColumns.Clear();
        _nonMoneyColumns.Clear();
        _qualifiedMoneyColumns.Clear();
        _columnTypes.Clear();

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
                LoadTableIntoMirror(meta.Name, table);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not refresh Access table '{meta.Name}'.", ex);
            }
        }

        BuildSavedQueries();
    }
}
