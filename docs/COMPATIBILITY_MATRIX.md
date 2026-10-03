# UCanAccess-csharp compatibility matrix

This document is the executable compatibility contract for the .NET provider.
The reference is the Java UCanAccess/JJackcess stack, but the target API is
ADO.NET. JDBC-only concepts are marked `N/A` instead of being copied under a
misleading name.

## Status definitions

| Status | Meaning | Required evidence |
|---|---|---|
| Supported | The behavior is implemented and intended for production use. | A focused test plus an integration or differential test where applicable. |
| Partial | Only a documented subset works. | Positive tests and explicit negative tests for the boundary. |
| Unsupported | The provider rejects the operation deterministically. | A test for the exception category/message contract. |
| N/A | The Java/JDBC concept has no direct ADO.NET equivalent. | A documented .NET alternative, when one exists. |
| Planned | Accepted gap with an implementation milestone. | A fixture or test design before implementation begins. |

## Functional surface

| Area | Status | Current evidence | Next action |
|---|---|---|---|
| Jet 3/4 `.mdb` read | Supported | File differential tests and fixtures | Add more real-world Jet 3 files |
| Jet 3 write | Supported | `Jet3WriteTests` and Java read-back | Add corruption/reopen matrix |
| Access 2007/2010/2016 | Supported | ACCDB fixtures and differential tests | Add 2013/large/OLE fixtures |
| Existing `EXT_DATE_TIME` columns | Partial | `extDateTime.accdb` differential test, provider read/write tests, and Java Jackcess read-back | Add `EXT_DATE_TIME` table-creation support |
| SELECT, joins, grouping, CTE, set operators | Supported | SQL oracle corpus (`sqljoin.sql`, 100 statements incl. 12 `?`-parameterized via `sqljoin.params.json` bound on both Java `PreparedStatement` and ADO.NET sides) | Add date/decimal parameter cases |
| `TOP n PERCENT` | Unsupported | Java UCanAccess 5.1.7 rejects it; explicit translator test preserves that boundary | Revisit only with a newer pinned upstream baseline |
| Window functions | Supported | `WindowFunctionTests` incl. ranking/value assertions, frame-edge NULLs, DESC NULL-first and non-SELECT rejection | Track expression-column `GetFieldType` fallback vs Java types |
| `TRANSFORM/PIVOT` | Partial | `CrosstabTests` (explicit `IN (...)`, inline dynamic, parameterized inline dynamic per command), translator rejection tests (unsupported aggregate, missing PIVOT, empty IN, trailing tokens), and `pivot.sql` | Saved dynamic crosstabs with parameters stay limited (no view materialization); action QueryDefs out of scope |
| INSERT/UPDATE/DELETE | Supported | `SqlWriteTests` including NULL, correlated subquery and JOIN cases; `DELETE * FROM` wildcard form with `SqlWriteTests.Delete_star_from_*`; file-state parity with Java 5.1.7 incl. `DdlParityTests.Update_delete_sequence_produces_same_file_state_as_java` | Add multi-row UPDATE...JOIN oracle cases |
| `CREATE/DROP TABLE` | Supported | `SqlDdlTests` and DDL parity, incl. `Create_table_accepts_access_type_aliases` (`VARCHAR2/NCHAR/SMALLDATETIME/IMAGE/VARBINARY/GENERAL`) and unsupported-type rejection | Add remaining exotic type aliases only with fixtures |
| `CREATE TABLE ... AS SELECT` | Supported | `SqlDdlTests.Create_table_as_select_*` | Add parameterized/complex-type cases |
| `SELECT ... INTO` | Supported (port extension) | `SqlDdlTests.Select_into_*` incl. transaction commit/rollback and reader rejection | Documented as extension: Java UCanAccess 5.1.7 rejects the Access `INTO` grammar |
| `DISABLE/ENABLE AUTOINCREMENT ON t` | Supported | `SqlDdlTests.Disable_autoincrement_*` and `DdlParityTests.Disable_enable_autoincrement_produces_same_file_state_as_java` (identical file state as Java 5.1.7) | A NULL AutoNumber insert while disabled raises a clean `DatabaseException` instead of the Java NPE; the flag covers numeric AutoNumber columns only |
| `ALTER TABLE` | Partial | AutoNumber-preserving add/drop tests, default backfill, rename, add-primary-key and foreign-key cascade tests; `SqlDdlTests.Alter_{drop_column_on_relationship,add_column_on_calculated,drop_column_on_calculated,add_unique_constraint}` lock the rejection boundaries | Add complex/`EXT_DATE_TIME` table mutation cases and broader oracle grammar coverage |
| `CREATE/DROP INDEX` | Supported | Index mutation tests plus `SqlDdlTests.Create_index_on_shared_relationship_index_is_rejected` (deterministic shared-index boundary) | Broaden retained B-tree preservation cases |
| `CREATE/DROP VIEW` | Partial | Managed SELECT QueryDef writer, catalog round-trip, INNER/LEFT JOIN and parameter expansion tests (`QueryTests`); FULL JOIN, comma/JOIN mix, missing ON, unterminated PARAMETERS, unsupported param types and TOP PERCENT are deterministically rejected | Crosstab QueryDef persistence; action QueryDefs remain out of scope |
| Saved SELECT queries | Supported (subset) | Read-only mirror views plus persisted managed QueryDefs; parameterized definitions are expanded per command | Crosstab QueryDef grammar |
| Linked tables | Supported | Linked read/write/remap tests; non-atomic direct fallback is explicit | Add concurrency tests |
| Access functions | Partial | Scalar/aggregate/domain registrations, EVAL/financial/statistical extensions, complex `Equals/Contains` filters, `docs/FUNCTION_CATALOG.md` catalog (incl. `MT` docs-typo decision), and `FunctionsTests.Java_financial_function_aliases` absolute-value checks | `IRR` is a future extension (absent from Java too), not parity |
| User-defined scalar functions | Supported | `UCanAccessConnection.RegisterFunction` and function tests | Add an aggregate registration API if needed |
| Complex types/attachments | Partial | `ComplexTypeTests` (incl. attachment metadata round-trip), `ComplexTypeProviderTests` incl. `Equals/EqualsIgnoreOrder/Contains` filters and attachment metadata, real COM-generated ACCDB fixture; typed arrays and child-table writes | Add version-history fixtures (needs COM runner); keep complex-field DDL out of scope |
| Password/encrypted files | Partial | Optional `JustyBase.UCanAccess.AccessCrypto`; `AccessComRoundTripTests` (opt-in) | Expand the profile matrix; legacy `.mdb` encryption is intentionally unsupported |

