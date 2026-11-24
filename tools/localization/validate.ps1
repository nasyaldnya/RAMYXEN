<#
.SYNOPSIS
  Validates all RAMYXEN localization packs, schema compliance, ICU pluralization, and completeness.
.EXAMPLE
  .\tools\localization\validate.ps1
#>
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
)

$ErrorActionPreference = "Stop"
$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) {
    Write-Error "Node.js is required to run the RAMYXEN localization validator."
    exit 1
}

$script = Join-Path $PSScriptRoot "validate.js"
& node $script $RepoRoot
exit $LASTEXITCODE
