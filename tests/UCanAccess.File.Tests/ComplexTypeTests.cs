using UCanAccess.File;
using Xunit;

namespace UCanAccess.File.Tests;

public sealed class ComplexTypeTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Reads_multivalue_and_attachment_child_tables()
    {
        using var db = Database.Open(Fixture("generated/complex.accdb"));
        Table table = db.GetTable("ComplexFixture")!;
        Row row = Assert.Single(table.Rows());

        AccessSingleValue[] values = Assert.IsType<AccessSingleValue[]>(row[1]);
        Assert.Equal(new object?[] { "alpha", "beta" }, values.Select(v => v.Value));

        AccessAttachment attachment = Assert.Single(Assert.IsType<AccessAttachment[]>(row[2]));
        Assert.Equal("uca-attachment.txt", attachment.FileName);
        Assert.Equal("txt", attachment.FileType);
        Assert.NotNull(attachment.FileData);
        Assert.NotEmpty(attachment.FileData!);
    }

    [Fact]
    public void Attachment_metadata_round_trips_through_flat_tables()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uca-complex-meta-{Guid.NewGuid():N}.accdb");
        System.IO.File.Copy(Fixture("generated/complex.accdb"), path);
        try
        {
            AccessAttachment source;
            using (var db = Database.Open(path, readOnly: true))
            {
                source = Assert.Single(Assert.IsType<AccessAttachment[]>(
                    Assert.Single(db.GetTable("ComplexFixture")!.Rows())[2]));
                // COM-generated fixture carries full attachment metadata.
                Assert.Equal("uca-attachment.txt", source.FileName);
                Assert.Equal("txt", source.FileType);
                Assert.NotNull(source.FileData);
                Assert.NotEmpty(source.FileData!);
            }

            var copy = new AccessAttachment(
                new byte[] { 10, 20, 30 },
                source.FileFlags,
                source.FileName,
                source.FileTimeStamp,
                source.FileType,
                source.FileURL);
            using (var db = Database.Open(path, readOnly: false))
            {
                db.GetTable("ComplexFixture")!.AddRow(new object?[]
                {
                    3, new[] { new AccessSingleValue("meta") }, new[] { copy },
                });
            }

            using (var db = Database.Open(path, readOnly: true))
            {
                AccessAttachment reread = Assert.Single(Assert.IsType<AccessAttachment[]>(
                    db.GetTable("ComplexFixture")!.Rows().Single(row => Convert.ToInt32(row[0]) == 3)[2]));
                Assert.Equal(copy.FileFlags, reread.FileFlags);
                Assert.Equal(copy.FileName, reread.FileName);
                Assert.Equal(copy.FileTimeStamp, reread.FileTimeStamp);
                Assert.Equal(copy.FileType, reread.FileType);
                Assert.Equal(copy.FileURL, reread.FileURL);
                Assert.Equal(copy.FileData, reread.FileData);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Complex_children_read_through_another_handle_are_not_stale()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uca-complex-stale-{Guid.NewGuid():N}.accdb");
        System.IO.File.Copy(Fixture("generated/complex.accdb"), path);
        try
        {
            using var db = Database.Open(path, readOnly: false);
            Table t1 = db.GetTable("ComplexFixture")!;
            Assert.Single(t1.Rows());

            Table t2 = db.GetTable("ComplexFixture")!;
            t2.AddRow(new object?[]
            {
                2,
                new[] { new AccessSingleValue("fresh") },
                new[] { new AccessAttachment(new byte[] { 1 }, 0, "fresh.bin", null, "bin", null) },
            });

            // t1 built its child lookup before t2 wrote: it must rebuild,
            // not serve the pre-write (single-row) snapshot.
            List<Row> rows = t1.Rows().ToList();
            Assert.Equal(2, rows.Count);
            Row added = rows.Single(row => Convert.ToInt32(row[0]) == 2);
            Assert.Equal(new[] { "fresh" },
                Assert.IsType<AccessSingleValue[]>(added[1]).Select(value => value.Value));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Complex_metadata_exposes_hidden_flat_tables()
    {
        using var db = Database.Open(Fixture("generated/complex.accdb"));
        Assert.NotNull(db.GetSystemTable("MSysComplexColumns"));
        Assert.Contains(db.GetSystemTableNames(), name => name.StartsWith("MSysComplexType", StringComparison.Ordinal));
    }

    [Fact]
    public void Writes_multivalue_and_attachment_values_through_flat_tables()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uca-complex-write-{Guid.NewGuid():N}.accdb");
        System.IO.File.Copy(Fixture("generated/complex.accdb"), path);
        try
        {
            using (var db = Database.Open(path, readOnly: false))
            {
                Table table = db.GetTable("ComplexFixture")!;
                table.AddRow(new object?[]
                {
                    2,
                    new[] { new AccessSingleValue("gamma"), new AccessSingleValue("delta") },
                    new[] { new AccessAttachment(new byte[] { 1, 2, 3 }, 0, "new.bin", null, "bin", null) },
                });

                Row second = table.Rows().Single(row => Convert.ToInt32(row[0]) == 2);
                Assert.Equal(new[] { "gamma", "delta" },
                    Assert.IsType<AccessSingleValue[]>(second[1]).Select(value => value.Value));
                Assert.Equal("new.bin", Assert.Single(Assert.IsType<AccessAttachment[]>(second[2])).FileName);

                table.UpdateRow(table.RowLocations().Single(location => Convert.ToInt32(location.Row[0]) == 2).PageNumber,
                    table.RowLocations().Single(location => Convert.ToInt32(location.Row[0]) == 2).RowNumber,
                    new object?[] { 2, new[] { new AccessSingleValue("updated") },
                        new[] { new AccessAttachment(new byte[] { 9 }, 0, "updated.bin", null, "bin", null) } });
            }

            using var verify = Database.Open(path, readOnly: true);
            Row updated = verify.GetTable("ComplexFixture")!.Rows()
                .Single(row => Convert.ToInt32(row[0]) == 2);
            Assert.Equal(new[] { "updated" },
                Assert.IsType<AccessSingleValue[]>(updated[1]).Select(value => value.Value));
            Assert.Equal("updated.bin", Assert.Single(Assert.IsType<AccessAttachment[]>(updated[2])).FileName);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
