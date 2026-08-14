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

## Version-history fixture

To create an append-only complex-text field (the Access version-history shape):

```powershell
pwsh tools/AccessFixtures/Generate-VersionFixture.ps1 `
  -OutputPath tests/fixtures/generated/version.accdb
$env:UCANACCESS_VERSION_FIXTURE = (Resolve-Path tests/fixtures/generated/version.accdb)
dotnet test tests/UCanAccess.File.Tests/UCanAccess.File.Tests.csproj `
  --filter FullyQualifiedName~VersionHistoryTests
```

The generator is optional and requires 32-bit/64-bit Microsoft Access matching
the PowerShell process. The provider itself only consumes the resulting
`MSysComplexType_*` rows and does not automate Access.

## Encrypted ACCDB fixture

`Generate-EncryptedFixture.ps1` creates a modern password-encrypted `.accdb`
through Access/DAO. It is an opt-in developer tool and is not run by normal CI:

```powershell
$env:UCANACCESS_ACCESS_FIXTURE_PASSWORD = 'Uca!fixture-2026'
pwsh tools/AccessFixtures/Generate-EncryptedFixture.ps1 `
  -OutputPath $env:TEMP\uca-encrypted.accdb
```

The plaintext staging file is temporary and removed after the encrypted copy is
created. The password is never printed by the script. Runtime code uses the
optional `JustyBase.UCanAccess.AccessCrypto` package and does not require Access.
