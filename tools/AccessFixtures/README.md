# Access fixture generators

These scripts use Microsoft Access through COM/DAO only to create fixtures
that the managed file layer cannot author yet, notably attachment and
multi-value fields. They are optional developer tools and are not run by CI.

Example:

```powershell
pwsh tools/AccessFixtures/Generate-ComplexFixture.ps1 `
  -OutputPath tests/fixtures/generated/complex.accdb
```

The generated file is consumed by `ComplexTypeTests`. The runtime provider does
not require Access or ACE; it reads and writes the generated flat child tables
directly.
