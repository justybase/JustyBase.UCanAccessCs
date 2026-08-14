namespace UCanAccess.File;

/// <summary>Describes a saved Access foreign-key relationship.</summary>
public sealed class RelationshipBuilder
{
    public RelationshipBuilder(string name, string fromTable, string toTable)
    {
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Relationship name is required.", nameof(name)) : name;
        FromTable = fromTable;
        ToTable = toTable;
    }

    /// <summary>The referenced/primary table.</summary>
    public string FromTable { get; }

    /// <summary>The referencing/foreign table.</summary>
    public string ToTable { get; }

    public string Name { get; }

    public List<(string FromColumn, string ToColumn)> Columns { get; } = new();

    public bool OneToOne { get; private set; }
    public bool ReferentialIntegrity { get; private set; } = true;
    public bool CascadeUpdates { get; private set; }
    public bool CascadeDeletes { get; private set; }
    public bool CascadeNullOnDelete { get; private set; }

    public RelationshipBuilder WithColumns(string fromColumn, string toColumn)
    {
        Columns.Add((fromColumn, toColumn));
        return this;
    }

    public RelationshipBuilder WithOneToOne(bool enabled = true)
    {
        OneToOne = enabled;
        return this;
    }

    public RelationshipBuilder WithReferentialIntegrity(bool enabled = true)
    {
        ReferentialIntegrity = enabled;
        return this;
    }

    public RelationshipBuilder WithCascadeUpdates(bool enabled = true)
    {
        CascadeUpdates = enabled;
        return this;
    }

    public RelationshipBuilder WithCascadeDeletes(bool enabled = true)
    {
        CascadeDeletes = enabled;
        return this;
    }

    public RelationshipBuilder WithCascadeNullOnDelete(bool enabled = true)
    {
        CascadeNullOnDelete = enabled;
        return this;
    }
}
