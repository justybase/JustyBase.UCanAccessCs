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
        if (column.DefaultValue == null && !column.Required && CanExtendDefinition(table))
        {
            AddColumnDefinition(database, table, column);
            return;
        }
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

    private static bool CanExtendDefinition(Table table)
        => table.Indexes.Count == table.IndexDatas.Count
            && !table.Columns.Any(column => column.Calculated
                || column.Type is DataType.ExtDateTime or DataType.ComplexType
                or DataType.Unknown0D or DataType.Unknown11 or DataType.UnsupportedFixedLen
                or DataType.UnsupportedVarLen);

    private static void AddColumnDefinition(Database database, Table original, ColumnBuilder column)
    {
        var columns = original.Columns.Select(ToColumnBuilder).ToList();
        columns.Add(column);
        var indexes = original.Indexes.Select(ToIndexBuilder).ToList();
        var existing = CaptureExistingDefinition(original, indexes);
        PageChannel pageChannel = database.PageChannel;
        pageChannel.StartWrite();
        try
        {
            IReadOnlyCollection<int> oldMetadata = original.MetadataPageNumbers;
            var retainedIndexPages = existing.Indexes.Where(state => state != null)
                .SelectMany(state => state!.OwnedPages).ToHashSet();
            Table replacement = TableCreator.CreateTableDefinitionForExistingData(
                database, original.Name, columns, indexes, existing);
            PatchIndexTableDefinitionPointers(database, retainedIndexPages, replacement.TableDefPageNumber);
            database.ReplaceTableDefinition(original.Name, original.TableDefPageNumber,
                replacement.TableDefPageNumber);
            database.RetargetForeignKeyIndexes(original.TableDefPageNumber,
                replacement.TableDefPageNumber);

            var retainedMetadata = replacement.MetadataPageNumbers.ToHashSet();
            foreach (int page in oldMetadata)
            {
                if (!retainedMetadata.Contains(page))
                {
                    pageChannel.DeallocatePage(page);
                }
            }
        }
        finally
        {
            pageChannel.FinishWrite();
        }
    }

    private static TableCreator.ExistingTableDefinition CaptureExistingDefinition(
        Table table, List<IndexBuilder> requestedIndexes)
    {
        var existingByName = table.Indexes.ToDictionary(index => index.Name ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);
        var states = new List<TableCreator.ExistingIndexDefinition?>(requestedIndexes.Count);
        foreach (IndexBuilder builder in requestedIndexes)
        {
            if (!existingByName.TryGetValue(builder.Name, out IndexImpl? index))
            {
                states.Add(null);
                continue;
            }
            states.Add(new TableCreator.ExistingIndexDefinition
            {
                RootPageNumber = index.IndexData.RootPageNumber,
                UniqueEntryCount = index.IndexData.UniqueEntryCount,
                OwnedPages = Table.EnumeratePages(index.IndexData.OwnedPages),
            });
        }
        return new TableCreator.ExistingTableDefinition
        {
            OwnedPages = Table.EnumeratePages(table.OwnedPages),
            FreeSpacePages = Table.EnumeratePages(table.FreeSpacePages),
            LongValuePages = table.CollectLongValuePages().ToArray(),
            Indexes = states,
            RowCount = table.RowCount,
            NextAutoNumber = table.LastLongAutoNumber,
        };
    }

    private static void PatchIndexTableDefinitionPointers(Database database,
        IEnumerable<int> pages, int tableDefinitionPage)
    {
        foreach (int pageNumber in pages)
        {
            byte[] page = new byte[database.Format.PageSize];
            database.PageChannel.ReadPage(page, pageNumber);
            page[4] = (byte)tableDefinitionPage;
            page[5] = (byte)(tableDefinitionPage >> 8);
            page[6] = (byte)(tableDefinitionPage >> 16);
            page[7] = (byte)(tableDefinitionPage >> 24);
            database.PageChannel.WritePage(page, pageNumber);
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
        int previousAutoNumber = original.LastLongAutoNumber;
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
                    values[i] = sourceColumn == null
                        ? columns[i].DefaultValue == null
                            ? null
                            : DefaultValueEvaluator.Evaluate(columns[i].DefaultValue!)
                        : location.Row[sourceColumn.ColumnIndex];
                }
                target.AddRowPreservingAutoNumbers(values);
            }
            target.PreserveLastLongAutoNumber(previousAutoNumber);
            database.RetargetForeignKeyIndexes(original.TableDefPageNumber,
                target.TableDefPageNumber);
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
        if (!string.IsNullOrWhiteSpace(column.DefaultValue))
        {
            builder.WithDefault(column.DefaultValue!);
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
        if (index.IsForeignKey)
        {
            builder.WithForeignKey(index.RelatedIndexNumber, index.RelatedTablePageNumber,
                index.CascadeUpdates, index.CascadeDeletes);
        }
        return builder;
    }

    // ------------------------------------------------------------------
    // VIEW (saved queries) is intentionally outside the table mutator's scope.
    // ------------------------------------------------------------------

    /// <summary>saves a SELECT query as a view (CREATE VIEW)</summary>
    public static void CreateView(Database database, string viewName, string selectSql)
        => QueryDefWriter.Create(database, viewName, selectSql);

    /// <summary>drops a saved query / view (DROP VIEW)</summary>
    public static void DropView(Database database, string viewName)
        => QueryDefWriter.Drop(database, viewName);
}
