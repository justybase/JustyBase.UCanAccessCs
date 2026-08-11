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

function Resolve-JavaTool([string]$toolName, [string]$overrideVariable) {
    $override = [Environment]::GetEnvironmentVariable($overrideVariable)
    if (-not [string]::IsNullOrWhiteSpace($override)) {
        return $override
    }

    $suffix = if ($IsWindows) { ".exe" } else { "" }
    foreach ($homeVariable in @("UCANACCESS_JAVA_HOME", "JAVA_HOME", "JDK_HOME")) {
        $javaHome = [Environment]::GetEnvironmentVariable($homeVariable)
        if ([string]::IsNullOrWhiteSpace($javaHome)) {
            continue
        }
        $candidate = Join-Path (Join-Path $javaHome "bin") ($toolName + $suffix)
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    return $toolName
}

function Get-JavaMajorVersion([string]$command) {
    try {
        $versionText = (& $command "-version" 2>&1 | Out-String)
        $exitCode = $LASTEXITCODE
    }
    catch {
        throw "A compatible Java JDK 11+ is required. Could not execute '$command'."
    }
    if ($exitCode -ne 0) {
        throw "A compatible Java JDK 11+ is required. '$command -version' failed."
    }

    $match = [regex]::Match($versionText, '(?m)(?:version\s+|javac\s+)[\"]?(?<major>\d+)')
    if (-not $match.Success) {
        throw "Could not determine the Java version reported by '$command'."
    }
    return [int]$match.Groups["major"].Value
}

$javaCommand = Resolve-JavaTool "java" "UCANACCESS_JAVA"
$javacCommand = Resolve-JavaTool "javac" "UCANACCESS_JAVAC"
$javaMajor = Get-JavaMajorVersion $javaCommand
$javacMajor = Get-JavaMajorVersion $javacCommand
if ($javaMajor -lt 11 -or $javacMajor -lt 11) {
    throw "Java 11 or newer is required; found runtime $javaMajor and compiler $javacMajor."
}
Write-Host "Using Java runtime '$javaCommand' (version $javaMajor) and compiler '$javacCommand' (version $javacMajor)."

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$toolDir = Join-Path (Join-Path $repo "tools") "JavaOracle"
$srcDir = Join-Path $toolDir "src"
$classesDir = Join-Path $toolDir "classes"
$fixturesDir = Join-Path (Join-Path $repo "tests") "fixtures"
$oracleDir = Join-Path $fixturesDir "oracle"

$jackVersion = "5.1.5"
$m2 = "https://repo1.maven.org/maven2/io/github/spannm/jackcess/$jackVersion"
# $env:TEMP is not defined on Linux runners; use the cross-platform temp path.
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "ucanaccess-csharp-oracle"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$jackJar = Join-Path $tmp "jackcess-$jackVersion.jar"
if (-not (Test-Path $jackJar)) {
    Write-Host "Downloading jackcess-$jackVersion.jar ..."
    Invoke-WebRequest -Uri "$m2/jackcess-$jackVersion.jar" -OutFile $jackJar
}

# ---- Java dependencies ------------------------------------------------------
# Download every dependency before compiling.  The oracle sources contain
# DbDump, DbGen, SqlDump, DdlRunner and AccdbGen; compiling only DbDump makes a
# clean checkout fail later with ClassNotFoundException.
$ucaJar = Join-Path $tmp "ucanaccess-5.1.6.jar"
$hsqldbJar = Join-Path $tmp "hsqldb-2.7.4.jar"
foreach ($jar in @($ucaJar, $hsqldbJar)) {
    if (-not (Test-Path $jar)) {
        $name = [System.IO.Path]::GetFileName($jar)
        Write-Host "Downloading $name ..."
        if ($name -like "ucanaccess*") {
            Invoke-WebRequest -Uri "https://repo1.maven.org/maven2/io/github/spannm/ucanaccess/5.1.6/$name" -OutFile $jar
        } else {
            Invoke-WebRequest -Uri "https://repo1.maven.org/maven2/org/hsqldb/hsqldb/2.7.4/$name" -OutFile $jar
        }
    }
}

$classpathSeparator = [System.IO.Path]::PathSeparator
function Join-ClassPath([string[]]$entries) {
    return [string]::Join($classpathSeparator, $entries)
}

New-Item -ItemType Directory -Force -Path $classesDir | Out-Null
$javaSources = @(Get-ChildItem -Path $srcDir -Filter *.java -File | Sort-Object Name | ForEach-Object { $_.FullName })
if ($javaSources.Count -eq 0) { throw "No Java oracle sources found in $srcDir" }
$compileArgs = @(
    "-encoding", "UTF-8",
    # The oracle sources use only Jackcess at compile time.  UCanAccess and
    # HSQLDB are runtime JDBC dependencies for SqlDump/DdlRunner.
    "-cp", $jackJar,
    "-d", $classesDir
) + $javaSources
& $javacCommand @compileArgs
# Some JDKs on Windows can print an AccessDeniedException while closing the
# ZipFS classpath, after it has already emitted all class files and returned 0.
# A real compilation failure still returns non-zero; the marker also prevents
# a silent partial compilation from reaching the runtime steps.
if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $classesDir "DbDump.class"))) {
    throw "javac failed"
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
