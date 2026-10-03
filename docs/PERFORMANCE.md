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

## Provider benchmarks (`ProviderBenchmarkTests`, opt-in `UCANACCESS_PERF=1`)

`UCANACCESS_PERF_ROWS` overrides the generated row count (default 20,000).
Every measurement reports wall time plus `GC.GetAllocatedBytesForCurrentThread`
deltas. No timing assertions — numbers are for before/after comparison only:

```powershell
$env:UCANACCESS_PERF = '1'
$env:UCANACCESS_PERF_ROWS = '20000'
dotnet test tests/UCanAccess.Tests/UCanAccess.Tests.csproj -c Release `
  --filter FullyQualifiedName~ProviderBenchmarkTests --logger 'console;verbosity=normal'
```

Baseline (4 Oct 2026, Windows, .NET 10, 20,000 generated rows):

| Operation | Time | Throughput | Allocated/row |
|---|---:|---:|---:|
| `Mirror.Open` (Lazy Load=false) | 279 ms | 71,705 rows/s | 2,278 B |
| Query filter (`amount > ?`, 10k rows) | 115 ms | 87,280 rows/s | 1,121 B |
| Query LIKE (`name LIKE 'row1*'`, 11k rows) | 82 ms | 135,467 rows/s | 1,112 B |
| Query decimal aggregates (full scan) | 123 ms | — | 23 MB total |
| Query GROUP BY (full scan) | 69 ms | — | 6.4 MB total |
| Query window functions (20k rows) | 17,153 ms | 1,166 rows/s | 25,737 B |
| Query concat (5k rows) | 3,758 ms | 1,330 rows/s | 18,392 B |
| `Translate` per statement (5 shapes x2000) | 1,325 us | — | 38,473 B |

The window/concat/translate rows are the optimization targets (F1); a fix must
improve time or allocations without regressing the SQL parity corpus or the
full test suite.

After F1 (memoized exact-decimal projection, hoisted rowid command, cached LIKE
regexes, ExactDecimal Pow10/span/`string.Create` fast paths), same machine:

| Operation | Time | Throughput | Allocated/row |
|---|---:|---:|---:|
| `Mirror.Open` | 300 ms | 66,723 rows/s | 1,815 B |
| Query filter | 157 ms | 63,572 rows/s | 790 B |
| Query LIKE | 114 ms | 97,872 rows/s | 798 B |
| Query decimal aggregates | 153 ms | — | 16.9 MB total |
| Query GROUP BY | 74 ms | — | 4.4 MB total |
| Query window functions | 703 ms | 28,466 rows/s | 1,193 B |
| Query concat | 66 ms | 75,442 rows/s | 539 B |
| `Translate` per statement | 1,459 us | — | 38,476 B |

Notable deltas: window 17,153 ms -> ~640 ms (~27x) with 21x fewer
allocations; concat 3,758 ms -> ~61 ms (~60x) with 34x fewer allocations.
Filter/LIKE/translate wall times sit in the machine-noise band; allocations
for LIKE/filter/mirror dropped 28-30%. `Translate` internals are untouched so
far (next: F1.4).

## F1.4 follow-up: per-mirror translation cache

`Mirror.TranslateQuery` memoizes translated statements (keyed by SQL text plus
the connection's `ConcatNulls` flag, capped at 512 entries, invalidated on any
column-classification change). Repeated commands skip the lexer/parser entirely
after the first execution; `TranslationCacheTests` pins reuse and
invalidation. New `Command.repeat` benchmark (`SELECT ? + ?`, 2000 iterations,
no table access so the scan does not dominate):

| Operation | Time | Allocations |
|---|---:|---:|
| `Command.repeat` per command | 694 us | 34,098 B |

Without the cache each repeat would re-run the translator (~38 KB and
~1.4 ms per statement per the `Translate` row above); the cache removes that
cost, leaving SQLite prepare/step plus reader metadata as the remainder.

## F2: file layer (same machine, 20,000 rows)

| Operation | Time | Throughput | Allocated/row |
|---|---:|---:|---:|
| `Mirror.Open` | 79 ms | 252,567 rows/s | 1,666 B |
| Query filter | 70 ms | 143,233 rows/s | 784 B |
| Query LIKE | 50 ms | 222,178 rows/s | 798 B |
| Query decimal aggregates | 74 ms | — | 16.9 MB total |
| Query GROUP BY | 42 ms | — | 4.4 MB total |
| Query window functions | 318 ms | 62,914 rows/s | 1,193 B |
| Query concat | 26 ms | 195,011 rows/s | 539 B |
| `Translate` per statement | 1,239 us | — | 37,627 B |

Changes: pooled page buffers in `Table.RowLocations`/`EnsureLongValuePageReferences`
(one `ArrayPool` rent per scan instead of per page), `stackalloc` GUID/numeric/index
scratch buffers, cached `Pow10` tables, hex via `Convert.ToHexString` +
`string.Create`, jump-table offsets decoded once per row (Jet 3), complex child
rows grouped once per read with invalidation on write, pooled encrypted-write
buffers in `PageChannel`. Wall times carry machine noise plus cross-test JIT
warmup; allocations are the stable signal (`Mirror.Open` -8% vs F1).
