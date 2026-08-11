# Regenerates the Java "oracle" JSON files used by the differential tests.
#
# Requires: any JDK distribution version 11+ and the jackcess-5.1.5.jar
# (auto-downloaded to a temp dir). Java is resolved from UCANACCESS_JAVA /
# UCANACCESS_JAVAC, JAVA_HOME, or PATH; no vendor-specific JDK is required.
#
# Usage:
#   pwsh tools/JavaOracle/run.ps1            # regenerate all oracle JSONs
#   pwsh tools/JavaOracle/run.ps1 -Db 01.mdb # regenerate a single database

param(
    [string]$Db = ""
)

$ErrorActionPreference = "Stop"

# Prepares the Java toolchain (JDK resolution, jar downloads, oracle class
# compilation) without touching any committed oracle artifacts.  Dot-sourcing
# exposes $javaCommand, $classesDir, $tmp and the jar paths to this scope.
. (Join-Path $PSScriptRoot "prepare.ps1")

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$fixturesDir = Join-Path (Join-Path $repo "tests") "fixtures"
$oracleDir = Join-Path $fixturesDir "oracle"

$classpathSeparator = [System.IO.Path]::PathSeparator
function Join-ClassPath([string[]]$entries) {
    return [string]::Join($classpathSeparator, $entries)
}

New-Item -ItemType Directory -Force -Path $oracleDir | Out-Null

function Invoke-Oracle([string]$mdb) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($mdb)
    $json = Join-Path $oracleDir "$name.json"
    Write-Host "Oracle: $name"
    & $javaCommand "-Duser.timezone=UTC" "-Duser.language=en" "-Duser.country=US" "-Djackcess.charset.VERSION_3=GBK" `
        -cp (Join-ClassPath @($jackJar, $classesDir)) DbDump $mdb $json
    if ($LASTEXITCODE -ne 0) { throw "oracle failed for $mdb" }
}

if ($Db -ne "") {
    Invoke-Oracle (Join-Path $fixturesDir $Db)
    return
}
else {
    Get-ChildItem $fixturesDir -File | Where-Object { $_.Extension -in @('.mdb', '.accdb') } | Sort-Object Name | ForEach-Object {
        Invoke-Oracle $_.FullName
    }
}

# ---- SQL behavioral oracle (executes SQL through the original UCanAccess) ----

$sqlDir = Join-Path $fixturesDir "sql"
if (Test-Path $sqlDir) {
    $sqlCp = Join-ClassPath @($jackJar, $hsqldbJar, $ucaJar, $classesDir)
    Get-ChildItem $sqlDir -Filter *.sql | Sort-Object Name | ForEach-Object {
        $corpus = [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
        $mdb = Join-Path $fixturesDir "$corpus.mdb"
        if (-not (Test-Path $mdb)) {
            $mdb = Join-Path $fixturesDir "$corpus.accdb"
        }
        if (Test-Path $mdb) {
            $out = Join-Path $sqlDir "$corpus.java.json"
            Write-Host "SqlDump: $corpus"
            & $javaCommand "-Duser.timezone=UTC" "-Duser.language=en" "-Duser.country=US" -cp $sqlCp SqlDump $mdb $_.FullName $out
            if ($LASTEXITCODE -ne 0) { throw "sqldump failed for $corpus" }
        }
    }
}

# ---- Generated fixtures (DbGen) -------------------------------------------
# Creates fresh .mdb files with the original Jackcess in a temp dir and dumps
# the oracle JSON from them. The committed tests/fixtures/generated/*.mdb are
# kept as the canonical binaries; if DbGen output changes, the oracle JSON diff
# will flag it and the .mdb files must be regenerated.
$genDir = Join-Path $fixturesDir "generated"
$tempGenDir = Join-Path $tmp "generated"
New-Item -ItemType Directory -Force -Path $tempGenDir | Out-Null
foreach ($gen in @("genAllTypes", "genIndexed", "genEmpty", "genIndexedAllTypes", "genRelated", "genIndexedEdge")) {
    Write-Host "DbGen: $gen"
    & $javaCommand -cp (Join-ClassPath @($jackJar, $classesDir)) DbGen $tempGenDir $gen
    if ($LASTEXITCODE -ne 0) { throw "dbgen failed for $gen" }
    & $javaCommand "-Duser.timezone=UTC" "-Duser.language=en" "-Duser.country=US" "-Djackcess.charset.VERSION_3=GBK" -cp (Join-ClassPath @($jackJar, $classesDir)) `
        DbDump (Join-Path $tempGenDir "$gen.mdb") (Join-Path $oracleDir "$gen.json")
    if ($LASTEXITCODE -ne 0) { throw "oracle failed for $gen" }
}

Write-Host "Done."
