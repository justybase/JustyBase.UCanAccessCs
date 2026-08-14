param(
    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [string] $Password = $env:UCANACCESS_ACCESS_FIXTURE_PASSWORD
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Password)) {
    throw 'Pass a password through -Password or UCANACCESS_ACCESS_FIXTURE_PASSWORD.'
}

$resolved = [IO.Path]::GetFullPath($OutputPath)
$directory = [IO.Path]::GetDirectoryName($resolved)
$token = [Guid]::NewGuid().ToString('N')
$plain = [IO.Path]::Combine([IO.Path]::GetTempPath(), "uca-encrypted-plain-$token.accdb")
$encrypted = [IO.Path]::Combine([IO.Path]::GetTempPath(), "uca-encrypted-result-$token.accdb")
$access = $null
$db = $null
$recordset = $null

function Release-ComObject([object] $value) {
    if ($null -ne $value) {
        try { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($value) | Out-Null } catch {}
    }
}

try {
    if (-not [OperatingSystem]::IsWindows()) {
        throw 'Microsoft Access COM fixtures can only be generated on Windows.'
    }
    if ([IO.File]::Exists($resolved)) { Remove-Item -LiteralPath $resolved -Force }
    [IO.Directory]::CreateDirectory($directory) | Out-Null

    $access = New-Object -ComObject Access.Application
    $access.Visible = $false
    $access.NewCurrentDatabase($plain)
    $db = $access.CurrentDb()

    # DAO constants: dbFailOnError=128, dbText=10, dbLong=4,
    # dbMemo=12, dbCurrency=5, dbDate=8, dbBoolean=1.
    $db.Execute(@'
CREATE TABLE CryptoFixture (
    ID COUNTER CONSTRAINT PK_CryptoFixture PRIMARY KEY,
    Code TEXT(64) NOT NULL,
    Description LONGTEXT,
    Amount CURRENCY,
    CreatedAt DATETIME,
    IsEnabled YESNO
)
'@, 128)
    $db.Execute("INSERT INTO CryptoFixture (Code, Description, Amount, CreatedAt, IsEnabled) VALUES ('COM-ROW', 'sentinel-Access-crypto', 123.45, #2024-01-02 03:04:05#, True)", 128)
    $db.Execute("INSERT INTO CryptoFixture (Code, Description, Amount, CreatedAt, IsEnabled) VALUES ('NULL-ROW', NULL, NULL, NULL, False)", 128)

    # The source must be closed before CompactDatabase can create the encrypted
    # copy.  The DstLocale string is the documented ACCDB password form.
    $db.Close()
    $db = $null
    $access.CloseCurrentDatabase()
    $access.Quit(2)
    Release-ComObject $access
    $access = $null

    $access = New-Object -ComObject Access.Application
    $engine = $access.DBEngine
    # DAO dbEncrypt is 2.  Request it first, but modern Access rejects that
    # legacy flag for ACCDB files.  In that case the documented DstLocale
    # password form is the authoritative encryption request for ACCDB.
    $dbEncrypt = 2
    try {
        $engine.CompactDatabase($plain, $encrypted, ";pwd=$Password", $dbEncrypt, ";pwd=$Password")
    }
    catch {
        if ([IO.File]::Exists($encrypted)) {
            Remove-Item -LiteralPath $encrypted -Force
        }
        Write-Warning 'Access rejected dbEncrypt for ACCDB; retrying with the DstLocale password form.'
        $engine.CompactDatabase($plain, $encrypted, ";pwd=$Password", 0, ";pwd=$Password")
    }
    Release-ComObject $engine
    $access.Quit(2)
    Release-ComObject $access
    $access = $null

    Move-Item -LiteralPath $encrypted -Destination $resolved -Force
    Write-Output "Created encrypted Access fixture: $resolved"
}
finally {
    if ($recordset -ne $null) { try { $recordset.Close() } catch {} }
    if ($db -ne $null) { try { $db.Close() } catch {} }
    if ($access -ne $null) {
        try { $access.CloseCurrentDatabase() } catch {}
        try { $access.Quit(2) } catch {}
    }
    Release-ComObject $recordset
    Release-ComObject $db
    Release-ComObject $access
    foreach ($temporary in @($plain, $encrypted)) {
        if ($temporary -and [IO.File]::Exists($temporary)) {
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        }
    }
}
