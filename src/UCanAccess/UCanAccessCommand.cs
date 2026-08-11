using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text;

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
        Mirror mirror = connection.Mirror;
        AccessSqlTranslator.Translate(CommandText, out int parameterCount, out IReadOnlyList<string>? names,
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
                IReadOnlyList<object?>? parameters = BindDmlParameters(statement,
                    _parameters.Cast<UCanAccessParameter>().ToList());
                batch.Add((statement, parameters));
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
            var supplied = _parameters.Cast<UCanAccessParameter>().ToList();
            IReadOnlyList<object?>? parameters = BindDmlParameters(sql, supplied);

            // inside a transaction, buffer the statement until commit
            UCanAccessTransaction? transaction = GetTransaction(connection);
            if (transaction != null)
            {
                return transaction.AddPending(sql, parameters);
            }

            return connection.ExecuteDmlAtomically(sql, parameters);
        }
        if (kind is "CREATE" or "DROP" or "ALTER")
        {
            UCanAccessTransaction? transaction = GetTransaction(connection);
            if (transaction != null)
            {
                return transaction.AddPending(sql, null);
            }
            if (AccessDdl.IsIndexMutation(sql))
            {
                return connection.ExecuteIndexDdlAtomically(sql);
            }
            Mirror? transientMirror = null;
            try
            {
                Mirror mirror = connection.KeepMirror
                    ? connection.Mirror
                    : transientMirror = connection.CreateMirrorFor(connection.AccessDatabase);
                int result = AccessDdl.Execute(connection.AccessDatabase, mirror, sql);
                connection.MarkDatabaseCurrent();
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
                result.Add(sb.ToString());
                sb.Clear();
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

    public override object? ExecuteScalar()
    {
        using var reader = ExecuteDbDataReader(CommandBehavior.SingleRow);
        if (reader.Read())
        {
            return reader.IsDBNull(0) ? null : reader.GetValue(0);
        }
        return null;
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
        try
        {
        UCanAccessTransaction? transaction = GetTransaction(connection);
        Mirror queryMirror = transaction?.QueryMirror
            ?? (connection.KeepMirror
                ? connection.Mirror
                : transientMirror = connection.CreateMirrorFor(connection.AccessDatabase));
        string effectiveCommandText = CommandText;
        var suppliedParameters = _parameters.Cast<UCanAccessParameter>().ToList();
        if (CrosstabTranslator.TryBuildDynamicValueQuery(CommandText, out string valueQuery))
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
            effectiveCommandText = CrosstabTranslator.AddPivotValues(CommandText, pivotValues);
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
            Action? readerCleanup = transientMirror is null && !closeConnection
                ? null
                : () =>
                {
                    transientMirror?.Dispose();
                    if (closeConnection)
                    {
                        connection.Close();
                    }
                };
            DbDataReader reader = queryMirror.ExecuteReader(sql, parameters, CommandTimeout,
                readerCleanup, behavior);
        return reader;
        }
        catch
        {
            transientMirror?.Dispose();
            throw;
        }
    }

    private static void ValidateParameterDirection(UCanAccessParameter parameter)
    {
        if (parameter.Direction != ParameterDirection.Input)
        {
            throw new NotSupportedException(
                $"Parameter direction {parameter.Direction} is not supported; only input parameters are supported.");
        }
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
