# Fails the build when any Cobertura report drops below the line-rate floor.
# Usage:
#   pwsh tools/CheckCoverage.ps1 -ResultsDir TestResults -MinimumLineRate 0.70
param(
    [string]$ResultsDir = "TestResults",
    [double]$MinimumLineRate = 0.70
)

$ErrorActionPreference = "Stop"

$files = @(Get-ChildItem -LiteralPath $ResultsDir -Recurse -Filter "coverage.cobertura.xml" -ErrorAction SilentlyContinue)
if ($files.Count -eq 0) {
    throw "No coverage.cobertura.xml found under '$ResultsDir'."
}

$failed = $false
foreach ($file in $files) {
    [xml]$xml = Get-Content -LiteralPath $file.FullName
    $rateAttr = $xml.coverage.GetAttribute("line-rate")
    if ([string]::IsNullOrWhiteSpace($rateAttr)) {
        throw "Missing line-rate in '$($file.FullName)'."
    }
    [double]$rate = [double]::Parse($rateAttr, [System.Globalization.CultureInfo]::InvariantCulture)
    $pct = "{0:P2}" -f $rate
    Write-Host "$($file.FullName): line-rate $pct (floor $([string]::Format('{0:P0}', $MinimumLineRate)))"
    if ($rate -lt $MinimumLineRate) {
        Write-Error "Coverage below floor in '$($file.FullName)': $pct < $([string]::Format('{0:P0}', $MinimumLineRate))"
        $failed = $true
    }
}

if ($failed) {
    throw "Coverage gate failed."
}
Write-Host "Coverage gate passed."
