# Changelog

## 1.1.0

- Performance (provider): memoized per-mirror SQL translation (`Mirror.TranslateQuery`,
  also used by DML/CTAS paths) with `TranslationCacheTests`; hoisted
  `last_insert_rowid()` command out of the `LoadData` row loop; cached compiled
  `LIKE` regexes; `ExactDecimal` cached powers of ten, span-based parsing
  without exceptions, `string.Create` formatting and same-scale add fast path;
  `IsExactDecimalColumn` O(1) lookup; source-generated regexes for per-command
  patterns; tokenizer/translator capacity hints and branchless aggregate matching.
- Performance (file layer): pooled page buffers in table scans, `stackalloc`
  GUID/numeric/index scratch buffers, cached decimal powers, jump-table offsets
  decoded once per row (Jet 3), complex child rows grouped once per read with
  write invalidation, pooled encrypted-write buffers. See `docs/PERFORMANCE.md`
  for before/after numbers (`ProviderBenchmarkTests`, opt-in `UCANACCESS_PERF=1`).
- Unified the build: `TreatWarningsAsErrors=true` locally and in CI,
  `EnableNETAnalyzers=true`, single `JustyBaseParserVersion` 0.8.8 consumed via
  `$(JustyBaseParserVersion)`, plus repo `.editorconfig`/`global.json` (S1).
- Pinned the Java oracle to UCanAccess 5.1.7 (Jackcess 5.1.5/HSQLDB 2.7.4
  unchanged); `run.ps1` regeneration is byte-identical for the committed
  fixtures, so the 5.1.7 baseline is adopted without oracle drift (S4).
- Prepare the provider package 1.1.0 for the published
  `JustyBase.NetezzaSqlParser` 0.8.8 Access AST contract.

- Aligned the provider with `JustyBase.NetezzaSqlParser` 0.8.8 and added
  package-level Access parser contract tests plus a parity-tested AST
  normalization bridge for a small SELECT/TOP/DISTINCTROW/crosstab subset. The
  parser remains the shared lexer/syntax dependency; SQLite translation and
  provider execution semantics remain local to UCanAccess.
- Added Java-compatible Access function coverage for `Sign`, `CLong`, `CSign`,
  and `StrComp` binary/text comparison modes.
- Added read/write support for existing Access `EXT_DATE_TIME` columns using
  the Jackcess wire format, including 100-nanosecond `DateTime` precision and
  Java Jackcess read-back verification. Creating new `EXT_DATE_TIME` columns
  remains unsupported.
- Added `MirrorReader.GetStream`/`GetTextReader` overrides (the ADO.NET
  `DbDataReader` defaults throw), so BLOB/OLE and text columns can be read
  through stream-based readers.
- Added SQL-corpus parity coverage for qualified `table.*` projections
  (`SELECT t_detail.*`, alias-qualified `d.*` in joins, and bracketed
  `[t_detail].*`) against Java UCanAccess 5.1.7.
- Added Access `DELETE * FROM <table>` statements (the Access wildcard
  projection), with file-state parity against Java UCanAccess 5.1.7.
- Added Access `DISABLE/ENABLE AUTOINCREMENT ON <table>` statements with
  Java UCanAccess 5.1.7 parity: explicit AutoNumber values are honored only
  while autoincrement is disabled, and `ENABLE` resumes at max+1. The flag is
  per-connection in-memory state, like the upstream implementation. Known
  divergences: a NULL AutoNumber insert while disabled raises a clean
  `DatabaseException` (Java throws an NPE and poisons the connection), and the
  flag applies to numeric AutoNumber columns only.
- Added Access `SELECT ... INTO` table-creating queries (atomic with the
  existing CTAS path; a port extension, the Java original rejects the grammar).
- Added Access SQL compatibility for `SELECT @@IDENTITY`, `ALTER TABLE ...
  RENAME TO`, and adding a primary-key index with `ALTER TABLE`.
- Added upstream-compatible connection-string aliases for persistent
  `keepMirror=<path>`, `memory`, `immediatelyReleaseResources`/
  `singleConnection`, `preventReloading`, and `sysSchema`.
- Added upstream-compatible `Remap=orig|new&...` for linked databases
  (trusted explicit config, bypasses the external-links guard), with file and
  provider tests plus matrix/README coverage.
