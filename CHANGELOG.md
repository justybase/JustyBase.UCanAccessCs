# Changelog

## Unreleased

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
