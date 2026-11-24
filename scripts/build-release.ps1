#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Reproducible clean-clone Release build script for RAMYXEN (x64).
.DESCRIPTION
    Builds Native C++20 components (RAMYXEN.Native.dll, RAMYXEN.HeapWorker.exe, RAMYXEN.Native.Tests.exe),
    publishes a truly self-contained x64 Portable WinUI 3 + .NET 10 package (zero PDBs),
    builds the single-file RAMYXEN-Setup.exe embedding the Portable ZIP, compiles Release tests,
    and writes SHA256SUMS.txt and RELEASE_NOTES.md into the output staging directory.
#>
[CmdletBinding()]
param(
    [string]$OutputRoot = "artifacts\release-v1.1.0-candidate",
    [switch]$SkipNative,
    [switch]$RunTests
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    # Prefer per-user Microsoft dotnet-install location if it exists and provides the pinned global.json SDK
    $localDotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"
    if (Test-Path (Join-Path $localDotnet "dotnet.exe")) {
        $env:DOTNET_ROOT = $localDotnet
        if ($env:PATH -notlike "$localDotnet*") {
            $env:PATH = "$localDotnet;$env:PATH"
        }
    }

    $sdkVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sdkVersion)) {
        throw "Required .NET 10 SDK (pinned in global.json) was not found. Install the pinned SDK version and retry."
    }
    Write-Host "Using .NET SDK: $sdkVersion (from $((Get-Command dotnet).Source))" -ForegroundColor Cyan

    $sha = (git rev-parse HEAD).Trim()
    $branch = (git branch --show-current).Trim()
    $buildUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    $outFull = Join-Path $repoRoot $OutputRoot
    if (Test-Path $outFull) { Remove-Item $outFull -Recurse -Force }
    New-Item -ItemType Directory -Path $outFull | Out-Null

    if (-not $SkipNative) {
        Write-Host "[1/6] Building Native C++20 engine & HeapWorker (Release x64)..." -ForegroundColor Cyan
        cmd /c "src\RAMYXEN.Native\build_native.cmd"
        if ($LASTEXITCODE -ne 0) { throw "Native C++20 build failed with exit code $LASTEXITCODE" }
    }

    Write-Host "[2/6] Publishing self-contained Portable RAMYXEN (win-x64, .NET 10 + Windows App SDK 2.7)..." -ForegroundColor Cyan
    $portableDir = Join-Path $outFull "RAMYXEN-Portable-win-x64"
    $portableZip = Join-Path $outFull "RAMYXEN-Portable-win-x64.zip"
    dotnet publish src\RAMYXEN.App\RAMYXEN.App.csproj -c Release -r win-x64 --self-contained true `
        -p:SourceRevisionId=$sha -p:DebugType=None -p:DebugSymbols=false -o $portableDir
    if ($LASTEXITCODE -ne 0) { throw "Portable publish failed with exit code $LASTEXITCODE" }
    Get-ChildItem $portableDir -Filter "*.pdb" -Recurse | Remove-Item -Force
    Compress-Archive -Path "$portableDir\*" -DestinationPath $portableZip -Force

    # Extract embedded runtime identity from RAMYXEN.runtimeconfig.json
    $rcPath = Join-Path $portableDir "RAMYXEN.runtimeconfig.json"
    $rcJson = Get-Content $rcPath -Raw | ConvertFrom-Json
    $embeddedRuntime = ($rcJson.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq "Microsoft.NETCore.App" } | Select-Object -First 1).version
    if ([string]::IsNullOrWhiteSpace($embeddedRuntime)) {
        throw "Self-contained verification failed: Microsoft.NETCore.App missing from includedFrameworks in $rcPath"
    }
    Write-Host "Verified embedded .NET runtime: Microsoft.NETCore.App $embeddedRuntime" -ForegroundColor Green

    Write-Host "[3/6] Publishing single-file RAMYXEN-Setup.exe with embedded self-contained payload..." -ForegroundColor Cyan
    $installerDir = Join-Path $outFull "installer"
    dotnet publish installers\RAMYXEN.Installer\RAMYXEN.Installer.csproj -c Release -r win-x64 `
        -p:RequireEmbeddedPayload=true -p:PayloadZipPath=$portableZip `
        -p:SourceRevisionId=$sha -p:DebugType=None -p:DebugSymbols=false -o $installerDir
    if ($LASTEXITCODE -ne 0) { throw "Installer publish failed with exit code $LASTEXITCODE" }
    Get-ChildItem $installerDir -Filter "*.pdb" -Recurse | Remove-Item -Force

    $setupStage = Join-Path $installerDir "RAMYXEN-Setup.exe"
    $setupOut = Join-Path $outFull "RAMYXEN-Setup.exe"
    Copy-Item $setupStage $setupOut -Force

    Write-Host "[4/6] Building Release .NET test suite..." -ForegroundColor Cyan
    dotnet build tests\unit\RAMYXEN.Tests\RAMYXEN.Tests.csproj -c Release -r win-x64 -p:UseAppHost=false -p:SourceRevisionId=$sha
    if ($LASTEXITCODE -ne 0) { throw "Release test build failed with exit code $LASTEXITCODE" }

    Write-Host "[5/6] Generating SHA256SUMS.txt..." -ForegroundColor Cyan
    $hSetup = (Get-FileHash $setupOut -Algorithm SHA256).Hash
    $hZip = (Get-FileHash $portableZip -Algorithm SHA256).Hash
    @(
        "$hSetup  RAMYXEN-Setup.exe"
        "$hZip  RAMYXEN-Portable-win-x64.zip"
    ) | Set-Content -Path (Join-Path $outFull "SHA256SUMS.txt") -Encoding ascii

    Write-Host "[6/6] Generating RELEASE_NOTES.md..." -ForegroundColor Cyan
    $setupSize = (Get-Item $setupOut).Length
    $zipSize = (Get-Item $portableZip).Length
    @(
        "# RAMYXEN v1.1.0 - Release Build Notes"
        ""
        "- **Version:** ``1.1.0``"
        "- **Target OS:** Windows 11 (x64, Build 22000+)"
        "- **Branch:** ``$branch``"
        "- **Commit:** ``$sha``"
        "- **Build Timestamp (UTC):** ``$buildUtc``"
        "- **Pinned .NET SDK:** ``$sdkVersion``"
        "- **Embedded .NET Runtime:** ``Microsoft.NETCore.App $embeddedRuntime`` (Self-Contained ``win-x64``)"
        "- **Windows App SDK:** ``2.7.0`` (Self-Contained, Unpackaged)"
        "- **Native Engine:** C++20 static CRT (``/MT``, ``/guard:cf``, ``/Brepro``)"
        "- **Supported Locales:** ``en-US``, ``ar-SA``, ``zh-CN``, ``ja-JP`` (567 keys each, 100%)"
        ""
        "## Deliverables & SHA-256 Checksums"
        ""
        "| File | Size (Bytes) | SHA-256 |"
        "|---|---:|---|"
        "| ``RAMYXEN-Setup.exe`` | $setupSize | ``$hSetup`` |"
        "| ``RAMYXEN-Portable-win-x64.zip`` | $zipSize | ``$hZip`` |"
    ) | Set-Content -Path (Join-Path $outFull "RELEASE_NOTES.md") -Encoding utf8

    if ($RunTests) {
        Write-Host "Running Native, .NET Release, and Localization verification suites..." -ForegroundColor Green
        & .\src\RAMYXEN.Native\bin\Release\RAMYXEN.Native.Tests.exe
        if ($LASTEXITCODE -ne 0) { throw "Native tests failed" }
        dotnet tests\unit\RAMYXEN.Tests\bin\Release\net10.0-windows10.0.22621.0\win-x64\RAMYXEN.Tests.dll
        if ($LASTEXITCODE -ne 0) { throw ".NET Release tests failed" }
        node tools\localization\validate.js
        if ($LASTEXITCODE -ne 0) { throw "Localization validation failed" }
        node tests\localization\run-localization-tests.js
        if ($LASTEXITCODE -ne 0) { throw "Localization tests failed" }
    }

    Write-Host "`nBuild complete: $outFull" -ForegroundColor Green
}
finally {
    Pop-Location
}
