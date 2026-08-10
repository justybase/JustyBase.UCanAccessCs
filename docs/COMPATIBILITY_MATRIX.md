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
| SELECT, joins, grouping, CTE, set operators | Supported | SQL oracle corpus | Add parameterized corpus cases |
| Window functions | Supported | `WindowFunctionTests` | Add type/null/error parity |
| `TRANSFORM/PIVOT` | Partial | `CrosstabTests`, translator tests, and `pivot.sql`; explicit `IN (...)` plus inline dynamic forms | Add parameterized saved-QueryDef coverage and richer Access crosstab grammar |
| INSERT/UPDATE/DELETE | Supported | `SqlWriteTests` | Add Java behavioral oracle for DML |
| `CREATE/DROP TABLE` | Supported | `SqlDdlTests` and DDL parity | Add more constraints and Access type inference cases |
| `CREATE TABLE ... AS SELECT` | Supported | `SqlDdlTests.Create_table_as_select_*` | Add parameterized/complex-type cases |
| `ALTER TABLE` | Partial | Safe recreation tests | Expand supported table shapes |
| `CREATE/DROP INDEX` | Supported | Index mutation tests | Add shared relationship index cases |
| `CREATE/DROP VIEW` | Unsupported | Explicit rejection | Persist saved SELECT definitions |
| Saved SELECT queries | Partial | Read-only mirror views | Add write lifecycle |
| Linked tables | Supported | Linked read/write tests | Add remap and concurrency tests |
| Access functions | Partial | Scalar/aggregate/domain registrations and function tests | Generate a complete parity catalog |
| User-defined scalar functions | Supported | `UCanAccessConnection.RegisterFunction` and function tests | Add an aggregate registration API if needed |
| Complex types/attachments | Partial | `ComplexTypeTests`, `ComplexTypeProviderTests`, real COM-generated ACCDB fixture; typed arrays and child-table writes | Add version-history and more attachment metadata fixtures; keep complex-field DDL out of scope |
| Password/encrypted files | Partial | `EncryptionTests`; password masking and `IAccessDatabaseOpener` boundary | Provide a separately maintained codec adapter for real encrypted files |

## ADO.NET surface

| Area | Status | Next action |
|---|---|---|
| `DbConnection`, `DbCommand`, `DbDataReader` | Supported | Add API contract tests for every override |
| Input parameters | Supported | Add parameterized Java oracle scripts |
| Output/return parameters | Unsupported | Document as input-only or implement where meaningful |
| Transactions | Supported | Atomic staging and transaction tests | Add linked-table cases |
| Savepoints | Supported | `DbTransaction.Save`/rollback-to-savepoint snapshot tests | Add savepoint stress/cleanup cases |
| `GetSchema` core collections | Partial | Add primary/foreign key, index-column and restriction coverage |
| Connection pooling | N/A/undocumented | Document ownership and test concurrent connections |
| Updatable JDBC `ResultSet` | N/A | Use ADO.NET DML commands as the supported alternative |

## Connection-string surface

| Option | Status | Notes |
|---|---|---|
| Data Source, Read Only | Supported | Required path and safe defaults |
| Password/PWD | Partial | Routed to `IAccessDatabaseOpener`; direct core opening fails deterministically |
| Encoding/Code Page | Supported | Especially relevant to Jet 3 |
| Show Schema, Column Order | Supported | Tested in metadata/query paths |
| Lazy Load, Keep Mirror | Supported | In-memory mirror semantics are .NET-specific |
| Allow External Links | Supported | Disabled by default for path safety |
| New Database Version | Supported | 2000/2002/2003/2007/2010/2016 |
| Time Zone/Prefer Date Timestamp | Partial | Accepted for compatibility; Access values remain `DateTime` |
| Remap | Planned | Add safe path remapping for linked databases |
| Skip Indexes | Planned | Optimize mirror construction without changing file data |
| Open Exclusive | Planned | Make locking mode explicit and testable |
| Mirror Path/Disk Mirror | Planned | Add only if a stable disk-mirror contract is required |
| Java/HSQLDB-only options | N/A | Do not expose false-compatible knobs |

## Evidence rule

Every new `Supported` entry must add a test and update this matrix in the same
change. Every change to a `Partial` or `Unsupported` entry must include a
fixture, an explicit negative test, or a documented reason why the behavior is
not applicable to ADO.NET.
