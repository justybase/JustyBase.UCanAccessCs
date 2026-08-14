# Samples

The database files in this directory are real Access files used by the provider
and file-layer examples. They can be inspected without Microsoft Access:

```powershell
dotnet run --project src/UCanAccess.Console -- samples/sample2007.accdb --schema
dotnet run --project src/UCanAccess.Console -- samples/sample2016.accdb --indexes
```

For ADO.NET reads, writes, transactions, savepoints, CTAS and user-defined
functions, see [`docs/GETTING_STARTED.md`](../docs/GETTING_STARTED.md). The
provider examples intentionally use `UCanAccessFactory` so the same code works
with either `.mdb` or `.accdb`.

The `GeneratePolishSample` and `GenerateLinkedSamples` tools regenerate the
derived sample files. They are not part of the solution because they are fixture
generators rather than runtime dependencies.

## Encrypted ACCDB sample

`sample_encrypted_password.accdb` is a real password-encrypted ACCDB generated
through Microsoft Access/DAO. Its password is `password` and it contains the
`CryptoFixture` table with two rows. To open it through the managed codec, add
the optional `JustyBase.UCanAccess.AccessCrypto` package and configure:

```csharp
using UCanAccess;
using UCanAccess.AccessCrypto;

using var connection = new UCanAccessConnection
{
    ConnectionString =
        "Data Source=samples/sample_encrypted_password.accdb;"
        + "Password=password;Read Only=true",
    DatabaseOpener = new AccessCryptoOpener(),
};
connection.Open();
```

The same file was opened successfully through Access COM and the managed
provider; `SELECT COUNT(*) FROM CryptoFixture` returns `2`.
