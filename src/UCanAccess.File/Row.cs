namespace UCanAccess.File;

/// <summary>
/// Uniquely identifies a row of data within the access database
/// (port of Jackcess <c>RowIdImpl</c>).
/// </summary>
public readonly record struct RowId(int PageNumber, int RowNumber) : IComparable<RowId>
{
    /// <summary>the row number of a deleted/invalid row</summary>
    public const int InvalidRowNumber = -1;

    /// <summary>special page number which sorts before any other valid page number</summary>
    public const int FirstPageNumber = -1;

    /// <summary>special page number which sorts after any other valid page number</summary>
    public const int LastPageNumber = -2;

    /// <summary>rowId which sorts before any other valid rowId</summary>
    public static readonly RowId FirstRowId = new(FirstPageNumber, InvalidRowNumber);

    /// <summary>rowId which sorts after any other valid rowId</summary>
    public static readonly RowId LastRowId = new(LastPageNumber, InvalidRowNumber);

    /// <summary>type attributes for rowIds which simplify comparisons</summary>
    public enum Type
    {
        AlwaysFirst,
        Normal,
        AlwaysLast,
    }

    public Type IdType => PageNumber == FirstPageNumber ? Type.AlwaysFirst : PageNumber == LastPageNumber ? Type.AlwaysLast : Type.Normal;

    /// <summary>whether this rowId potentially represents an actual row of data</summary>
    public bool IsValid => RowNumber >= 0 && PageNumber >= 0;

    public int CompareTo(RowId other)
    {
        int compare = IdType.CompareTo(other.IdType);
        if (compare == 0)
        {
            compare = PageNumber.CompareTo(other.PageNumber);
            if (compare == 0)
            {
                compare = RowNumber.CompareTo(other.RowNumber);
            }
        }
        return compare;
    }
}

/// <summary>
/// A single row of data from an Access table, keyed by column name.
/// </summary>
public sealed class Row
{
    private readonly object?[] _values;
    private readonly IReadOnlyDictionary<string, int> _nameToIndex;

    internal Row(Table table, object?[] values)
    {
        Table = table;
        _values = values;
        _nameToIndex = table.ColumnIndexes;
    }

    public Table Table { get; }

    public int Count => _values.Length;

    public object? this[int index] => _values[index];

    public object? this[string name] => _values[_nameToIndex[name]];

    public bool TryGetValue(string name, out object? value)
    {
        if (_nameToIndex.TryGetValue(name, out int index))
        {
            value = _values[index];
            return true;
        }
        value = null;
        return false;
    }

    public object?[] ToArray() => (object?[])_values.Clone();

    public override string ToString()
    {
        return string.Join(", ", _values.Select(v => v switch
        {
            null => "null",
            byte[] bytes => $"<{bytes.Length} bytes>",
            _ => v.ToString()
        }));
    }
}
