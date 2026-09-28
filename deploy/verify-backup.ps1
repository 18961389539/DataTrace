#Requires -Version 5.1
<#
.SYNOPSIS
  Validate a full-data backup set without changing the installation.
.PARAMETER InstallDir
  Installation directory containing DataTrace.Web.exe and SQLite dependencies.
.PARAMETER BackupSet
  Full-data backup set directory to validate.
.PARAMETER Latest
  Validate the newest complete full-data backup under DataRoot\backups.
#>
param(
    [Parameter(Mandatory)][string]$InstallDir,
    [string]$BackupSet = '',
    [switch]$Latest
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')
. (Join-Path $PSScriptRoot '_backup.ps1')

try {
    $InstallDir = (Resolve-Path -LiteralPath $InstallDir).Path
    if (-not (Test-Path -LiteralPath (Join-Path $InstallDir 'DataTrace.Web.exe'))) {
        throw "Not an install dir: $InstallDir"
    }
    if ($Latest -and $BackupSet) {
        throw "Use either -Latest or -BackupSet, not both"
    }

    $meta = Read-CustomerMeta -InstallDir $InstallDir
    $configuredRoot = if ($meta.DataRoot) { [string]$meta.DataRoot } else { 'data' }
    $dataRoot = if ([IO.Path]::IsPathRooted($configuredRoot)) { $configuredRoot } else { Join-Path $InstallDir $configuredRoot }
    if ($Latest) {
        $BackupSet = Get-LatestFullBackupSet -BackupRoot (Join-Path $dataRoot 'backups')
    }
    if ([string]::IsNullOrWhiteSpace($BackupSet)) {
        throw "Specify -BackupSet or -Latest"
    }

    $verified = Assert-FullBackupSet -BackupSet $BackupSet -InstallDir $InstallDir
    Write-DtOk "Verified full-data backup: $($verified.Path)"
    Write-DtInfo "Files=$($verified.Summary.FileCount)  Databases=$($verified.Summary.DatabaseCount)  Bytes=$($verified.Summary.TotalBytes)"
    Write-Host "BACKUPSET=$($verified.Path)"
    Write-Host "FILES=$($verified.Summary.FileCount)"
    Write-Host "DATABASES=$($verified.Summary.DatabaseCount)"
    exit 0
} catch {
    Write-DtFail "Backup verification failed: $($_.Exception.Message)"
    exit 1
}
