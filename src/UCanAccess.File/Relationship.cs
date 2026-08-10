namespace UCanAccess.File;

/// <summary>
/// Describes a relationship between two tables (port of Jackcess <c>RelationshipImpl</c>).
/// The "from" table is the referenced (primary) side; the "to" table is the referencing
/// (foreign) side.
/// </summary>
public sealed class Relationship
{
    /// <summary>flag indicating a one-to-one relationship</summary>
    public const int OneToOneFlag = 0x00000001;

    /// <summary>flag indicating no referential integrity</summary>
    public const int NoReferentialIntegrityFlag = 0x00000002;

    /// <summary>flag indicating cascading updates (requires referential integrity)</summary>
    public const int CascadeUpdatesFlag = 0x00000100;

    /// <summary>flag indicating cascading deletes (requires referential integrity)</summary>
    public const int CascadeDeletesFlag = 0x00001000;

    /// <summary>flag indicating cascading null on delete (requires referential integrity)</summary>
    public const int CascadeNullFlag = 0x00002000;

    internal Relationship(string name, Table fromTable, Table toTable, int flags, int columnCount)
    {
        Name = name;
        FromTable = fromTable;
        ToTable = toTable;
        Flags = flags;
        FromColumns = new Column[columnCount];
        ToColumns = new Column[columnCount];
    }

    public string Name { get; }

    /// <summary>the referenced (primary) table</summary>
    public Table FromTable { get; }

    /// <summary>the referencing (foreign) table</summary>
    public Table ToTable { get; }

    /// <summary>the columns on the referenced (primary) table</summary>
    public Column[] FromColumns { get; }

    /// <summary>the columns on the referencing (foreign) table</summary>
    public Column[] ToColumns { get; }

    public int Flags { get; }

    public bool IsOneToOne => (Flags & OneToOneFlag) != 0;

    public bool HasReferentialIntegrity => (Flags & NoReferentialIntegrityFlag) == 0;

    public bool CascadeUpdates => (Flags & CascadeUpdatesFlag) != 0;

    public bool CascadeDeletes => (Flags & CascadeDeletesFlag) != 0;

    public bool CascadeNullOnDelete => (Flags & CascadeNullFlag) != 0;
}
