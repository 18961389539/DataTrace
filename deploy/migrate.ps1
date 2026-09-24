#Requires -Version 5.1
<#
.SYNOPSIS
  Document / trigger DB schema ensure for an install.
.DESCRIPTION
  DataTrace does NOT use EF Database.Migrate(). Schema is applied the same way as normal app startup:
    Config DB  : DatabaseSeeder -> EnsureCreatedAsync + SqliteSchema.AddColumnIfMissing
    Runtime DB : RuntimeDbFactory -> EnsureCreated + additive column/table patches
  This script simply restarts the target instance so startup seeder runs. No manual SQL.
  Optional -MigrateOnly is NOT a separate host; it is an alias for stop+start (same path).
.EXAMPLE
  .\migrate.ps1 -InstallDir D:\Apps\DataTrace\CustomerA
#>
param(
    [string]$InstallDir = '',
    [string]$CustomerId = '',
    [string]$InstallRoot = 'D:\Apps\DataTrace',
    [switch]$MigrateOnly
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

if (-not $InstallDir) {
    if (-not $CustomerId) { Write-DtFail "Specify -InstallDir or -CustomerId"; exit 1 }
    $InstallDir = Join-Path $InstallRoot $CustomerId
}

Write-Host "======== DataTrace migrate (startup seeder) ========"
Write-DtInfo "InstallDir: $InstallDir"
Write-DtInfo "Mode: app startup EnsureCreated + AddColumnIfMissing (no EF Migrate / no SQL scripts)"
if ($MigrateOnly) {
    Write-DtInfo "-MigrateOnly: restart only (same seeder path; no separate migrate host)"
}

& (Join-Path $PSScriptRoot 'stop.ps1') -InstallDir $InstallDir -Force
Start-Sleep -Seconds 1
& (Join-Path $PSScriptRoot 'start.ps1') -InstallDir $InstallDir -Replace
if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 4) { exit $LASTEXITCODE }
Start-Sleep -Seconds 2
& (Join-Path $PSScriptRoot 'verify.ps1') -InstallDir $InstallDir
exit $LASTEXITCODE