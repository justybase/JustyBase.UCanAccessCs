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
    private readonly Func<string, int, bool[]>? _exactDecimalProjections;
    private readonly Action<SqliteCommand> _releaseCommand;
    private readonly Action? _onDispose;
    private readonly ReaderColumnCache? _resultColumns;
    private readonly string _sql;
    private ReaderColumn[]? _columns;
    private DataTable? _schemaTable;
    private bool _schemaTableResolved;
    private bool _disposed;

    internal MirrorReader(SqliteDataReader inner, SqliteCommand command,
        Func<string, string, bool> isBooleanColumn,
        Func<string, string, DataType?> getColumnType,
        Func<string, int, bool[]>? exactDecimalProjections,
        Action<SqliteCommand> releaseCommand,
        Action? onDispose = null,
        ReaderColumnCache? resultColumns = null,
        string sql = "")
    {
        _inner = inner;
        _command = command;
        _isBooleanColumn = isBooleanColumn;
        _getColumnType = getColumnType;
        _exactDecimalProjections = exactDecimalProjections;
        _releaseCommand = releaseCommand;
        _onDispose = onDispose;
        _resultColumns = resultColumns;
        _sql = sql;
    }

    private bool IsBooleanColumn(int ordinal)
        => (uint)ordinal < (uint)Columns.Length && Columns[ordinal].IsBoolean;

    /// <summary>
    /// Per-result-column metadata, resolved once per statement (and reused for
    /// later readers of the same translated SQL through the mirror's cache).
    /// </summary>
    private ReaderColumn[] Columns
    {
        get
        {
            ReaderColumn[]? columns = _columns;
            if (columns != null)
            {
                return columns;
            }
            if (_resultColumns != null && _resultColumns.TryGet(_sql, out ReaderColumn[]? cached))
            {
                _columns = cached;
                return cached;
            }
            columns = BuildColumns();
            _columns = columns;
            _resultColumns?.Store(_sql, columns);
            return columns;
        }
    }

    /// <summary>
    /// Source-table/column mapping for every result column. Microsoft.Data.Sqlite
    /// builds a fresh 20-column <see cref="DataTable"/> (plus three schema queries
    /// per column, one of which scans the base table) on every
    /// <c>GetSchemaTable</c> call, so it is fetched at most once per reader.
    /// </summary>
    private DataTable? SchemaTable
    {
        get
        {
            if (!_schemaTableResolved)
            {
                _schemaTableResolved = true;
                _schemaTable = _inner.GetSchemaTable();
            }
            return _schemaTable;
        }
    }

    private ReaderColumn[] BuildColumns()
    {
        int count = _inner.FieldCount;
        var columns = new ReaderColumn[count];
        DataTable? schema = SchemaTable;
        bool[]? exactDecimal = _exactDecimalProjections?.Invoke(_sql, count);
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            string? table = null;
            string? column = null;
            if (schema != null && ordinal < schema.Rows.Count)
            {
                DataRow row = schema.Rows[ordinal];
                table = row.Table.Columns.Contains("BaseTableName") ? row["BaseTableName"] as string : null;
                column = row.Table.Columns.Contains("BaseColumnName") ? row["BaseColumnName"] as string : null;
            }
            DataType? type = table == null || column == null ? null : _getColumnType(table, column);
            bool isBoolean = _isBooleanColumn(table ?? string.Empty, column ?? _inner.GetName(ordinal));
            columns[ordinal] = new ReaderColumn(table, column, type, isBoolean,
                exactDecimal != null && ordinal < exactDecimal.Length && exactDecimal[ordinal]);
        }
        return columns;
    }

    private DataType? GetColumnType(int ordinal)
        => (uint)ordinal < (uint)Columns.Length ? Columns[ordinal].Type : null;

    private bool IsExactDecimalResult(int ordinal)
        => (uint)ordinal < (uint)Columns.Length && Columns[ordinal].IsExactDecimalProjection;

    private object ConvertValue(int ordinal, object value)
    {
        DataType? type = GetColumnType(ordinal);
        return AccessValueCodec.ConvertFromSqlite(value, type, type == null
            && IsExactDecimalResult(ordinal));
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
            : IsExactDecimalResult(ordinal) ? "NUMERIC"
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

    // Matches the trim annotation of DbDataReader.GetFieldType (IL2093):
    // overrides must declare the same DynamicallyAccessedMembers usage.
    [return: DynamicallyAccessedMembers(
        DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
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
            _ => IsExactDecimalResult(ordinal) ? typeof(decimal) : _inner.GetFieldType(ordinal),
        };

    public override T GetFieldValue<T>(int ordinal)
    {
        object value = GetValue(ordinal);
        if (value is T typed)
        {
            return typed;
        }
        // AOT/trim-friendly conversions with statically visible target types.
        // (Convert.ChangeType(value, typeof(T)) relies on runtime conversion
        // dispatch keyed by a generic type argument, which the trimmer cannot
        // see; the explicit switch below keeps every conversion reachable.)
        if (value is DBNull)
        {
            throw new InvalidCastException(
                $"Cannot convert DBNull to {typeof(T).FullName}.");
        }
        Type targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (targetType.IsEnum)
        {
            object enumValue = Enum.ToObject(targetType,
                Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
            return (T)enumValue;
        }
        System.Globalization.CultureInfo invariant =
            System.Globalization.CultureInfo.InvariantCulture;
        object? converted = Type.GetTypeCode(targetType) switch
        {
            TypeCode.Boolean => Convert.ToBoolean(value, invariant),
            TypeCode.Char => Convert.ToChar(value, invariant),
            TypeCode.SByte => Convert.ToSByte(value, invariant),
            TypeCode.Byte => Convert.ToByte(value, invariant),
            TypeCode.Int16 => Convert.ToInt16(value, invariant),
            TypeCode.UInt16 => Convert.ToUInt16(value, invariant),
            TypeCode.Int32 => Convert.ToInt32(value, invariant),
            TypeCode.UInt32 => Convert.ToUInt32(value, invariant),
            TypeCode.Int64 => Convert.ToInt64(value, invariant),
            TypeCode.UInt64 => Convert.ToUInt64(value, invariant),
            TypeCode.Single => Convert.ToSingle(value, invariant),
            TypeCode.Double => Convert.ToDouble(value, invariant),
            TypeCode.Decimal => Convert.ToDecimal(value, invariant),
            TypeCode.DateTime => Convert.ToDateTime(value, invariant),
            TypeCode.String => Convert.ToString(value, invariant),
            _ when targetType == typeof(Guid) => value is Guid guid
                ? guid
                : Guid.Parse(value.ToString()!),
            _ when targetType == typeof(byte[]) => value is byte[] bytes
                ? bytes
                : throw new InvalidCastException(
                    $"Cannot convert {value.GetType().FullName} to byte[]."),
            _ => throw new InvalidCastException(
                $"Cannot convert {value.GetType().FullName} to {typeof(T).FullName}."),
        };
        return (T)converted!;
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

    public override Stream GetStream(int ordinal) => _inner.GetStream(ordinal);

    public override TextReader GetTextReader(int ordinal) => _inner.GetTextReader(ordinal);

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
