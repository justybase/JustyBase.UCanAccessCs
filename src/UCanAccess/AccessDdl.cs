using System.Globalization;
using UCanAccess.File;
using static UCanAccess.AccessTokenizer;

namespace UCanAccess;

/// <summary>
/// Executes Access SQL data-definition statements (CREATE/DROP/ALTER) against the MDB file.
///
/// Supported grammar (subset of the UCanAccess/HSQLDB surface):
///   CREATE TABLE name (col type [NOT NULL] [, ...] [, PRIMARY KEY (cols)] [, UNIQUE (cols)])
///   CREATE TABLE name AS SELECT ... [WITH DATA|WITH NO DATA]
///   CREATE INDEX name ON table (col [ASC|DESC], ...) [WITH PRIMARY|UNIQUE|DISALLOW NULL|IGNORE NULL]
///   CREATE VIEW name AS SELECT ...
///   DROP TABLE name
///   DROP INDEX name ON table
///   DROP VIEW name
///   ALTER TABLE name ADD COLUMN col type
///   ALTER TABLE name DROP COLUMN col
/// </summary>
public static class AccessDdl
{
    internal static bool IsIndexMutation(string sql)
    {
        List<Token> tokens = Tokenize(sql);
        if (tokens.Count < 2
            || (!tokens[0].Text.Equals("create", StringComparison.OrdinalIgnoreCase)
                && !tokens[0].Text.Equals("drop", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        int index = tokens[1].Text.Equals("unique", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        return index < tokens.Count && tokens[index].Text.Equals("index", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Executes the DDL statement; returns the number of rows affected (0 for DDL).</summary>
    public static int Execute(File.Database db, Mirror? mirror, string sql, bool dryRun = false)
    {
        List<Token> tokens = Tokenize(sql);
        while (tokens.Count > 0 && tokens[^1].Text == ";")
        {
            tokens.RemoveAt(tokens.Count - 1);
        }
        if (tokens.Count == 0)
        {
            throw new NotSupportedException("Empty statement.");
        }

        if (!dryRun)
        {
            mirror?.ThrowIfActiveReaders();
        }

        string kind = tokens[0].Text.ToUpperInvariant();
        int affected;
        switch (kind)
        {
            case "CREATE":
                affected = ExecuteCreate(db, mirror, tokens, dryRun);
                break;
            case "DROP":
                affected = ExecuteDrop(db, tokens, dryRun);
                break;
            case "ALTER":
                affected = ExecuteAlter(db, tokens, dryRun);
                break;
            default:
                throw new NotSupportedException($"Statement type '{kind}' is not supported for DDL.");
        }

        if (!dryRun)
        {
            mirror?.RefreshAll();
        }
        return affected;
    }

    private static int ExecuteCreate(File.Database db, Mirror? mirror, List<Token> tokens, bool dryRun)
    {
        int pos = 1;
        string objType = ReadWord(tokens, ref pos).ToUpperInvariant();
        bool uniquePrefix = false;
        if (objType == "UNIQUE")
        {
            uniquePrefix = true;
            objType = ReadWord(tokens, ref pos).ToUpperInvariant();
        }
        switch (objType)
        {
            case "TABLE":
                CreateTable(db, mirror, tokens, ref pos, dryRun);
                return 0;
            case "INDEX":
                CreateIndex(db, tokens, ref pos, dryRun, uniquePrefix);
                return 0;
            case "VIEW":
                CreateView(db, tokens, ref pos, dryRun);
                return 0;
            default:
                throw new NotSupportedException($"CREATE {objType} is not supported.");
        }
    }

    private static int ExecuteDrop(File.Database db, List<Token> tokens, bool dryRun)
    {
        int pos = 1;
        string objType = ReadWord(tokens, ref pos).ToUpperInvariant();
        switch (objType)
        {
            case "TABLE":
                DropTable(db, tokens, ref pos, dryRun);
                return 0;
            case "INDEX":
                DropIndex(db, tokens, ref pos, dryRun);
                return 0;
            case "VIEW":
                DropView(db, tokens, ref pos, dryRun);
                return 0;
            default:
                throw new NotSupportedException($"DROP {objType} is not supported.");
        }
    }

    private static int ExecuteAlter(File.Database db, List<Token> tokens, bool dryRun)
    {
        int pos = 1;
        string objType = ReadWord(tokens, ref pos).ToUpperInvariant();
        if (objType != "TABLE")
        {
            throw new NotSupportedException($"ALTER {objType} is not supported.");
        }
        AlterTable(db, tokens, ref pos, dryRun);
        return 0;
    }

    // ------------------------------------------------------------------
    // CREATE TABLE
    // ------------------------------------------------------------------

    private static void CreateTable(File.Database db, Mirror? mirror, List<Token> tokens, ref int pos, bool dryRun)
    {
        string tableName = ReadName(tokens, ref pos);

        if (PeekWord(tokens, pos, "as"))
        {
            pos++;
            CreateTableAsSelect(db, mirror, tableName, tokens, pos, dryRun);
            pos = tokens.Count;
            return;
        }

        ExpectSymbol(tokens, ref pos, "(");

        var columns = new List<ColumnBuilder>();
        var indexes = new List<IndexBuilder>();
        var autoPkIndexes = new List<IndexBuilder>();
        int pkColNumber = -1;

        while (true)
        {
            if (Peek(tokens, pos) is { Text: ")" })
            {
                break;
            }

            // table-level constraint: PRIMARY KEY (cols) / UNIQUE (cols)
            if (PeekWord(tokens, pos, "primary") || PeekWord(tokens, pos, "unique"))
            {
                bool primary = PeekWord(tokens, pos, "primary");
                string idxName = primary ? "PrimaryKey" : "idx_" + Guid.NewGuid().ToString("N")[..8];
                var builder = new IndexBuilder(idxName);
                if (primary)
                {
                    builder.WithPrimaryKey();
                }
                else
                {
                    builder.WithUnique();
                }
                pos++;
                if (primary)
                {
                    ExpectWord(tokens, ref pos, "key");
                }
                ExpectSymbol(tokens, ref pos, "(");
                while (true)
                {
                    string colName = ReadName(tokens, ref pos);
                    builder.WithColumns(colName);
                    if (Peek(tokens, pos) is { Text: "," })
                    {
                        pos++;
                        continue;
                    }
                    break;
                }
                ExpectSymbol(tokens, ref pos, ")");
                indexes.Add(builder);
                if (primary)
                {
                    pkColNumber = -1;
                }
            }
            else
            {
                string colName = ReadName(tokens, ref pos);
                ColumnBuilder column = ParseColumnType(tokens, ref pos, colName);

                // column constraints
                bool colPrimary = false;
                bool colUnique = false;
                while (true)
                {
                    if (PeekWord(tokens, pos, "not") && PeekAheadWord(tokens, pos, 1, "null"))
                    {
                        pos += 2;
                        column.WithRequired();
                    }
                    else if (PeekWord(tokens, pos, "primary") && PeekAheadWord(tokens, pos, 1, "key"))
                    {
                        pos += 2;
                        colPrimary = true;
                    }
                    else if (PeekWord(tokens, pos, "unique"))
                    {
                        pos++;
                        colUnique = true;
                    }
                    else if (PeekWord(tokens, pos, "autoincrement") || PeekWord(tokens, pos, "identity"))
                    {
                        pos++;
                        column.WithAutoNumber();
                    }
                    else
                    {
                        break;
                    }
                }

                columns.Add(column);
                if (colPrimary)
                {
                    if (pkColNumber >= 0)
                    {
                        throw new InvalidOperationException("Only one PRIMARY KEY is allowed.");
                    }
                    pkColNumber = columns.Count - 1;
                    indexes.Add(new IndexBuilder("PrimaryKey").WithPrimaryKey().WithColumns(colName));
                }
                else if (colUnique)
                {
                    indexes.Add(new IndexBuilder("idx_" + Guid.NewGuid().ToString("N")[..8])
                        .WithUnique()
                        .WithColumns(colName));
                }
            }

            if (Peek(tokens, pos) is { Text: "," })
            {
                pos++;
                continue;
            }
            break;
        }
        ExpectSymbol(tokens, ref pos, ")");
        EnsureEnd(tokens, pos);

        if (!dryRun)
        {
            db.CreateTable(tableName, columns, indexes);
        }
    }

    private static void CreateTableAsSelect(File.Database db, Mirror? mirror, string tableName,
        List<Token> tokens, int selectStart, bool dryRun)
    {
        if (mirror == null)
        {
            throw new InvalidOperationException("CREATE TABLE ... AS SELECT requires an active SQL mirror.");
        }
        if (!PeekWord(tokens, selectStart, "select") && !PeekWord(tokens, selectStart, "with"))
        {
            throw new InvalidOperationException("CREATE TABLE ... AS requires a SELECT query.");
        }

        int selectEnd = tokens.Count;
        bool withData = true;
        if (selectEnd >= selectStart + 2
            && PeekWord(tokens, selectEnd - 2, "with")
            && PeekWord(tokens, selectEnd - 1, "data"))
        {
            selectEnd -= 2;
        }
        else if (selectEnd >= selectStart + 3
            && PeekWord(tokens, selectEnd - 3, "with")
            && PeekWord(tokens, selectEnd - 2, "no")
            && PeekWord(tokens, selectEnd - 1, "data"))
        {
            withData = false;
            selectEnd -= 3;
        }

        string selectSql = RebuildSql(tokens, selectStart, selectEnd);
        string translated = AccessSqlTranslator.Translate(selectSql, out int parameterCount, out _,
            mirror.IsMoneyColumn, mirror.IsExactDecimalColumn, mirror.IsDateColumn);
        if (parameterCount != 0)
        {
            throw new NotSupportedException("CREATE TABLE ... AS SELECT does not support parameters.");
        }

        var rows = new List<object?[]>();
        var names = new List<string>();
        var fieldTypes = new List<Type>();
        using (MirrorReader reader = mirror.ExecuteReader(translated))
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                names.Add(UniqueColumnName(reader.GetName(i), names));
                fieldTypes.Add(reader.GetFieldType(i));
            }
            while (reader.Read())
            {
                var row = new object?[reader.FieldCount];
                for (int i = 0; i < row.Length; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
            }
        }

        var columns = new List<ColumnBuilder>(names.Count);
        for (int i = 0; i < names.Count; i++)
        {
            columns.Add(InferColumn(names[i], fieldTypes[i], rows, i));
        }
        if (columns.Count == 0)
        {
            throw new InvalidOperationException("CREATE TABLE ... AS SELECT returned no columns.");
        }
        EnsureNoExistingTable(db, tableName);
        if (dryRun)
        {
            return;
        }

        Table table = db.CreateTable(tableName, columns);
        if (withData)
        {
            foreach (object?[] row in rows)
            {
                table.AddRow(row);
            }
        }
    }

    private static void EnsureNoExistingTable(File.Database db, string tableName)
    {
        if (db.GetTable(tableName) != null)
        {
            throw new InvalidOperationException($"Table '{tableName}' already exists.");
        }
    }

    private static string UniqueColumnName(string name, List<string> existing)
    {
        string candidate = string.IsNullOrWhiteSpace(name) ? "Expr" : name;
        string baseName = candidate;
        int suffix = 2;
        while (existing.Any(n => n.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = baseName + suffix++;
        }
        return candidate;
    }

    private static ColumnBuilder InferColumn(string name, Type fieldType, List<object?[]> rows, int ordinal)
    {
        if (fieldType == typeof(bool))
        {
            return new ColumnBuilder(name, DataType.Boolean);
        }
        if (fieldType == typeof(byte))
        {
            return new ColumnBuilder(name, DataType.Byte);
        }
        if (fieldType == typeof(short))
        {
            return new ColumnBuilder(name, DataType.Int);
        }
        if (fieldType == typeof(int) || fieldType == typeof(long))
        {
            return new ColumnBuilder(name, DataType.Long);
        }
        if (fieldType == typeof(float))
        {
            return new ColumnBuilder(name, DataType.Float);
        }
        if (fieldType == typeof(double))
        {
            return new ColumnBuilder(name, DataType.Double);
        }
        if (fieldType == typeof(decimal))
        {
            return new ColumnBuilder(name, DataType.Numeric).WithPrecision(28).WithScale(10);
        }
        if (fieldType == typeof(DateTime) || fieldType == typeof(DateTimeOffset))
        {
            return new ColumnBuilder(name, DataType.ShortDateTime);
        }
        if (fieldType == typeof(Guid))
        {
            return new ColumnBuilder(name, DataType.Guid);
        }
        if (fieldType == typeof(byte[]))
        {
            return new ColumnBuilder(name, DataType.Ole);
        }

        int maxLength = rows
            .Select(row => row[ordinal] as string)
            .Where(value => value != null)
            .Select(value => value!.Length)
            .DefaultIfEmpty(1)
            .Max();
        if (maxLength > 255)
        {
            return new ColumnBuilder(name, DataType.Memo);
        }
        return new ColumnBuilder(name, DataType.Text).WithLength(Math.Max(2, maxLength * 2));
    }

    private static ColumnBuilder ParseColumnType(List<Token> tokens, ref int pos, string name)
    {
        string typeName = ReadTypeName(tokens, ref pos).ToUpperInvariant();
        switch (typeName)
        {
            case "TEXT" or "VARCHAR" or "CHAR" or "CHARACTER" or "NVARCHAR" or "STRING":
            {
                int len = ReadOptionalParenInt(tokens, ref pos) ?? 50;
                return new ColumnBuilder(name, DataType.Text).WithLength(2 * len);
            }
            case "MEMO" or "LONGCHAR":
                return new ColumnBuilder(name, DataType.Memo);
            case "BYTE" or "TINYINT":
                return new ColumnBuilder(name, DataType.Byte);
            case "SMALLINT" or "SHORT" or "SHORTINTEGER" or "INT2" or "INTEGER2" or "INTEGER" or "INT":
                return new ColumnBuilder(name, DataType.Int);
            case "LONG" or "INT4" or "LONGINTEGER":
                return new ColumnBuilder(name, DataType.Long);
            case "COUNTER" or "AUTOINCREMENT" or "IDENTITY":
                return new ColumnBuilder(name, DataType.Long).WithAutoNumber();
            case "BIGINT" or "BIGINTEGER" or "INT8":
                return new ColumnBuilder(name, DataType.BigInt);
            case "MONEY" or "CURRENCY":
                return new ColumnBuilder(name, DataType.Money);
            case "NUMERIC" or "DECIMAL":
            {
                (int precision, int scale) = ReadOptionalPrecisionScale(tokens, ref pos);
                return new ColumnBuilder(name, DataType.Numeric).WithPrecision(precision).WithScale(scale);
            }
            case "DOUBLE" or "FLOAT" or "REAL":
                return new ColumnBuilder(name, DataType.Double);
            case "SINGLE":
                return new ColumnBuilder(name, DataType.Float);
            case "DATETIME" or "DATE" or "TIME" or "TIMESTAMP":
                return new ColumnBuilder(name, DataType.ShortDateTime);
            case "BOOLEAN" or "BIT" or "YESNO" or "LOGICAL":
                return new ColumnBuilder(name, DataType.Boolean);
            case "GUID" or "UNIQUEIDENTIFIER":
                return new ColumnBuilder(name, DataType.Guid);
            case "OLE" or "BINARY" or "LONGBINARY":
                return new ColumnBuilder(name, DataType.Ole);
            default:
                throw new NotSupportedException($"Unsupported column type '{typeName}'.");
        }
    }

    private static string ReadTypeName(List<Token> tokens, ref int pos)
    {
        string first = ReadWord(tokens, ref pos);
        if (PeekWord(tokens, pos, "integer"))
        {
            string? word = Peek(tokens, pos)?.Text;
            if (word != null && word.Equals("integer", StringComparison.OrdinalIgnoreCase))
            {
                pos++;
                return first + "INTEGER";
            }
        }
        return first;
    }

    private static (int precision, int scale) ReadOptionalPrecisionScale(List<Token> tokens, ref int pos)
    {
        if (Peek(tokens, pos) is not { Text: "(" })
        {
            return (18, 0);
        }
        pos++;
        Token pt = PeekOrThrow(tokens, pos);
        int precision = pt.Kind == Kind.Number ? int.Parse(pt.Text, CultureInfo.InvariantCulture) : 18;
        if (pt.Kind == Kind.Number)
        {
            pos++;
        }
        int scale = 0;
        if (Peek(tokens, pos) is { Text: "," })
        {
            pos++;
            Token st = PeekOrThrow(tokens, pos);
            if (st.Kind == Kind.Number)
            {
                scale = int.Parse(st.Text, CultureInfo.InvariantCulture);
                pos++;
            }
        }
        ExpectSymbol(tokens, ref pos, ")");
        return (precision, scale);
    }

    private static int? ReadOptionalParenInt(List<Token> tokens, ref int pos)
    {
        if (Peek(tokens, pos) is not { Text: "(" })
        {
            return null;
        }
        pos++;
        Token t = PeekOrThrow(tokens, pos);
        if (t.Kind != Kind.Number)
        {
            throw new InvalidOperationException("Expected a number inside parentheses.");
        }
        pos++;
        ExpectSymbol(tokens, ref pos, ")");
        return int.Parse(t.Text, CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------
    // DROP TABLE
    // ------------------------------------------------------------------

    private static void DropTable(File.Database db, List<Token> tokens, ref int pos, bool dryRun)
    {
        string tableName = ReadName(tokens, ref pos);
        if (dryRun)
        {
            if (db.GetTable(tableName) == null)
            {
                throw new InvalidOperationException($"Table '{tableName}' does not exist.");
            }
            EnsureEnd(tokens, pos);
            return;
        }
        EnsureEnd(tokens, pos);
        db.DeleteTable(tableName);
    }

    // ------------------------------------------------------------------
    // CREATE / DROP INDEX
    // ------------------------------------------------------------------

    private static void CreateIndex(File.Database db, List<Token> tokens, ref int pos,
        bool dryRun, bool uniquePrefix = false)
    {
        string indexName = ReadName(tokens, ref pos);
        ExpectWord(tokens, ref pos, "on");
        string tableName = ReadName(tokens, ref pos);
        ExpectSymbol(tokens, ref pos, "(");

        var builder = new IndexBuilder(indexName);
        if (uniquePrefix)
        {
            builder.WithUnique();
        }
        while (true)
        {
            string colName = ReadName(tokens, ref pos);
            bool ascending = true;
            if (PeekWord(tokens, pos, "asc"))
            {
                pos++;
            }
            else if (PeekWord(tokens, pos, "desc"))
            {
                pos++;
                ascending = false;
            }
            builder.WithColumns(ascending, colName);
            if (Peek(tokens, pos) is { Text: "," })
            {
                pos++;
                continue;
            }
            break;
        }
        ExpectSymbol(tokens, ref pos, ")");

        if (PeekWord(tokens, pos, "with"))
        {
            pos++;
            while (true)
            {
                if (PeekWord(tokens, pos, "primary"))
                {
                    pos++;
                    ExpectWord(tokens, ref pos, "key");
                    builder.WithPrimaryKey();
                }
                else if (PeekWord(tokens, pos, "unique"))
                {
                    pos++;
                    builder.WithUnique();
                }
                else if (PeekWord(tokens, pos, "disallow") && PeekAheadWord(tokens, pos, 1, "null"))
                {
                    pos += 2;
                    builder.WithRequired();
                }
                else if (PeekWord(tokens, pos, "ignore") && PeekAheadWord(tokens, pos, 1, "null"))
                {
                    pos += 2;
                    builder.WithIgnoreNulls();
                }
                else
                {
                    break;
                }
                if (Peek(tokens, pos) is { Text: "," })
                {
                    pos++;
                }
            }
        }

        EnsureEnd(tokens, pos);

        if (!dryRun)
        {
            db.AddIndex(tableName, builder);
        }
    }

    private static void DropIndex(File.Database db, List<Token> tokens, ref int pos, bool dryRun)
    {
        string indexName = ReadName(tokens, ref pos);
        ExpectWord(tokens, ref pos, "on");
        string tableName = ReadName(tokens, ref pos);
        if (dryRun)
        {
            Table? table = db.GetTable(tableName)
                ?? throw new InvalidOperationException($"Table '{tableName}' does not exist.");
            if (!db.GetIndexNames(tableName).Any(n => n.Equals(indexName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Index '{indexName}' does not exist on table '{tableName}'.");
            }
            EnsureEnd(tokens, pos);
            return;
        }
        EnsureEnd(tokens, pos);
        db.DropIndex(tableName, indexName);
    }

    // ------------------------------------------------------------------
    // CREATE / DROP VIEW (saved queries)
    // ------------------------------------------------------------------

    private static void CreateView(File.Database db, List<Token> tokens, ref int pos, bool dryRun)
    {
        string viewName = ReadName(tokens, ref pos);
        ExpectWord(tokens, ref pos, "as");
        string selectSql = RebuildSql(tokens, pos);
        if (selectSql.Length == 0)
        {
            throw new InvalidOperationException("CREATE VIEW requires a SELECT query.");
        }
        if (!dryRun)
        {
            db.CreateView(viewName, selectSql);
        }
    }

    private static void DropView(File.Database db, List<Token> tokens, ref int pos, bool dryRun)
    {
        string viewName = ReadName(tokens, ref pos);
        EnsureEnd(tokens, pos);
        if (!dryRun)
        {
            db.DropView(viewName);
        }
    }

    // ------------------------------------------------------------------
    // ALTER TABLE
    // ------------------------------------------------------------------

    private static void AlterTable(File.Database db, List<Token> tokens, ref int pos, bool dryRun)
    {
        string tableName = ReadName(tokens, ref pos);
        string action = ReadWord(tokens, ref pos).ToUpperInvariant();
        switch (action)
        {
            case "ADD":
                ExpectWord(tokens, ref pos, "column");
                {
                    string colName = ReadName(tokens, ref pos);
                    ColumnBuilder column = ParseColumnType(tokens, ref pos, colName);
                    if (PeekWord(tokens, pos, "not") && PeekAheadWord(tokens, pos, 1, "null"))
                    {
                        pos += 2;
                        column.WithRequired();
                    }
                    EnsureEnd(tokens, pos);
                    if (column.Required)
                    {
                        Table? existing = db.GetTable(tableName)
                            ?? throw new InvalidOperationException($"Table '{tableName}' does not exist.");
                        if (existing.RowCount > 0)
                        {
                            throw new NotSupportedException(
                                "ALTER TABLE ADD COLUMN NOT NULL requires a DEFAULT value for existing rows.");
                        }
                    }
                    if (!dryRun)
                    {
                        db.AddColumn(tableName, column);
                    }
                }
                break;
            case "DROP":
                ExpectWord(tokens, ref pos, "column");
                {
                    string colName = ReadName(tokens, ref pos);
                    if (dryRun)
                    {
                        Table? table = db.GetTable(tableName)
                            ?? throw new InvalidOperationException($"Table '{tableName}' does not exist.");
                        if (!table.Columns.Any(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase)))
                        {
                            throw new InvalidOperationException($"Column '{colName}' does not exist on table '{tableName}'.");
                        }
                        EnsureEnd(tokens, pos);
                        return;
                    }
                    EnsureEnd(tokens, pos);
                    db.RemoveColumn(tableName, colName);
                }
                break;
            default:
                throw new NotSupportedException($"ALTER TABLE ... {action} is not supported.");
        }
    }

    // ------------------------------------------------------------------
    // token helpers
    // ------------------------------------------------------------------

    private static Token? Peek(List<Token> tokens, int pos) => pos < tokens.Count ? tokens[pos] : null;

    private static Token PeekOrThrow(List<Token> tokens, int pos)
        => Peek(tokens, pos) ?? throw new InvalidOperationException("Unexpected end of statement.");

    private static void EnsureEnd(List<Token> tokens, int pos)
    {
        if (pos < tokens.Count)
        {
            throw new InvalidOperationException($"Unexpected token '{tokens[pos].Text}'.");
        }
    }

    private static bool PeekWord(List<Token> tokens, int pos, string word)
        => Peek(tokens, pos) is { Kind: Kind.Word or Kind.Ident } t
            && t.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    private static bool PeekAheadWord(List<Token> tokens, int pos, int offset, string word)
        => Peek(tokens, pos + offset) is { Kind: Kind.Word or Kind.Ident } t
            && t.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    private static void ExpectSymbol(List<Token> tokens, ref int pos, string symbol)
    {
        Token t = PeekOrThrow(tokens, pos);
        if (t.Text != symbol)
        {
            throw new InvalidOperationException($"Expected '{symbol}' but found '{t.Text}'.");
        }
        pos++;
    }

    private static void ExpectWord(List<Token> tokens, ref int pos, string word)
    {
        Token t = PeekOrThrow(tokens, pos);
        if (!t.Text.Equals(word, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected '{word}' but found '{t.Text}'.");
        }
        pos++;
    }

    private static string ReadName(List<Token> tokens, ref int pos)
    {
        Token t = PeekOrThrow(tokens, pos);
        if (t.Kind is Kind.Word or Kind.Ident)
        {
            pos++;
            return t.Text;
        }
        throw new InvalidOperationException($"Expected a name but found '{t.Text}'.");
    }

    private static string ReadWord(List<Token> tokens, ref int pos)
        => ReadName(tokens, ref pos);

    private static string RebuildSql(List<Token> tokens, int start, int end = -1)
    {
        if (end < 0)
        {
            end = tokens.Count;
        }
        var sb = new System.Text.StringBuilder();
        for (int i = start; i < end; i++)
        {
            if (i > start && !NeedsNoSpace(tokens[i - 1], tokens[i]))
            {
                sb.Append(' ');
            }
            sb.Append(RenderToken(tokens[i]));
        }
        return sb.ToString();
    }

    private static bool NeedsNoSpace(Token prev, Token cur)
        => prev.Text is "(" or "." || cur.Text is ")" or "," or "(" or ".";

    private static string RenderToken(Token token) => token.Kind switch
    {
        Kind.Ident => "\"" + token.Text.Replace("\"", "\"\"") + "\"",
        Kind.Str => "'" + token.Text.Replace("'", "''") + "'",
        Kind.Date => "#" + token.Text + "#",
        _ => token.Text,
    };
}
