import io.github.spannm.jackcess.ColumnBuilder;
import io.github.spannm.jackcess.Database;
import io.github.spannm.jackcess.DatabaseBuilder;
import io.github.spannm.jackcess.DataType;
import io.github.spannm.jackcess.IndexBuilder;
import io.github.spannm.jackcess.RelationshipBuilder;
import io.github.spannm.jackcess.Table;
import io.github.spannm.jackcess.TableBuilder;

import java.io.File;
import java.math.BigDecimal;
import java.time.LocalDateTime;

/**
 * Creates MS Access databases with the ORIGINAL Jackcess library so the .NET
 * port can be cross-checked against files produced by the Java implementation
 * (the reverse direction of DbDump).
 *
 * Usage: java -cp "jackcess.jar<path-separator>." DbGen <outDir> <name>
 */
public class DbGen {

    public static void main(String[] args) throws Exception {
        if (args.length < 2) {
            System.err.println("usage: DbGen <outDir> <name>");
            System.exit(2);
        }
        File outDir = new File(args[0]);
        outDir.mkdirs();
        String name = args[1];
        File file = new File(outDir, name + ".mdb");
        switch (name) {
            case "genAllTypes":
                createAllTypes(file);
                break;
            case "genIndexed":
                createIndexed(file);
                break;
            case "genEmpty":
                createEmpty(file);
                break;
            case "genIndexedAllTypes":
                createIndexedAllTypes(file);
                break;
            case "genRelated":
                createRelated(file);
                break;
            case "genIndexedEdge":
                createIndexedEdge(file);
                break;
            case "genLinked":
                createLinked(file);
                break;
            case "genRelational":
                createRelational(file);
                break;
            case "sqljoin":
                createRelational(file);
                break;
            default:
                throw new IllegalArgumentException("unknown generator: " + name);
        }
        System.out.println("created " + file.getAbsolutePath());
    }

    private static Database create(String name, File file) throws Exception {
        return DatabaseBuilder.create(io.github.spannm.jackcess.Database.FileFormat.V2003, file);
    }

    private static void createAllTypes(File file) throws Exception {
        try (Database db = create("genAllTypes", file)) {
            Table t = new TableBuilder("t_alltypes")
                .addColumn(new ColumnBuilder("id", DataType.LONG).withAutoNumber(true))
                .addColumn(new ColumnBuilder("name", DataType.TEXT).withLength(50))
                .addColumn(new ColumnBuilder("memo", DataType.MEMO))
                .addColumn(new ColumnBuilder("i", DataType.INT))
                .addColumn(new ColumnBuilder("l", DataType.LONG))
                .addColumn(new ColumnBuilder("d", DataType.DOUBLE))
                .addColumn(new ColumnBuilder("f", DataType.FLOAT))
                .addColumn(new ColumnBuilder("m", DataType.MONEY))
                .addColumn(new ColumnBuilder("num", DataType.NUMERIC).withPrecision(10).withScale(2))
                .addColumn(new ColumnBuilder("dt", DataType.SHORT_DATE_TIME))
                .addColumn(new ColumnBuilder("b", DataType.BOOLEAN))
                .addColumn(new ColumnBuilder("guid", DataType.GUID))
                .addColumn(new ColumnBuilder("bin", DataType.OLE))
                .toTable(db);

            t.addRow(null, "Alpha", "some memo text", (short) 1, 100, 3.14159265358979, 1.5f, new BigDecimal("12345.6789"), new BigDecimal("1234.56"), LocalDateTime.of(2020, 6, 15, 13, 45, 30), true, "{12345678-1234-5678-9ABC-DEF012345678}", new byte[] {1, 2, 3, 4, 5});
            t.addRow(null, "Beta", null, (short) -2, -200, -0.001, -2.5f, new BigDecimal("-0.01"), new BigDecimal("-0.02"), LocalDateTime.of(1899, 12, 30, 0, 0, 0), false, "{87654321-4321-8765-4321-123456789ABC}", new byte[0]);
            t.addRow(null, "Gamma", "unicode: \u00e9\u00fc\u0142\u4e2d\u6587", (short) 32767, Integer.MAX_VALUE, 1.0E100, 3.4E38f, new BigDecimal("9999999999.99"), new BigDecimal("99999999.99"), LocalDateTime.of(2023, 12, 31, 23, 59, 59), true, "{00000000-0000-0000-0000-000000000001}", new byte[] {(byte) 0xFF, (byte) 0x00, (byte) 0x7F});
            t.addRow(null, "Delta", "trailing spaces   ", (short) 0, 0, 0.0, 0.0f, BigDecimal.ZERO, BigDecimal.ZERO, LocalDateTime.of(2000, 1, 1, 12, 0, 0), false, "{11111111-2222-3333-4444-555555555555}", null);
            t.addRow(null, "Epsilon", "tab\there\nnewline", (short) 5, 5, 5.5, 5.5f, new BigDecimal("0.01"), new BigDecimal("0.02"), LocalDateTime.of(1970, 1, 1, 0, 0, 1), true, "{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}", new byte[] {42});
            t.addRow(null, null, null, null, null, null, null, null, null, null, null, null, null);
        }
    }

