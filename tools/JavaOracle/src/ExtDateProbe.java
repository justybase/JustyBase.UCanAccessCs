import io.github.spannm.jackcess.ColumnBuilder;
import io.github.spannm.jackcess.DataType;
import io.github.spannm.jackcess.Database;
import io.github.spannm.jackcess.DatabaseBuilder;
import io.github.spannm.jackcess.Table;
import io.github.spannm.jackcess.TableBuilder;

import java.io.File;
import java.time.LocalDateTime;

/**
 * Creates an MDB with an EXT_DATE_TIME column, inserts rows with known
 * values (including null), then prints the values Jackcess reads back.
 * Usage: ExtDateProbe <output.accdb>
 */
public final class ExtDateProbe {

    private ExtDateProbe() {
    }

    public static void main(String[] args) throws Exception {
        allowExtDateTimeCreation();
        File file = new File(args[0]);
        try (Database db = DatabaseBuilder.create(Database.FileFormat.V2007, file)) {
            Table t = new TableBuilder("t_ext")
                .addColumn(new ColumnBuilder("id", DataType.LONG))
                .addColumn(new ColumnBuilder("dt", DataType.EXT_DATE_TIME))
                .addColumn(new ColumnBuilder("note", DataType.TEXT).withLength(30))
                .toTable(db);

            t.addRow(1, LocalDateTime.of(2024, 1, 2, 3, 4, 5, 123456000), "modern");
            t.addRow(2, LocalDateTime.of(1899, 12, 30, 0, 0, 0), "base");
            t.addRow(3, null, "null dt");
            t.addRow(4, LocalDateTime.of(9999, 12, 31, 23, 59, 59, 999999000), "max");
            t.addRow(5, LocalDateTime.of(1, 1, 1, 0, 0, 0), "year one");
            t.addRow(6, LocalDateTime.of(2024, 1, 2, 3, 4, 5, 678900000), "frac");
        }

        try (Database db = DatabaseBuilder.open(file)) {
            Table t = db.getTable("t_ext");
            for (io.github.spannm.jackcess.Row row : t) {
                System.out.println("id=" + row.get("id") + " dt=" + row.get("dt") + " note=" + row.get("note"));
            }
        }
    }

    /**
     * Jackcess 5.1.5 rejects EXT_DATE_TIME at table-creation time in every
     * format (its unsupported-type sets are unioned).  Real Access creates such
     * columns in V12+ files, so clear the set entry before generating the
     * fixture; the underlying write path is fully implemented.
     */
    private static void allowExtDateTimeCreation() throws Exception {
        java.lang.reflect.Field field = io.github.spannm.jackcess.impl.JetFormat.class
            .getDeclaredField("V12_UNSUPP_TYPES");
        field.setAccessible(true);
        @SuppressWarnings("unchecked")
        java.util.Set<io.github.spannm.jackcess.DataType> set =
            (java.util.Set<io.github.spannm.jackcess.DataType>) field.get(null);
        set.remove(io.github.spannm.jackcess.DataType.EXT_DATE_TIME);
    }
}
