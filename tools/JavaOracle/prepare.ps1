# Prepares the Java toolchain used by the differential and read-back tests
# without touching any committed oracle artifacts: resolves a JDK, downloads
# the Jackcess/HSQLDB/UCanAccess jars into the shared temp dir, and compiles
# the oracle classes into tools/JavaOracle/classes.
#
# Requires: any JDK distribution version 11+ (resolved from UCANACCESS_JAVA /
# UCANACCESS_JAVAC, JAVA_HOME, or PATH; no vendor-specific JDK is required).
#
# Usage:
#   pwsh tools/JavaOracle/prepare.ps1
#
# The script is also dot-sourced by run.ps1 so it can reuse the resolved
# $javaCommand and the path variables without duplicating the logic.

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

# $env:TEMP is not defined on Linux runners; use the cross-platform temp path.
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "ucanaccess-csharp-oracle"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

# ---- Java dependencies ------------------------------------------------------
# Download every dependency before compiling.  The oracle sources contain
# DbDump, DbGen, SqlDump, DdlRunner and AccdbGen; compiling only DbDump makes a
# clean checkout fail later with ClassNotFoundException.
$jackVersion = "5.1.5"
$m2 = "https://repo1.maven.org/maven2/io/github/spannm/jackcess/$jackVersion"
$jackJar = Join-Path $tmp "jackcess-$jackVersion.jar"
if (-not (Test-Path $jackJar)) {
    Write-Host "Downloading jackcess-$jackVersion.jar ..."
    Invoke-WebRequest -Uri "$m2/jackcess-$jackVersion.jar" -OutFile $jackJar
}

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

# ---- Oracle classes ----------------------------------------------------------
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

Write-Host "Java oracle toolchain ready (classes in $classesDir)."
