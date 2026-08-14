import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.ResultSet;
import java.sql.ResultSetMetaData;
import java.sql.Statement;

/**
 * Executes a script against the ORIGINAL UCanAccess and prints every result set as
 * CSV rows, with 'ERR <message>' lines for statements that fail (the run continues).
 * Used to probe the exact semantics of DISABLE/ENABLE AUTOINCREMENT ON <table>.
 *
 * Usage: java -cp "ucanaccess.jar<path-separator>jackcess.jar<path-separator>hsqldb.jar<path-separator>." AutoIncrementProbe <dbPath> <scriptFile>
 */
public class AutoIncrementProbe {

    public static void main(String[] args) throws Exception {
        if (args.length < 2) {
            System.err.println("usage: AutoIncrementProbe <dbPath> <scriptFile>");
            System.exit(2);
        }

        File dbFile = new File(args[0]);
        java.util.List<String> statements = SqlDump.splitStatements(
            Files.readString(new File(args[1]).toPath(), StandardCharsets.UTF_8));

        String url = "jdbc:ucanaccess://" + dbFile.getAbsolutePath().replace('\\', '/')
            + ";memory=true";
        try (Connection conn = DriverManager.getConnection(url)) {
            try (Statement st = conn.createStatement()) {
                for (String sql : statements) {
                    String trimmed = SqlDump.stripLeadingComments(sql).trim();
                    if (trimmed.isEmpty()) {
                        continue;
                    }
                    System.out.println("SQL> " + trimmed);
                    try {
                        boolean hasResult = st.execute(trimmed);
                        if (!hasResult) {
                            System.out.println("OK (update count " + st.getUpdateCount() + ")");
                            continue;
                        }
                        try (ResultSet rs = st.getResultSet()) {
                            ResultSetMetaData md = rs.getMetaData();
                            int cols = md.getColumnCount();
                            StringBuilder header = new StringBuilder();
                            for (int i = 1; i <= cols; i++) {
                                if (i > 1) header.append('|');
                                header.append(md.getColumnName(i));
                            }
                            System.out.println(header);
                            int rows = 0;
                            while (rs.next()) {
                                StringBuilder line = new StringBuilder();
                                for (int i = 1; i <= cols; i++) {
                                    if (i > 1) line.append('|');
                                    line.append(rs.getString(i));
                                }
                                System.out.println(line);
                                rows++;
                            }
                            System.out.println("(" + rows + " rows)");
                        }
                    } catch (Exception ex) {
                        String message = ex.getMessage();
                        System.out.println("ERR " + (message == null ? ex.toString() : message.replace('\n', ' ')));
                    }
                }
            }
        }
    }
}