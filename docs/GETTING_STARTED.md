# Getting started with UCanAccess-csharp

## Install and connect

Add the `JustyBase.UCanAccessCs` package and open a database through the normal
ADO.NET abstractions:

```csharp
using UCanAccess;

using DbConnection connection = UCanAccessFactory.Instance.CreateConnection()!;
connection.ConnectionString =
    "Data Source=C:\\data\\Northwind.accdb;Read Only=true";
connection.Open();

using DbCommand command = connection.CreateCommand();
command.CommandText = "SELECT * FROM [Order Details] WHERE Quantity > ?";
DbParameter parameter = command.CreateParameter();
parameter.Value = 1;
command.Parameters.Add(parameter);

using DbDataReader reader = command.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine(reader.GetValue(0));
}
```

The provider does not require Microsoft Access, ODBC or ACE. The file layer is
also available directly when a caller needs schema, indexes, relationships or
low-level row access.

## Connection string defaults

`Data Source` is required. `Read Only` defaults to `true`. The currently
supported options are documented in the [compatibility matrix](COMPATIBILITY_MATRIX.md)
and include encoding/code page, schema visibility, column order, lazy loading,
mirror lifetime, linked-database policy and new database version.

## Writes and transactions

Open a writable connection explicitly and use parameters for values:

```csharp
using var connection = UCanAccessFactory.Instance.CreateConnection()!;
connection.ConnectionString = "Data Source=C:\\data\\work.mdb;Read Only=false";
connection.Open();

using var transaction = connection.BeginTransaction();
using var command = connection.CreateCommand();
command.Transaction = transaction;
command.CommandText = "UPDATE People SET [Name] = ? WHERE Id = ?";

var name = command.CreateParameter();
name.Value = "Anna";
command.Parameters.Add(name);
var id = command.CreateParameter();
id.Value = 1;
command.Parameters.Add(id);
command.ExecuteNonQuery();
transaction.Commit();
```

Transactions stage changes in a same-directory file copy and install the result
atomically. Native linked-table transactions are rejected because they cannot be
made atomic across multiple files.

Savepoints use the same private staging file, so rollback-to-savepoint keeps the
earlier part of the transaction:

```csharp
using var savepoint = connection.BeginTransaction();
// execute the first group of commands with savepoint as DbTransaction
savepoint.Save("before_optional_part");
// execute more commands
savepoint.Rollback("before_optional_part");
savepoint.Commit();
```

Applications can register scalar functions before opening a typed connection:

```csharp
var custom = (UCanAccessConnection)UCanAccessFactory.Instance.CreateConnection()!;
custom.RegisterFunction("DoubleText", 1,
    args => $"{args[0]}{args[0]}", deterministic: true);
custom.ConnectionString = "Data Source=C:\\data\\work.mdb;Read Only=true";
custom.Open();
```

For DDL-heavy workflows, `CREATE TABLE target AS SELECT ... WITH DATA` and
`WITH NO DATA` are supported; see [SQL compatibility](SQL_COMPATIBILITY.md).

### Exact decimal, crosstab and complex fields

`MONEY` and `NUMERIC` columns are exposed as CLR `decimal`. Known arithmetic
and aggregate expressions use an exact, string-backed decimal path in the
SQLite mirror. `TRANSFORM ... PIVOT ... IN (...)` and inline dynamic crosstabs
are translated into conditional aggregation.

Existing Access multi-value, attachment and version fields are exposed through
`AccessSingleValue[]`, `AccessAttachment[]` and `AccessVersion[]` from
`UCanAccess.File`; writes update the Access flat child tables. The provider does
not create new complex fields through DDL. A real fixture can be generated on a
machine with Access using `tools/AccessFixtures/Generate-ComplexFixture.ps1`.

Password-protected files require an application-supplied
`IAccessDatabaseOpener`. The password is passed to that adapter and masked in
connection-string text; the core package intentionally does not bundle an
Access encryption codec.

## Low-level file API

```csharp
using UCanAccess.File;

using var database = Database.Open(@"C:\data\Northwind.mdb");
foreach (string tableName in database.GetTableNames())
{
    Table? table = database.GetTable(tableName);
    Console.WriteLine($"{tableName}: {table?.RowCount} rows");
}
```

Use the [compatibility matrix](COMPATIBILITY_MATRIX.md) before relying on DDL,
complex Access types, encryption or crosstab queries.
