# UCanAccess-csharp feature matrix

This matrix describes the behavior implemented and verified in this repository.
It is intentionally conservative: a feature is marked partial when it is only
readable or when a write path has a documented restriction.

Legend: ✅ supported, 🟡 partial/limited, ❌ unsupported.

## File formats

| Feature | Status | Notes |
|---|---:|---|
| Jet 3 / Access 97 `.mdb` read | ✅ | Includes configurable code-page decoding. |
| Jet 3 `.mdb` write | ✅ | Port extension; the Java original is read-only for Jet 3. |
| Jet 4 / Access 2000–2003 `.mdb` read/write | ✅ | Rows, long values, indexes, links, and supported DDL. |
| Access 2007 `.accdb` read/write | ✅ | Verified with the bundled fixture and Java reader. |
| Access 2010/2016 `.accdb` read/write | ✅ | Verified with bundled fixtures. |
| Existing `EXT_DATE_TIME` columns | 🟡 | Existing columns can be read and written with 100-nanosecond CLR `DateTime` precision; creating a new `EXT_DATE_TIME` column remains unsupported. |
| Calculated-column values | 🟡 | Values can be read; table recreation/DDL on calculated tables is rejected. |
| Complex types, attachments, multi-value fields | 🟡 | Existing Access fields are exposed as typed CLR arrays and can be written through their flat child tables; creating a new complex field remains unsupported. |
| Password-encrypted databases | 🟡 | Optional `JustyBase.UCanAccess.AccessCrypto` opens and writes modern Agile-encrypted `.accdb` files; the core package remains codec-free. Legacy `.mdb` encryption and unsupported Access profiles remain rejected. |

## SQL

| Feature | Status | Notes |
|---|---:|---|
| SELECT, filtering, grouping, HAVING, ordering | ✅ | Executed through the SQLite mirror with Access rewrites. Qualified `table.*` projections (plain, alias-qualified in joins, bracketed) are covered by the SQL parity corpus. |
| INNER/LEFT/RIGHT/FULL JOIN | ✅ | Covered by the SQL parity corpus where supported by SQLite translation. |
| Subqueries, UNION/EXCEPT/INTERSECT, CTE | ✅ | SELECT and correlated DML subqueries are evaluated by the SQLite mirror. |
| DISTINCT, DISTINCTROW, TOP n | ✅ | `TOP n PERCENT` is rejected to match UCanAccess 5.1.6. |
| Window functions | ✅ | `ROW_NUMBER`, `RANK`, `DENSE_RANK`, `LAG`, `LEAD`, partitions, ordering, and SQLite window frames are preserved by the translator. |
| TRANSFORM/PIVOT crosstab queries | 🟡 | Explicit `IN (...)` and inline dynamic pivots are supported; saved dynamic pivots with parameters remain limited. |
| Linked tables in queries | ✅ | Targets are resolved and mirrored under the link name. |
| Access/VBA functions | 🟡 | Broad function set including Java-compatible `Sign`, `CLong`, `CSign`, and `StrComp` comparison modes; not every Access/VBA function exists. |
| Access LIKE (`*`, `?`, `#`, `[... ]`) | ✅ | Includes case-insensitive matching. |
| Case-insensitive text comparisons | ✅ | Text columns use the mirror's Access-compatible collation. |
| Access NULL ordering | ✅ | DESC sort keys are rewritten to place NULL first. |
| MONEY/NUMERIC SQL arithmetic precision | ✅ | Recognized columns, arithmetic, comparisons, and SUM/MIN/MAX use string-backed exact decimal functions; arbitrary SQLite expressions can still fall back to SQLite affinity. |

## Writes and transactions

