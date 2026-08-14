# Pure .NET Access encryption profile

`JustyBase.UCanAccess.AccessCrypto` is an optional package. It contains no COM,
ACE or ODBC dependency and is not referenced by the core provider. Applications
install it explicitly and configure the existing opener seam:

```csharp
using UCanAccess;
using UCanAccess.AccessCrypto;

using var connection = new UCanAccessConnection
{
    ConnectionString = "Data Source=C:\\data\\secure.accdb;Password=...;Read Only=false",
    DatabaseOpener = new AccessCryptoOpener(),
};
connection.Open();
```

The first implementation targets the modern Agile password profile used by
current `.accdb` files. The root page contains the `EncryptionInfo` XML and an
encoding key; Access protects the header region with its fixed RC4 stream. Pages
after the root are transformed independently with AES-CBC;
the password envelope uses the Agile SHA-based key derivation and verifier. The
codec operates directly in `PageChannel`, so staged writes and transactions
never create an unencrypted intermediate database.

The Microsoft documentation confirms that password encryption is an `.accdb`
feature and that the password is the encryption key for Access 2007+ files:

- [Encrypt a database by using a database password](https://support.microsoft.com/en-us/access/encrypt-a-database-by-using-a-database-password)
- [DBEngine.CompactDatabase method](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/dbengine-compactdatabase-method-dao)

## Scope and failure behavior

- encrypted `.accdb` formats 14 and 16 (Access 2010 and later) are recognized;
  legacy `.mdb` encryption and encrypted Access 2007 format 12 are rejected
  rather than silently treated as plaintext.
- Plaintext databases can still be opened through the optional opener, which is
  useful when an application uses one connection configuration for mixed files.
- Wrong passwords, malformed `EncryptionInfo`, unsupported algorithms and
  truncated pages produce `AccessEncryptionException`.
- Keys and derived password buffers are cleared after use. Passwords are not
  included in exception text or diagnostic output.
- The codec supports read-only and read/write operations, including provider
  staging, transactions, page allocation and DDL. It does not add a separate
  encrypt/decrypt conversion API.
- A `Mirror Mode=file` SQLite mirror is a provider cache and is not encrypted
  by this codec; protect its directory or use the default in-memory mirror for
  sensitive data.

## COM interoperability test

The fixture generator uses Microsoft Access/DAO only in developer or opt-in CI
tests:

```powershell
$env:UCANACCESS_ACCESS_COM = 'true'
$env:UCANACCESS_ACCESS_FIXTURE_PASSWORD = 'Uca!fixture-2026'
dotnet test tests/UCanAccess.AccessCrypto.Tests/UCanAccess.AccessCrypto.Tests.csproj `
  --filter 'Category=AccessCom'
```

The test creates an encrypted fixture with
`tools/AccessFixtures/Generate-EncryptedFixture.ps1`, reads and modifies it in
.NET, verifies those changes through Access COM, then modifies it through COM
and verifies the result after reopening it in .NET. Normal CI skips this test
unless the explicit environment flag is set and Access COM is installed.
