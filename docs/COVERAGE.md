# Test coverage

Coverage is collected in CI with Coverlet and uploaded as a Cobertura artifact
for both Windows and Ubuntu. Run the same collector locally with:

```powershell
dotnet test UCanAccess.slnx -c Release --collect:"XPlat Code Coverage" --results-directory TestResults
```

The latest local Release run (10 August 2026) produced these project-level
baselines after the crosstab, exact-decimal and complex-field additions:

| Project | Line coverage | Branch coverage |
|---|---:|---:|
| `UCanAccess` | 72.01% | 56.72% |
| `UCanAccess.File` | 73.98% | 57.30% |

The SQL lexer infrastructure comes from the `JustyBase.NetezzaSqlParser` NuGet
package and is not instrumented. CI currently treats the coverage files as
required artifacts and keeps the numeric baselines visible. Threshold
enforcement is intentionally deferred until generated and oracle-only code are
excluded consistently on both operating systems.
