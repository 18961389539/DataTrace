#Requires -Version 5.1
<#
.SYNOPSIS
  Offline helper: create a timestamped SQLite backup set using the Microsoft.Data.Sqlite Backup API.
.DESCRIPTION
  Prefer the in-app 「立即备份」 button (Settings, Administrator) while the service is running —
  that path uses the same online Backup API without stopping collection.

  This script is for maintenance windows / smoke tests when you can stop the app briefly,
  or when you want a copy without going through the UI. It opens each DB and calls BackupDatabase
  (NOT a raw file copy of a live WAL database).
#>
param(
    [Parameter(Mandatory)][string]$InstallDir,
    [switch]$StopApp
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\_common.ps1"

$InstallDir = (Resolve-Path -LiteralPath $InstallDir).Path
if (-not (Test-Path -LiteralPath (Join-Path $InstallDir "DataTrace.Web.exe"))) { Write-DtFail "Not an install dir: $InstallDir"; throw "Not an install dir: $InstallDir"; throw "bad install" }
$meta = Read-CustomerMeta -InstallDir $InstallDir
$dataRoot = if ($meta.DataRoot) { [string]$meta.DataRoot } else { 'data' }
$dataDir = if ([IO.Path]::IsPathRooted($dataRoot)) { $dataRoot } else { Join-Path $InstallDir $dataRoot }
$backupRoot = Join-Path $dataDir 'backups'
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null

$sqliteDll = Get-ChildItem -Path $InstallDir -Filter 'Microsoft.Data.Sqlite.dll' -Recurse -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $sqliteDll) {
    Write-DtFail "Microsoft.Data.Sqlite.dll not found under $InstallDir (publish/install first). Or use Settings → 立即备份 while the app is running."; throw "Microsoft.Data.Sqlite.dll not found under $InstallDir (publish/install first). Or use Settings → 立即备份 while the app is running."
}

if ($StopApp) {
    & "$PSScriptRoot\stop.ps1" -InstallDir $InstallDir
}

Add-Type -Path $sqliteDll
$stamp = Get-Date -Format 'yyyy-MM-dd_HHmm'
$setDir = Join-Path $backupRoot $stamp
$n = 1
while (Test-Path -LiteralPath $setDir) {
    $setDir = Join-Path $backupRoot ($stamp + "_$n")
    $n++
}
New-Item -ItemType Directory -Path $setDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $setDir 'runtime') -Force | Out-Null

function Backup-OneDb([string]$src, [string]$dest) {
    $csSrc = "Data Source=$src"
    $csDst = "Data Source=$dest"
    $source = New-Object Microsoft.Data.Sqlite.SqliteConnection $csSrc
    $destConn = New-Object Microsoft.Data.Sqlite.SqliteConnection $csDst
    try {
        $source.Open()
        $destConn.Open()
        $source.BackupDatabase($destConn)
    }
    finally {
        if ($source.State -ne 'Closed') { $source.Close() }
        if ($destConn.State -ne 'Closed') { $destConn.Close() }
        $source.Dispose(); $destConn.Dispose()
    }
}

$files = @()
$configDb = Join-Path $dataDir 'config.db'
if (Test-Path -LiteralPath $configDb) {
    $dest = Join-Path $setDir 'config.db'
    Backup-OneDb -src $configDb -dest $dest
    $files += 'config.db'
    Write-DtOk "Backed up config.db"
}

$runtimeDir = Join-Path $dataDir 'runtime'
if (Test-Path -LiteralPath $runtimeDir) {
    Get-ChildItem -LiteralPath $runtimeDir -Filter 'data_*.db' | ForEach-Object {
        $dest = Join-Path (Join-Path $setDir 'runtime') $_.Name
        Backup-OneDb -src $_.FullName -dest $dest
        $files += ("runtime/" + $_.Name)
        Write-DtOk "Backed up $($_.Name)"
    }
}

$manifest = [ordered]@{
    createdAt = (Get-Date).ToString('o')
    source    = 'deploy/backup-now.ps1'
    files     = $files
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $setDir 'manifest.json') -Encoding UTF8
Write-DtOk "Backup set: $setDir"
Write-Host "BACKUPSET=$setDir"

if ($StopApp) {
    & "$PSScriptRoot\start.ps1" -InstallDir $InstallDir
}
