import java.io.File;
import java.math.BigDecimal;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.ResultSet;
import java.sql.ResultSetMetaData;
import java.sql.SQLException;
import java.sql.Statement;
import java.sql.Timestamp;
import java.text.SimpleDateFormat;
import java.util.Base64;
import java.util.TimeZone;

/**
 * Executes SQL statements against an MS Access database through the ORIGINAL
 * UCanAccess JDBC driver and dumps the result sets to canonical JSON.
 * Used as the behavioral oracle for the .NET port (UCanAccess-csharp).
 *
 * Usage: java -cp "ucanaccess.jar<path-separator>jackcess.jar<path-separator>hsqldb.jar<path-separator>." SqlDump <dbPath> <statementsFile> [outJson]
 *
 * The statements file accepts semicolon-delimited SQL, including multiline
 * statements and semicolons inside strings/comments.  The historical format
 * with one SQL statement per line remains supported.
 */
public class SqlDump {

    private static final SimpleDateFormat DT_FMT = new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS");

    static {
        DT_FMT.setTimeZone(TimeZone.getTimeZone("UTC"));
    }

    public static void main(String[] args) throws Exception {
        if (args.length < 2) {
            System.err.println("usage: SqlDump <dbPath> <statementsFile> [outJson]");
            System.exit(2);
        }

        File dbFile = new File(args[0]);
        String script = Files.readString(new File(args[1]).toPath(), StandardCharsets.UTF_8);
        java.util.List<String> statements = splitStatements(script);

        StringBuilder sb = new StringBuilder(1 << 20);
        sb.append("[\n");

        String url = "jdbc:ucanaccess://" + dbFile.getAbsolutePath().replace('\\', '/')
            + ";memory=true";
        try (Connection conn = DriverManager.getConnection(url)) {
            boolean first = true;
            for (String sql : statements) {
                String trimmed = stripLeadingComments(sql).trim();
                if (trimmed.isEmpty()) {
                    continue;
                }
                if (!first) {
                    sb.append(",\n");
                }
                first = false;
                dumpStatement(sb, conn, trimmed);
            }
        } finally {
            // ensure the HSQLDB mirror is shut down cleanly
            try {
                DriverManager.getConnection("jdbc:hsqldb:mem:ucanaccess_" + System.identityHashCode(null) + ";shutdown=true");
            } catch (Exception ignored) {
                // not critical
            }
        }

        sb.append("\n]\n");

        if (args.length > 2) {
            Files.writeString(new File(args[2]).toPath(), sb.toString(), StandardCharsets.UTF_8);
        } else {
            System.out.print(sb.toString());
        }
    }

    private static void dumpStatement(StringBuilder sb, Connection conn, String sql) {
        sb.append("  {\"sql\": ").append(jstr(sql)).append(", ");
        try (Statement st = conn.createStatement()) {
            boolean hasResultSet = st.execute(sql);
            if (!hasResultSet) {
                sb.append("\"resultSet\": false, \"columnCount\": 0, ")
                    .append("\"affectedRows\": ").append(st.getUpdateCount())
                    .append(", \"rows\": []}");
                return;
            }
            try (ResultSet rs = st.getResultSet()) {
                ResultSetMetaData md = rs.getMetaData();
                int colCount = md.getColumnCount();

                sb.append("\"resultSet\": true, \"columnCount\": ").append(colCount)
                    .append(", \"columns\": [");
                for (int i = 1; i <= colCount; i++) {
                    if (i > 1) {
                        sb.append(", ");
                    }
                    sb.append(jstr(md.getColumnLabel(i)));
                }
                sb.append("], \"columnTypes\": [");
                for (int i = 1; i <= colCount; i++) {
                    if (i > 1) {
                        sb.append(", ");
                    }
                    sb.append("{\"name\": ").append(jstr(md.getColumnLabel(i)))
                        .append(", \"jdbcType\": ").append(md.getColumnType(i))
                        .append(", \"typeName\": ").append(jstr(md.getColumnTypeName(i)))
                        .append(", \"className\": ").append(jstr(md.getColumnClassName(i)))
                        .append("}");
                }
                sb.append("], \"rows\": [");

                boolean firstRow = true;
                while (rs.next()) {
                    if (!firstRow) {
                        sb.append(",");
                    }
                    firstRow = false;
                    sb.append("\n    [");
                    for (int i = 1; i <= colCount; i++) {
                        if (i > 1) {
                            sb.append(",");
                        }
                        appendValue(sb, rs.getObject(i));
                    }
                    sb.append("]");
                }
                sb.append("\n  ]}");
            }
        } catch (Exception ex) {
            sb.append("\"errorCategory\": ").append(jstr(errorCategory(ex)))
                .append(", \"error\": ").append(jstr(String.valueOf(ex))).append("}");
        }
    }

