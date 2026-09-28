#Requires -Version 5.1
<#
.SYNOPSIS
  Validate and restore a complete DataRoot backup set.
.DESCRIPTION
  The selected set is fully checked before the app is stopped. A separate full-data
  snapshot of the live DataRoot is then created and verified for rollback. Data is
  staged on the target volume before switching; failures attempt to restore the snapshot.
#>
param(
    [Parameter(Mandatory)][string]$InstallDir,
    [string]$BackupSet = '',
    [switch]$Latest
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')
. (Join-Path $PSScriptRoot '_backup.ps1')

function New-ValidatedStage([string]$SourceSet, $Manifest, [string]$DataRoot, [string]$InstallPath) {
    $stage = Join-Path $DataRoot ('.restore-staging-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    try {
        Copy-Item -LiteralPath (Join-Path $SourceSet 'manifest.json') -Destination (Join-Path $stage 'manifest.json')
        foreach ($root in $script:FullBackupRoots | Where-Object { $_ -ne 'config.db' }) {
            New-Item -ItemType Directory -Path (Join-Path $stage $root) -Force | Out-Null
        }
        foreach ($entry in @($Manifest.files)) {
            $relative = ([string]$entry.file).Replace('/', [IO.Path]::DirectorySeparatorChar)
            $source = Join-Path $SourceSet $relative
            $destination = Join-Path $stage $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination
        }
        $null = Test-FullBackupPayload -PayloadRoot $stage -Manifest $Manifest -InstallDir $InstallPath
        return $stage
    } catch {
        if (Test-Path -LiteralPath $stage) {
            Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

function Set-LiveDataFromStage([string]$Stage, [string]$DataRoot) {
    $configDb = Join-Path $DataRoot 'config.db'
    foreach ($path in @($configDb, "$configDb-wal", "$configDb-shm")) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    foreach ($root in @('runtime', 'curves', 'archive', 'audit-archive', 'spool')) {
        $target = Join-Path $DataRoot $root
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Recurse -Force
        }
    }

    [IO.File]::Move((Join-Path $Stage 'config.db'), $configDb)
    foreach ($root in @('runtime', 'curves', 'archive', 'audit-archive', 'spool')) {
        [IO.Directory]::Move((Join-Path $Stage $root), (Join-Path $DataRoot $root))
    }
}

function Start-AndVerify([string]$InstallPath) {
    & (Join-Path $PSScriptRoot 'start.ps1') -InstallDir $InstallPath
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 4) {
        throw "start.ps1 failed with exit code $LASTEXITCODE"
    }
    & (Join-Path $PSScriptRoot 'verify.ps1') -InstallDir $InstallPath
    if ($LASTEXITCODE -ne 0) {
        throw "verify.ps1 failed with exit code $LASTEXITCODE"
    }
}

$stage = $null
$preRestore = $null
$appStopped = $false
$dataSwitched = $false
try {
    $InstallDir = (Resolve-Path -LiteralPath $InstallDir).Path
    if (-not (Test-Path -LiteralPath (Join-Path $InstallDir 'DataTrace.Web.exe'))) {
        throw "Not an install dir: $InstallDir"
    }
    if ($Latest -and $BackupSet) { throw "Use either -Latest or -BackupSet, not both" }

    $meta = Read-CustomerMeta -InstallDir $InstallDir
    $configuredRoot = if ($meta.DataRoot) { [string]$meta.DataRoot } else { 'data' }
    $dataRoot = if ([IO.Path]::IsPathRooted($configuredRoot)) { $configuredRoot } else { Join-Path $InstallDir $configuredRoot }
    $dataRoot = [IO.Path]::GetFullPath($dataRoot)
    $backupRoot = Join-Path $dataRoot 'backups'
    if ($Latest) {
        $BackupSet = Get-LatestFullBackupSet -BackupRoot $backupRoot
    }
    if ([string]::IsNullOrWhiteSpace($BackupSet)) { throw "Specify -BackupSet or -Latest" }

    # Refuse legacy DB-only backup sets and validate every byte before stopping production.
    $selected = Assert-FullBackupSet -BackupSet $BackupSet -InstallDir $InstallDir
    $BackupSet = $selected.Path
    Write-DtInfo "InstallDir : $InstallDir"
    Write-DtInfo "DataRoot   : $dataRoot"
    Write-DtInfo "BackupSet  : $BackupSet"
    Write-DtOk "Preflight passed: $($selected.Summary.DatabaseCount) databases, $($selected.Summary.FileCount) files, SHA-256 and SQLite checks passed"

    $pidFile = Get-PidFilePath -InstallDir $InstallDir
    $instancePid = $null
    if (Test-Path -LiteralPath $pidFile) {
        $runningPid = [int](Get-Content -LiteralPath $pidFile | Select-Object -First 1)
        $runningProcess = Get-Process -Id $runningPid -ErrorAction SilentlyContinue
        $expectedExe = [IO.Path]::GetFullPath((Join-Path $InstallDir 'DataTrace.Web.exe'))
        if ($runningProcess) {
            if (-not $runningProcess.Path) {
                throw "Cannot verify executable identity for PID=$runningPid; refusing to stop it"
            }
            if (-not [string]::Equals([IO.Path]::GetFullPath($runningProcess.Path), $expectedExe, [StringComparison]::OrdinalIgnoreCase)) {
                throw "PID file points to a different executable (PID=$runningPid); refusing to stop it"
            }
            $instancePid = $runningPid
        }
    }

    Write-DtInfo "Stopping app..."
    & (Join-Path $PSScriptRoot 'stop.ps1') -InstallDir $InstallDir
    if ($LASTEXITCODE -ne 0) { throw "stop.ps1 failed with exit code $LASTEXITCODE" }
    $appStopped = $true
    if ($instancePid) {
        $stopDeadline = (Get-Date).AddSeconds(15)
        while ((Get-Process -Id $instancePid -ErrorAction SilentlyContinue) -and (Get-Date) -lt $stopDeadline) {
            Start-Sleep -Milliseconds 250
        }
        if (Get-Process -Id $instancePid -ErrorAction SilentlyContinue) {
            throw "Application PID=$instancePid did not stop; no live data has been changed"
        }
    }
    $port = [int]$meta.Port
    if ($port -gt 0 -and (Get-ListeningPid -Port $port)) {
        throw "Port $port is still listening after stop.ps1; no live data has been changed"
    }

    # Reuse the same full-data writer for a verified rollback point.
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    $existingSets = @{}
    foreach ($directory in (Get-ChildItem -LiteralPath $backupRoot -Directory)) {
        $existingSets[$directory.FullName.ToLowerInvariant()] = $true
    }
    $null = & (Join-Path $PSScriptRoot 'backup-now.ps1') -InstallDir $InstallDir
    if ($LASTEXITCODE -ne 0) { throw "Could not create pre-restore full-data snapshot" }
    $createdSets = @(Get-ChildItem -LiteralPath $backupRoot -Directory |
        Where-Object {
            -not $existingSets.ContainsKey($_.FullName.ToLowerInvariant()) -and
            (Test-Path -LiteralPath (Join-Path $_.FullName 'manifest.json') -PathType Leaf)
        })
    if ($createdSets.Count -ne 1) { throw "Could not identify the newly generated rollback snapshot" }
    $createdSet = $createdSets[0].FullName
    $preRestore = Join-Path $backupRoot ("pre-restore-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $suffix = 1
    $basePreRestore = $preRestore
    while (Test-Path -LiteralPath $preRestore) {
        $preRestore = $basePreRestore + "-$suffix"
        $suffix++
    }
    [IO.Directory]::Move($createdSet, $preRestore)
    $preSnapshot = Assert-FullBackupSet -BackupSet $preRestore -InstallDir $InstallDir
    Write-DtOk "Verified rollback snapshot: $preRestore"

    $stage = New-ValidatedStage -SourceSet $BackupSet -Manifest $selected.Manifest -DataRoot $dataRoot -InstallPath $InstallDir
    $dataSwitched = $true
    Set-LiveDataFromStage -Stage $stage -DataRoot $dataRoot
    $liveSummary = Test-LiveDataAgainstManifest -DataRoot $dataRoot -Manifest $selected.Manifest -InstallDir $InstallDir
    Write-DtOk "Restored DataRoot verified: $($liveSummary.DatabaseCount) databases, $($liveSummary.FileCount) files"
    Remove-Item -LiteralPath $stage -Recurse -Force
    $stage = $null

    Write-DtInfo "Starting app and verifying restored data..."
    Start-AndVerify -InstallPath $InstallDir
    $appStopped = $false
    Write-DtOk "Restore finished. Full-data rollback snapshot kept at: $preRestore"
    Write-Host "BACKUPSET=$BackupSet"
    Write-Host "PRE_RESTORE=$preRestore"
} catch {
    $restoreError = $_.Exception.Message
    if ($stage -and (Test-Path -LiteralPath $stage)) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($dataSwitched -and $preRestore -and (Test-Path -LiteralPath $preRestore)) {
        try {
            Write-DtWarn "Restore failed; stopping app and rolling back to the verified pre-restore snapshot..."
            & (Join-Path $PSScriptRoot 'stop.ps1') -InstallDir $InstallDir
            $rollbackManifest = Get-FullBackupManifest -BackupSet $preRestore
            $rollbackStage = New-ValidatedStage -SourceSet $preRestore -Manifest $rollbackManifest -DataRoot $dataRoot -InstallPath $InstallDir
            Set-LiveDataFromStage -Stage $rollbackStage -DataRoot $dataRoot
            $null = Test-LiveDataAgainstManifest -DataRoot $dataRoot -Manifest $rollbackManifest -InstallDir $InstallDir
            Remove-Item -LiteralPath $rollbackStage -Recurse -Force
            Start-AndVerify -InstallPath $InstallDir
            $appStopped = $false
            Write-DtOk "Rollback verified; original data restored"
        } catch {
            Write-DtFail "Automatic rollback failed: $($_.Exception.Message). Keep the pre-restore snapshot at $preRestore"
        }
    } elseif ($appStopped) {
        try {
            Start-AndVerify -InstallPath $InstallDir
            $appStopped = $false
        } catch {
            Write-DtFail "Could not restart the unchanged original data: $($_.Exception.Message)"
        }
    }
    Write-DtFail "Restore failed: $restoreError"
    exit 1
}
