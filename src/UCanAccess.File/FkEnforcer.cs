namespace UCanAccess.File;

/// <summary>
/// Enforces foreign-key relationships on row writes (port of the Jackcess
/// <c>FKEnforcer</c> checks, driven from the relationships read out of
/// <c>MSysRelationships</c>).
/// </summary>
internal static class FkEnforcer
{
    /// <summary>validates the foreign-key values of a new row on a "secondary" (FK) table</summary>
    internal static void AddRow(Table table, object?[] values)
    {
        Database db = table.Database;
        if (!db.EnforceForeignKeys)
        {
            return;
        }
        foreach (Relationship rel in db.GetRelationships())
        {
            if (!IsSameTable(rel.ToTable, table) || !rel.HasReferentialIntegrity)
            {
                continue;
            }
            EnsureComplete(rel);
            if (AreNull(rel.ToColumns, values))
            {
                continue;
            }
            if (!HasRow(rel.FromTable, rel.FromColumns, ValuesAt(rel.ToColumns, values)))
            {
                throw new DatabaseException(
                    $"Adding new row to '{table.Name}' violates foreign key constraint '{rel.Name}' (missing referenced row in '{rel.FromTable.Name}').");
            }
        }
    }

    /// <summary>handles foreign-key constraints when deleting a row on a "primary" (referenced) table</summary>
    internal static void DeleteRow(Table table, object?[] oldValues)
    {
        Database db = table.Database;
        if (!db.EnforceForeignKeys)
        {
            return;
        }
        foreach (Relationship rel in db.GetRelationships())
        {
            if (!IsSameTable(rel.FromTable, table) || !rel.HasReferentialIntegrity)
            {
                continue;
            }
            EnsureComplete(rel);

            object?[] keyValues = ValuesAt(rel.FromColumns, oldValues);
            var referencing = FindReferencingRows(rel.ToTable, rel.ToColumns, keyValues);
            if (referencing.Count == 0)
            {
                continue;
            }

            if (rel.CascadeDeletes)
            {
                foreach (var loc in referencing)
                {
                    rel.ToTable.DeleteRow(loc.PageNumber, loc.RowNumber);
                }
            }
            else if (rel.CascadeNullOnDelete)
            {
                foreach (var loc in referencing)
                {
                    object?[] childValues = rel.ToTable.GetRow(loc.PageNumber, loc.RowNumber)!.ToArray();
                    foreach (Column col in rel.ToColumns)
                    {
                        childValues[col.ColumnIndex] = null;
                    }
                    rel.ToTable.UpdateRow(loc.PageNumber, loc.RowNumber, childValues);
                }
            }
            else
            {
                throw new DatabaseException(
                    $"Cannot delete row from '{table.Name}': still referenced by '{rel.ToTable.Name}' (constraint '{rel.Name}').");
            }
        }
    }

    /// <summary>handles foreign-key constraints when updating a row</summary>
    internal static void UpdateRow(Table table, object?[] oldValues, object?[] newValues)
    {
        Database db = table.Database;
        if (!db.EnforceForeignKeys)
        {
            return;
        }

        // secondary (FK) side: the new foreign-key values must reference an existing row
        foreach (Relationship rel in db.GetRelationships())
        {
            if (!IsSameTable(rel.ToTable, table) || !rel.HasReferentialIntegrity)
            {
                continue;
            }
            EnsureComplete(rel);
            if (!ColumnsChanged(rel.ToColumns, oldValues, newValues))
            {
                continue;
            }
            if (AreNull(rel.ToColumns, newValues))
            {
                continue;
            }
            if (!HasRow(rel.FromTable, rel.FromColumns, ValuesAt(rel.ToColumns, newValues)))
            {
                throw new DatabaseException(
                    $"Updating row in '{table.Name}' violates foreign key constraint '{rel.Name}' (missing referenced row in '{rel.FromTable.Name}').");
            }
        }

        // primary side: if the referenced key changed, handle rows that reference it
        foreach (Relationship rel in db.GetRelationships())
        {
            if (!IsSameTable(rel.FromTable, table) || !rel.HasReferentialIntegrity)
            {
                continue;
            }
            EnsureComplete(rel);
            if (!ColumnsChanged(rel.FromColumns, oldValues, newValues))
            {
                continue;
            }

            object?[] keyValues = ValuesAt(rel.FromColumns, oldValues);
            var referencing = FindReferencingRows(rel.ToTable, rel.ToColumns, keyValues);
            if (referencing.Count == 0)
            {
                continue;
            }

            if (rel.CascadeUpdates)
            {
                foreach (var loc in referencing)
                {
                    object?[] childValues = rel.ToTable.GetRow(loc.PageNumber, loc.RowNumber)!.ToArray();
                    for (int i = 0; i < rel.ToColumns.Length; i++)
                    {
                        childValues[rel.ToColumns[i].ColumnIndex] = newValues[rel.FromColumns[i].ColumnIndex];
                    }
                    rel.ToTable.UpdateRow(loc.PageNumber, loc.RowNumber, childValues);
                }
            }
            else
            {
                throw new DatabaseException(
                    $"Cannot update row in '{table.Name}': still referenced by '{rel.ToTable.Name}' (constraint '{rel.Name}').");
            }
        }
    }

