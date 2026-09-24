#Requires -Version 5.1
<#
.SYNOPSIS
  Rollback after upgrade: restore binaries (and optionally data) from backups\upgrade-* folder.
.NOTES
  Schema patches are forward-only (AddColumnIfMissing). Prefer restoring binaries + data together
  from the same backup if a destructive migration ever exists. Today migrations are additive;
  rolling back binaries alone is usually enough if data was not corrupted.
.PARAMETER InstallDir
  Customer install directory
.PARAMETER BackupDir
  Path to backups\upgrade-yyyyMMdd-HHmmss (required unless -List)
.PARAMETER RestoreData
  Also restore data\ from backup (overwrites current data after safety rename)
.PARAMETER List
  List available upgrade backups under InstallDir\backups
.EXAMPLE
  .\rollback.ps1 -InstallDir D:\Apps\DataTrace\CustomerA -List
  .\rollback.ps1 -InstallDir D:\Apps\DataTrace\CustomerA -BackupDir D:\Apps\DataTrace\CustomerA\backups\upgrade-20260101-120000
  .\rollback.ps1 -InstallDir D:\Apps\DataTrace\CustomerA -BackupDir ... -RestoreData
#>
param(
    [string]$InstallDir = '',
    [string]$CustomerId = '',
    [string]$InstallRoot = 'D:\Apps\DataTrace',
    [string]$BackupDir = '',
    [switch]$RestoreData,
    [switch]$List,
    [switch]$SkipStart,
    [switch]$SkipVerify
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

if (-not $InstallDir) {
    if (-not $CustomerId) { Write-DtFail "Specify -InstallDir or -CustomerId"; exit 1 }
    $InstallDir = Join-Path $InstallRoot $CustomerId
}
if (-not (Test-Path -LiteralPath $InstallDir)) {
    Write-DtFail "Install dir missing: $InstallDir"
    exit 1
}

$backupsRoot = Join-Path $InstallDir 'backups'
if ($List) {
    Write-Host "======== Upgrade backups ========"
    if (-not (Test-Path -LiteralPath $backupsRoot)) {
        Write-DtWarn "No backups folder: $backupsRoot"
        exit 0
    }
    Get-ChildItem -LiteralPath $backupsRoot -Directory -Filter 'upgrade-*' |
        Sort-Object Name -Descending |
        ForEach-Object {
            $m = Join-Path $_.FullName 'backup-manifest.json'
            $ver = ''
            if (Test-Path $m) {
                try { $ver = (Get-Content $m -Raw -Encoding UTF8 | ConvertFrom-Json).AppVersion } catch {}
            }
            Write-Host ("  {0}  version={1}" -f $_.FullName, $(if ($ver) { $ver } else { '?' }))
        }
    exit 0
}

if (-not $BackupDir) {
    Write-DtFail "Specify -BackupDir (or -List). Example: .\rollback.ps1 -InstallDir ... -List"
    exit 1
}
if (-not (Test-Path -LiteralPath $BackupDir)) {
    Write-DtFail "BackupDir missing: $BackupDir"
    exit 1
}
$binSrc = Join-Path $BackupDir 'binaries'
if (-not (Test-Path -LiteralPath $binSrc)) {
    Write-DtFail "Backup missing binaries\: $binSrc"
    exit 1
}

$meta = $null
if (Test-Path (Get-CustomerMetaPath -InstallDir $InstallDir)) {
    $meta = Read-CustomerMeta -InstallDir $InstallDir
}
$port = if ($meta) { [int]$meta.Port } else { 0 }

Write-Host "======== DataTrace rollback ========"
Write-DtInfo "InstallDir : $InstallDir"
Write-DtInfo "BackupDir  : $BackupDir"
Write-DtInfo "RestoreData: $RestoreData"
Write-DtWarn "Schema is forward-only (additive columns). Restore binaries+data together if unsure."

& (Join-Path $PSScriptRoot 'stop.ps1') -InstallDir $InstallDir -Force
Start-Sleep -Seconds 1

Write-DtInfo "Restoring binaries from backup..."
$rcArgs = @(
    $binSrc, $InstallDir,
    '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NC', '/NS',
    '/XD', 'data', 'branding', 'backups',
    '/XF', 'customer.json', 'appsettings.Production.json', '.datatrace.pid',
    'console.out.log', 'console.err.log', 'version.json'
)
& robocopy @rcArgs | Out-Null
if ($LASTEXITCODE -ge 8) {
    Write-DtFail "restore binaries robocopy failed: $LASTEXITCODE"
    exit 1
}

# Restore customer.json / Production / version from backup snapshot (preserves SiteName of that era)
foreach ($name in @('customer.json', 'appsettings.Production.json', 'version.json')) {
    $src = Join-Path $BackupDir $name
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination (Join-Path $InstallDir $name) -Force
        Write-DtInfo "Restored $name"
    }
}

if ($RestoreData) {
    $dataBak = Join-Path $BackupDir 'data'
    if (-not (Test-Path -LiteralPath $dataBak)) {
        Write-DtFail "Backup has no data\: $dataBak"
        exit 1
    }
    $dataDir = Join-Path $InstallDir 'data'
    if (Test-Path -LiteralPath $dataDir) {
        $bad = Join-Path $InstallDir ("data-bad-" + (Get-Date).ToString('yyyyMMdd-HHmmss'))
        Write-DtWarn "Moving current data to $bad"
        Move-Item -LiteralPath $dataDir -Destination $bad -Force
    }
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    & robocopy $dataBak $dataDir /E /NFL /NDL /NJH /NJS /NC /NS | Out-Null
    if ($LASTEXITCODE -ge 8) {
        Write-DtFail "restore data robocopy failed: $LASTEXITCODE"
        exit 1
    }
    Write-DtOk "data\ restored from backup"
} else {
    Write-DtInfo "data\ left as-is (pass -RestoreData to restore DB from backup)"
}

$restoredVer = Get-DtAppVersion -ExePath (Join-Path $InstallDir 'DataTrace.Web.exe')
if ($restoredVer) {
    Write-VersionMeta -InstallDir $InstallDir -AppVersion $restoredVer -PreviousVersion 'rollback'
    Update-CustomerMetaVersion -InstallDir $InstallDir -AppVersion $restoredVer -PreviousVersion 'rollback'
}

if (-not $SkipStart) {
    & (Join-Path $PSScriptRoot 'start.ps1') -InstallDir $InstallDir -Replace
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 4) { exit $LASTEXITCODE }
    Start-Sleep -Seconds 2
    if (-not $SkipVerify) {
        & (Join-Path $PSScriptRoot 'verify.ps1') -InstallDir $InstallDir
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}

Write-DtOk "Rollback complete; version=$(if ($restoredVer) { $restoredVer } else { '?' })"
exit 0