    private static void createIndexed(File file) throws Exception {
        try (Database db = create("genIndexed", file)) {
            IndexBuilder pk = new IndexBuilder("PrimaryKey").withColumns("id").withPrimaryKey();
            IndexBuilder codeIdx = new IndexBuilder("idx_code").withColumns("code").withUnique();

            Table t = new TableBuilder("t_indexed")
                .addColumn(new ColumnBuilder("id", DataType.LONG))
                .addColumn(new ColumnBuilder("code", DataType.TEXT).withLength(20))
                .addColumn(new ColumnBuilder("value", DataType.DOUBLE))
                .addIndex(pk)
                .addIndex(codeIdx)
                .toTable(db);

            for (int i = 1; i <= 50; i++) {
                t.addRow(i, String.format("code%02d", i), i * 0.5);
            }
        }
    }

    private static void createEmpty(File file) throws Exception {
        try (Database db = create("genEmpty", file)) {
            new TableBuilder("t_empty")
                .addColumn(new ColumnBuilder("id", DataType.LONG).withAutoNumber(true))
                .addColumn(new ColumnBuilder("name", DataType.TEXT).withLength(100))
                .toTable(db);
        }
    }

    private static void createRelated(File file) throws Exception {
        try (Database db = create("genRelated", file)) {
            Table parent = new TableBuilder("t_parent")
                .addColumn(new ColumnBuilder("id", DataType.LONG))
                .addColumn(new ColumnBuilder("name", DataType.TEXT).withLength(30))
                .addIndex(new IndexBuilder("PrimaryKey").withColumns("id").withPrimaryKey())
                .toTable(db);
            Table child = new TableBuilder("t_child")
                .addColumn(new ColumnBuilder("id", DataType.LONG).withAutoNumber(true))
                .addColumn(new ColumnBuilder("parent_id", DataType.LONG))
                .addColumn(new ColumnBuilder("note", DataType.TEXT).withLength(30))
                .addIndex(new IndexBuilder("PrimaryKey").withColumns("id").withPrimaryKey())
                .toTable(db);
            new RelationshipBuilder(parent, child)
                .addColumns("id", "parent_id")
                .withReferentialIntegrity()
                .withCascadeUpdates()
                .withCascadeDeletes()
                .toRelationship(db);

            parent.addRow(1, "parent one");
            parent.addRow(2, "parent two");
            child.addRow(null, 1, "child of one");
            child.addRow(null, 2, "child of two");
        }
    }

    private static void createIndexedEdge(File file) throws Exception {
        try (Database db = create("genIndexedEdge", file)) {
            Table t = new TableBuilder("t_edge")
                .addColumn(new ColumnBuilder("id", DataType.LONG).withAutoNumber(true))
                .addColumn(new ColumnBuilder("first", DataType.TEXT).withLength(20))
                .addColumn(new ColumnBuilder("last", DataType.TEXT).withLength(20))
                .addColumn(new ColumnBuilder("val", DataType.DOUBLE))
                .addColumn(new ColumnBuilder("note", DataType.TEXT).withLength(20))
                .addColumn(new ColumnBuilder("code", DataType.TEXT).withLength(20))
                .addIndex(new IndexBuilder("PrimaryKey").withColumns("id").withPrimaryKey())
                .addIndex(new IndexBuilder("idx_fullname").withColumns("first", "last"))
                .addIndex(new IndexBuilder("idx_val_desc").withColumns(false, "val"))
                .addIndex(new IndexBuilder("idx_note_ignorenulls").withColumns("note").withIgnoreNulls())
                .addIndex(new IndexBuilder("idx_code_required").withColumns("code").withRequired())
                .toTable(db);

            t.addRow(null, "Jan", "Kowalski", 3.5, "n1", "C1");
            t.addRow(null, "Anna", "Nowak", -1.5, null, "C2");
            t.addRow(null, "Piotr", "Wozniak", 0.0, "n3", "C3");
            t.addRow(null, "Jan", "Nowak", 100.0, null, "C4");
        }
    }

