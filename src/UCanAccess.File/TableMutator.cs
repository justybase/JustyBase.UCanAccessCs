using System.Text;

namespace UCanAccess.File;

/// <summary>
/// Mutates an existing table definition for ALTER TABLE operations which still
/// require a table reconstruction. CREATE/DROP INDEX is handled by
/// <see cref="IndexMutator"/> so row pages and retained B-trees stay in place.
/// </summary>
internal static class TableMutator
{
    /// <summary>adds a column to an existing table (ALTER TABLE ... ADD COLUMN)</summary>
    public static void AddColumn(Database database, Table table, ColumnBuilder column)
    {
        GuardRecreatable(database, table, "ALTER TABLE ... ADD COLUMN");

        var columns = table.Columns.Select(ToColumnBuilder).ToList();
        columns.Add(column);
        RecreateTable(database, table, columns, table.Indexes.Select(ToIndexBuilder).ToList());
    }

    /// <summary>removes a column from an existing table (ALTER TABLE ... DROP COLUMN)</summary>
    public static void RemoveColumn(Database database, Table table, string columnName)
    {
        GuardRecreatable(database, table, "ALTER TABLE ... DROP COLUMN");

        Column? target = table.Columns.FirstOrDefault(c => c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Column '{columnName}' does not exist on table '{table.Name}'.");

        var columns = table.Columns
            .Where(c => !c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
            .Select(ToColumnBuilder)
            .ToList();
        var indexes = table.Indexes
            .Where(i => !i.IndexData.Columns.Any(d => d.Column.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase)))
            .Select(ToIndexBuilder)
            .ToList();
        RecreateTable(database, table, columns, indexes);
    }

    private static void GuardRecreatable(Database database, Table table, string operation)
    {
        if (table.AutoNumbered)
        {
            throw new NotSupportedException($"{operation} on a table with an autonumber column is not supported yet.");
        }
        if (database.GetRelationships(table.Name).Count > 0)
        {
            throw new NotSupportedException($"{operation} on a table with relationships is not supported yet.");
        }
        if (table.Columns.Any(column => column.Calculated))
        {
            throw new NotSupportedException($"{operation} on a table with calculated columns is not supported yet.");
        }
        if (table.Columns.Any(column => column.Type is DataType.ExtDateTime or DataType.ComplexType
            or DataType.Unknown0D or DataType.Unknown11 or DataType.UnsupportedFixedLen
            or DataType.UnsupportedVarLen))
        {
            throw new NotSupportedException($"{operation} on a table with unsupported column types is not supported yet.");
        }
    }

    /// <summary>
    /// Creates a new table with the same data but the given column/index set, then
    /// replaces the original (the new table keeps the original name).
    /// </summary>
    private static void RecreateTable(Database database, Table original, List<ColumnBuilder> columns, List<IndexBuilder> indexes)
    {
        if (columns.Count == 0)
        {
            throw new InvalidOperationException("A table must retain at least one column.");
        }
        string tempName = "__ucanaccess_tmp_" + Guid.NewGuid().ToString("N")[..8];
        database.CreateTable(tempName, columns, indexes);
        try
        {
            Table target = database.GetTable(tempName)!;
            foreach (Table.RowLocation location in original.RowLocations())
            {
                var values = new object?[columns.Count];
                for (int i = 0; i < columns.Count; i++)
                {
                    Column? sourceColumn = original.Columns.FirstOrDefault(c =>
                        c.Name.Equals(columns[i].Name, StringComparison.OrdinalIgnoreCase));
                    values[i] = sourceColumn == null ? null : location.Row[sourceColumn.ColumnIndex];
                }
                target.AddRow(values);
            }
            database.DeleteTable(original.Name);
            RenameTable(database, tempName, original.Name);
        }
        catch
        {
            try
            {
                database.DeleteTable(tempName);
            }
            catch
            {
                // best effort
            }
            throw;
        }
    }

    private static void RenameTable(Database database, string fromName, string toName)
        => database.RenameTable(fromName, toName);

    private static ColumnBuilder ToColumnBuilder(Column column)
    {
        ColumnBuilder builder = column.Type switch
        {
            DataType.Text or DataType.Memo or DataType.Binary
                => new ColumnBuilder(column.Name, column.Type).WithLength(column.ColumnLength),
            DataType.Numeric => new ColumnBuilder(column.Name, column.Type).WithPrecision(column.Precision).WithScale(column.Scale),
            _ => new ColumnBuilder(column.Name, column.Type),
        };
        if (column.AutoNumber)
        {
            builder.WithAutoNumber();
        }
        if (column.Required)
        {
            builder.WithRequired();
        }
        if (column.CompressedUnicode)
        {
            builder.WithCompressedUnicode();
        }
        if (column.TextSortOrder is TextSortOrder sortOrder)
        {
            builder.WithTextSortOrder(sortOrder);
        }
        return builder;
    }

    private static IndexBuilder ToIndexBuilder(IndexImpl index)
    {
        var builder = new IndexBuilder(index.Name ?? "idx" + index.IndexNumber);
        foreach (IndexData.ColumnDescriptor descriptor in index.IndexData.Columns)
        {
            builder.WithColumns(descriptor.IsAscending, descriptor.Column.Name);
        }
        if (index.IsPrimaryKey)
        {
            builder.WithPrimaryKey();
        }
        else if (index.IndexData.IsUnique)
        {
            builder.WithUnique();
        }
        if (index.IndexData.IsRequired)
        {
            builder.WithRequired();
        }
        if (index.IndexData.ShouldIgnoreNulls)
        {
            builder.WithIgnoreNulls();
        }
        return builder;
    }

    // ------------------------------------------------------------------
    // VIEW (saved queries) is intentionally outside the table mutator's scope.
    // ------------------------------------------------------------------

    /// <summary>saves a SELECT query as a view (CREATE VIEW)</summary>
    public static void CreateView(Database database, string viewName, string selectSql)
    {
        throw new NotSupportedException("CREATE VIEW is not supported.");
    }

    /// <summary>drops a saved query / view (DROP VIEW)</summary>
    public static void DropView(Database database, string viewName)
    {
        throw new NotSupportedException("DROP VIEW is not supported.");
    }
}
