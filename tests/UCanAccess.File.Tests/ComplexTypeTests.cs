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