    private static void createLinked(File file) throws Exception {
        // first create the linkee database in the same directory
        File linkeeFile = new File(file.getParentFile(), "genLinkee.mdb");
        try (Database linkee = create("genLinkee", linkeeFile)) {
            Table t = new TableBuilder("t_linkee")
                .addColumn(new ColumnBuilder("id", DataType.LONG).withAutoNumber(true))
                .addColumn(new ColumnBuilder("name", DataType.TEXT).withLength(50))
                .toTable(linkee);
            t.addRow(null, "linkee one");
            t.addRow(null, "linkee two");
        }

        try (Database db = create("genLinked", file)) {
            db.createLinkedTable("t_linked", "genLinkee.mdb", "t_linkee");
        }
    }

    /**
     * A small relational schema (master + detail) with rich, parity-stressing data:
     * NULLs, duplicate category/key values, negative amounts, the 1899-12-30 "zero"
     * date, money amounts and text that survives a Jet round-trip. Used by the SQL
     * corpus in tests/fixtures/sql/sqljoin.sql.
     */
    private static void createRelational(File file) throws Exception {
        try (Database db = create("sqljoin", file)) {
            Table master = new TableBuilder("t_master")
                .addColumn(new ColumnBuilder("id", DataType.LONG))
                .addColumn(new ColumnBuilder("name", DataType.TEXT).withLengthInUnits(30))
                .addColumn(new ColumnBuilder("cat", DataType.TEXT).withLengthInUnits(10))
                .addColumn(new ColumnBuilder("active", DataType.BOOLEAN))
                .addColumn(new ColumnBuilder("created", DataType.SHORT_DATE_TIME))
                .addColumn(new ColumnBuilder("budget", DataType.MONEY))
                .addIndex(new IndexBuilder("PrimaryKey").withColumns("id").withPrimaryKey())
                .toTable(db);
            Table detail = new TableBuilder("t_detail")
                .addColumn(new ColumnBuilder("id", DataType.LONG).withAutoNumber(true))
                .addColumn(new ColumnBuilder("master_id", DataType.LONG))
                .addColumn(new ColumnBuilder("qty", DataType.INT))
                .addColumn(new ColumnBuilder("price", DataType.MONEY))
                .addColumn(new ColumnBuilder("dt", DataType.SHORT_DATE_TIME))
                .addColumn(new ColumnBuilder("note", DataType.TEXT).withLengthInUnits(30))
                .addColumn(new ColumnBuilder("code", DataType.TEXT).withLengthInUnits(5))
                .addIndex(new IndexBuilder("PrimaryKey").withColumns("id").withPrimaryKey())
                .toTable(db);
            new RelationshipBuilder(master, detail)
                .addColumns("id", "master_id")
                .withReferentialIntegrity()
                .withCascadeUpdates()
                .withCascadeDeletes()
                .toRelationship(db);

            master.addRow(1, "Alpha", "A", true, LocalDateTime.of(2020, 6, 15, 13, 45, 30), new BigDecimal("1000.00"));
            master.addRow(2, "Beta", "B", false, LocalDateTime.of(1899, 12, 30, 0, 0, 0), new BigDecimal("-250.75"));
            master.addRow(3, "Gamma", "A", true, LocalDateTime.of(2023, 12, 31, 23, 59, 59), new BigDecimal("0.00"));
            master.addRow(4, "Delta", "A", false, LocalDateTime.of(2000, 1, 1, 12, 0, 0), new BigDecimal("12345.6789"));
            master.addRow(5, "Epsilon", "B", true, LocalDateTime.of(1970, 1, 1, 0, 0, 1), new BigDecimal("0.01"));
            master.addRow(6, null, null, null, null, null);
            master.addRow(7, "Alpha", "C", true, LocalDateTime.of(2010, 5, 15, 8, 30, 15), new BigDecimal("999999.99"));

            detail.addRow(null, 1, 2, new BigDecimal("10.50"), LocalDateTime.of(2021, 1, 5, 9, 0, 0), "first item", "a01");
            detail.addRow(null, 1, 3, new BigDecimal("-5.25"), LocalDateTime.of(2021, 2, 10, 10, 30, 0), null, "a02");
            detail.addRow(null, 1, 0, new BigDecimal("0.00"), LocalDateTime.of(2021, 3, 15, 11, 45, 30), "zero qty", "a03");
            detail.addRow(null, 2, 10, new BigDecimal("99.99"), LocalDateTime.of(1899, 12, 30, 0, 0, 0), "old date", "b01");
            detail.addRow(null, 2, 1, new BigDecimal("1000.00"), LocalDateTime.of(2022, 6, 30, 23, 59, 59), "big price", "b02");
            detail.addRow(null, 3, 5, new BigDecimal("7.75"), LocalDateTime.of(2023, 1, 1, 0, 0, 0), "gamma detail", "c01");
            detail.addRow(null, 4, 2, new BigDecimal("1.00"), LocalDateTime.of(2020, 6, 15, 13, 45, 30), "delta detail", "d01");
            detail.addRow(null, null, null, null, null, null, null);
            detail.addRow(null, 5, 100, new BigDecimal("0.01"), LocalDateTime.of(2024, 12, 31, 12, 0, 0), "new year", "e01");
            detail.addRow(null, 1, 4, new BigDecimal("2.50"), LocalDateTime.of(2021, 5, 20, 14, 15, 0), "first item again", "a04");
            detail.addRow(null, 2, 8, new BigDecimal("-0.25"), LocalDateTime.of(2022, 1, 15, 9, 9, 9), "neg price", "b03");
            detail.addRow(null, 7, 6, new BigDecimal("1234.56"), LocalDateTime.of(2010, 5, 15, 8, 30, 15), "seven detail", "g01");
        }
    }

