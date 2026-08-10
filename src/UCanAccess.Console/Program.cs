using System.Text;
using UCanAccess.File;

namespace UCanAccess.Console;

internal static class Program
{
    public static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }
        string path = args[0];

        if (args.Any(a => a is "--create" or "-c"))
        {
            return CreateDatabase(args.Skip(1).FirstOrDefault() ?? path);
        }

        bool showSystem = args.Any(a => a is "-s" or "--system");
        bool schemaOnly = args.Any(a => a is "--schema");
        bool showIndexes = args.Any(a => a is "-i" or "--indexes");
        bool showRelationships = args.Any(a => a is "-r" or "--relationships");
        string? tableFilter = GetOption(args, "--table");

        try
        {
            using var db = Database.Open(path);

            System.Console.WriteLine($"File:     {path}");
            System.Console.WriteLine($"Format:   {db.Format.Name} {(db.Format.ReadOnly ? "(read-only)" : "(read-write)")}");
            System.Console.WriteLine($"Encoding: {db.TextEncoding.WebName}");

            var tableNames = db.GetTableNames().ToList();
            if (showSystem)
            {
                tableNames.AddRange(db.GetSystemTableNames());
            }
            System.Console.WriteLine($"Tables ({tableNames.Count}):");
            foreach (string name in tableNames)
            {
                if (tableFilter != null && !name.Equals(tableFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                DumpTable(db, name, showSystem, schemaOnly, showIndexes);
            }

            if (showRelationships)
            {
                DumpRelationships(db);
            }
            return 0;
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int CreateDatabase(string path)
    {
        string version = "2003";
        int idx = Array.IndexOf(Environment.GetCommandLineArgs(), "--version");
        if (idx > 0 && idx + 1 < Environment.GetCommandLineArgs().Length)
        {
            version = Environment.GetCommandLineArgs()[idx + 1];
        }
        try
        {
            using var db = Database.Create(path, version: version);
            Table table = db.CreateTable("t_people", new[]
            {
                new ColumnBuilder("id", DataType.Long).WithAutoNumber(),
                new ColumnBuilder("name", DataType.Text).WithLength(60),
                new ColumnBuilder("age", DataType.Int),
                new ColumnBuilder("salary", DataType.Money),
                new ColumnBuilder("joined", DataType.ShortDateTime),
                new ColumnBuilder("active", DataType.Boolean),
            });
            table.AddRow(new object?[] { null, "Anna Kowalska", 34, 12500.50m, new DateTime(2020, 6, 15, 9, 30, 0), true });
            table.AddRow(new object?[] { null, "Jan Nowak", 41, 9800.00m, new DateTime(2018, 2, 1, 8, 0, 0), true });
            table.AddRow(new object?[] { null, "Ewa Wiśniewska", 29, 7400.75m, new DateTime(2023, 11, 20, 12, 15, 0), false });

            System.Console.WriteLine($"Created {path} ({db.Format.Name}) with table t_people ({table.RowCount} rows).");
            System.Console.WriteLine("Open it in MS Access / LibreOffice, or verify with:");
            System.Console.WriteLine($"  UCanAccess.Console {path}");
            return 0;
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static void DumpRelationships(Database db)
    {
        var relationships = db.GetRelationships();
        if (relationships.Count == 0)
        {
            System.Console.WriteLine("\nRelationships: (none)");
            return;
        }
        System.Console.WriteLine($"\nRelationships ({relationships.Count}):");
        foreach (Relationship rel in relationships)
        {
            string fromCols = string.Join(", ", rel.FromColumns.Select(c => c.Name));
            string toCols = string.Join(", ", rel.ToColumns.Select(c => c.Name));
            var flags = new List<string>();
            if (rel.IsOneToOne) flags.Add("1-1");
            if (!rel.HasReferentialIntegrity) flags.Add("no-RI");
            if (rel.CascadeUpdates) flags.Add("cascade-update");
            if (rel.CascadeDeletes) flags.Add("cascade-delete");
            if (rel.CascadeNullOnDelete) flags.Add("cascade-null");
            string flagStr = flags.Count > 0 ? $" [{string.Join(", ", flags)}]" : "";
            System.Console.WriteLine($"  {rel.Name}: {rel.FromTable.Name}({fromCols}) -> {rel.ToTable.Name}({toCols}){flagStr}");
        }
    }

    private static void DumpTable(Database db, string name, bool showSystem, bool schemaOnly, bool showIndexes)
    {
        try
        {
            Table? table = db.GetTable(name) ?? db.GetSystemTable(name);
            if (table == null)
            {
                System.Console.WriteLine($"\n== {name} == (not readable)");
                return;
            }

            System.Console.WriteLine($"\n== {name} == ({table.RowCount} rows)");
            foreach (Column col in table.Columns)
            {
                string typeInfo = col.Type.ToString();
                if (col.AutoNumber) typeInfo += " AUTOINCREMENT";
                if (col.VariableLength) typeInfo += $" len {col.ColumnLength}";
                System.Console.WriteLine($"  {col.Name,-30} {typeInfo,-24} #{col.ColumnNumber}");
            }

            if (showIndexes)
            {
                foreach (IndexImpl idx in table.Indexes)
                {
                    string cols = string.Join(", ", idx.IndexData.Columns.Select(c => c.Column.Name + (c.IsAscending ? "" : " DESC")));
                    var flags = new List<string>();
                    if (idx.IsPrimaryKey) flags.Add("PK");
                    if (idx.IndexData.IsUnique) flags.Add("unique");
                    if (idx.IndexData.ShouldIgnoreNulls) flags.Add("ignore-nulls");
                    if (idx.IndexData.IsRequired) flags.Add("required");
                    string flagStr = flags.Count > 0 ? $" [{string.Join(", ", flags)}]" : "";
                    System.Console.WriteLine($"    index {idx.Name}: ({cols}){flagStr}");
                }
            }

            if (schemaOnly)
            {
                return;
            }

            foreach (Row row in table.Rows())
            {
                var values = row.ToArray().Select(v => Format(v)).ToArray();
                System.Console.WriteLine("  [" + string.Join(", ", values) + "]");
            }
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"\n== {name} == (error: {ex.Message})");
        }
    }

    private static string Format(object? value) => value switch
    {
        null => "NULL",
        byte[] bytes => $"<blob {bytes.Length}b>",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff"),
        byte b => b.ToString(),
        sbyte b => b.ToString(),
        short n => n.ToString(),
        ushort n => n.ToString(),
        int n => n.ToString(),
        uint n => n.ToString(),
        long n => n.ToString(),
        ulong n => n.ToString(),
        float f => f.ToString("R"),
        double d => d.ToString("R"),
        decimal m => m.ToString(),
        bool b => b ? "true" : "false",
        string s => $"\"{s}\"",
        _ => value.ToString() ?? "NULL",
    };

    private static string? GetOption(string[] args, string option)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == option)
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static void PrintUsage()
    {
        System.Console.WriteLine("Usage: UCanAccess.Console <database.mdb|accdb> [options]");
        System.Console.WriteLine();
        System.Console.WriteLine("Dumps the tables, columns, rows, indexes and relationships of an MS Access database file.");
        System.Console.WriteLine("  --create <path>  create a new database with a sample t_people table");
        System.Console.WriteLine("  --version <v>    version for --create: 2003 (default), 2007, 2010 or 2016");
        System.Console.WriteLine("  --table <name>  only dump the given table");
        System.Console.WriteLine("  -s, --system    also show system tables");
        System.Console.WriteLine("      --schema    show the schema only (no rows)");
        System.Console.WriteLine("  -i, --indexes   show each table's indexes");
        System.Console.WriteLine("  -r, --relations show the relationships");
    }
}
