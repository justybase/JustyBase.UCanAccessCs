import io.github.spannm.jackcess.ColumnBuilder;
import io.github.spannm.jackcess.Database;
import io.github.spannm.jackcess.DatabaseBuilder;
import io.github.spannm.jackcess.Database.FileFormat;
import io.github.spannm.jackcess.DataType;
import io.github.spannm.jackcess.Table;
import io.github.spannm.jackcess.TableBuilder;

import java.io.File;
import java.math.BigDecimal;
import java.time.LocalDateTime;

/**
 * Creates MS Access ACCDB databases (.accdb) with the ORIGINAL Jackcess library so the
 * .NET port can be cross-checked against files produced by the Java implementation.
 *
 * Usage: java -cp "jackcess.jar<path-separator>." AccdbGen <outDir> <name> <version:2007|2010|2016>
 */
public class AccdbGen {

    public static void main(String[] args) throws Exception {
        if (args.length < 3) {
            System.err.println("usage: AccdbGen <outDir> <name> <version>");
            System.exit(2);
        }
        File outDir = new File(args[0]);
        outDir.mkdirs();
        String name = args[1];
        FileFormat ff = switch (args[2]) {
            case "2010" -> FileFormat.V2010;
            case "2016" -> FileFormat.V2016;
            default -> FileFormat.V2007;
        };
        File file = new File(outDir, name + ".accdb");
        try (Database db = DatabaseBuilder.create(ff, file)) {
            Table t = new TableBuilder("t_data")
                .addColumn(new ColumnBuilder("id", DataType.LONG).withAutoNumber(true))
                .addColumn(new ColumnBuilder("name", DataType.TEXT).withLengthInUnits(30))
                .addColumn(new ColumnBuilder("val", DataType.DOUBLE))
                .addColumn(new ColumnBuilder("m", DataType.MONEY))
                .addColumn(new ColumnBuilder("dt", DataType.SHORT_DATE_TIME))
                .addColumn(new ColumnBuilder("active", DataType.BOOLEAN))
                .toTable(db);
            t.addRow(null, "Alpha", 1.5, new BigDecimal("10.50"), LocalDateTime.of(2020, 6, 15, 13, 45, 30), true);
            t.addRow(null, "Beta", -2.25, new BigDecimal("-0.01"), LocalDateTime.of(1899, 12, 30, 0, 0, 0), false);
            t.addRow(null, "Gamma", 100.0, new BigDecimal("999.99"), LocalDateTime.of(2023, 12, 31, 23, 59, 59), true);
            t.addRow(null, null, null, null, null, null);

            Table u = new TableBuilder("t_grp")
                .addColumn(new ColumnBuilder("gid", DataType.LONG).withAutoNumber(true))
                .addColumn(new ColumnBuilder("grp", DataType.TEXT).withLengthInUnits(10))
                .addColumn(new ColumnBuilder("val", DataType.LONG))
                .toTable(db);
            u.addRow(null, "A", 10);
            u.addRow(null, "A", 20);
            u.addRow(null, "B", 5);
            u.addRow(null, "B", 15);
            u.addRow(null, "C", 100);
        }
        System.out.println("created " + file.getAbsolutePath());
    }
}
