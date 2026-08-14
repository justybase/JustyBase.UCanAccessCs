using System.Text;

namespace UCanAccess.File;

/// <summary>types of saved Access queries</summary>
public enum QueryType
{
    Select,
    MakeTable,
    Append,
    Update,
    Delete,
    CrossTab,
    DataDefinition,
    Passthrough,
    Union,
    Unknown,
}

/// <summary>
/// A single row of the system query table (MSysQueries).
/// </summary>
public sealed class QueryRow
{
    public byte Attribute { get; init; }
    public string? Expression { get; init; }
    public short Flag { get; init; }
    public int Extra { get; init; }
    public string? Name1 { get; init; }
    public string? Name2 { get; init; }
    public int ObjectId { get; init; }
    public byte[]? Order { get; init; }
}

/// <summary>
/// A saved Access query (querydef), read from MSysObjects + MSysQueries
/// (port of Jackcess <c>QueryImpl</c>/<c>BaseSelectQueryImpl</c>).
/// </summary>
public sealed class QueryDef
{
    private const int ObjectFlagMask = 0xF0;

    private const byte StartAttribute = 0;
    private const byte TypeAttribute = 1;
    private const byte ParameterAttribute = 2;
    private const byte FlagAttribute = 3;
    private const byte RemoteDbAttribute = 4;
    private const byte TableAttribute = 5;
    private const byte ColumnAttribute = 6;
    private const byte JoinAttribute = 7;
    private const byte WhereAttribute = 8;
    private const byte GroupByAttribute = 9;
    private const byte HavingAttribute = 10;
    private const byte OrderByAttribute = 11;
    private const byte EndAttribute = 255;

    private const short SelectStarSelectType = 0x01;
    private const short DistinctSelectType = 0x02;
    private const short DistinctRowSelectType = 0x08;
    private const short TopSelectType = 0x10;
    private const short PercentSelectType = 0x20;

    private static readonly Dictionary<short, string> JoinTypeMap = new()
    {
        [1] = " INNER JOIN ",
        [2] = " LEFT JOIN ",
        [3] = " RIGHT JOIN ",
    };

    internal QueryDef(string name, int objectId, int objectFlags, IReadOnlyList<QueryRow> rows)
    {
        Name = name;
        ObjectId = objectId;
        ObjectFlags = objectFlags;
        Rows = rows;

        int objTypeFlag = objectFlags & ObjectFlagMask;
        if (objTypeFlag == 0)
        {
            // sometimes the query rows tell a different story
            QueryRow? typeRow = GetRowByAttribute(TypeAttribute);
            if (typeRow != null)
            {
                QueryType? rowType = TypeFromValue(typeRow.Flag);
                if (rowType != null && (int)rowType != objTypeFlag)
                {
                    objTypeFlag = (int)rowType;
                }
            }
        }

        Type = objTypeFlag switch
        {
            0 => QueryType.Select,
            80 => QueryType.MakeTable,
            64 => QueryType.Append,
            48 => QueryType.Update,
            32 => QueryType.Delete,
            16 => QueryType.CrossTab,
            96 => QueryType.DataDefinition,
            112 => QueryType.Passthrough,
            128 => QueryType.Union,
            _ => QueryType.Unknown,
        };
    }

    public string Name { get; }

    public int ObjectId { get; }

    public int ObjectFlags { get; }

    public QueryType Type { get; }

    public IReadOnlyList<QueryRow> Rows { get; }

    /// <summary>whether the saved SELECT declares one or more parameters</summary>
    public bool HasParameters => Rows.Any(row => row.Attribute == ParameterAttribute);

    /// <summary>declared parameter names in Access catalog order</summary>
    public IReadOnlyList<string> ParameterNames
        => Rows.Where(row => row.Attribute == ParameterAttribute && row.Name1 != null)
            .Select(row => row.Name1!)
            .ToArray();

    private static QueryType? TypeFromValue(short value) => value switch
    {
        1 => QueryType.Select,
        2 => QueryType.MakeTable,
        3 => QueryType.Append,
        4 => QueryType.Update,
        5 => QueryType.Delete,
        6 => QueryType.CrossTab,
        7 => QueryType.DataDefinition,
        8 => QueryType.Passthrough,
        9 => QueryType.Union,
        _ => null,
    };

    // ------------------------------------------------------------------
    // row accessors
    // ------------------------------------------------------------------

    private List<QueryRow> GetRowsByAttribute(byte attribute)
        => Rows.Where(r => r.Attribute == attribute).ToList();

    private QueryRow? GetRowByAttribute(byte attribute)
    {
        List<QueryRow> rows = GetRowsByAttribute(attribute);
        if (rows.Count == 1)
        {
            return rows[0];
        }
        return null;
    }

