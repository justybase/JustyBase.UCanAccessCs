using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UCanAccess;

/// <summary>
/// ADO.NET command for MS Access databases.
/// </summary>
public sealed class UCanAccessCommand : DbCommand
{
    private UCanAccessConnection? _connection;
    private string _commandText = string.Empty;
    private CommandType _commandType = CommandType.Text;
    private readonly UCanAccessParameterCollection _parameters = new();
    private UCanAccessTransaction? _transaction;
    private Mirror? _activeMirror;

    internal UCanAccessCommand(UCanAccessConnection connection)
    {
        _connection = connection;
    }

    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set => _commandText = value ?? string.Empty;
    }

    private int _commandTimeout;

    public override int CommandTimeout
    {
        get => _commandTimeout;
        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            _commandTimeout = value;
        }
    }

    public override CommandType CommandType
    {
        get => _commandType;
        set
        {
            if (value != CommandType.Text)
            {
                throw new NotSupportedException($"CommandType {value} is not supported.");
            }
            _commandType = value;
        }
    }

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    public override void Cancel()
    {
        _activeMirror?.CancelActiveCommands();
        _connection?.MirrorIfCreated?.CancelActiveCommands();
    }

    public override void Prepare()
    {
        UCanAccessConnection connection = _connection
            ?? throw new InvalidOperationException("The command has no associated connection.");
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("The connection is not open.");
        }
        connection.EnsureDatabaseCurrent();
        UCanAccessTransaction? transaction = GetTransaction(connection);
        Mirror mirror = transaction?.QueryMirror ?? connection.Mirror;
        File.Database queryDatabase = transaction?.QueryDatabase ?? connection.AccessDatabase;
        string effectiveCommandText = SavedQueryExpander.Expand(CommandText, queryDatabase);
        AccessSqlTranslator.Translate(effectiveCommandText, out int parameterCount, out IReadOnlyList<string>? names,
            mirror.IsMoneyColumn, mirror.IsExactDecimalColumn, mirror.IsDateColumn);
        if (parameterCount == 0 && _parameters.Count != 0)
        {
            throw new InvalidOperationException("The command has parameters, but its SQL contains no placeholders.");
        }
        if (names != null && names.Count != parameterCount)
        {
            throw new InvalidOperationException("The command contains an inconsistent parameter declaration.");
        }
    }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set
        {
            if (value is not null and not UCanAccessConnection)
            {
                throw new ArgumentException("The connection must belong to UCanAccess.", nameof(value));
            }
            _connection = (UCanAccessConnection?)value;
        }
    }

    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set
        {
            if (value is not null and not UCanAccessTransaction)
            {
                throw new ArgumentException("The transaction must belong to UCanAccess.", nameof(value));
            }
            if (value is UCanAccessTransaction transaction && _connection != null
                && !ReferenceEquals(transaction.OwnerConnection, _connection))
            {
                throw new InvalidOperationException("The transaction belongs to another connection.");
            }
            _transaction = (UCanAccessTransaction?)value;
        }
    }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    public override int ExecuteNonQuery()
    {
        UCanAccessConnection connection = _connection
            ?? throw new InvalidOperationException("The command has no associated connection.");
        connection.EnsureDatabaseCurrent();
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("The connection is not open.");
        }

        // A sequence of DML statements is staged once. This preserves
        // autocommit atomicity while avoiding one source-file copy per statement.
        string[] statements = SplitStatements(CommandText ?? string.Empty)
            .Select(StripLeadingComments)
            .Select(statement => statement.Trim())
            .Where(statement => statement.Length != 0)
            .ToArray();
        bool allDml = statements.Length > 0
            && statements.All(statement => FirstWord(statement) is "INSERT" or "UPDATE" or "DELETE");
        if (GetTransaction(connection) == null && allDml
            && (statements.Length == 1 || _parameters.Count == 0))
        {
            var batch = new List<(string Sql, IReadOnlyList<object?>? Parameters)>(statements.Length);
            foreach (string statement in statements)
            {
                string expandedStatement = SavedQueryExpander.Expand(statement, connection.AccessDatabase);
                IReadOnlyList<object?>? parameters = BindDmlParameters(expandedStatement,
                    _parameters.Cast<UCanAccessParameter>().ToList());
                batch.Add((expandedStatement, parameters));
            }
            return connection.ExecuteDmlBatchAtomically(batch);
        }
        int total = 0;
        foreach (string statement in statements)
        {
            total += ExecuteSingle(connection, statement);
        }
        return total;
    }

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ExecuteNonQuery();
        }, cancellationToken);
    }

    private int ExecuteSingle(UCanAccessConnection connection, string sql)
    {
        sql = StripLeadingComments(sql).Trim();
        if (sql.Length == 0)
        {
            return 0;
        }
        string kind = FirstWord(sql);
        if (kind is "INSERT" or "UPDATE" or "DELETE")
        {
            UCanAccessTransaction? transaction = GetTransaction(connection);
            sql = SavedQueryExpander.Expand(sql,
                transaction?.QueryDatabase ?? connection.AccessDatabase);
            var supplied = _parameters.Cast<UCanAccessParameter>().ToList();
            IReadOnlyList<object?>? parameters = BindDmlParameters(sql, supplied);

            // inside a transaction, buffer the statement until commit
            if (transaction != null)
            {
                return transaction.AddPending(sql, parameters);
            }

            return connection.ExecuteDmlAtomically(sql, parameters);
        }
        if (kind is "CREATE" or "DROP" or "ALTER" or "DISABLE" or "ENABLE"
            || (kind == "SELECT" && AccessDdl.IsSelectInto(sql)))
        {
            UCanAccessTransaction? transaction = GetTransaction(connection);
            if (transaction != null)
            {
                return transaction.AddPending(sql, null);
            }
            if (AccessDdl.RequiresAtomicFileMutation(sql))
            {
                return connection.ExecuteDdlAtomically(sql);
            }
            Mirror? transientMirror = null;
            try
            {
                Mirror mirror = connection.KeepMirror
                    ? connection.Mirror
                    : transientMirror = connection.CreateMirrorFor(connection.AccessDatabase);
                int result = AccessDdl.Execute(connection.AccessDatabase, mirror, sql);
                connection.MarkDatabaseCurrent();
                connection.SyncAutoNumberFlags(connection.AccessDatabase);
                return result;
            }
            finally
            {
                transientMirror?.Dispose();
            }
        }

        throw new NotSupportedException($"Statement type '{kind}' is not supported for writes.");
    }

    private static IReadOnlyList<object?>? BindDmlParameters(string sql, IReadOnlyList<UCanAccessParameter> supplied)
    {
        AccessSqlTranslator.Translate(sql, out int parameterCount, out IReadOnlyList<string>? names);
        if (parameterCount == 0)
        {
            if (supplied.Count > 0)
            {
                throw new InvalidOperationException("The command has parameters, but its SQL contains no parameter placeholders.");
            }
            return null;
        }

        var result = new object?[parameterCount];
        foreach (UCanAccessParameter parameter in supplied)
        {
            ValidateParameterDirection(parameter);
        }
        if (names == null)
        {
            if (supplied.Count != parameterCount)
            {
                throw new InvalidOperationException(
                    $"The statement expects {parameterCount} parameter(s), but {supplied.Count} were supplied.");
            }
            for (int i = 0; i < parameterCount; i++)
            {
                result[i] = supplied[i].Value;
            }
            return result;
        }

        var used = new bool[parameterCount];
        var positional = new Queue<object?>();
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (UCanAccessParameter parameter in supplied)
        {
            string name = parameter.ParameterName?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                positional.Enqueue(parameter.Value);
                continue;
            }

            name = name.TrimStart('@', ':', '?');
            if (name.Length >= 2 && name[0] == '[' && name[^1] == ']')
            {
                name = name[1..^1];
            }
            int matches = 0;
            for (int i = 0; i < names.Count; i++)
            {
                if (names[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    result[i] = parameter.Value;
                    used[i] = true;
                    matches++;
                }
            }
            if (matches == 0)
            {
                throw new InvalidOperationException($"Parameter '{parameter.ParameterName}' was not found in the command text.");
            }
            if (!named.Add(name))
            {
                throw new InvalidOperationException($"Parameter '{parameter.ParameterName}' was supplied more than once.");
            }
        }

        for (int i = 0; i < parameterCount; i++)
        {
            if (!used[i])
            {
                if (positional.Count == 0)
                {
                    throw new InvalidOperationException("Not enough parameter values were supplied.");
                }
                result[i] = positional.Dequeue();
            }
        }
        if (positional.Count > 0)
        {
            throw new InvalidOperationException("Too many positional parameter values were supplied.");
        }
        return result;
    }

    private UCanAccessTransaction? GetTransaction(UCanAccessConnection connection)
    {
        if (_transaction != null && !ReferenceEquals(_transaction.OwnerConnection, connection))
        {
            throw new InvalidOperationException("The command transaction belongs to another connection.");
        }
        return _transaction ?? connection.ActiveTransaction;
    }

    private static string StripLeadingComments(string sql)
    {
        int offset = 0;
        while (true)
        {
            while (offset < sql.Length && char.IsWhiteSpace(sql[offset]))
            {
                offset++;
            }
            if (offset + 1 < sql.Length && sql[offset] == '-' && sql[offset + 1] == '-')
            {
                int newline = sql.IndexOf('\n', offset + 2);
                offset = newline < 0 ? sql.Length : newline + 1;
                continue;
            }
            if (offset + 1 < sql.Length && sql[offset] == '/' && sql[offset + 1] == '*')
            {
                int close = sql.IndexOf("*/", offset + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    throw new InvalidOperationException("Unterminated SQL block comment.");
                }
                offset = close + 2;
                continue;
            }
            return sql[offset..];
        }
    }

    private static string FirstWord(string sql)
    {
        int start = 0;
        while (start < sql.Length && char.IsWhiteSpace(sql[start]))
        {
            start++;
        }
        int end = start;
        while (end < sql.Length && !char.IsWhiteSpace(sql[end]))
        {
            end++;
        }
        return sql[start..end].ToUpperInvariant();
    }

    /// <summary>splits a SQL script while respecting strings, bracketed names and comments</summary>
    private static string[] SplitStatements(string sql)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        char? quote = null;
        bool bracketed = false;
        bool lineComment = false;
        bool blockComment = false;
        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            if (lineComment)
            {
                sb.Append(c);
                if (c == '\n')
                {
                    lineComment = false;
                }
                continue;
            }
            if (blockComment)
            {
                sb.Append(c);
                if (c == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
                {
                    sb.Append(sql[++i]);
                    blockComment = false;
                }
                continue;
            }
            if (quote != null)
            {
                sb.Append(c);
                if (c == quote)
                {
                    if (i + 1 < sql.Length && sql[i + 1] == quote)
                    {
                        sb.Append(sql[i + 1]);
                        i++;
                    }
                    else
                    {
                        quote = null;
                    }
                }
            }
            else if (bracketed)
            {
                sb.Append(c);
                if (c == ']')
                {
                    if (i + 1 < sql.Length && sql[i + 1] == ']')
                    {
                        sb.Append(sql[++i]);
                    }
                    else
                    {
                        bracketed = false;
                    }
                }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                sb.Append(c);
            }
            else if (c == '[')
            {
                bracketed = true;
                sb.Append(c);
            }
            else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                lineComment = true;
                sb.Append(c);
                sb.Append(sql[++i]);
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                blockComment = true;
                sb.Append(c);
                sb.Append(sql[++i]);
            }
            else if (c == ';')
            {
                // A saved Access QueryDef may contain an internal semicolon
                // between its PARAMETERS declaration and SELECT body.  Keep
                // that delimiter in the CREATE VIEW statement; only the next
                // semicolon terminates the command script.
                if (IsQueryDefParameterTerminator(sb))
                {
                    sb.Append(c);
                }
                else
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0)
        {
            result.Add(sb.ToString());
        }
        return result.ToArray();
    }

    private static bool IsQueryDefParameterTerminator(StringBuilder statement)
    {
        string text = statement.ToString();
        return !text.Contains(';')
            && Regex.IsMatch(text,
                @"^\s*CREATE\s+VIEW\b.*\bAS\s+PARAMETERS\b",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    public override object? ExecuteScalar()
    {
        using var reader = ExecuteDbDataReader(CommandBehavior.SingleRow);
        if (reader.Read())
        {
            return reader.IsDBNull(0) ? null : reader.GetValue(0);
        }
        return null;
    }

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ExecuteScalar();
        }, cancellationToken);
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        UCanAccessConnection connection = _connection
            ?? throw new InvalidOperationException("The command has no associated connection.");
        connection.EnsureDatabaseCurrent();
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("The connection is not open.");
        }
        Mirror? transientMirror = null;
        bool readerHandedOff = false;
        try
        {
        UCanAccessTransaction? transaction = GetTransaction(connection);
            Mirror queryMirror = transaction?.QueryMirror
            ?? (connection.KeepMirror
                ? connection.Mirror
                : transientMirror = connection.CreateMirrorFor(connection.AccessDatabase));
        _activeMirror = queryMirror;
        string effectiveCommandText = RewriteIdentitySelect(CommandText ?? string.Empty, connection.LastInsertedId);
        if (AccessDdl.IsSelectInto(effectiveCommandText))
        {
            throw new InvalidOperationException(
                "SELECT INTO is a table-creating write statement; use ExecuteNonQuery instead.");
        }
        effectiveCommandText = SavedQueryExpander.Expand(effectiveCommandText,
            transaction?.QueryDatabase ?? connection.AccessDatabase);
        var suppliedParameters = _parameters.Cast<UCanAccessParameter>().ToList();
        if (CrosstabTranslator.TryBuildDynamicValueQuery(effectiveCommandText, out string valueQuery))
        {
            string translatedValueQuery = AccessSqlTranslator.Translate(valueQuery,
                out int valueParameterCount, out IReadOnlyList<string>? valueNames,
                queryMirror.IsMoneyColumn, queryMirror.IsExactDecimalColumn, queryMirror.IsDateColumn);
            object?[]? valueParameters = BindQueryParameters(valueParameterCount, valueNames, suppliedParameters);
            var pivotValues = new List<object?>();
            using (MirrorReader valueReader = queryMirror.ExecuteReader(translatedValueQuery, valueParameters,
                       CommandTimeout))
            {
                while (valueReader.Read())
                {
                    if (!valueReader.IsDBNull(0))
                    {
                        pivotValues.Add(valueReader.GetValue(0));
                    }
                }
            }
            effectiveCommandText = CrosstabTranslator.AddPivotValues(effectiveCommandText, pivotValues);
        }

        string sql = AccessSqlTranslator.Translate(effectiveCommandText, out int parameterCount, out IReadOnlyList<string>? names,
            queryMirror.IsMoneyColumn, queryMirror.IsExactDecimalColumn, queryMirror.IsDateColumn);
        object?[]? parameters = null;
        if (parameterCount > 0)
        {
            parameters = new object?[parameterCount];
            var supplied = suppliedParameters;
            foreach (UCanAccessParameter parameter in supplied)
            {
                ValidateParameterDirection(parameter);
            }
            if (names != null)
            {
                // Named parameters may occur more than once.  One DbParameter binds
                // every occurrence of the same name; unnamed parameters fill the
                // remaining placeholder slots in order.
                var used = new bool[parameterCount];
                var positional = new Queue<object?>();
                var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (UCanAccessParameter p in supplied)
                {
                    string pname = p.ParameterName?.Trim() ?? "";
                    if (pname.Length > 0)
                    {
                        pname = pname.TrimStart('@', ':', '?');
                        if (pname.Length >= 2 && pname[0] == '[' && pname[^1] == ']')
                        {
                            pname = pname[1..^1];
                        }
                        int matches = 0;
                        for (int k = 0; k < names.Count; k++)
                        {
                            if (names[k].Equals(pname, StringComparison.OrdinalIgnoreCase))
                            {
                                parameters[k] = p.Value;
                                used[k] = true;
                                matches++;
                            }
                        }
                        if (matches == 0)
                        {
                            throw new InvalidOperationException($"Parameter '{p.ParameterName}' was not found in the command text.");
                        }
                        if (!named.Add(pname))
                        {
                            throw new InvalidOperationException($"Parameter '{p.ParameterName}' was supplied more than once.");
                        }
                    }
                    else
                    {
                        positional.Enqueue(p.Value);
                    }
                }
                for (int k = 0; k < parameterCount; k++)
                {
                    if (!used[k])
                    {
                        if (positional.Count == 0)
                        {
                            throw new InvalidOperationException($"The command text expects {parameterCount} parameter(s) but not enough values were supplied.");
                        }
                        parameters[k] = positional.Dequeue();
                    }
                }
                if (positional.Count > 0)
                {
                    throw new InvalidOperationException("Too many parameter values were supplied.");
                }
            }
            else
            {
                // positional parameters
                if (supplied.Count != parameterCount)
                {
                    throw new InvalidOperationException(
                        $"The query expects {parameterCount} parameter(s) but {supplied.Count} were supplied.");
                }
                for (int i = 0; i < parameterCount; i++)
                {
                    parameters[i] = supplied[i].Value;
                }
            }
        }
        else if (_parameters.Count > 0)
        {
            throw new InvalidOperationException(
                "The command has parameters, but its SQL contains no parameter placeholders.");
        }
            bool closeConnection = (behavior & CommandBehavior.CloseConnection) != 0;
            Action readerCleanup = () =>
                {
                    transientMirror?.Dispose();
                    if (closeConnection)
                    {
                        connection.Close();
                    }
                    if (ReferenceEquals(_activeMirror, queryMirror))
                    {
                        _activeMirror = null;
                    }
                };
            DbDataReader reader = queryMirror.ExecuteReader(sql, parameters, CommandTimeout,
                readerCleanup, behavior);
        readerHandedOff = true;
        return reader;
        }
        catch
        {
            if (!readerHandedOff)
            {
                _activeMirror = null;
            }
            transientMirror?.Dispose();
            throw;
        }
    }

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run<DbDataReader>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ExecuteDbDataReader(behavior);
        }, cancellationToken);
    }

    private static void ValidateParameterDirection(UCanAccessParameter parameter)
    {
        if (parameter.Direction != ParameterDirection.Input)
        {
            throw new NotSupportedException(
                $"Parameter direction {parameter.Direction} is not supported; only input parameters are supported.");
        }
    }

    private static string RewriteIdentitySelect(string sql, long? lastInsertedId)
    {
        string trimmed = sql.Trim();
        while (trimmed.EndsWith(';'))
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        Match match = Regex.Match(trimmed, @"^SELECT\s+@@IDENTITY(?=\s|$)(?<tail>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        if (!match.Success)
        {
            return sql;
        }

        string tail = match.Groups["tail"].Value.Trim();
        string alias;
        if (tail.Length == 0)
        {
            alias = "[@@IDENTITY]";
        }
        else
        {
            if (tail.Length >= 2 && tail.StartsWith("AS", StringComparison.OrdinalIgnoreCase)
                && (tail.Length == 2 || char.IsWhiteSpace(tail[2])))
            {
                tail = tail[2..].Trim();
            }
            if (!IsIdentityAlias(tail))
            {
                return sql;
            }
            alias = tail;
        }

        string value = lastInsertedId?.ToString(CultureInfo.InvariantCulture) ?? "NULL";
        return $"SELECT {value} AS {alias}";
    }

    private static bool IsIdentityAlias(string alias)
    {
        if (alias.Length >= 2 && ((alias[0] == '[' && alias[^1] == ']')
                || (alias[0] == '"' && alias[^1] == '"')))
        {
            return true;
        }

        return Regex.IsMatch(alias, @"^[A-Za-z_][A-Za-z0-9_$#@]*$",
            RegexOptions.CultureInvariant);
    }

    private static object?[]? BindQueryParameters(int parameterCount, IReadOnlyList<string>? names,
        IReadOnlyList<UCanAccessParameter> supplied)
    {
        foreach (UCanAccessParameter parameter in supplied)
        {
            ValidateParameterDirection(parameter);
        }
        if (parameterCount == 0)
        {
            if (supplied.Count > 0)
            {
                throw new InvalidOperationException(
                    "The command has parameters, but its SQL contains no parameter placeholders.");
            }
            return null;
        }

        var result = new object?[parameterCount];
        if (names == null)
        {
            if (supplied.Count != parameterCount)
            {
                throw new InvalidOperationException(
                    $"The query expects {parameterCount} parameter(s) but {supplied.Count} were supplied.");
            }
            for (int i = 0; i < parameterCount; i++) result[i] = supplied[i].Value;
            return result;
        }

        var used = new bool[parameterCount];
        var positional = new Queue<object?>();
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (UCanAccessParameter parameter in supplied)
        {
            string name = parameter.ParameterName?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                positional.Enqueue(parameter.Value);
                continue;
            }
            name = name.TrimStart('@', ':', '?');
            if (name.Length >= 2 && name[0] == '[' && name[^1] == ']') name = name[1..^1];
            int matches = 0;
            for (int i = 0; i < names.Count; i++)
            {
                if (!names[i].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                result[i] = parameter.Value;
                used[i] = true;
                matches++;
            }
            if (matches == 0)
            {
                throw new InvalidOperationException($"Parameter '{parameter.ParameterName}' was not found in the command text.");
            }
            if (!named.Add(name))
            {
                throw new InvalidOperationException($"Parameter '{parameter.ParameterName}' was supplied more than once.");
            }
        }
        for (int i = 0; i < parameterCount; i++)
        {
            if (used[i]) continue;
            if (positional.Count == 0) throw new InvalidOperationException("Not enough parameter values were supplied.");
            result[i] = positional.Dequeue();
        }
        if (positional.Count > 0) throw new InvalidOperationException("Too many positional parameter values were supplied.");
        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Cancel();
            _connection = null;
            _transaction = null;
        }
        base.Dispose(disposing);
    }

    protected override DbParameter CreateDbParameter()
        => new UCanAccessParameter();
}