    private static String errorCategory(Throwable ex) {
        Throwable current = ex;
        while (current.getCause() != null && !(current instanceof SQLException)) {
            current = current.getCause();
        }
        if (current instanceof SQLException sqlEx) {
            String state = sqlEx.getSQLState();
            if (state != null && state.length() >= 2) {
                return switch (state.substring(0, 2)) {
                    case "08" -> "connection";
                    case "22" -> "data";
                    case "23" -> "constraint";
                    case "42" -> "syntax";
                    default -> "sql";
                };
            }
            return "sql";
        }
        return ex instanceof UnsupportedOperationException ? "unsupported" : "execution";
    }

    /**
     * Splits semicolon-delimited scripts without treating semicolons in strings,
     * bracketed identifiers or comments as terminators.  The historical oracle
     * format (one statement per line with no semicolons) remains supported.
     */
    static java.util.List<String> splitStatements(String script) {
        java.util.List<String> semicolonParts = new java.util.ArrayList<>();
        StringBuilder current = new StringBuilder();
        Character quote = null;
        boolean bracketed = false;
        boolean lineComment = false;
        boolean blockComment = false;
        boolean sawSemicolon = false;

        for (int i = 0; i < script.length(); i++) {
            char c = script.charAt(i);
            if (lineComment) {
                current.append(c);
                if (c == '\n') {
                    lineComment = false;
                }
                continue;
            }
            if (blockComment) {
                current.append(c);
                if (c == '*' && i + 1 < script.length() && script.charAt(i + 1) == '/') {
                    current.append(script.charAt(++i));
                    blockComment = false;
                }
                continue;
            }
            if (quote != null) {
                current.append(c);
                if (c == quote) {
                    if (i + 1 < script.length() && script.charAt(i + 1) == quote) {
                        current.append(script.charAt(++i));
                    } else {
                        quote = null;
                    }
                }
                continue;
            }
            if (bracketed) {
                current.append(c);
                if (c == ']') {
                    if (i + 1 < script.length() && script.charAt(i + 1) == ']') {
                        current.append(script.charAt(++i));
                    } else {
                        bracketed = false;
                    }
                }
                continue;
            }
            if (c == '\'' || c == '"') {
                quote = c;
                current.append(c);
            } else if (c == '[') {
                bracketed = true;
                current.append(c);
            } else if (c == '-' && i + 1 < script.length() && script.charAt(i + 1) == '-') {
                lineComment = true;
                current.append(c).append(script.charAt(++i));
            } else if (c == '/' && i + 1 < script.length() && script.charAt(i + 1) == '*') {
                blockComment = true;
                current.append(c).append(script.charAt(++i));
            } else if (c == ';') {
                sawSemicolon = true;
                semicolonParts.add(current.toString());
                current.setLength(0);
            } else {
                current.append(c);
            }
        }
        if (current.length() > 0) {
            semicolonParts.add(current.toString());
        }
        if (sawSemicolon) {
            return semicolonParts;
        }

        java.util.List<String> lines = new java.util.ArrayList<>();
        boolean oneStatementPerLine = true;
        for (String line : script.split("\\r?\\n", -1)) {
            String trimmed = stripLeadingComments(line).trim();
            if (trimmed.isEmpty()) {
                continue;
            }
            if (!startsStatement(trimmed)) {
                oneStatementPerLine = false;
            }
            lines.add(line);
        }
        // Preserve the historical one-statement-per-line input only when every
        // non-comment line starts a statement.  Otherwise the script is one
        // multiline statement, even when its final semicolon is omitted.
        return oneStatementPerLine ? lines : java.util.List.of(script);
    }

