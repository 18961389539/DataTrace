#Requires -Version 7.0
<#
.SYNOPSIS
  Isolated install/upgrade/backup/rollback acceptance used by CI.
  Creates and removes only a uniquely named directory under the system temp root.
#>
param(
    [Parameter(Mandatory)][string]$PublishDir,
    [switch]$Worker,
    [string]$AcceptanceRoot = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

if (-not $Worker) {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('datatrace-acceptance-' + [Guid]::NewGuid().ToString('N'))
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $root = [IO.Path]::GetFullPath($root)
    $expectedPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar + 'datatrace-acceptance-'
    if (-not $root.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($root) -notmatch '^datatrace-acceptance-[0-9a-f]{32}$') {
        throw "Refusing unsafe acceptance root: $root"
    }

    $workerProcess = $null
    $workerExitCode = 1
    try {
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        $startInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        foreach ($argument in @('-NoProfile', '-File', $PSCommandPath, '-PublishDir', $PublishDir,
                '-Worker', '-AcceptanceRoot', $root)) {
            $startInfo.ArgumentList.Add($argument)
        }
        $workerProcess = [Diagnostics.Process]::Start($startInfo)
        $workerProcess.WaitForExit()
        $workerExitCode = $workerProcess.ExitCode
    } finally {
        if (Test-Path -LiteralPath $root) {
            $absoluteRoot = [IO.Path]::GetFullPath($root)
            if ($absoluteRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -and
                [IO.Path]::GetFileName($absoluteRoot) -match '^datatrace-acceptance-[0-9a-f]{32}$') {
                Remove-Item -LiteralPath $absoluteRoot -Recurse -Force
            } else {
                throw "Refusing unsafe cleanup target: $absoluteRoot"
            }
        }
    }
    if ($workerExitCode -ne 0) { exit $workerExitCode }
    Write-DtOk 'Isolated acceptance worker exited; verified temp root was removed.'
    exit 0
}

if ([string]::IsNullOrWhiteSpace($AcceptanceRoot)) { throw 'Worker mode requires -AcceptanceRoot.' }
$root = [IO.Path]::GetFullPath($AcceptanceRoot)
$customer = 'ci-isolated'
$installRoot = Join-Path $root 'install'
$installDir = Join-Path $installRoot $customer
$port = 0
$appStarted = $false
function Wait-ForApp([int]$Port, [int]$TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/login" -TimeoutSec 2 -MaximumRedirection 0 -SkipHttpErrorCheck
            if ($response.StatusCode -in @(200, 302)) { return }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
    throw "Isolated DataTrace app did not become ready on port $Port within $TimeoutSeconds seconds."
}

try {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $root = [IO.Path]::GetFullPath($root)
    $expectedPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar + 'datatrace-acceptance-'
    if (-not $root.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($root) -notmatch '^datatrace-acceptance-[0-9a-f]{32}$') {
        throw "Refusing unsafe acceptance root: $root"
    }
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        $candidate = Get-Random -Minimum 20000 -Maximum 60000
        if (-not (Test-PortListening -Port $candidate)) { $port = $candidate; break }
    }
    if (-not $port) { throw 'Could not find an unused local test port.' }

    & (Join-Path $PSScriptRoot 'install.ps1') -CustomerId $customer -InstallRoot $installRoot -Port $port -SiteName 'CI Isolated' -PublishDir $PublishDir -SkipPublish
    if ($LASTEXITCODE -ne 0) { throw "install.ps1 failed: $LASTEXITCODE" }
    & (Join-Path $PSScriptRoot 'start.ps1') -InstallDir $installDir
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 4) { throw "start.ps1 failed: $LASTEXITCODE" }
    $appStarted = $true
    Wait-ForApp -Port $port -TimeoutSeconds 60
    & (Join-Path $PSScriptRoot 'verify.ps1') -InstallDir $installDir
    if ($LASTEXITCODE -ne 0) { throw 'Initial verify failed.' }

    $meta = Read-CustomerMeta -InstallDir $installDir
    $dataRoot = Resolve-DtDataRoot -InstallDir $installDir -DataRoot ([string]$meta.DataRoot)
    $marker = Join-Path $dataRoot 'archive\ci-acceptance-marker.bin'
    New-Item -ItemType Directory -Path (Split-Path -Parent $marker) -Force | Out-Null
    $payload = [byte[]]::new(8MB)
    [Random]::new(7).NextBytes($payload)
    [IO.File]::WriteAllBytes($marker, $payload)
    $originalMarkerHash = (Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash

    & (Join-Path $PSScriptRoot 'upgrade.ps1') -InstallDir $installDir -PublishDir $PublishDir -SkipPublish
    if ($LASTEXITCODE -ne 0) { throw "upgrade.ps1 failed: $LASTEXITCODE" }
    if (-not (Test-Path -LiteralPath $marker)) { throw 'Upgrade removed the isolated DataRoot marker.' }
    $upgradeBackup = Get-ChildItem -LiteralPath (Join-Path $installDir 'backups') -Directory -Filter 'upgrade-*' |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $upgradeBackup) { throw 'Upgrade did not create its rollback snapshot.' }

    # Deliberately change an isolated file so RestoreData proves it returns to the snapshotted bytes.
    [IO.File]::WriteAllText((Join-Path $dataRoot 'archive\post-upgrade-mutation.txt'), 'post-upgrade mutation')
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & (Join-Path $PSScriptRoot 'backup-now.ps1') -InstallDir $installDir -StopApp
    $watch.Stop()
    if ($LASTEXITCODE -ne 0) { throw "backup-now.ps1 failed: $LASTEXITCODE" }
    Write-Host ("BACKUP_STOP_START_SECONDS={0:N3}" -f $watch.Elapsed.TotalSeconds)
    $latest = Get-ChildItem -LiteralPath (Join-Path $dataRoot 'backups') -Directory |
        Where-Object Name -NotLike '.incomplete-*' | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $latest) { throw 'No complete backup set was published.' }
    Write-Host 'BACKUP_PAYLOAD_FILES:'
    Get-ChildItem -LiteralPath $latest.FullName -File -Recurse | ForEach-Object { Write-Host ("  {0} ({1})" -f $_.FullName.Substring($latest.FullName.Length + 1), $_.Length) }
    & (Join-Path $PSScriptRoot 'verify-backup.ps1') -InstallDir $installDir -BackupSet $latest.FullName
    if ($LASTEXITCODE -ne 0) { throw 'verify-backup.ps1 rejected the isolated backup.' }

    & (Join-Path $PSScriptRoot 'stop.ps1') -InstallDir $installDir -Force
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop isolated app before rollback.' }
    $appStarted = $false
    & (Join-Path $PSScriptRoot 'rollback.ps1') -InstallDir $installDir -BackupDir $upgradeBackup.FullName -RestoreData -SkipStart
    if ($LASTEXITCODE -ne 0) { throw "rollback.ps1 failed: $LASTEXITCODE" }
    if ((Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash -cne $originalMarkerHash) {
        throw 'Rollback did not restore the original marker bytes.'
    }
    if (Test-Path -LiteralPath (Join-Path $dataRoot 'archive\post-upgrade-mutation.txt')) {
        throw 'Rollback left the post-upgrade mutation in DataRoot; RestoreData was not applied.'
    }
    & (Join-Path $PSScriptRoot 'start.ps1') -InstallDir $installDir
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 4) { throw 'Could not restart after rollback.' }
    $appStarted = $true
    Wait-ForApp -Port $port -TimeoutSeconds 60
    & (Join-Path $PSScriptRoot 'verify.ps1') -InstallDir $installDir
    if ($LASTEXITCODE -ne 0) { throw 'Post-rollback verify failed.' }
    Write-DtOk 'Isolated install, upgrade, backup verification, and data rollback acceptance passed.'
} finally {
    if ($appStarted -and (Test-Path -LiteralPath $installDir)) {
        & (Join-Path $PSScriptRoot 'stop.ps1') -InstallDir $installDir -Force | Out-Null
    }
    if ($root -and (Test-Path -LiteralPath $root)) {
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        $absoluteRoot = [IO.Path]::GetFullPath($root)
        if ($absoluteRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($absoluteRoot) -match '^datatrace-acceptance-[0-9a-f]{32}$') {
            # The parent process owns this isolated root and removes it only after
            # this worker exits, releasing the native SQLite module handle.
        } else {
            Write-DtFail "Refusing unsafe cleanup target: $absoluteRoot"
            throw "Unsafe cleanup target: $absoluteRoot"
        }
    }
}