- Added upstream `Skip Indexes` (accepted, mirror carries no secondary indexes)
  and `Open Exclusive`/`Lock Mdb` (locks even read-only opens) with
  `ConnectionOptionsTests` and matrix/README coverage.
- Added upstream `Ignore Case` (default true, binary when false) and
  `Concat Nulls` (default false maps NULL to '', true propagates NULL for
  `&`/`||`) with translator/mirror/`LIKE` support and `TextSemanticsTests`.
  Note: previously `||` passed NULL through (SQLite semantics); it now follows
  the same `ConcatNulls` contract as `&`, matching Java UCanAccess.
- Added upstream complex-type filters `Equals`, `EqualsIgnoreOrder` and
  `Contains` over the JSON mirror with complex-array parameter support and
  `ComplexTypeProviderTests` coverage.
- Added `docs/FUNCTION_CATALOG.md` Java-vs-.NET parity catalog, input-only
  output/return-parameter contract tests, `GetSchema` restriction/column-default
  coverage, and fixed `ForeignKeys` restriction mapping (constraint name at
  index 2, FK table at index 5).
- Added CI coverage gate (`tools/CheckCoverage.ps1`, 70% line-rate floor) and
  `UCanAccess.Console` smoke test (`--help`, `--schema --indexes`).
- Added a parameterized SQL parity corpus: 12 `?` statements in `sqljoin.sql`
  with `sqljoin.params.json` bindings executed via Java `PreparedStatement`
  (`SqlDump` 4th argument, `run.ps1` passes it when present) and ADO.NET
  `DbParameter` on the port side; fixed `?` numbering inside `&`/`||` concat
  operands found by the new corpus.
- Added `DdlParityTests.Update_delete_sequence_produces_same_file_state_as_java`
  (INSERT/UPDATE/DELETE with concat, subquery and `DELETE *` against Java 5.1.7).
- Added window-function parity coverage (ranking/value assertions, frame-edge
  NULLs, DESC NULL-first, non-SELECT rejection) and documented the
  expression-column `GetFieldType` fallback.
- Added absolute-value financial alias checks
  (`FunctionsTests.Java_financial_function_aliases`: FV/PV/NPER/IPMT/PPMT/DDB/
  NPV/MIRR/RATE) and decided the `MT` catalog entry (docs typo for `MIRR`,
  verified against Java `Functions.java`; stays unimplemented by design).
- Added attachment-metadata round-trip coverage (file and provider layers) and
  raised the CI coverage floor to 72% (local baselines: File 76.25%,
  Provider 74.56%).
- Added ALTER-boundary tests (relationship/calculated tables, `ADD CONSTRAINT
  UNIQUE`), shared-index DDL boundary, QueryDef grammar boundaries
  (INNER/LEFT JOIN round-trip; FULL JOIN, comma/JOIN mix, missing ON,
  unterminated PARAMETERS, bad param type, TOP PERCENT rejected), crosstab
  aggregate/grammar boundaries and parameterized inline-dynamic pivot coverage,
  `CREATE TABLE` Access type aliases, and a DDL-created FK `GetSchema`
  round-trip test.
- Added the optional `JustyBase.UCanAccess.AccessCrypto` package with a pure
  .NET Agile-encryption page codec for Access 2010+ `.accdb` files, including
  opt-in Access COM round-trip fixtures and tests.

- Added the compatibility matrix for the ADO.NET behavior contract.
- Enabled XML documentation generation for library projects.
- Added Coverlet collection to both test projects and CI coverage artifacts.
- Added the security policy and links to focused documentation.
- Added CTAS (`CREATE TABLE ... AS SELECT`), transaction savepoints, connection-
  local scalar function registration, and Access statistical aggregates.
- Added exact string-backed MONEY/NUMERIC mirror arithmetic and aggregates.
- Added explicit and dynamic `TRANSFORM/PIVOT` translation with real Access
  fixture coverage.
- Added typed complex-field models and flat-table read/write support for
  multi-value fields and attachments.
- Added the `IAccessDatabaseOpener` extension point for password/encrypted
  containers and a COM/DAO fixture generator for complex Access fields.
- Replaced the sibling-checkout `JustyBase.NetezzaSqlParser` reference with the
  published NuGet package (see `Directory.Build.props` for the version).

## 1.0.0

- Initial pure .NET provider and Access file-format implementation.
