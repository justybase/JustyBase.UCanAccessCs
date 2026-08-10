import io.github.spannm.jackcess.DataType;
import io.github.spannm.jackcess.Database;
import io.github.spannm.jackcess.DatabaseBuilder;
import io.github.spannm.jackcess.Table;
import io.github.spannm.jackcess.TableBuilder;
import io.github.spannm.jackcess.Row;

import java.io.File;
import java.time.LocalDateTime;
import java.util.Locale;

/**
 * Measures the low-level Jackcess insert and read paths used as the Java
 * comparison for the UCanAccess-csharp file layer.
 *
 * Usage: java -cp "jackcess.jar<path-separator>." InsertBench [rows] [file]
 */
public class InsertBench {

    private static final LocalDateTime BASE_DATE = LocalDateTime.of(2000, 1, 1, 0, 0, 0);

    public static void main(String[] args) throws Exception {
        int rows = args.length > 0 ? Integer.parseInt(args[0]) : 100_000;
        if (rows < 1) {
            throw new IllegalArgumentException("rows must be positive");
        }

        File file;
        boolean deleteFile;
        if (args.length > 1) {
            file = new File(args[1]);
            deleteFile = false;
        } else {
            file = File.createTempFile("ucanaccess-java-bench-", ".mdb");
            deleteFile = true;
        }

        long checksum;
        double insertMs;
        try (Database db = createDatabase(file)) {
            Table table = createTable(db);
            long insertStart = System.nanoTime();
            for (int i = 0; i < rows; i++) {
                table.addRow(null, "row" + i, i * 0.5, i % 2 == 0,
                    BASE_DATE.plusDays(i % 3650));
            }
            insertMs = elapsedMillis(insertStart);
        }

        double readMs;
        long readRows = 0;
        checksum = 0;
        try (Database db = new DatabaseBuilder(file).withReadOnly(true).open()) {
            Table table = db.getTable("t_perf");
            long readStart = System.nanoTime();
            for (Row row : table) {
                checksum += ((Number) row.get("id")).longValue();
                checksum += ((Number) row.get("amount")).longValue();
                if (Boolean.TRUE.equals(row.get("active"))) {
                    checksum++;
                }
                readRows++;
            }
            readMs = elapsedMillis(readStart);
        }

        System.out.println("JAVA_ROWS=" + rows);
        System.out.printf(Locale.ROOT, "JAVA_INSERT_MS=%.3f%n", insertMs);
        System.out.println("JAVA_READ_ROWS=" + readRows);
        System.out.printf(Locale.ROOT, "JAVA_READ_MS=%.3f%n", readMs);
        System.out.println("JAVA_CHECKSUM=" + checksum);

        if (deleteFile && !file.delete()) {
            file.deleteOnExit();
        }
    }

    private static Database createDatabase(File file) throws Exception {
        return DatabaseBuilder.create(Database.FileFormat.V2003, file);
    }

    private static Table createTable(Database db) throws Exception {
        return new TableBuilder("t_perf")
            .addColumn(new io.github.spannm.jackcess.ColumnBuilder("id", DataType.LONG)
                .withAutoNumber(true))
            .addColumn(new io.github.spannm.jackcess.ColumnBuilder("name", DataType.TEXT)
                .withLength(60))
            .addColumn(new io.github.spannm.jackcess.ColumnBuilder("amount", DataType.DOUBLE))
            .addColumn(new io.github.spannm.jackcess.ColumnBuilder("active", DataType.BOOLEAN))
            .addColumn(new io.github.spannm.jackcess.ColumnBuilder("created", DataType.SHORT_DATE_TIME))
            .toTable(db);
    }

    private static double elapsedMillis(long start) {
        return (System.nanoTime() - start) / 1_000_000.0;
    }
}
