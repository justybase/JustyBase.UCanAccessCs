using System.Text.Json;
using UCanAccess.File;
using Xunit;

namespace UCanAccess.File.Tests;

/// <summary>
/// Differential tests: opens each Access fixture with the port and compares the
/// decoded schema + data against the JSON produced by the real Jackcess (oracle).
/// </summary>
public class DifferentialTests
{
    static DifferentialTests()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "fixtures");

    private static string OracleDir => Path.Combine(FixtureDir, "oracle");

    /// <summary>fixtures which contain linked tables pointing at non-existent files</summary>
    private static readonly string[] LinkedOnlyFixtures = { "linked", "generated/genLinked" };

    /// <summary>Jet 3 (Access 97) fixtures; the oracle decoded these with the GBK charset</summary>
    private static readonly string[] Jet3Fixtures = { "charsetGBK", "size97" };

    public static IEnumerable<object[]> Fixtures()
    {
        foreach (string database in Directory.GetFiles(FixtureDir, "*.*")
                     .Where(IsAccessDatabase)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileNameWithoutExtension(database);
            if (LinkedOnlyFixtures.Contains(name))
            {
                continue;
            }
            yield return new object[] { name };
        }
        // generated fixtures (produced by the Java DbGen oracle)
        string generatedDir = Path.Combine(FixtureDir, "generated");
        if (Directory.Exists(generatedDir))
        {
            foreach (string database in Directory.GetFiles(generatedDir, "*.*")
                         .Where(IsAccessDatabase)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string name = "generated/" + Path.GetFileNameWithoutExtension(database);
                if (LinkedOnlyFixtures.Contains(name))
                {
                    continue;
                }
                // This fixture is authored through Access COM/DAO because the
                // Java fixture generator cannot create attachment and
                // multi-value fields. Its focused ComplexTypeTests provide the
                // contract instead of a Jackcess JSON oracle.
                if (name.Equals("generated/complex", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                yield return new object[] { name };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Schema_and_data_match_oracle(string fixture)
    {
        // "generated/x" fixtures live in the generated/ subdirectory
        string relative = fixture.StartsWith("generated/", StringComparison.Ordinal)
            ? fixture.Replace('/', Path.DirectorySeparatorChar)
            : fixture;
        string database = Path.Combine(FixtureDir, relative);
        if (!IsAccessDatabase(database))
        {
            database = Directory.GetFiles(Path.GetDirectoryName(database)!,
                    Path.GetFileName(database) + ".*")
                .FirstOrDefault(IsAccessDatabase) ?? database;
        }
        string json = Path.Combine(OracleDir, Path.GetFileName(fixture) + ".json");
        Assert.True(System.IO.File.Exists(database), $"missing fixture {database}");
        Assert.True(System.IO.File.Exists(json), $"missing oracle {json}");

        using var db = Jet3Fixtures.Contains(Path.GetFileNameWithoutExtension(fixture))
            ? Database.Open(database, System.Text.Encoding.GetEncoding(936))
            : Database.Open(database);
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(json));

        var oracleTables = doc.RootElement.GetProperty("tables").EnumerateArray().ToList();
        var myTables = db.GetTableNames().ToHashSet(StringComparer.OrdinalIgnoreCase);

        // every oracle table (that wasn't an error) must exist
        foreach (var ot in oracleTables)
        {
            string tname = ot.GetProperty("name").GetString()!;
            if (ot.TryGetProperty("error", out _))
            {
                // oracle could not load it (e.g. broken index); just check name presence
                Assert.True(myTables.Contains(tname), $"oracle table '{tname}' not found in port");
                continue;
            }
            Assert.True(myTables.Contains(tname), $"oracle table '{tname}' not found in port");

            var table = db.GetTable(tname);
            Assert.NotNull(table);
            Assert.Equal(tname, table!.Name);

            if (ot.TryGetProperty("structure", out JsonElement structure))
            {
                Assert.Equal(structure.GetProperty("rowCount").GetInt32(), table.RowCount);
                Assert.Equal(structure.GetProperty("columnCount").GetInt32(), table.Columns.Count);
                Assert.Equal(structure.GetProperty("indexCount").GetInt32(), table.Indexes.Count);
            }

            // ---- columns ----
            var oracleCols = ot.GetProperty("columns").EnumerateArray().ToList();
            Assert.Equal(oracleCols.Count, table.Columns.Count);

            for (int c = 0; c < oracleCols.Count; c++)
            {
                var oc = oracleCols[c];
                Column col = table.Columns[c];
                Assert.Equal(oc.GetProperty("name").GetString(), col.Name);
                Assert.Equal(oc.GetProperty("type").GetString(), DataTypeToOracleName(col.Type));
                Assert.Equal(oc.GetProperty("length").GetInt32(), col.ColumnLength);
                Assert.Equal(oc.GetProperty("autoNumber").GetBoolean(), col.AutoNumber);
                Assert.Equal(oc.GetProperty("calculated").GetBoolean(), col.Calculated);
                Assert.Equal(oc.GetProperty("precision").GetInt32(), (int)col.Precision);
                Assert.Equal(oc.GetProperty("scale").GetInt32(), (int)col.Scale);
                if (oc.TryGetProperty("required", out JsonElement required))
                {
                    Assert.Equal(required.GetBoolean(), col.Required);
                }
            }

            if (ot.TryGetProperty("indexes", out JsonElement oracleIndexes))
            {
                var myIndexes = db.GetIndexInfo(tname)
                    .ToDictionary(index => index.Name, StringComparer.OrdinalIgnoreCase);
                Assert.Equal(oracleIndexes.GetArrayLength(), myIndexes.Count);
                foreach (JsonElement oi in oracleIndexes.EnumerateArray())
                {
                    string indexName = oi.GetProperty("name").GetString()!;
                    Assert.True(myIndexes.TryGetValue(indexName, out IndexInfo? index),
                        $"index '{indexName}' missing from table '{tname}'");
                    IndexImpl physicalIndex = table.Indexes.First(i =>
                        string.Equals(i.Name, indexName, StringComparison.OrdinalIgnoreCase));
                    Assert.Equal(oi.GetProperty("foreignKey").GetBoolean(), physicalIndex.IsForeignKey);
                    Assert.Equal(oi.GetProperty("primaryKey").GetBoolean(), index!.PrimaryKey);
                    Assert.Equal(oi.GetProperty("unique").GetBoolean(), index.Unique);
                    Assert.Equal(oi.GetProperty("required").GetBoolean(), index.Required);
                    Assert.Equal(oi.GetProperty("ignoreNulls").GetBoolean(), index.IgnoreNulls);
                    var oracleIndexColumns = oi.GetProperty("columns").EnumerateArray().ToList();
                    Assert.Equal(oracleIndexColumns.Count, index.Columns.Count);
                    for (int i = 0; i < oracleIndexColumns.Count; i++)
                    {
                        Assert.Equal(oracleIndexColumns[i].GetProperty("name").GetString(), index.Columns[i].Name);
                        Assert.Equal(oracleIndexColumns[i].GetProperty("ascending").GetBoolean(), index.Columns[i].Ascending);
                    }
                }
            }

            // ---- rows ----
            var oracleRows = ot.GetProperty("rows").EnumerateArray().ToList();
            var myRows = table.Rows().ToList();

            Assert.True(myRows.Count >= oracleRows.Count,
                $"table '{tname}': expected {oracleRows.Count} rows, got {myRows.Count}");
            Assert.Equal(oracleRows.Count, myRows.Count);

            for (int r = 0; r < oracleRows.Count; r++)
            {
                var oracleRow = oracleRows[r];
                Row myRow = myRows[r];
                var oracleValues = oracleRow.EnumerateArray().ToList();
                Assert.Equal(oracleValues.Count, myRow.Count);
                for (int c = 0; c < oracleValues.Count; c++)
                {
                    string expected = CanonicalizeJson(oracleValues[c]);
                    string actual = CanonicalizeCSharp(myRow[c]);
                    Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
                        $"table '{tname}' row {r} col {c} ('{table.Columns[c].Name}'): expected {expected}, got {actual}");
                }
            }
        }

        if (doc.RootElement.TryGetProperty("relationships", out JsonElement oracleRelationships))
        {
            var actualRelationships = db.GetRelationships();
            Assert.Equal(oracleRelationships.GetArrayLength(), actualRelationships.Count);
            foreach (JsonElement expected in oracleRelationships.EnumerateArray())
            {
                string relationshipName = expected.GetProperty("name").GetString()!;
                Relationship actual = actualRelationships.Single(rel =>
                    rel.Name.Equals(relationshipName, StringComparison.OrdinalIgnoreCase));
                Assert.Equal(expected.GetProperty("fromTable").GetString(), actual.FromTable.Name);
                Assert.Equal(expected.GetProperty("toTable").GetString(), actual.ToTable.Name);
                Assert.Equal(expected.GetProperty("oneToOne").GetBoolean(), actual.IsOneToOne);
                Assert.Equal(expected.GetProperty("referentialIntegrity").GetBoolean(), actual.HasReferentialIntegrity);
                Assert.Equal(expected.GetProperty("cascadeUpdates").GetBoolean(), actual.CascadeUpdates);
                Assert.Equal(expected.GetProperty("cascadeDeletes").GetBoolean(), actual.CascadeDeletes);
                Assert.Equal(expected.GetProperty("cascadeNullOnDelete").GetBoolean(), actual.CascadeNullOnDelete);
                Assert.Equal(expected.GetProperty("fromColumns").EnumerateArray()
                    .Select(column => column.GetString()), actual.FromColumns.Select(column => column.Name));
                Assert.Equal(expected.GetProperty("toColumns").EnumerateArray()
                    .Select(column => column.GetString()), actual.ToColumns.Select(column => column.Name));
            }
        }
    }

    private static string DataTypeToOracleName(DataType type) => type switch
    {
        DataType.Boolean => "BOOLEAN",
        DataType.Byte => "BYTE",
        DataType.Int => "INT",
        DataType.Long => "LONG",
        DataType.Money => "MONEY",
        DataType.Float => "FLOAT",
        DataType.Double => "DOUBLE",
        DataType.ShortDateTime => "SHORT_DATE_TIME",
        DataType.Binary => "BINARY",
        DataType.Text => "TEXT",
        DataType.Ole => "OLE",
        DataType.Memo => "MEMO",
        DataType.Unknown0D => "UNKNOWN_0D",
        DataType.Guid => "GUID",
        DataType.Numeric => "NUMERIC",
        DataType.Unknown11 => "UNKNOWN_11",
        DataType.ComplexType => "COMPLEX_TYPE",
        DataType.BigInt => "BIG_INT",
        DataType.ExtDateTime => "EXT_DATE_TIME",
        _ => type.ToString(),
    };

    private static bool IsAccessDatabase(string path)
        => string.Equals(Path.GetExtension(path), ".mdb", StringComparison.OrdinalIgnoreCase)
           || string.Equals(Path.GetExtension(path), ".accdb", StringComparison.OrdinalIgnoreCase);

    private static string CanonicalizeJson(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return "null";
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.Number:
                return value.GetRawText();
            case JsonValueKind.String:
                return value.GetString() ?? "null";
            case JsonValueKind.Object:
                if (value.TryGetProperty("f", out var f)) return "f:" + Trim0x(f.GetString());
                if (value.TryGetProperty("d", out var d)) return "d:" + Trim0x(d.GetString());
                if (value.TryGetProperty("dec", out var dec))
                {
                    var parts = dec.EnumerateArray().ToList();
                    var unscaled = System.Numerics.BigInteger.Parse(parts[0].GetString()!);
                    int scale = parts[1].GetInt32();
                    return "dec:" + CanonicalDecimal(unscaled, scale);
                }
                if (value.TryGetProperty("dt", out var dt)) return "dt:" + dt.GetString();
                if (value.TryGetProperty("b64", out var b64)) return "b64:" + b64.GetString();
                return value.GetRawText();
            default:
                return value.GetRawText();
        }
    }

    private static string CanonicalizeCSharp(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        byte n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        sbyte n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        short n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ushort n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        int n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        uint n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        long n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ulong n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        float f => "f:" + BitConverter.SingleToInt32Bits(f).ToString("x"),
        double d => "d:" + BitConverter.DoubleToInt64Bits(d).ToString("x"),
        decimal m => "dec:" + CanonicalDecimal(m),
        DateTime dt => "dt:" + FormatDateTime(dt),
        byte[] bytes => "b64:" + Convert.ToBase64String(bytes),
        string s => s,
        _ => value.ToString() ?? "null",
    };

    private static string Trim0x(string? hex)
        => hex is not null && hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex ?? "";

    private static string CanonicalDecimal(decimal value)
    {
        int[] bits = decimal.GetBits(value);
        var unscaled = (System.Numerics.BigInteger)(uint)bits[0]
                       | ((System.Numerics.BigInteger)(uint)bits[1] << 32)
                       | ((System.Numerics.BigInteger)(uint)bits[2] << 64);
        if ((bits[3] & int.MinValue) != 0)
        {
            unscaled = -unscaled;
        }

        return CanonicalDecimal(unscaled, (bits[3] >> 16) & 0x7f);
    }

    private static string CanonicalDecimal(System.Numerics.BigInteger unscaled, int scale)
    {
        bool negative = unscaled.Sign < 0;
        string digits = System.Numerics.BigInteger.Abs(unscaled).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string result;
        if (scale <= 0)
        {
            result = digits + new string('0', -scale);
        }
        else if (digits.Length <= scale)
        {
            result = "0." + new string('0', scale - digits.Length) + digits;
        }
        else
        {
            int point = digits.Length - scale;
            result = digits[..point] + "." + digits[point..];
        }

        if (result.Contains('.'))
        {
            result = result.TrimEnd('0').TrimEnd('.');
        }

        if (result.Length == 0 || result == "0")
        {
            return "0";
        }

        return negative ? "-" + result : result;
    }

    /// <summary>
    /// Formats a DateTime the same way Java's <c>LocalDateTime.toString()</c> formats
    /// a value, retaining the available fractional precision (seconds omitted when zero).
    /// </summary>
    private static string FormatDateTime(DateTime dt)
    {
        string baseStr = dt.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        int fraction = (int)(dt.Ticks % TimeSpan.TicksPerSecond);
        int seconds = dt.Second;
        if (seconds == 0 && fraction == 0)
        {
            return baseStr[..^3]; // "yyyy-MM-ddTHH:mm"
        }
        if (fraction == 0)
        {
            return baseStr;
        }
        long nanos = (long)fraction * 100L;
        string fractionStr = nanos % 1_000_000L == 0
            ? (nanos / 1_000_000L).ToString("000")
            : nanos % 1_000L == 0
                ? (nanos / 1_000L).ToString("000000")
                : nanos.ToString("000000000");
        return baseStr + "." + fractionStr;
    }
}
