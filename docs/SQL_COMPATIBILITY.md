# SQL compatibility guide

## Parser and provider boundary

The provider consumes the Access lexer and syntax contract from
`JustyBase.NetezzaSqlParser` 0.8.2. Parser support is used for shared lexical
and authoring behavior; the provider's `AccessSqlTranslator`, DDL, DML and
SQLite mirror remain responsible for execution semantics.

These are intentionally separate capabilities. A statement may be accepted by
the parser but rejected by the provider's compatibility baseline (for example
`TOP ... PERCENT`), while provider-specific execution extensions must not be
rejected solely because the editor AST does not model them yet. The architectural
boundary is described in the
[JustyBase.NetezzaSql architecture decision](https://github.com/justybase/JustyBase.NetezzaSql/blob/master/docs/architecture-access-provider-boundary.md).

For a small whitelist of simple `SELECT` shapes, UCanAccess parses the shared
AST and formats it back to canonical Access SQL before invoking the existing
translator. This covers the shared contract for `TOP`, `DISTINCTROW`, date and
identifier literals, inline parameters and explicit `TRANSFORM/PIVOT` queries.
`PARAMETERS` wrappers, joins, subqueries, CTEs, set operations, windows and
`TOP ... PERCENT` stay on the compatibility path (or are rejected by the
provider where appropriate). Any AST parse or whitelist uncertainty also falls
back to the legacy translator; the AST bridge does not become a second SQLite
execution engine.

## Supported query families

The mirror executes translated Access SQL for SELECT queries with filtering,
grouping, HAVING, ordering, joins, subqueries, UNION/EXCEPT/INTERSECT, CTEs,
DISTINCT, TOP, window functions and Access-style LIKE patterns.

Parameters may be positional (`?`) or named (`@name`, `:name`, `$name`). Access
date literals use `#...#`, and identifiers containing spaces or reserved words
should be escaped with square brackets.

## Writes and DDL

INSERT, UPDATE and DELETE are applied to a private staging copy for ordinary
autocommit connections and the mirror is refreshed only for affected tables after
successful installation. CREATE/DROP TABLE and INDEX are supported for
the table shapes listed in the compatibility matrix. ALTER TABLE uses a hybrid
metadata-extension/rebuild path: nullable columns on ordinary and relationship
tables can be added without rewriting rows, while rebuilds preserve AutoNumber
values and counters. Calculated and complex table shapes remain explicitly limited.

UPDATE and DELETE expressions are evaluated by the translated mirror query. This
supports correlated subqueries and JOIN forms, while the file layer remains the
authoritative writer. A JOIN that maps one target row to different SET values is
rejected deterministically.

`CREATE TABLE` supports named primary/unique constraints, table-level foreign
keys, and persisted `DEFAULT` expressions for portable literals and
`Now()`/`Date()`. `ALTER TABLE ... ADD CONSTRAINT ... FOREIGN KEY` writes the
Access relationship catalog and supports `ON UPDATE CASCADE`, `ON DELETE
CASCADE` and `ON DELETE SET NULL`.

`CREATE TABLE target AS SELECT ... WITH DATA` copies the result-set schema and
rows into a new Access table. `WITH NO DATA` creates only the inferred schema;
parameterized CTAS is rejected because DDL commands do not carry parameter values.

## Deliberate boundaries

`TRANSFORM/PIVOT` is translated into conditional aggregation. Explicit
`PIVOT ... IN (...)` lists and inline dynamic pivots are supported; saved
dynamic crosstabs that require parameters remain limited. Managed `CREATE VIEW`
and `DROP VIEW` persist/rewrite a conservative SELECT QueryDef, and
parameterized QueryDefs are expanded as derived tables when the command runs.
Action QueryDefs, unsupported saved-query grammar and `TOP ... PERCENT` are
rejected explicitly rather than silently rewritten into a different operation.

MONEY and NUMERIC columns are mirrored as text with an Access-compatible exact
decimal collation. Recognized arithmetic, comparisons and SUM/MIN/MAX use
registered exact-decimal functions and are converted back to CLR `decimal`.
Expressions whose result type cannot be inferred as decimal retain SQLite's
normal dynamic typing.

Access date literals and date columns support serial-date arithmetic through
provider date helpers, including `date_column + 1`, chained additions and
`date2 - date1`. Unsupported combinations such as date plus a non-numeric
column or string are rejected instead of being evaluated with SQLite's numeric
coercion. Values returned through the ADO.NET reader remain timezone-free
`DateTime` values with millisecond precision.

Single-level foreign-key cascade updates and deletes refresh the affected mirror
tables. Multi-level cascade updates (parent key -> child key -> grandchild key)
are not currently supported.

## Access functions

The provider registers Access/VBA-compatible scalar, aggregate, date, string,
conversion, financial and domain functions. Applications can add connection-local
scalar functions before `Open()` with `UCanAccessConnection.RegisterFunction`.
Function behavior is tested through
`FunctionsTests` and the Java SQL oracle where the result type and value are
stable across the two runtimes.

Complex fields in existing Access files are returned as
`AccessSingleValue[]`, `AccessAttachment[]` or `AccessVersion[]` from
`UCanAccess.File`. The provider serializes them in the SQLite mirror as JSON
only internally; file writes update the Access flat child tables. New complex
fields are not created by the DDL layer.

Password-protected files require an application-supplied
`IAccessDatabaseOpener`. This keeps cryptographic/container support optional and
allows an adapter to use Access/ACE or another licensed codec without making it
a dependency of the managed provider.
