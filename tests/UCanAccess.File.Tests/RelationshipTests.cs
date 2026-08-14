using UCanAccess.File;
using Xunit;

namespace UCanAccess.File.Tests;

/// <summary>
/// P1.2: reading relationships (foreign keys) from <c>MSysRelationships</c> in the
/// Java-created <c>genRelated.mdb</c> (t_parent PK <c>id</c> referenced by t_child
/// <c>parent_id</c>, with cascade updates and deletes).
/// </summary>
public class RelationshipTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Reads_relationship_between_tables()
    {
        using var db = Database.Open(Fixture("generated/genRelated.mdb"));
        var rels = db.GetRelationships("t_parent", "t_child");
        var rel = Assert.Single(rels);

        Assert.Equal("t_parent", rel.FromTable.Name);
        Assert.Equal("t_child", rel.ToTable.Name);
        Assert.Equal("id", Assert.Single(rel.FromColumns).Name);
        Assert.Equal("parent_id", Assert.Single(rel.ToColumns).Name);
        Assert.True(rel.HasReferentialIntegrity);
        Assert.True(rel.CascadeUpdates);
        Assert.True(rel.CascadeDeletes);
        Assert.False(rel.IsOneToOne);
    }

    [Fact]
    public void Reads_relationships_for_a_table()
    {
        using var db = Database.Open(Fixture("generated/genRelated.mdb"));
        var rels = db.GetRelationships("t_child");
        var rel = Assert.Single(rels);
        Assert.Equal("t_parent", rel.FromTable.Name);
    }

    [Fact]
    public void Database_without_relationships_returns_empty()
    {
        using var db = Database.Open(Fixture("generated/genIndexed.mdb"));
        Assert.Empty(db.GetRelationships());
    }

    [Fact]
    public void One_to_one_relationship_creates_unique_index_and_rejects_duplicates()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uca-one-to-one-{Guid.NewGuid():N}.mdb");
        try
        {
            using var db = Database.Create(path);
            db.CreateTable("parent", new[]
            {
                new ColumnBuilder("id", DataType.Long),
            }, new[]
            {
                new IndexBuilder("pk_parent").WithPrimaryKey().WithColumns("id"),
            });
            Table child = db.CreateTable("child", new[]
            {
                new ColumnBuilder("id", DataType.Long),
                new ColumnBuilder("parent_id", DataType.Long),
            }, new[]
            {
                new IndexBuilder("pk_child").WithPrimaryKey().WithColumns("id"),
            });
            db.GetTable("parent")!.AddRow(new object?[] { 1 });

            db.AddRelationship(new RelationshipBuilder("one_to_one", "parent", "child")
                .WithColumns("id", "parent_id")
                .WithOneToOne());
            Assert.True(db.GetIndexInfo("child").Single(index => index.ForeignKey).Unique);

            child = db.GetTable("child")!;
            child.AddRow(new object?[] { 1, 1 });
            Assert.Throws<DatabaseException>(() => child.AddRow(new object?[] { 2, 1 }));
        }
        finally
        {
            System.IO.File.Delete(path);
            System.IO.File.Delete(Path.ChangeExtension(path, ".ldb"));
        }
    }

    [Fact]
    public void Relationship_flag_constants()
    {
        Assert.Equal(0x00000001, Relationship.OneToOneFlag);
        Assert.Equal(0x00000002, Relationship.NoReferentialIntegrityFlag);
        Assert.Equal(0x00000100, Relationship.CascadeUpdatesFlag);
        Assert.Equal(0x00001000, Relationship.CascadeDeletesFlag);
    }

    [Fact]
    public void Add_child_with_unknown_parent_throws()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_fk_{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Fixture("generated/genRelated.mdb"), tmp, true);
        try
        {
            using (var db = Database.Open(tmp, readOnly: false))
            {
                var t = db.GetTable("t_child")!;
                // parent_id 99 does not exist
                Assert.Throws<DatabaseException>(() => t.AddRow(new object?[] { null, 99, "orphan" }));
                // valid parent id works
                t.AddRow(new object?[] { null, 1, "valid child" });
            }
            using (var db = Database.Open(tmp))
            {
                var t = db.GetTable("t_child")!;
                Assert.Equal(3, t.RowCount);
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Delete_with_enforcement_disabled_is_allowed()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_fk_off_{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Fixture("generated/genRelated.mdb"), tmp, true);
        try
        {
            using (var db = Database.Open(tmp, readOnly: false))
            {
                db.EnforceForeignKeys = false;
                var parent = db.GetTable("t_parent")!;
                var (page, rnum) = FindRowId(parent, "parent one");
                // with enforcement off, the referenced parent can be deleted
                parent.DeleteRow(page, rnum);
            }
            using (var db = Database.Open(tmp))
            {
                var parent = db.GetTable("t_parent")!;
                Assert.Equal(1, parent.RowCount);
                // the child keeps a dangling reference (allowed without enforcement)
                var child = db.GetTable("t_child")!;
                Assert.Equal(2, child.RowCount);
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void Cascade_delete_removes_children()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"ucanaccess_fk_casc_{Guid.NewGuid():N}.mdb");
        System.IO.File.Copy(Fixture("generated/genRelated.mdb"), tmp, true);
        try
        {
            using (var db = Database.Open(tmp, readOnly: false))
            {
                var parent = db.GetTable("t_parent")!;
                var (page, rnum) = FindRowId(parent, "parent two");
                // cascade deletes are enabled in genRelated: deleting a parent removes its children
                parent.DeleteRow(page, rnum);
            }
            using (var db = Database.Open(tmp))
            {
                var child = db.GetTable("t_child")!;
                var notes = child.Rows().Select(r => (string)r["note"]!).ToList();
                Assert.Equal(new[] { "child of one" }, notes); // child of two removed by cascade
                var parent = db.GetTable("t_parent")!;
                Assert.Equal(1, parent.RowCount);
            }
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    private static (int Page, int RowNumber) FindRowId(Table table, string name)
    {
        foreach (Table.RowLocation location in table.RowLocations())
        {
            if (Equals(location.Row["name"], name))
            {
                return (location.PageNumber, location.RowNumber);
            }
        }
        throw new InvalidOperationException($"row '{name}' not found");
    }
}
