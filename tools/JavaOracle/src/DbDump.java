import io.github.spannm.jackcess.Database;
import io.github.spannm.jackcess.DatabaseBuilder;
import io.github.spannm.jackcess.Column;
import io.github.spannm.jackcess.Table;
import io.github.spannm.jackcess.DataType;
import io.github.spannm.jackcess.Row;
import io.github.spannm.jackcess.Index;
import io.github.spannm.jackcess.Relationship;
import io.github.spannm.jackcess.PropertyMap;

import java.io.File;
import java.math.BigDecimal;
import java.nio.charset.StandardCharsets;
import java.time.LocalDateTime;
import java.util.Base64;
import java.util.Set;
import java.util.SortedSet;

/**
 * Dumps an MS Access database to a canonical JSON document. Used as a
 * differential-test oracle for the .NET port (UCanAccess-csharp).
 *
 * Usage: java -cp "jackcess.jar<path-separator>." DbDump <dbPath> [outJson]
 */
public class DbDump {

    public static void main(String[] args) throws Exception {
        if (args.length < 1) {
            System.err.println("usage: DbDump <dbPath> [outJson]");
            System.exit(2);
        }
        File dbFile = new File(args[0]);
        StringBuilder sb = new StringBuilder(1 << 20);
        sb.append("{\n  \"file\": ").append(jstr(dbFile.getName())).append(",\n  \"tables\": [\n");

        Database db = new DatabaseBuilder(dbFile).withReadOnly(true).open();
        try {
            Set<String> tableNames = db.getTableNames();
            SortedSet<String> sorted = new java.util.TreeSet<>(String.CASE_INSENSITIVE_ORDER);
            sorted.addAll(tableNames);

            boolean firstTable = true;
            for (String name : sorted) {
                if (!firstTable) {
                    sb.append(",\n");
                }
                firstTable = false;
                try {
                    Table table = db.getTable(name);
                    dumpTable(sb, table);
                } catch (Exception ex) {
                    // linked tables with unresolvable paths, etc.
                    sb.append("    {\"name\": ").append(jstr(name))
                      .append(", \"error\": ").append(jstr(String.valueOf(ex))).append("}");
                }
            }
            sb.append("\n  ],\n  \"relationships\": [");
            java.util.List<Relationship> relationships = db.getRelationships();
            for (int i = 0; i < relationships.size(); i++) {
                if (i > 0) {
                    sb.append(",");
                }
                Relationship relationship = relationships.get(i);
                sb.append("\n    {\"name\": ").append(jstr(relationship.getName()))
                  .append(", \"fromTable\": ").append(jstr(relationship.getFromTable().getName()))
                  .append(", \"toTable\": ").append(jstr(relationship.getToTable().getName()))
                  .append(", \"oneToOne\": ").append(relationship.isOneToOne())
                  .append(", \"referentialIntegrity\": ").append(relationship.hasReferentialIntegrity())
                  .append(", \"cascadeUpdates\": ").append(relationship.cascadeUpdates())
                  .append(", \"cascadeDeletes\": ").append(relationship.cascadeDeletes())
                  .append(", \"cascadeNullOnDelete\": ").append(relationship.cascadeNullOnDelete())
                  .append(", \"fromColumns\": [");
                for (int j = 0; j < relationship.getFromColumns().size(); j++) {
                    if (j > 0) sb.append(", ");
                    sb.append(jstr(relationship.getFromColumns().get(j).getName()));
                }
                sb.append("], \"toColumns\": [");
                for (int j = 0; j < relationship.getToColumns().size(); j++) {
                    if (j > 0) sb.append(", ");
                    sb.append(jstr(relationship.getToColumns().get(j).getName()));
                }
                sb.append("]}");
            }
        } finally {
            db.close();
        }

        sb.append("\n  ]\n}\n");

        if (args.length > 1) {
            java.nio.file.Files.writeString(new File(args[1]).toPath(), sb.toString(), StandardCharsets.UTF_8);
        } else {
            System.out.print(sb.toString());
        }
    }

    private static void dumpTable(StringBuilder sb, Table table) throws Exception {
        var cols = table.getColumns();
        var indexes = table.getIndexes();
        sb.append("    {\n      \"name\": ").append(jstr(table.getName()))
          .append(",\n      \"structure\": {\"rowCount\": ").append(table.getRowCount())
          .append(", \"columnCount\": ").append(cols.size())
          .append(", \"indexCount\": ").append(indexes.size()).append("},")
          .append("\n      \"columns\": [");
        for (int i = 0; i < cols.size(); i++) {
            Column c = cols.get(i);
            if (i > 0) {
                sb.append(",");
            }
            sb.append("\n        {\"name\": ").append(jstr(c.getName()))
              .append(", \"type\": \"").append(c.getType().name()).append('"')
              .append(", \"length\": ").append(c.getLength())
              .append(", \"autoNumber\": ").append(c.isAutoNumber())
              .append(", \"calculated\": ").append(c.isCalculated())
              .append(", \"precision\": ").append(c.getPrecision())
              .append(", \"scale\": ").append(c.getScale())
              .append(", \"required\": ").append(Boolean.TRUE.equals(
                  c.getProperties().getValue(PropertyMap.REQUIRED_PROP, Boolean.FALSE)))
              .append('}');
        }
        sb.append("\n      ],\n      \"indexes\": [");
        for (int i = 0; i < indexes.size(); i++) {
            Index index = indexes.get(i);
            if (i > 0) {
                sb.append(",");
            }
            sb.append("\n        {\"name\": ").append(jstr(index.getName()))
              .append(", \"primaryKey\": ").append(index.isPrimaryKey())
              .append(", \"foreignKey\": ").append(index.isForeignKey())
              .append(", \"unique\": ").append(index.isUnique())
              .append(", \"required\": ").append(index.isRequired())
              .append(", \"ignoreNulls\": ").append(index.shouldIgnoreNulls())
              .append(", \"columns\": [");
            var indexColumns = index.getColumns();
            for (int j = 0; j < indexColumns.size(); j++) {
                if (j > 0) {
                    sb.append(", ");
                }
                Index.Column indexColumn = indexColumns.get(j);
                sb.append("{\"name\": ").append(jstr(indexColumn.getName()))
                  .append(", \"ascending\": ").append(indexColumn.isAscending()).append("}");
            }
            sb.append("]}");
        }
        sb.append("\n      ],\n      \"rows\": [");
        boolean firstRow = true;
        try {
            for (Row row : table) {
                if (!firstRow) {
                    sb.append(",");
                }
                firstRow = false;
                sb.append("\n        [");
                for (int i = 0; i < cols.size(); i++) {
                    if (i > 0) {
                        sb.append(",");
                    }
                    appendValue(sb, row.get(cols.get(i).getName()));
                }
                sb.append("]");
            }
        } catch (Exception ex) {
            // linked tables, broken data, etc. -- record the failure and move on
            if (!firstRow) {
                sb.append(",\n");
            }
            sb.append("\n        {\"error\": ").append(jstr(String.valueOf(ex))).append("}");
        }
        sb.append("\n      ]\n    }");
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
        } else if (value instanceof LocalDateTime) {
            sb.append("{\"dt\": \"").append(value.toString()).append("\"}");
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