    private static bool IsSameTable(Table a, Table b)
        => a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase);

    private static bool AreNull(Column[] columns, object?[] values)
    {
        foreach (Column col in columns)
        {
            if (values[col.ColumnIndex] is not null and not DBNull)
            {
                return false;
            }
        }
        return true;
    }

    private static bool ColumnsChanged(Column[] columns, object?[] oldValues, object?[] newValues)
    {
        foreach (Column col in columns)
        {
            if (!ValuesEqual(col.Type, oldValues[col.ColumnIndex], newValues[col.ColumnIndex]))
            {
                return true;
            }
        }
        return false;
    }

    private static void EnsureComplete(Relationship relationship)
    {
        if (relationship.FromColumns.Any(column => column == null)
            || relationship.ToColumns.Any(column => column == null))
        {
            throw new DatabaseException(
                $"Relationship '{relationship.Name}' is incomplete in the Access catalog.");
        }
    }

    private static object?[] ValuesAt(Column[] columns, object?[] values)
    {
        var key = new object?[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            key[i] = values[columns[i].ColumnIndex];
        }
        return key;
    }

    private static bool HasRow(Table table, Column[] columns, object?[] key)
    {
        foreach (Table.RowLocation location in table.RowLocations())
        {
            if (MatchesKey(location.Row, columns, key))
            {
                return true;
            }
        }
        return false;
    }

    private static List<Table.RowLocation> FindReferencingRows(Table table, Column[] columns, object?[] key)
    {
        var result = new List<Table.RowLocation>();
        foreach (Table.RowLocation location in table.RowLocations())
        {
            if (MatchesKey(location.Row, columns, key))
            {
                result.Add(location);
            }
        }
        return result;
    }

    private static bool MatchesKey(Row row, Column[] columns, object?[] key)
    {
        for (int i = 0; i < columns.Length; i++)
        {
            object? actual = row[columns[i].Name];
            if (!ValuesEqual(columns[i].Type, actual, key[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ValuesEqual(DataType type, object? left, object? right)
    {
        if (left is null or DBNull || right is null or DBNull)
        {
            return left is null or DBNull && right is null or DBNull;
        }
        if (type is DataType.Byte or DataType.Int or DataType.Long or DataType.BigInt
            or DataType.Money or DataType.Numeric or DataType.Float or DataType.Double)
        {
            return Convert.ToDecimal(left, System.Globalization.CultureInfo.InvariantCulture)
                == Convert.ToDecimal(right, System.Globalization.CultureInfo.InvariantCulture);
        }
        if (type == DataType.ShortDateTime || type == DataType.ExtDateTime)
        {
            return Convert.ToDateTime(left, System.Globalization.CultureInfo.InvariantCulture)
                == Convert.ToDateTime(right, System.Globalization.CultureInfo.InvariantCulture);
        }
        if (type == DataType.Text || type == DataType.Memo)
        {
            return string.Equals(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        return Equals(left, right);
    }
}
