import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.Statement;

/**
 * Executes a script of DDL/DML statements against an MS Access database through the
 * ORIGINAL UCanAccess (used by the DDL parity tests: the port applies the same script
 * and the resulting files are compared).
 *
 * Usage: java -cp "ucanaccess.jar<path-separator>jackcess.jar<path-separator>hsqldb.jar<path-separator>." DdlRunner <dbPath> <statementsFile>
 */
public class DdlRunner {

    public static void main(String[] args) throws Exception {
        if (args.length < 2) {
            System.err.println("usage: DdlRunner <dbPath> <statementsFile>");
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
                    st.execute(trimmed);
                }
            }
        } catch (Exception ex) {
            System.err.println("DdlRunner failed: " + ex);
            System.exit(1);
        }
    }
}
