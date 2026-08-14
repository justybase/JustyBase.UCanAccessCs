using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using UCanAccess.File;

namespace UCanAccess;

/// <summary>
/// Wraps the SQLite reader produced by the mirror and converts boolean columns
/// (stored as INTEGER 0/1 in SQLite) back to C# <see cref="bool"/>, matching the
/// Boolean values the original UCanAccess/JDBC driver returns.
/// </summary>
public sealed class MirrorReader : DbDataReader
{
    private readonly SqliteDataReader _inner;
    private readonly SqliteCommand _command;
    private readonly Func<string, string, bool> _isBooleanColumn;
    private readonly Func<string, string, DataType?> _getColumnType;
    private readonly Func<int, bool>? _isExactDecimalResult;
    private readonly Action<SqliteCommand> _releaseCommand;
    private readonly Action? _onDispose;
    private readonly Dictionary<int, bool> _columnIsBoolean = new();
    private readonly Dictionary<int, (string? Table, string? Column)> _baseColumns = new();
    private bool _disposed;

    internal MirrorReader(SqliteDataReader inner, SqliteCommand command,
        Func<string, string, bool> isBooleanColumn,
        Func<string, string, DataType?> getColumnType,
        Func<int, bool>? isExactDecimalResult,
        Action<SqliteCommand> releaseCommand,
        Action? onDispose = null)
    {
        _inner = inner;
        _command = command;
        _isBooleanColumn = isBooleanColumn;
        _getColumnType = getColumnType;
        _isExactDecimalResult = isExactDecimalResult;
        _releaseCommand = releaseCommand;
        _onDispose = onDispose;
    }

    private bool IsBooleanColumn(int ordinal)
    {
        if (!_columnIsBoolean.TryGetValue(ordinal, out bool value))
        {
            (string? table, string? column) = GetBaseColumn(ordinal);
            value = _isBooleanColumn(table ?? string.Empty, column ?? GetName(ordinal));
            _columnIsBoolean[ordinal] = value;
        }
        return value;
    }

    private (string? Table, string? Column) GetBaseColumn(int ordinal)
    {
        if (_baseColumns.TryGetValue(ordinal, out var result))
        {
            return result;
        }

        DataTable? schema = _inner.GetSchemaTable();
        if (schema == null || ordinal >= schema.Rows.Count)
        {
            result = (null, null);
        }
        else
        {
            DataRow row = schema.Rows[ordinal];
            result = (
                row.Table.Columns.Contains("BaseTableName") ? row["BaseTableName"] as string : null,
                row.Table.Columns.Contains("BaseColumnName") ? row["BaseColumnName"] as string : null);
        }
        _baseColumns[ordinal] = result;
        return result;
    }

    private DataType? GetColumnType(int ordinal)
    {
        (string? table, string? column) = GetBaseColumn(ordinal);
        return table == null || column == null ? null : _getColumnType(table, column);
    }

    private object ConvertValue(int ordinal, object value)
    {
        DataType? type = GetColumnType(ordinal);
        return AccessValueCodec.ConvertFromSqlite(value, type, type == null
            && _isExactDecimalResult?.Invoke(ordinal) == true);
    }

    private string DataTypeName(int ordinal)
    {
        return GetColumnType(ordinal) switch
        {
            DataType.ShortDateTime => "SHORT_DATE_TIME",
            DataType.ExtDateTime => "EXT_DATE_TIME",
            DataType.Boolean => "BOOLEAN",
            DataType.Byte => "BYTE",
            DataType.Int => "INT",
            DataType.Long => "LONG",
            DataType.BigInt => "BIG_INT",
            DataType.Money => "MONEY",
            DataType.Numeric => "NUMERIC",
            DataType.ComplexType => "COMPLEX",
            DataType.Text => "TEXT",
            DataType.Memo => "MEMO",
            DataType.Binary => "BINARY",
            DataType.Ole => "OLE",
            DataType.Guid => "GUID",
            _ => string.Empty,
        };
    }

    public override object this[int ordinal]
        => GetValue(ordinal);

    public override object this[string name]
        => GetValue(GetOrdinal(name));

    public override int Depth => _inner.Depth;

    public override int FieldCount => _inner.FieldCount;

    public override bool HasRows => _inner.HasRows;

    public override bool IsClosed => _inner.IsClosed;

    public override int RecordsAffected => _inner.RecordsAffected;

