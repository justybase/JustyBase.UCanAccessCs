namespace UCanAccess.File;

/// <summary>
/// Changes the logical index set without rebuilding the table rows.  A new table
/// definition is assembled on the same file (normally a provider staging copy),
/// while the data pages and retained B-trees are referenced in place.
/// </summary>
internal static class IndexMutator
{
    internal static void AddIndex(Database database, Table table, IndexBuilder builder)
    {
        ValidateIndexBuilder(table, builder);
        if (builder.PrimaryKey && table.Indexes.Any(index => index.IsPrimaryKey))
        {
            throw new InvalidOperationException($"Table '{table.Name}' already has a primary key.");
        }
        if (table.Indexes.Any(index => string.Equals(index.Name, builder.Name,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Index '{builder.Name}' already exists on table '{table.Name}'.");
        }

        var indexes = table.Indexes.Select(ToIndexBuilder).ToList();
        indexes.Add(builder);
        var existing = CaptureExistingDefinition(table, indexes);
        Apply(database, table, indexes, existing, builder.Name, table.RowLocations().ToList());
    }

    internal static void DropIndex(Database database, Table table, string indexName)
    {
        IndexImpl? target = table.Indexes.FirstOrDefault(index =>
            string.Equals(index.Name, indexName, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            throw new InvalidOperationException(
                $"Index '{indexName}' does not exist on table '{table.Name}'.");
        }

        var indexes = table.Indexes
            .Where(index => !string.Equals(index.Name, indexName, StringComparison.OrdinalIgnoreCase))
            .Select(ToIndexBuilder)
            .ToList();
        var existing = CaptureExistingDefinition(table, indexes);
        Apply(database, table, indexes, existing, null, table.RowLocations().ToList());
    }

    private static void Apply(Database database, Table original, List<IndexBuilder> indexes,
        TableCreator.ExistingTableDefinition existing, string? newIndexName,
        List<Table.RowLocation> rows)
    {
        PageChannel pageChannel = database.PageChannel;
        pageChannel.StartWrite();
        try
        {
            IReadOnlyCollection<int> oldMetadata = original.MetadataPageNumbers;
            var retainedIndexPages = new HashSet<int>();
            foreach (TableCreator.ExistingIndexDefinition? state in existing.Indexes)
            {
                if (state != null)
                {
                    retainedIndexPages.UnionWith(state.OwnedPages);
                }
            }

            Table replacement = TableCreator.CreateTableDefinitionForExistingData(
                database, original.Name, original.Columns.Select(ToColumnBuilder).ToList(), indexes, existing);

            if (newIndexName != null)
            {
                IndexImpl newIndex = replacement.Indexes.First(index =>
                    string.Equals(index.Name, newIndexName, StringComparison.OrdinalIgnoreCase));
                replacement.BuildIndex(newIndex.IndexData, rows);
            }

            PatchIndexTableDefinitionPointers(database, retainedIndexPages, replacement.TableDefPageNumber);
            database.ReplaceTableDefinition(original.Name, original.TableDefPageNumber,
                replacement.TableDefPageNumber);

            var retainedMetadata = new HashSet<int>(replacement.MetadataPageNumbers);
            foreach (int page in oldMetadata)
            {
                if (!retainedMetadata.Contains(page))
                {
                    database.PageChannel.DeallocatePage(page);
                }
            }

            var retainedPages = new HashSet<int>(retainedIndexPages);
            foreach (IndexData indexData in original.IndexDatas)
            {
                foreach (int page in Table.EnumeratePages(indexData.OwnedPages))
                {
                    if (!retainedPages.Contains(page))
                    {
                        pageChannel.DeallocatePage(page);
                    }
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
        if (table.Indexes.Count != table.IndexDatas.Count)
        {
            throw new NotSupportedException(
                "Index mutation is not supported for tables with shared relationship indexes.");
        }

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

    private static void ValidateIndexBuilder(Table table, IndexBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Name))
        {
            throw new ArgumentException("Index name is required.");
        }
        if (builder.Columns.Count == 0)
        {
            throw new ArgumentException($"Index '{builder.Name}' must contain at least one column.");
        }
        if (builder.Columns.Count > IndexData.MaxColumns)
        {
            throw new ArgumentException(
                $"Index '{builder.Name}' may contain at most {IndexData.MaxColumns} columns.");
        }
        foreach ((string columnName, _) in builder.Columns)
        {
            if (!table.Columns.Any(column =>
                    column.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Index '{builder.Name}' refers to unknown column '{columnName}'.");
            }
        }
    }

    private static ColumnBuilder ToColumnBuilder(Column column)
    {
        ColumnBuilder builder = column.Type switch
        {
            DataType.Text or DataType.Memo or DataType.Binary
                => new ColumnBuilder(column.Name, column.Type).WithLength(column.ColumnLength),
            DataType.Numeric
                => new ColumnBuilder(column.Name, column.Type)
                    .WithPrecision(column.Precision).WithScale(column.Scale),
            _ => new ColumnBuilder(column.Name, column.Type),
        };
        if (column.AutoNumber) builder.WithAutoNumber();
        if (column.Required) builder.WithRequired();
        if (column.CompressedUnicode) builder.WithCompressedUnicode();
        if (column.TextSortOrder is TextSortOrder sortOrder) builder.WithTextSortOrder(sortOrder);
        return builder;
    }

    private static IndexBuilder ToIndexBuilder(IndexImpl index)
    {
        var builder = new IndexBuilder(index.Name ?? "idx" + index.IndexNumber);
        foreach (IndexData.ColumnDescriptor descriptor in index.IndexData.Columns)
        {
            builder.WithColumns(descriptor.IsAscending, descriptor.Column.Name);
        }
        if (index.IsPrimaryKey) builder.WithPrimaryKey();
        else if (index.IndexData.IsUnique) builder.WithUnique();
        if (index.IndexData.IsRequired) builder.WithRequired();
        if (index.IndexData.ShouldIgnoreNulls) builder.WithIgnoreNulls();
        return builder;
    }
}
