param(
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$AppExe = Join-Path $Root "artifacts\app\DailyPlan.exe"

if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot "build.ps1")
}

if (-not (Test-Path $AppExe)) {
    throw "DailyPlan.exe was not found. Run scripts\build.ps1 first."
}

Start-Process -FilePath $AppExe -WorkingDirectory (Split-Path -Parent $AppExe)
Write-Host "Started $AppExe"
