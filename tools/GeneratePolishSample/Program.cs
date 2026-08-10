using UCanAccess.File;

string samplesDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples"));
string dbPath = Path.Combine(samplesDir, "sample_polish.accdb");
int count = 20;

if (File.Exists(dbPath))
{
    Console.WriteLine($"Removing existing {dbPath}");
    File.Delete(dbPath);
}

Console.WriteLine($"Creating {dbPath} (version 2016, Polish sort order text index)");
using (var db = Database.Create(dbPath, version: "2016"))
{
    Table table = db.CreateTable("sample_table_01", new[]
    {
        new ColumnBuilder("Identyfikator", DataType.Long).WithAutoNumber(),
        new ColumnBuilder("text_col1", DataType.Text)
            .WithLength(510)
            .WithRequired()
            .WithTextSortOrder(TextSortOrder.Polish),
        new ColumnBuilder("num_col2", DataType.Long),
    }, new[]
    {
        new IndexBuilder("PrimaryKey")
            .WithColumns("text_col1")
            .WithPrimaryKey()
            .WithRequired(),
        new IndexBuilder("num_col2")
            .WithColumns("num_col2"),
    });

    Console.WriteLine($"Inserting {count} rows");
    using (var batch = db.BeginWriteBatch())
    {
        for (int i = 0; i < count; i++)
        {
            table.AddRow(new object?[] { null, $"row_{i:D6}_text", i });
        }
        batch.Commit();
    }
    Console.WriteLine($"Table now has {table.RowCount} rows.");
}

Console.WriteLine("Done.");
