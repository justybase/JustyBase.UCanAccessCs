using System.Diagnostics.CodeAnalysis;
using UCanAccess.File;

namespace UCanAccess;

/// <summary>
/// Resolved metadata for one result column of a mirror statement.
/// </summary>
internal readonly struct ReaderColumn
{
    /// <summary>Creates a column entry from its resolved SQLite origin and Access classification.</summary>
    public ReaderColumn(string? table, string? column, DataType? type, bool isBoolean,
        bool isExactDecimalProjection)
    {
        Table = table;
        Column = column;
        Type = type;
        IsBoolean = isBoolean;
        IsExactDecimalProjection = isExactDecimalProjection;
    }

    /// <summary>the mirrored base table of the column, when the projection is a plain column reference</summary>
    public string? Table { get; }

    /// <summary>the base column name, when the projection is a plain column reference</summary>
    public string? Column { get; }

    /// <summary>the Access type of the base column, or null for expressions</summary>
    public DataType? Type { get; }

    /// <summary>whether the column carries Access BOOLEAN values (SQLite INTEGER 0/1)</summary>
    public bool IsBoolean { get; }

    /// <summary>whether the projection's outer expression is an exact-decimal operation</summary>
    public bool IsExactDecimalProjection { get; }
}

/// <summary>
/// Per-mirror memoization of <see cref="ReaderColumn"/> metadata, keyed by the
/// translated statement text.
/// <para>
/// Microsoft.Data.Sqlite's <c>GetSchemaTable</c> runs three SQL statements per
/// result column to report column origins — a unique-index probe, a
/// <c>SELECT typeof(col) … GROUP BY typeof(col)</c> query that scans the whole
/// base table, and a <c>sqlite_master</c> lookup. Column origins are a pure
/// function of the statement text and the mirrored schema, so the result is
/// resolved once and reused by every later read of the same statement.
/// </para>
/// Entries are dropped together with the translation cache, i.e. whenever table
/// or column metadata can change.
/// </summary>
internal sealed class ReaderColumnCache
{
    private const int MaxEntries = 256;
    private readonly Dictionary<string, ReaderColumn[]> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string sql, [NotNullWhen(true)] out ReaderColumn[]? columns)
    {
        lock (_entries)
        {
            return _entries.TryGetValue(sql, out columns);
        }
    }

    public void Store(string sql, ReaderColumn[] columns)
    {
        lock (_entries)
        {
            if (_entries.Count >= MaxEntries)
            {
                _entries.Clear();
            }
            _entries[sql] = columns;
        }
    }

    public void Clear()
    {
        lock (_entries)
        {
            _entries.Clear();
        }
    }
}