## ADO.NET surface

| Area | Status | Next action |
|---|---|---|
| `DbConnection`, `DbCommand`, `DbDataReader` | Supported | `GetStream`/`GetTextReader` overrides covered by `AdoNetTests.Reader_get_stream_and_get_text_reader_read_columns`; add API contract tests for every override |
| Input parameters | Supported | Add parameterized Java oracle scripts |
| Output/return parameters | Unsupported | Input-only by design; `AdoNetTests.Output_parameters_*` asserts `NotSupportedException` |
| Transactions | Supported | Atomic staging and transaction tests | Add linked-table cases |
| Savepoints | Supported | `DbTransaction.Save`/rollback-to-savepoint snapshot tests | Add savepoint stress/cleanup cases |
| `GetSchema` core collections | Partial | Tables/Columns/Indexes/IndexColumns/PrimaryKeys/ForeignKeys/Views with restriction tests in `MetadataTests`, incl. `GetSchema_exposes_ddl_created_foreign_key_round_trip`; column defaults exposed | Add version-history attachment metadata columns |
| Connection pooling | N/A/undocumented | Provider-owned SQLite file mirrors disable SQLite pooling to release files deterministically |
| Updatable JDBC `ResultSet` | N/A | Use ADO.NET DML commands as the supported alternative |

## Connection-string surface

| Option | Status | Notes |
|---|---|---|
| Data Source, Read Only | Supported | Required path and safe defaults |
| Password/PWD | Partial | Routed to `IAccessDatabaseOpener`; direct core opening fails deterministically |
| Encoding/Code Page | Supported | Especially relevant to Jet 3 |
| Show Schema, Column Order | Supported | Tested in metadata/query paths |
| Lazy Load, Keep Mirror | Supported | Boolean mirror lifetime plus upstream `keepMirror=<path>` persistent-cache form |
| Memory, Immediately Release Resources, Prevent Reloading, Sys Schema | Supported | Upstream aliases mapped to the provider's mirror, reload and schema semantics |
| Mirror Mode, Mirror Path, Mirror Folder | Supported | `memory` is default; `file` uses a provider-owned SQLite cache |
| Allow External Links | Supported | Disabled by default for path safety |
| New Database Version | Supported | 2000/2002/2003/2007/2010/2016 |
| Time Zone/Prefer Date Timestamp | Partial | Accepted for compatibility; Access values remain `DateTime` |
| Remap | Supported | Upstream `orig|new&...` trusted remap; bypasses the external-links guard, tested in `LinkedTablesTests`/`LinkedWriteTests` |
| Skip Indexes | Supported | Accepted for compatibility; mirror carries no secondary indexes, no file-data effect (`ConnectionOptionsTests`) |
| Open Exclusive | Supported | Locks even read-only opens; `Lock Mdb` alias; writable opens always lock (`ConnectionOptionsTests`) |
| Ignore Case | Supported | Case-insensitive text/`LIKE` by default (HSQLDB parity); `false` uses binary collation (`TextSemanticsTests`) |
| Concat Nulls | Supported | `&`/`||` map NULL to '' by default; `true` restores NULL propagation (`TextSemanticsTests`) |
| Mirror Path/Disk Mirror | Supported | Superseded by `Mirror Mode=file`, `Mirror Path` and `Mirror Folder` |
| Java/HSQLDB-only options | N/A | Do not expose false-compatible knobs |

## Evidence rule

Every new `Supported` entry must add a test and update this matrix in the same
change. Every change to a `Partial` or `Unsupported` entry must include a
fixture, an explicit negative test, or a documented reason why the behavior is
not applicable to ADO.NET.