| Feature | Status | Notes |
|---|---:|---|
| INSERT, multi-row INSERT, INSERT…SELECT | ✅ | Includes parameters and supported type coercion. |
| UPDATE and DELETE | ✅ | Includes translated expressions, correlated subqueries, UPDATE/DELETE JOIN, foreign-key checks, long values, and the Access `DELETE * FROM` wildcard form. |
| DML NULL semantics | ✅ | WHERE selection follows SQL three-valued logic; UNKNOWN never selects a row. |
| Generated AutoNumber / `@@IDENTITY` | ✅ | Numeric AutoNumber from the latest successful INSERT is exposed through `LastInsertedId` and `SELECT @@IDENTITY`. |
| `DISABLE/ENABLE AUTOINCREMENT` | ✅ | `DISABLE AUTOINCREMENT ON t` honors explicit values; `ENABLE` resumes at max+1 — matching Java UCanAccess 5.1.6. The flag is per-connection in-memory state. Divergences: a NULL AutoNumber insert while disabled raises a clean `DatabaseException` (Java throws an NPE and poisons the connection), and GUID AutoNumber columns always auto-generate. |
| Positional/named parameters | ✅ | `?`, `@name`, `:name`, `$name`, and declared `PARAMETERS` references. |
| CREATE TABLE | ✅ | Supported Access types, named PK/UNIQUE/FK constraints, and column-level `NOT NULL`/`DEFAULT` are written to the file property map. |
| CREATE TABLE AS SELECT | ✅ | `WITH DATA` copies rows; `WITH NO DATA` copies the inferred scalar schema. |
| SELECT ... INTO (table-creating query) | ✅ | Rewritten to an atomic table creation with copied rows. A port extension: Java UCanAccess 5.1.6 rejects the Access `INTO` grammar. |
| DROP TABLE | ✅ | Deallocates data, index, table-definition, and referenced long-value pages. |
| ALTER ADD/DROP COLUMN, RENAME, ADD PRIMARY KEY, FOREIGN KEY | 🟡 | Nullable `ADD COLUMN` can extend the table definition without rewriting rows; rebuilds preserve AutoNumber values/counters. Defaults are stored in the Access property map and backfill required columns. Named PK/UNIQUE constraints and `ADD/DROP CONSTRAINT` for foreign keys are supported with cascade enforcement. Calculated/complex table rebuilds remain limited. `DROP PRIMARY KEY` remains rejected to match UCanAccess 5.1.6. |
| CREATE/DROP INDEX | ✅ | Uses a same-directory staging copy and mutates only index/table-definition pages; row pages, row locations, data, and retained B-trees are preserved. Relationship/shared indexes remain limited. |
| CREATE/DROP VIEW | 🟡 | Saved SELECT QueryDefs can be created, listed, reopened, dropped and queried; parameterized definitions are expanded at execution. Action QueryDefs and unsupported saved-query grammar remain rejected. |
| Commit/rollback transactions | ✅ | Writes are applied to a staged file copy and installed atomically. Native linked-table transactions are rejected. Autocommit DML uses the same safety boundary. |
| Transaction savepoints | ✅ | `DbTransaction.Save` and rollback-to-savepoint use private staging snapshots. |
| DML through linked tables | ✅ | Direct DML can reach the link target; atomic transactions containing native links are not supported. |

## ADO.NET and metadata

| Feature | Status | Notes |
|---|---:|---|
| DbConnection/DbCommand/DbDataReader | ✅ | Includes scalar queries, batched DML staging, cancellation, command timeout, and disposal. |
| DbParameter/parameter collection | ✅ | Case-insensitive named lookup and input parameters. |
| DbTransaction | ✅ | One active transaction per connection; rollback on close/dispose. |
| GetSchema tables/columns/indexes/keys/views | 🟡 | Core collections and restrictions are implemented; column defaults and DDL-created foreign keys are exposed through the schema/relationship metadata. |
| Result-set type metadata | ✅ | Boolean, integer-width, decimal, date/time, GUID, and binary types are mapped back to CLR values; `GetStream`/`GetTextReader` expose BLOB and text columns. |
| File locking | ✅ | Writable opens create the Access lock file and release it on disposal. |

## Connection-string options

| Option | Status | Notes |
|---|---:|---|
| `Read Only` | ✅ | Defaults to `true`. |
| `Encoding` / `Code Page` | ✅ | Relevant mainly to Jet 3 text. |
| `Show Schema` / `Sys Schema` | ✅ | Controls exposure of system objects; `Sys Schema` is an upstream alias. |
| `Column Order=natural|display` | ✅ | Selects file or Access display order. |
| `Lazy Load` | ✅ | Controls whether the mirror is built during `Open`. |
| `Keep Mirror` | ✅ | `false` uses a per-operation mirror; a non-boolean path selects a persistent file mirror. Transactions keep their staged mirror. |
| `Memory` | ✅ | Upstream alias for `Mirror Mode=memory|file`. |
| `Immediately Release Resources` / `Single Connection` | ✅ | One-shot mode that releases the provider-owned mirror after each operation. |
| `Prevent Reloading` | ✅ | Suppresses change detection/reopen during the current connection. |
| `Mirror Mode=memory|file` | ✅ | File mode uses a provider-owned SQLite file and avoids keeping all mirror pages only in process memory. |
| `Mirror Path` / `Mirror Folder` | ✅ | Selects an explicit file mirror or its containing folder. |
| `Allow External Links` | ✅ | Disabled by default to prevent link-path escape. |
| `New Database Version` | ✅ | 2000/2002/2003 use the Jet 4 template; 2007/2010/2016 use ACCDB templates. |
| `Time Zone` / `Prefer Date Timestamp` | 🟡 | Accepted for compatibility; Access values are exposed as timezone-free `DateTime`. |

## Verification

- `dotnet build UCanAccess.slnx` and `dotnet test UCanAccess.slnx` are the
  baseline checks.
- The current SQL oracle contains five SELECT corpora: 124 statements in total;
  result values, result-set shape/types, and normalized error categories are
  compared.
- File differential tests compare the port with Jackcess dumps for MDB and
  ACCDB fixtures, including column properties, indexes, primary/foreign-key
  metadata, relationships, and generated/indexed files.
- DML, parameter binding, transactions, locking, failed operations, and
  staging-file hash preservation have dedicated provider tests. DML tests also
  cover SQL NULL logic, correlated subqueries, UPDATE/DELETE JOIN, generated
  AutoNumber values, and atomic failed statements.
- DDL and write tests verify that files written by the port remain readable by
  the Java Jackcess reader when the Java oracle is available.
- `tools/JavaOracle/run.ps1` regenerates the Java classes, fixtures, and oracle
  output without requiring a globally installed database driver. It accepts any
  JDK distribution version 11 or newer and honors `UCANACCESS_JAVA`,
  `UCANACCESS_JAVAC` and `JAVA_HOME`.
