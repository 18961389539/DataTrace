#Requires -Version 5.1
<#
.SYNOPSIS
  Restore SQLite databases from an automatic backup set (or -Latest).
.DESCRIPTION
  Stops the app for this InstallDir only, copies current DBs aside to
  data\pre-restore-<stamp>\, restores *.db from the backup set, then starts and verifies.
  Does NOT touch other installs. Prefer restoring from a set produced by the in-app online backup.
.PARAMETER InstallDir
  Installation directory (contains data\, customer.json).
.PARAMETER BackupSet
  Path to a backup set folder (e.g. data\backups\2026-09-24_0230).
.PARAMETER Latest
  Use the newest timestamped folder under data\backups (excluding pre-restore-* / upgrade-*).
#>
param(
    [Parameter(Mandatory)][string]$InstallDir,
    [string]$BackupSet = '',
    [switch]$Latest
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\_common.ps1"

$InstallDir = (Resolve-Path -LiteralPath $InstallDir).Path
if (-not (Test-Path -LiteralPath (Join-Path $InstallDir "DataTrace.Web.exe"))) { Write-DtFail "Not an install dir: $InstallDir"; throw "Not an install dir: $InstallDir"; throw "bad install" }
$meta = Read-CustomerMeta -InstallDir $InstallDir
$dataRoot = if ($meta.DataRoot) { [string]$meta.DataRoot } else { 'data' }
$dataDir = if ([IO.Path]::IsPathRooted($dataRoot)) { $dataRoot } else { Join-Path $InstallDir $dataRoot }
$backupRoot = Join-Path $dataDir 'backups'

if ($Latest) {
    if (-not (Test-Path -LiteralPath $backupRoot)) {
        Write-DtFail "No backups folder: $backupRoot"; throw "No backups folder: $backupRoot"
    }
    $BackupSet = Get-ChildItem -LiteralPath $backupRoot -Directory |
        Where-Object { $_.Name -notlike 'pre-restore-*' -and $_.Name -notlike 'upgrade-*' } |
        Sort-Object Name -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $BackupSet) {
        Write-DtFail "No backup sets under $backupRoot"; throw "No backup sets under $backupRoot"
    }
}

if (-not $BackupSet) {
    Write-DtFail "Specify -BackupSet or -Latest"; throw "Specify -BackupSet or -Latest"
}
if (-not (Test-Path -LiteralPath $BackupSet)) {
    Write-DtFail "BackupSet missing: $BackupSet"; throw "BackupSet missing: $BackupSet"
}

$manifest = Join-Path $BackupSet 'manifest.json'
Write-DtInfo "InstallDir : $InstallDir"
Write-DtInfo "BackupSet  : $BackupSet"
if (Test-Path -LiteralPath $manifest) {
    Write-DtInfo "manifest.json present"
} else {
    Write-DtWarn "manifest.json missing (will still copy *.db)"
}

Write-DtInfo "Stopping app..."
& "$PSScriptRoot\stop.ps1" -InstallDir $InstallDir

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$pre = Join-Path $dataDir ("pre-restore-" + $stamp)
New-Item -ItemType Directory -Path $pre -Force | Out-Null

# Save current live DBs
$configDb = Join-Path $dataDir 'config.db'
if (Test-Path -LiteralPath $configDb) {
    Copy-Item -LiteralPath $configDb -Destination (Join-Path $pre 'config.db') -Force
    foreach ($sfx in @('-wal', '-shm')) {
        $side = $configDb + $sfx
        if (Test-Path -LiteralPath $side) {
            Copy-Item -LiteralPath $side -Destination (Join-Path $pre ("config.db" + $sfx)) -Force
        }
    }
}
$runtimeDir = Join-Path $dataDir 'runtime'
$preRuntime = Join-Path $pre 'runtime'
if (Test-Path -LiteralPath $runtimeDir) {
    New-Item -ItemType Directory -Path $preRuntime -Force | Out-Null
    Copy-Item -Path (Join-Path $runtimeDir '*') -Destination $preRuntime -Recurse -Force -ErrorAction SilentlyContinue
}
Write-DtOk "Current DBs saved to $pre"

# Restore config.db
$srcConfig = Join-Path $BackupSet 'config.db'
if (Test-Path -LiteralPath $srcConfig) {
    Copy-Item -LiteralPath $srcConfig -Destination $configDb -Force
    foreach ($sfx in @('-wal', '-shm')) {
        $side = $configDb + $sfx
        if (Test-Path -LiteralPath $side) { Remove-Item -LiteralPath $side -Force }
    }
    Write-DtOk "Restored config.db"
} else {
    Write-DtWarn "Backup set has no config.db"
}

# Restore runtime/*.db
$srcRuntime = Join-Path $BackupSet 'runtime'
if (Test-Path -LiteralPath $srcRuntime) {
    New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null
    Get-ChildItem -LiteralPath $runtimeDir -Filter 'data_*.db*' -ErrorAction SilentlyContinue | Remove-Item -Force
    Copy-Item -Path (Join-Path $srcRuntime '*') -Destination $runtimeDir -Force
    Write-DtOk "Restored runtime databases"
} else {
    # flat layout fallback
    $flat = Get-ChildItem -LiteralPath $BackupSet -Filter 'data_*.db' -ErrorAction SilentlyContinue
    if ($flat) {
        New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null
        Copy-Item -Path ($flat.FullName) -Destination $runtimeDir -Force
        Write-DtOk "Restored flat data_*.db into runtime\"
    }
}

Write-DtInfo "Starting app..."
& "$PSScriptRoot\start.ps1" -InstallDir $InstallDir
Start-Sleep -Seconds 3
& "$PSScriptRoot\verify.ps1" -InstallDir $InstallDir
Write-DtOk "Restore finished. Pre-restore copy kept at: $pre"
Write-Host "BACKUPSET=$BackupSet"
Write-Host "PRE_RESTORE=$pre"
