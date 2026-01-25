<#
.SYNOPSIS
    Builds and packages G-AutoSwitch for release using Clowd.Squirrel.

.DESCRIPTION
    This script builds the application in Release configuration, then uses
    Squirrel to create an installer and update packages.

.PARAMETER Version
    The version number for this release (e.g., "1.0.0")

.PARAMETER Architecture
    Target architecture: x64, x86, or arm64. Default is x64.

.PARAMETER Configuration
    Build configuration: Release or Debug. Default is Release.

.PARAMETER SkipBuild
    Skip the dotnet publish step and use existing publish output.

.PARAMETER SkipRustBuild
    Skip building the Rust audio-proxy component.

.EXAMPLE
    .\Build-Release.ps1 -Version "1.0.0"

.EXAMPLE
    .\Build-Release.ps1 -Version "1.0.1" -Architecture "arm64"
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [ValidateSet("x64", "x86", "arm64")]
    [string]$Architecture = "x64",

    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [switch]$SkipBuild,

    [switch]$SkipRustBuild
)

$ErrorActionPreference = "Stop"

# Paths
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\GAutoSwitch.UI\GAutoSwitch.UI.csproj"
$publishDir = Join-Path $repoRoot "publish\$Architecture"
$releasesDir = Join-Path $repoRoot "releases\$Architecture"

# Map architecture to RID
$ridMap = @{
    "x64"   = "win-x64"
    "x86"   = "win-x86"
    "arm64" = "win-arm64"
}
$rid = $ridMap[$Architecture]

