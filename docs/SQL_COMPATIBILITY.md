# SQL compatibility guide

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
the table shapes listed in the compatibility matrix. ALTER TABLE uses safe table
recreation and therefore rejects autonumber, calculated, relationship-bearing or
otherwise unsupported table shapes.

UPDATE and DELETE expressions are evaluated by the translated mirror query. This
supports correlated subqueries and JOIN forms, while the file layer remains the
authoritative writer. A JOIN that maps one target row to different SET values is
rejected deterministically.

`CREATE TABLE target AS SELECT ... WITH DATA` copies the result-set schema and
rows into a new Access table. `WITH NO DATA` creates only the inferred schema;
parameterized CTAS is rejected because DDL commands do not carry parameter values.

## Deliberate boundaries

`TRANSFORM/PIVOT` is translated into conditional aggregation. Explicit
`PIVOT ... IN (...)` lists and inline dynamic pivots are supported; saved
dynamic crosstabs that require parameters remain limited. `TOP ... PERCENT`,
CREATE/DROP VIEW and unsupported complex/encrypted file operations are rejected
explicitly rather than silently rewritten into a different operation.

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
