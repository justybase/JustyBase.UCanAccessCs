namespace UCanAccess.File;

/// <summary>one column of an index (for metadata APIs)</summary>
public sealed class IndexColumnInfo
{
    internal IndexColumnInfo(string name, bool ascending)
    {
        Name = name;
        Ascending = ascending;
    }

    public string Name { get; }

    public bool Ascending { get; }
}

/// <summary>a logical index of a table (for metadata APIs)</summary>
public sealed class IndexInfo
{
    internal IndexInfo(string name, IReadOnlyList<IndexColumnInfo> columns, bool unique, bool primaryKey, bool required, bool ignoreNulls)
    {
        Name = name;
        Columns = columns;
        Unique = unique;
        PrimaryKey = primaryKey;
        Required = required;
        IgnoreNulls = ignoreNulls;
    }

    public string Name { get; }

    public IReadOnlyList<IndexColumnInfo> Columns { get; }

    public bool Unique { get; }

    public bool PrimaryKey { get; }

    public bool Required { get; }

    public bool IgnoreNulls { get; }
}