    private static void createIndexedAllTypes(File file) throws Exception {
        try (Database db = create("genIndexedAllTypes", file)) {
            Table t = new TableBuilder("t_idx_alltypes")
                .addColumn(new ColumnBuilder("b", DataType.BYTE))
                .addColumn(new ColumnBuilder("i", DataType.INT))
                .addColumn(new ColumnBuilder("l", DataType.LONG))
                .addColumn(new ColumnBuilder("m", DataType.MONEY))
                .addColumn(new ColumnBuilder("f", DataType.FLOAT))
                .addColumn(new ColumnBuilder("d", DataType.DOUBLE))
                .addColumn(new ColumnBuilder("dt", DataType.SHORT_DATE_TIME))
                .addColumn(new ColumnBuilder("bool", DataType.BOOLEAN))
                .addColumn(new ColumnBuilder("num", DataType.NUMERIC).withPrecision(10).withScale(2))
                .addColumn(new ColumnBuilder("guid", DataType.GUID))
                .addColumn(new ColumnBuilder("txt", DataType.TEXT).withLength(20))
                .addIndex(new IndexBuilder("idx_b").withColumns("b"))
                .addIndex(new IndexBuilder("idx_i").withColumns("i"))
                .addIndex(new IndexBuilder("idx_l").withColumns("l"))
                .addIndex(new IndexBuilder("idx_l_desc").withColumns(false, "l"))
                .addIndex(new IndexBuilder("idx_m").withColumns("m"))
                .addIndex(new IndexBuilder("idx_f").withColumns("f"))
                .addIndex(new IndexBuilder("idx_d").withColumns("d"))
                .addIndex(new IndexBuilder("idx_dt").withColumns("dt"))
                .addIndex(new IndexBuilder("idx_bool").withColumns("bool"))
                .addIndex(new IndexBuilder("idx_num").withColumns("num"))
                .addIndex(new IndexBuilder("idx_guid").withColumns("guid"))
                .addIndex(new IndexBuilder("idx_txt").withColumns("txt"))
                .toTable(db);

            t.addRow((byte) 1, (short) 10, 100, new BigDecimal("1.25"), 1.5f, 2.5, LocalDateTime.of(2020, 1, 2, 3, 4, 5), true, new BigDecimal("12.34"), "{11111111-1111-1111-1111-111111111111}", "alpha");
            t.addRow((byte) 2, (short) 20, 200, new BigDecimal("-2.50"), -1.5f, -2.5, LocalDateTime.of(1999, 12, 31, 23, 59, 59), false, new BigDecimal("-99.99"), "{22222222-2222-2222-2222-222222222222}", "beta");
            t.addRow((byte) 3, (short) 30, 300, new BigDecimal("0.00"), 0.0f, 0.0, LocalDateTime.of(1970, 1, 1, 0, 0, 0), true, new BigDecimal("0.01"), "{33333333-3333-3333-3333-333333333333}", "gamma");
            t.addRow((byte) 4, (short) 40, 400, new BigDecimal("99999.99"), 3.4E38f, 1.0E100, LocalDateTime.of(2023, 6, 15, 8, 30, 15), false, new BigDecimal("1234567.89"), "{44444444-4444-4444-4444-444444444444}", null);
            t.addRow((byte) 5, (short) 50, 500, new BigDecimal("0.01"), -3.4E38f, -1.0E100, LocalDateTime.of(1899, 12, 30, 0, 0, 0), true, new BigDecimal("0.00"), "{55555555-5555-5555-5555-555555555555}", "\u00e9psilon");
        }
    }
}
