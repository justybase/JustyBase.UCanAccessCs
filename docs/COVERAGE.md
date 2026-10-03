# Test coverage

Coverage is collected in CI with Coverlet and uploaded as a Cobertura artifact
for both Windows and Ubuntu. Run the same collector locally with:

```powershell
dotnet test UCanAccess.slnx -c Release --collect:"XPlat Code Coverage" --results-directory TestResults
```

CI enforces a 72% line-rate floor on every Cobertura report:

```powershell
pwsh tools/CheckCoverage.ps1 -ResultsDir TestResults -MinimumLineRate 0.72
```

Project-level baselines (local Release runs):

| Run | Project | Line coverage | Branch coverage |
|---|---|---:|---:|
| 10 August 2026 | `UCanAccess` | 72.01% | 56.72% |
| 10 August 2026 | `UCanAccess.File` | 73.98% | 57.30% |
| 4 October 2026 | `UCanAccess` | 74.56% | 62.14% |
| 4 October 2026 | `UCanAccess.File` | 76.25% | 57.80% |

The SQL lexer infrastructure comes from the `JustyBase.NetezzaSqlParser` NuGet
package and is not instrumented. Raise the floor (not the baselines) when both
projects clear it with margin on Windows and Ubuntu.
