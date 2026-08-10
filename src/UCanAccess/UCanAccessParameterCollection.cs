using System.Collections;
using System.Data.Common;

namespace UCanAccess;

public sealed class UCanAccessParameterCollection : DbParameterCollection
{
    private readonly List<UCanAccessParameter> _items = new();

    public override int Count => _items.Count;

    public override object SyncRoot => ((ICollection)_items).SyncRoot;

    public override int Add(object value)
    {
        if (value is not UCanAccessParameter parameter)
        {
            if (value is not DbParameter source)
            {
                parameter = new UCanAccessParameter { Value = value };
            }
            else
            {
                parameter = Copy(source);
            }
        }
        _items.Add(parameter);
        return _items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (object value in values)
        {
            Add(value);
        }
    }

    public override void Clear() => _items.Clear();

    public override bool Contains(object value)
        => value is UCanAccessParameter p && _items.Contains(p);

    public override bool Contains(string value)
        => _items.Any(p => string.Equals(p.ParameterName, value, StringComparison.OrdinalIgnoreCase));

    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _items.GetEnumerator();

    public override int IndexOf(object value)
        => value is UCanAccessParameter p ? _items.IndexOf(p) : -1;

    public override int IndexOf(string parameterName)
        => _items.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));

    public override void Insert(int index, object value)
        => _items.Insert(index, value is UCanAccessParameter parameter
            ? parameter
            : value is DbParameter source ? Copy(source) : new UCanAccessParameter { Value = value });

    public override void Remove(object value)
    {
        if (value is UCanAccessParameter p)
        {
            _items.Remove(p);
        }
    }

    public override void RemoveAt(int index) => _items.RemoveAt(index);

    public override void RemoveAt(string parameterName)
        => _items.RemoveAt(IndexOf(parameterName));

    protected override DbParameter GetParameter(int index) => _items[index];

    protected override DbParameter GetParameter(string parameterName)
        => _items[IndexOf(parameterName)];

    protected override void SetParameter(int index, DbParameter value)
        => _items[index] = Copy(value);

    protected override void SetParameter(string parameterName, DbParameter value)
        => _items[IndexOf(parameterName)] = Copy(value);

    private static UCanAccessParameter Copy(DbParameter source)
        => new()
        {
            DbType = source.DbType,
            Direction = source.Direction,
            IsNullable = source.IsNullable,
            ParameterName = source.ParameterName,
            SourceColumn = source.SourceColumn,
            SourceColumnNullMapping = source.SourceColumnNullMapping,
            SourceVersion = source.SourceVersion,
            Size = source.Size,
            Precision = source.Precision,
            Scale = source.Scale,
            Value = source.Value,
        };
}