    private static short GetShort(short? value, short def) => value ?? def;

    private static bool HasFlag(QueryRow? row, int flagMask)
        => row != null && (GetShort(row.Flag, 0) & flagMask) != 0;

    // ------------------------------------------------------------------
    // SQL reconstruction (SELECT queries)
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns the reconstructed Access SQL for this query (null if it cannot be reconstructed).
    /// </summary>
    public string? Sql
    {
        get
        {
            try
            {
                return Type == QueryType.Select ? ToSelectSql() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private string ToSelectSql()
    {
        var sb = new StringBuilder();
        AppendParameters(sb);
        AppendSelect(sb);
        return sb.ToString();
    }

    private void AppendParameters(StringBuilder sb)
    {
        List<QueryRow> parameters = GetRowsByAttribute(ParameterAttribute);
        if (parameters.Count == 0)
        {
            return;
        }
        sb.Append("PARAMETERS ");
        var parts = new List<string>();
        foreach (QueryRow row in parameters)
        {
            string? typeName = DataTypeInfo.GetTypeName(row.Flag);
            if (typeName == null)
            {
                throw new NotSupportedException(
                    $"Saved query '{Name}' uses an unsupported parameter type value {row.Flag}.");
            }
            string p = row.Name1 + " " + typeName;
            if (DataTypeInfo.IsTextual((DataType)row.Flag) && row.Extra > 0)
            {
                p += $"({row.Extra})";
            }
            parts.Add(p);
        }
        sb.Append(string.Join(", ", parts));
        sb.Append(";\n");
    }

    private void AppendSelect(StringBuilder sb)
    {
        sb.Append("SELECT ");

        QueryRow? flagRow = GetRowByAttribute(FlagAttribute);
        if (HasFlag(flagRow, DistinctSelectType))
        {
            sb.Append("DISTINCT ");
        }
        else if (HasFlag(flagRow, DistinctRowSelectType))
        {
            sb.Append("DISTINCTROW ");
        }

        if (HasFlag(flagRow, TopSelectType))
        {
            sb.Append("TOP ");
            sb.Append(flagRow!.Name1 ?? "1");
            if (HasFlag(flagRow, PercentSelectType))
            {
                sb.Append(" PERCENT");
            }
            sb.Append(' ');
        }

        // columns
        var cols = new List<string>();
        foreach (QueryRow row in GetRowsByAttribute(ColumnAttribute))
        {
            var c = new StringBuilder();
            c.Append(row.Expression);
            AppendAlias(c, row.Name1);
            cols.Add(c.ToString());
        }
        if (HasFlag(flagRow, SelectStarSelectType))
        {
            cols.Add("*");
        }
        sb.Append(string.Join(", ", cols));

        // FROM (tables + joins)
        List<string> fromTables = FromTables();
        if (fromTables.Count > 0)
        {
            sb.Append("\nFROM ").Append(string.Join(", ", fromTables));
        }

        QueryRow? whereRow = GetRowByAttribute(WhereAttribute);
        if (whereRow?.Expression != null)
        {
            sb.Append("\nWHERE ").Append(whereRow.Expression);
        }

        List<QueryRow> groupings = GetRowsByAttribute(GroupByAttribute);
        if (groupings.Count > 0)
        {
            sb.Append("\nGROUP BY ");
            sb.Append(string.Join(", ", groupings.Select(g => g.Expression)));
        }

        QueryRow? havingRow = GetRowByAttribute(HavingAttribute);
        if (havingRow?.Expression != null)
        {
            sb.Append("\nHAVING ").Append(havingRow.Expression);
        }

        List<QueryRow> orderings = GetRowsByAttribute(OrderByAttribute);
        if (orderings.Count > 0)
        {
            sb.Append("\nORDER BY ");
            sb.Append(string.Join(", ", orderings.Select(o =>
                o.Expression + (string.Equals(o.Name1, "D", StringComparison.OrdinalIgnoreCase) ? " DESC" : ""))));
        }
    }

    private static void AppendAlias(StringBuilder sb, string? alias)
    {
        if (alias != null)
        {
            sb.Append(" AS ");
            sb.Append(QuoteIdentifier(alias));
        }
    }

    private static string QuoteIdentifier(string name)
        => name.Any(c => !char.IsLetterOrDigit(c) && c != '_') && !(name.StartsWith('[') && name.EndsWith(']'))
            ? "[" + name + "]"
            : name;

    private List<string> FromTables()
    {
        var tableExprs = new List<TableSource>();
        foreach (QueryRow table in GetRowsByAttribute(TableAttribute))
        {
            var builder = new StringBuilder();
            if (table.Expression != null)
            {
                QuoteExpr(builder, table.Expression);
                builder.Append('.');
            }
            if (table.Name1 != null)
            {
                OptionalQuoteExpr(builder, table.Name1, true);
            }
            AppendAlias(builder, table.Name2);

            string key = table.Name2 ?? table.Name1 ?? "";
            tableExprs.Add(new SimpleTable(key, builder.ToString()));
        }

        foreach (QueryRow joinRow in GetRowsByAttribute(JoinAttribute))
        {
            string fromTable = joinRow.Name1 ?? "";
            string toTable = joinRow.Name2 ?? "";

            TableSource? fromTs = null;
            TableSource? toTs = null;
            for (int i = tableExprs.Count - 1; i >= 0 && (fromTs == null || toTs == null); i--)
            {
                TableSource ts = tableExprs[i];
                if (fromTs == null && ts.ContainsTable(fromTable))
                {
                    fromTs = ts;
                    if (toTs == null && ts.ContainsTable(toTable))
                    {
                        toTs = ts;
                        break;
                    }
                    tableExprs.RemoveAt(i);
                }
                else if (toTs == null && ts.ContainsTable(toTable))
                {
                    toTs = ts;
                    tableExprs.RemoveAt(i);
                }
            }

            fromTs ??= new SimpleTable(fromTable);
            toTs ??= new SimpleTable(toTable);

            if (fromTs == toTs)
            {
                if (fromTs.SameJoin(joinRow.Flag, joinRow.Expression))
                {
                    continue;
                }
                throw new InvalidOperationException($"Inconsistent join types for {fromTable} and {toTable}");
            }

            tableExprs.Add(new JoinTable(fromTs, toTs, joinRow.Flag, joinRow.Expression));
        }

        return tableExprs.Select(t => t.ToString()).ToList();
    }

    private static void QuoteExpr(StringBuilder builder, string expr)
        => builder.Append(expr.Length >= 2 && expr[0] == '[' && expr[^1] == ']' ? expr : $"[{expr}]");

    private static void OptionalQuoteExpr(StringBuilder builder, string fullExpr, bool isIdentifier)
    {
        string[] exprs = isIdentifier ? fullExpr.Split('.') : new[] { fullExpr };
        for (int i = 0; i < exprs.Length; i++)
        {
            string expr = exprs[i];
            if (expr.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
            {
                QuoteExpr(builder, expr);
            }
            else
            {
                builder.Append(expr);
            }
            if (i < exprs.Length - 1)
            {
                builder.Append('.');
            }
        }
    }

    private abstract class TableSource
    {
        public override string ToString()
        {
            var sb = new StringBuilder();
            Render(sb, true);
            return sb.ToString();
        }

        internal abstract void Render(StringBuilder sb, bool isTopLevel);

        public abstract bool ContainsTable(string table);

        public abstract bool SameJoin(short type, string? on);
    }

    private sealed class SimpleTable : TableSource
    {
        private readonly string _tableName;
        private readonly string _tableExpr;

        public SimpleTable(string tableName, string? tableExpr = null)
        {
            _tableName = tableName;
            _tableExpr = tableExpr ?? QuoteIdentifier(tableName);
        }

        internal override void Render(StringBuilder sb, bool isTopLevel) => sb.Append(_tableExpr);

        public override bool ContainsTable(string table) => _tableName.Equals(table, StringComparison.OrdinalIgnoreCase);

        public override bool SameJoin(short type, string? on) => false;
    }

    private sealed class JoinTable : TableSource
    {
        private readonly TableSource _from;
        private readonly TableSource _to;
        private readonly short _jType;
        private readonly List<string> _on = new();

        public JoinTable(TableSource from, TableSource to, short type, string? on)
        {
            _from = from;
            _to = to;
            _jType = type;
            if (on != null)
            {
                _on.Add(on);
            }
        }

        internal override void Render(StringBuilder sb, bool isTopLevel)
        {
            string joinType = JoinTypeMap[_jType];
            if (!isTopLevel)
            {
                sb.Append('(');
            }
            _from.Render(sb, false);
            sb.Append(joinType);
            _to.Render(sb, false);
            sb.Append(" ON ");
            bool multi = _on.Count > 1;
            if (multi)
            {
                sb.Append('(');
            }
            sb.Append(string.Join(") AND (", _on));
            if (multi)
            {
                sb.Append(')');
            }
            if (!isTopLevel)
            {
                sb.Append(')');
            }
        }

        public override bool ContainsTable(string table)
            => _from.ContainsTable(table) || _to.ContainsTable(table);

        public override bool SameJoin(short type, string? on)
        {
            if (_jType == type)
            {
                _on.Insert(0, on ?? "");
                return true;
            }
            return false;
        }
    }
}
