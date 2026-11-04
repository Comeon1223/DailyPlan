param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$Tools = Join-Path $Root ".tools"
$Dotnet = Join-Path $Tools "dotnet\dotnet.exe"
$MinGwRoot = Join-Path $Tools "msys64\mingw64"
$MinGwBin = Join-Path $MinGwRoot "bin"
$Gcc = Join-Path $MinGwBin "gcc.exe"
$Gxx = Join-Path $MinGwBin "g++.exe"
$Make = Join-Path $MinGwBin "mingw32-make.exe"

# CMake treats backslashes as escapes on the command line; use forward slashes.
$MinGwRootCMake = $MinGwRoot.Replace("\", "/")
$GccCMake = $Gcc.Replace("\", "/")
$GxxCMake = $Gxx.Replace("\", "/")
$NativeBuild = Join-Path $Root "build\native"
$NativeOutput = Join-Path $Root "artifacts\native"
$AppProject = Join-Path $Root "src\DailyPlan.App\DailyPlan.App.csproj"
$TestProject = Join-Path $Root "tests\DailyPlan.Client.Tests\DailyPlan.Client.Tests.csproj"
$PublishDir = Join-Path $Root "artifacts\app"

foreach ($required in @($Dotnet, $Gcc, $Gxx, $Make)) {
    if (-not (Test-Path $required)) {
        throw "Missing toolchain file: $required`nRun: powershell -ExecutionPolicy Bypass -File .\scripts\setup-toolchain.ps1"
    }
}

$cmake = Get-Command cmake -ErrorAction SilentlyContinue
if ($null -eq $cmake) {
    throw "CMake was not found on PATH."
}

$env:PATH = "$MinGwBin;$env:PATH"

Write-Host ""
Write-Host "==> Building native core ($Configuration)" -ForegroundColor Cyan
& cmake -S (Join-Path $Root "native") -B $NativeBuild `
    -G "MinGW Makefiles" `
    -DCMAKE_BUILD_TYPE=$Configuration `
    -DCMAKE_C_COMPILER="$GccCMake" `
    -DCMAKE_CXX_COMPILER="$GxxCMake" `
    -DDAILYPLAN_MSYS2_PREFIX="$MinGwRootCMake"
if ($LASTEXITCODE -ne 0) {
    throw "CMake configure failed."
}

& cmake --build $NativeBuild --parallel
if ($LASTEXITCODE -ne 0) {
    throw "Native build failed."
}

$nativeDll = Join-Path $NativeOutput "DailyPlanCore.dll"
if (-not (Test-Path $nativeDll)) {
    throw "Native build did not produce $nativeDll"
}

Write-Host ""
Write-Host "==> Building C# client and tests ($Configuration)" -ForegroundColor Cyan
& $Dotnet build $TestProject -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "C# build failed."
}

if (-not $SkipTests) {
    Write-Host ""
    Write-Host "==> Running integration tests" -ForegroundColor Cyan
    & $Dotnet run --project $TestProject -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) {
        throw "Integration tests failed."
    }
}

Write-Host ""
Write-Host "==> Publishing WPF application" -ForegroundColor Cyan
& $Dotnet publish $AppProject -c $Configuration -o $PublishDir
if ($LASTEXITCODE -ne 0) {
    throw "Application publish failed."
}

Copy-Item $nativeDll (Join-Path $PublishDir "DailyPlanCore.dll") -Force

$appExe = Join-Path $PublishDir "DailyPlan.exe"
if (-not (Test-Path $appExe)) {
    throw "Publish did not produce $appExe"
}

Write-Host ""
Write-Host "Build complete." -ForegroundColor Green
Write-Host "  App: $appExe"
Write-Host "  Run: powershell -ExecutionPolicy Bypass -File .\scripts\run.ps1"
