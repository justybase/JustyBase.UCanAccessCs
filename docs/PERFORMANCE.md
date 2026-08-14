# Performance and profiling

The provider has two different cost centres and they must be measured
separately:

* the file layer (page decoding, row writes and index maintenance), and
* the ADO.NET mirror (one-time SQLite materialization versus repeated query
  execution).

The opt-in `InsertBenchmarkTests` test measures 100,000 low-level rows with
the same schema in managed C# and Jackcess 5.1.5. It reports insert/read
milliseconds, rows/second and the cost of the non-atomic `WriteBatch` path.
The benchmark is deliberately excluded from normal CI because timings depend
on the host and Java installation:

```powershell
$env:UCANACCESS_PERF = '1'
$env:UCANACCESS_PERF_ROWS = '100000'
dotnet test tests/UCanAccess.Tests/UCanAccess.Tests.csproj -c Release `
  --filter FullyQualifiedName~InsertBenchmarkTests --logger 'console;verbosity=normal'
```

For mirror profiling, run the regular provider tests under a sampling profiler
(PerfView, dotnet-trace or Visual Studio) and compare `Keep Mirror=true` with
`Mirror Mode=file`. The useful boundaries are `Mirror.BuildSchemaAndLoad`,
`Mirror.LoadData`, `AccessSqlTranslator.Translate` and
`UCanAccessConnection.ExecuteDmlBatchAtomically`. File-backed mirrors are a
cache and are rebuilt at open; they are not a database transaction log.

When changing page traversal, row codecs or mirror loading, record the row
count, database size, runtime/OS and both C# modes in the pull request. A
benchmark result without those inputs is not comparable across machines.
