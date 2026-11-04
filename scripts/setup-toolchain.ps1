$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$Root = Split-Path -Parent $PSScriptRoot
$Tools = Join-Path $Root ".tools"

New-Item -ItemType Directory -Path $Tools -Force | Out-Null

function Download-File {
    param(
        [string]$Uri,
        [string]$Destination
    )
    Write-Host "Downloading $Uri"
    Invoke-WebRequest -Uri $Uri -OutFile $Destination -UseBasicParsing
}

# ---------------------------------------------------------------------------
# .NET 10 SDK (portable, no system changes)
# ---------------------------------------------------------------------------
$DotnetDir = Join-Path $Tools "dotnet"
$DotnetExe = Join-Path $DotnetDir "dotnet.exe"
if (-not (Test-Path $DotnetExe)) {
    $InstallScript = Join-Path $Tools "dotnet-install.ps1"
    if (-not (Test-Path $InstallScript)) {
        Download-File -Uri "https://dot.net/v1/dotnet-install.ps1" -Destination $InstallScript
    }

    Write-Host "Installing .NET 10 SDK into $DotnetDir"
    & powershell -NoProfile -ExecutionPolicy Bypass -File $InstallScript `
        -Channel "10.0" -InstallDir $DotnetDir -NoPath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $DotnetExe)) {
        throw ".NET SDK installation failed."
    }
} else {
    Write-Host ".NET SDK already present: $DotnetExe"
}

# ---------------------------------------------------------------------------
# MSYS2 MinGW-w64 toolchain (portable C/C++ compiler + SQLite)
# ---------------------------------------------------------------------------
$MsysRoot = Join-Path $Tools "msys64"
$Bash = Join-Path $MsysRoot "usr\bin\bash.exe"
$MinGwRoot = Join-Path $MsysRoot "mingw64"
$Gcc = Join-Path $MinGwRoot "bin\gcc.exe"
$Gxx = Join-Path $MinGwRoot "bin\g++.exe"
$Make = Join-Path $MinGwRoot "bin\mingw32-make.exe"
$SqliteHeader = Join-Path $MinGwRoot "include\sqlite3.h"

$toolchainReady = (Test-Path $Gcc) -and (Test-Path $Gxx) -and
    (Test-Path $Make) -and (Test-Path $SqliteHeader)

if (-not $toolchainReady) {
    if (-not (Test-Path $Bash)) {
        $MsysArchive = Join-Path $Tools "msys2-base.tar.xz"
        if (-not (Test-Path $MsysArchive)) {
            Download-File `
                -Uri "https://mirrors.ustc.edu.cn/msys2/distrib/msys2-x86_64-latest.tar.xz" `
                -Destination $MsysArchive
        }

        $Tar = Get-Command tar.exe -ErrorAction SilentlyContinue
        if ($null -eq $Tar) {
            throw "tar.exe was not found; Windows 11 normally provides it."
        }

        Write-Host "Extracting MSYS2 into $MsysRoot"
        & $Tar.Source -xf $MsysArchive -C $Tools
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $Bash)) {
            throw "MSYS2 extraction failed."
        }
    }

    # Prefer Chinese mirrors; fall back to the next entry if one is unavailable.
    $MirrorDirectory = Join-Path $MsysRoot "etc\pacman.d"
    @(
        "Server = https://mirrors.ustc.edu.cn/msys2/mingw/`$repo/",
        "Server = https://mirrors.tuna.tsinghua.edu.cn/msys2/mingw/`$repo/"
    ) | Set-Content -Path (Join-Path $MirrorDirectory "mirrorlist.mingw") -Encoding ASCII
    @(
        "Server = https://mirrors.ustc.edu.cn/msys2/msys/`$arch/",
        "Server = https://mirrors.tuna.tsinghua.edu.cn/msys2/msys/`$arch/"
    ) | Set-Content -Path (Join-Path $MirrorDirectory "mirrorlist.msys") -Encoding ASCII

    $previousMsystem = $env:MSYSTEM
    $env:MSYSTEM = "MINGW64"
    try {
        Write-Host "Refreshing MSYS2 package databases"
        & $Bash -lc "pacman -Sy --noconfirm --disable-download-timeout"
        if ($LASTEXITCODE -ne 0) {
            throw "MSYS2 package database update failed."
        }

        Write-Host "Installing GCC, make and SQLite development files"
        & $Bash -lc ("pacman -S --needed --noconfirm --disable-download-timeout " +
            "mingw-w64-x86_64-gcc mingw-w64-x86_64-make mingw-w64-x86_64-sqlite3")
        if ($LASTEXITCODE -ne 0) {
            throw "MSYS2 package installation failed."
        }
    } finally {
        $env:MSYSTEM = $previousMsystem
    }

    if (-not (Test-Path $Gxx)) {
        throw "MSYS2 installation did not produce $Gxx"
    }
    if (-not (Test-Path $Make)) {
        throw "MSYS2 installation did not produce $Make"
    }
    if (-not (Test-Path $SqliteHeader)) {
        throw "MSYS2 installation did not produce $SqliteHeader"
    }
} else {
    Write-Host "MSYS2 MinGW-w64 toolchain already present: $Gxx"
}

Write-Host ""
Write-Host "Toolchain ready."
Write-Host "  dotnet : $DotnetExe"
Write-Host "  g++    : $Gxx"
Write-Host "Next: powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1"