    public override bool GetBoolean(int ordinal)
        => IsBooleanColumn(ordinal) ? Convert.ToBoolean(GetValue(ordinal)) : _inner.GetBoolean(ordinal);

    public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal),
        System.Globalization.CultureInfo.InvariantCulture);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => _inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);

    public override char GetChar(int ordinal) => Convert.ToChar(GetValue(ordinal),
        System.Globalization.CultureInfo.InvariantCulture);

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => _inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);

    public override string GetDataTypeName(int ordinal)
        => DataTypeName(ordinal) is { Length: > 0 } typeName
            ? typeName
            : _isExactDecimalResult?.Invoke(ordinal) == true ? "NUMERIC"
            : IsBooleanColumn(ordinal) ? "BOOLEAN" : _inner.GetDataTypeName(ordinal);

    public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(GetValue(ordinal),
        System.Globalization.CultureInfo.InvariantCulture);

    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal),
        System.Globalization.CultureInfo.InvariantCulture);

    public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal),
        System.Globalization.CultureInfo.InvariantCulture);

    public override IEnumerator GetEnumerator()
    {
        while (Read())
        {
            yield return this;
        }
    }

    public override Type GetFieldType(int ordinal)
        => GetColumnType(ordinal) switch
        {
            DataType.Boolean => typeof(bool),
            DataType.Byte => typeof(byte),
            DataType.Int => typeof(short),
            DataType.Long => typeof(long),
            DataType.BigInt => typeof(long),
            DataType.Float => typeof(float),
            DataType.Double => typeof(double),
            DataType.Money or DataType.Numeric => typeof(decimal),
            DataType.ComplexType => typeof(object),
            DataType.ShortDateTime or DataType.ExtDateTime => typeof(DateTime),
            DataType.Guid => typeof(Guid),
            DataType.Binary or DataType.Ole => typeof(byte[]),
            _ => _isExactDecimalResult?.Invoke(ordinal) == true ? typeof(decimal) : _inner.GetFieldType(ordinal),
        };

    public override T GetFieldValue<T>(int ordinal)
    {
        object value = GetValue(ordinal);
        if (value is T typed)
        {
            return typed;
        }
        return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal),
        System.Globalization.CultureInfo.InvariantCulture);

    public override Guid GetGuid(int ordinal) => GetValue(ordinal) is Guid guid
        ? guid
        : Guid.Parse(GetValue(ordinal).ToString()!);

    public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal),
        System.Globalization.CultureInfo.InvariantCulture);

    public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal),
        System.Globalization.CultureInfo.InvariantCulture);

    public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal),
        System.Globalization.CultureInfo.InvariantCulture);

    public override string GetName(int ordinal) => _inner.GetName(ordinal);

    public override int GetOrdinal(string name) => _inner.GetOrdinal(name);

    public override string GetString(int ordinal)
    {
        object value = GetValue(ordinal);
        return value is string text
            ? text
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
                ?? string.Empty;
    }

    public override object GetValue(int ordinal)
    {
        object raw = _inner.GetValue(ordinal);
        if (raw is null or DBNull)
        {
            return DBNull.Value;
        }
        return ConvertValue(ordinal, raw);
    }

    public override int GetValues(object[] values)
    {
        int count = _inner.FieldCount;
        for (int i = 0; i < count && i < values.Length; i++)
        {
            values[i] = GetValue(i);
        }
        count = Math.Min(count, values.Length);
        return count;
    }

    public override bool IsDBNull(int ordinal) => _inner.IsDBNull(ordinal);

    public override bool NextResult() => _inner.NextResult();

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NextResult());
    }

    public override bool Read() => _inner.Read();

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read());
    }

    public override DataTable? GetSchemaTable()
    {
        DataTable? schema = _inner.GetSchemaTable();
        if (schema == null)
        {
            return null;
        }
        foreach (DataRow row in schema.Rows)
        {
            int ordinal = Convert.ToInt32(row["ColumnOrdinal"]);
            string typeName = DataTypeName(ordinal);
            if (typeName.Length > 0)
            {
                row["DataType"] = GetFieldType(ordinal);
                row["DataTypeName"] = typeName;
            }
        }
        return schema;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try
            {
                _inner.Dispose();
            }
            finally
            {
                try
                {
                    _releaseCommand(_command);
                }
                finally
                {
                    _onDispose?.Invoke();
                }
            }
        }
        base.Dispose(disposing);
    }
}