    private static boolean startsStatement(String sql) {
        int end = 0;
        while (end < sql.length() && Character.isLetter(sql.charAt(end))) {
            end++;
        }
        if (end == 0) {
            return false;
        }
        String word = sql.substring(0, end).toUpperCase(java.util.Locale.ROOT);
        return switch (word) {
            case "SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER",
                "PARAMETERS", "TRANSFORM", "WITH", "SET", "DISABLE", "ENABLE" -> true;
            default -> false;
        };
    }

    static String stripLeadingComments(String sql) {
        String value = sql;
        while (true) {
            value = value.trim();
            if (value.startsWith("--")) {
                int newline = value.indexOf('\n');
                if (newline < 0) {
                    return "";
                }
                value = value.substring(newline + 1);
            } else if (value.startsWith("/*")) {
                int end = value.indexOf("*/", 2);
                if (end < 0) {
                    return "";
                }
                value = value.substring(end + 2);
            } else {
                return value;
            }
        }
    }

    private static void appendValue(StringBuilder sb, Object value) {
        if (value == null) {
            sb.append("null");
        } else if (value instanceof Boolean) {
            sb.append(value);
        } else if (value instanceof Byte || value instanceof Short || value instanceof Integer || value instanceof Long) {
            sb.append(((Number) value).longValue());
        } else if (value instanceof Float) {
            sb.append("{\"f\": \"0x").append(Integer.toHexString(Float.floatToIntBits((Float) value))).append("\"}");
        } else if (value instanceof Double) {
            sb.append("{\"d\": \"0x").append(Long.toHexString(Double.doubleToLongBits((Double) value))).append("\"}");
        } else if (value instanceof BigDecimal) {
            BigDecimal dec = (BigDecimal) value;
            sb.append("{\"dec\": [\"").append(dec.unscaledValue()).append("\", ").append(dec.scale()).append("]}");
        } else if (value instanceof Timestamp) {
            sb.append("{\"dt\": \"").append(DT_FMT.format((Timestamp) value)).append("\"}");
        } else if (value instanceof java.util.Date) {
            sb.append("{\"dt\": \"").append(DT_FMT.format((java.util.Date) value)).append("\"}");
        } else if (value instanceof java.time.LocalDateTime) {
            sb.append("{\"dt\": \"").append(DT_FMT.format(
                Timestamp.valueOf((java.time.LocalDateTime) value))).append("\"}");
        } else if (value instanceof byte[]) {
            sb.append("{\"b64\": \"").append(Base64.getEncoder().encodeToString((byte[]) value)).append("\"}");
        } else if (value instanceof String) {
            sb.append(jstr((String) value));
        } else {
            sb.append(jstr(String.valueOf(value)));
        }
    }

    private static String jstr(String s) {
        if (s == null) {
            return "null";
        }
        StringBuilder sb = new StringBuilder(s.length() + 2);
        sb.append('"');
        for (int i = 0; i < s.length(); i++) {
            char ch = s.charAt(i);
            switch (ch) {
                case '"': sb.append("\\\""); break;
                case '\\': sb.append("\\\\"); break;
                case '\b': sb.append("\\b"); break;
                case '\f': sb.append("\\f"); break;
                case '\n': sb.append("\\n"); break;
                case '\r': sb.append("\\r"); break;
                case '\t': sb.append("\\t"); break;
                default:
                    if (ch < 0x20) {
                        sb.append(String.format("\\u%04x", (int) ch));
                    } else {
                        sb.append(ch);
                    }
            }
        }
        sb.append('"');
        return sb.toString();
    }
}
