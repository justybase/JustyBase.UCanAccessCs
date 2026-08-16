# Changelog

## Unreleased

- Prepare the provider package 1.0.3 for the published
  `JustyBase.NetezzaSqlParser` 0.8.2 Access AST contract.

- Aligned the provider with `JustyBase.NetezzaSqlParser` 0.8.2 and added
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
  `[t_detail].*`) against Java UCanAccess 5.1.6.
- Added Access `DELETE * FROM <table>` statements (the Access wildcard
  projection), with file-state parity against Java UCanAccess 5.1.6.
- Added Access `DISABLE/ENABLE AUTOINCREMENT ON <table>` statements with
  Java UCanAccess 5.1.6 parity: explicit AutoNumber values are honored only
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
- Added the optional `JustyBase.UCanAccess.AccessCrypto` package with a pure
  .NET Agile-encryption page codec for Access 2010+ `.accdb` files, including
  opt-in Access COM round-trip fixtures and tests.

## 1.1.0

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