# Map architecture to Rust target triple
$rustTargetMap = @{
    "x64"   = "x86_64-pc-windows-gnu"
    "x86"   = "i686-pc-windows-gnu"
    "arm64" = "aarch64-pc-windows-gnullvm"
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " G-AutoSwitch Release Builder" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Version:       $Version"
Write-Host "Architecture:  $Architecture ($rid)"
Write-Host "Configuration: $Configuration"
Write-Host "Publish Dir:   $publishDir"
Write-Host "Releases Dir:  $releasesDir"
Write-Host ""

# Find Squirrel.exe from NuGet package
function Find-SquirrelExe {
    $nugetPackagesPath = Join-Path $env:USERPROFILE ".nuget\packages\clowd.squirrel"
    if (Test-Path $nugetPackagesPath) {
        $versions = Get-ChildItem -Path $nugetPackagesPath -Directory | Sort-Object Name -Descending
        foreach ($version in $versions) {
            $squirrelExe = Join-Path $version.FullName "tools\Squirrel.exe"
            if (Test-Path $squirrelExe) {
                return $squirrelExe
            }
        }
    }
    return $null
}

# Check if Rust/Cargo is installed
function Test-RustInstalled {
    try {
        $null = & cargo --version 2>&1
        return $LASTEXITCODE -eq 0
    } catch {
        return $false
    }
}

Write-Host "Locating Squirrel.exe from NuGet package..." -ForegroundColor Yellow
$squirrelExe = Find-SquirrelExe

if (-not $squirrelExe) {
    Write-Host "Squirrel not found in NuGet cache. Restoring packages..." -ForegroundColor Yellow
    & dotnet restore $projectPath
    $squirrelExe = Find-SquirrelExe
}

if (-not $squirrelExe) {
    Write-Error "Could not find Squirrel.exe. Please ensure Clowd.Squirrel NuGet package is installed."
    exit 1
}

Write-Host "Found Squirrel: $squirrelExe" -ForegroundColor Green
Write-Host ""

# Build and Publish
if (-not $SkipBuild) {
    Write-Host "Building and publishing..." -ForegroundColor Yellow

    # Clean previous publish
    if (Test-Path $publishDir) {
        Remove-Item -Path $publishDir -Recurse -Force
    }

    # Publish the application
    & dotnet publish $projectPath `
        --configuration $Configuration `
        --runtime $rid `
        --self-contained true `
        --output $publishDir `
        -p:Version=$Version `
        -p:PublishReadyToRun=true `
        -p:PublishSingleFile=false

    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed"
        exit 1
    }
    Write-Host "Publish completed" -ForegroundColor Green
}
else {
    Write-Host "Skipping build (using existing publish output)" -ForegroundColor Yellow
    if (-not (Test-Path $publishDir)) {
        Write-Error "Publish directory not found: $publishDir"
        exit 1
    }
}
Write-Host ""

# Build Rust audio-proxy
if (-not $SkipRustBuild) {
    Write-Host "Building audio-proxy (Rust)..." -ForegroundColor Yellow

    $audioProxyDir = Join-Path $repoRoot "src\audio-proxy"
    $rustTarget = $rustTargetMap[$Architecture]

    # Verify Rust is installed
    if (-not (Test-RustInstalled)) {
        Write-Error "Rust/Cargo not found. Install from https://rustup.rs"
        exit 1
    }

    # Build audio-proxy
    Push-Location $audioProxyDir
    try {
        # Set MinGW PATH for GNU toolchain
        $mingwPaths = @{
            "x64"   = "C:\msys64\mingw64\bin"
            "x86"   = "C:\msys64\mingw32\bin"
            "arm64" = "C:\msys64\mingw64\bin"  # ARM64 may need llvm-mingw instead
        }
        $env:PATH = "$($mingwPaths[$Architecture]);$env:PATH"

        & cargo build --release --target $rustTarget
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Cargo build failed"
            exit 1
        }
    } finally {
        Pop-Location
    }

    # Copy to publish directory
    $audioProxyExe = Join-Path $audioProxyDir "target\$rustTarget\release\audio-proxy.exe"
    if (Test-Path $audioProxyExe) {
        Copy-Item $audioProxyExe -Destination $publishDir
        Write-Host "audio-proxy.exe copied to publish directory" -ForegroundColor Green
    } else {
        Write-Error "audio-proxy.exe not found at: $audioProxyExe"
        exit 1
    }
}
else {
    Write-Host "Skipping Rust build" -ForegroundColor Yellow
}
Write-Host ""

# Create releases directory
if (-not (Test-Path $releasesDir)) {
    New-Item -ItemType Directory -Path $releasesDir -Force | Out-Null
}

# Find the main executable
$mainExe = Get-ChildItem -Path $publishDir -Filter "GAutoSwitch.UI.exe" | Select-Object -First 1
if (-not $mainExe) {
    Write-Error "Could not find GAutoSwitch.UI.exe in publish directory"
    exit 1
}

# Find icon
$iconPath = Join-Path $publishDir "Assets\app.ico"
if (-not (Test-Path $iconPath)) {
    $iconPath = $null
    Write-Host "Warning: Icon not found at Assets\app.ico" -ForegroundColor Yellow
}

# Package with Squirrel
Write-Host "Creating Squirrel package..." -ForegroundColor Yellow

$squirrelArgs = @(
    "pack"
    "--packId", "GAutoSwitch"
    "--packVersion", $Version
    "--packDir", $publishDir
    "--releaseDir", $releasesDir
    "--packAuthors", "G-AutoSwitch"
    "--packTitle", "G-AutoSwitch"
)

if ($iconPath) {
    $squirrelArgs += "--icon", $iconPath
}

# Add architecture-specific settings
if ($Architecture -eq "arm64") {
    $squirrelArgs += "--runtime", "win-arm64"
}

& $squirrelExe @squirrelArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "Squirrel pack failed"
    exit 1
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host " Build Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Release files created in: $releasesDir"
Write-Host ""
Write-Host "Files:"
Get-ChildItem -Path $releasesDir | ForEach-Object {
    Write-Host "  - $($_.Name)" -ForegroundColor Cyan
}
Write-Host ""
Write-Host "To distribute:"
Write-Host "  1. Upload Setup.exe for fresh installs"
Write-Host "  2. Upload .nupkg and RELEASES files for auto-updates"
Write-Host ""
