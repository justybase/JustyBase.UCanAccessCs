using UCanAccess.File;

string samplesDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "linked"));
string aPath = Path.Combine(samplesDir, "A.mdb");
string bPath = Path.Combine(samplesDir, "B.mdb");

Directory.CreateDirectory(samplesDir);
foreach (string path in new[] { aPath, bPath })
{
    if (File.Exists(path))
    {
        Console.WriteLine($"Removing existing {path}");
        File.Delete(path);
    }
}

Console.WriteLine($"Creating {aPath} (linkee, Jet 4)");
using (var db = Database.Create(aPath))
{
    Table people = db.CreateTable("t_people", new[]
    {
        new ColumnBuilder("id", DataType.Long).WithAutoNumber(),
        new ColumnBuilder("name", DataType.Text).WithLength(40),
        new ColumnBuilder("age", DataType.Int),
    });
    people.AddRow(new object?[] { null, "Anna Smith", 34 });
    people.AddRow(new object?[] { null, "John Doe", 41 });
    people.AddRow(new object?[] { null, "Eva Williams", 29 });
    people.AddRow(new object?[] { null, "Peter Johnson", 52 });
    Console.WriteLine($"  t_people: {people.RowCount} rows");
}

Console.WriteLine($"Creating {bPath} (links to A.mdb::t_people)");
using (var db = Database.Create(bPath))
{
    AddLinkedTable(db, "t_people_link", "A.mdb", "t_people");
}

Console.WriteLine("Done.");
return;

static void AddLinkedTable(Database db, string name, string linkedDbName, string linkedTableName)
{
    // replicate Database's catalog discovery of the virtual "Tables" container id
    const int dbParentId = 0xF000000;
    int tablesParentId = -1;
    foreach (Row row in db.SystemCatalog.Rows())
    {
        if (row["Name"] is string n
            && string.Equals(n, "Tables", StringComparison.OrdinalIgnoreCase)
            && row["ParentId"] is int p
            && p == dbParentId
            && row["Id"] is int id)
        {
            tablesParentId = id;
            break;
        }
    }
    if (tablesParentId <= 0)
    {
        throw new InvalidOperationException("Did not find required parent table id");
    }

    // synthetic page number not used by a real table definition
    const int objectId = 0x00FFFFFF;
    var values = new object?[db.SystemCatalog.Columns.Count];
    for (int i = 0; i < db.SystemCatalog.Columns.Count; i++)
    {
        string colName = db.SystemCatalog.Columns[i].Name;
        values[i] = colName switch
        {
            "Id" => objectId,
            "Name" => name,
            "Type" => (byte)6, // TypeLinkedTable
            "DateUpdate" or "DateCreate" => DateTime.Now,
            "Flags" => 0,
            "ParentId" => tablesParentId,
            "Database" => linkedDbName,
            "ForeignName" => linkedTableName,
            _ => null,
        };
    }
    db.SystemCatalog.AddRow(values);
    Console.WriteLine($"  {name} -> {linkedDbName}::{linkedTableName}");
}
