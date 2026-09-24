#Requires -Version 5.1
<#
.SYNOPSIS
  Safe in-place upgrade (升得了): stop this InstallDir only, backup, swap binaries, preserve data/branding/customer.json, restart + verify.
.PARAMETER InstallDir
  Customer install directory, e.g. D:\Apps\DataTrace\CustomerA
.PARAMETER CustomerId
  If InstallDir omitted: InstallRoot\CustomerId
.PARAMETER PublishDir
  Publish output (default deploy\publish). Rebuilds unless -SkipPublish.
.PARAMETER SkipPublish
  Use existing PublishDir without running publish.ps1
.PARAMETER SkipStart
  After binary swap, do not start (caller will start / service)
.PARAMETER SkipVerify
  Do not run verify.ps1 after start
.EXAMPLE
  .\upgrade.ps1 -InstallDir D:\Apps\DataTrace\CustomerA
  .\upgrade.ps1 -CustomerId CustomerA -SkipPublish
#>
param(
    [string]$InstallDir = '',
    [string]$CustomerId = '',
    [string]$InstallRoot = 'D:\Apps\DataTrace',
    [string]$PublishDir = '',
    [switch]$SkipPublish,
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
    Write-DtFail "Install dir missing: $InstallDir (run install.ps1 first)"
    exit 1
}

$meta = Read-CustomerMeta -InstallDir $InstallDir
$port = [int]$meta.Port
$cid = [string]$meta.CustomerId
if (-not $cid) { $cid = Split-Path $InstallDir -Leaf }

Write-Host "======== DataTrace upgrade (升得了) ========"
Write-DtInfo "InstallDir : $InstallDir"
Write-DtInfo "CustomerId : $cid"
Write-DtInfo "Port       : $port"

# Never touch other ports (e.g. demo 5080) unless this install owns that port
$demoPort = 5080
$demoBefore = Test-PortListening -Port $demoPort
$demoPidBefore = if ($demoBefore) { Get-ListeningPid -Port $demoPort } else { $null }
if ($port -ne $demoPort -and $demoBefore) {
    Write-DtInfo "Demo port $demoPort running PID=$demoPidBefore (will not touch)"
}

$oldExe = Join-Path $InstallDir 'DataTrace.Web.exe'
# Prefer recorded AppVersion (customer.json / version.json) so ops stamps win over same-binary smoke/reinstall
$prevVersion = $null
if ($meta.PSObject.Properties['AppVersion'] -and $meta.AppVersion) {
    $prevVersion = [string]$meta.AppVersion
}
if (-not $prevVersion) {
    $vm = Read-VersionMeta -InstallDir $InstallDir
    if ($vm -and $vm.AppVersion) { $prevVersion = [string]$vm.AppVersion }
}
if (-not $prevVersion) {
    $prevVersion = Get-DtAppVersion -ExePath $oldExe
}
Write-DtInfo "Previous version: $(if ($prevVersion) { $prevVersion } else { '(unknown)' })"

# 1) Stop ONLY this InstallDir instance
Write-DtInfo "Stopping target instance only..."
& (Join-Path $PSScriptRoot 'stop.ps1') -InstallDir $InstallDir -Force
if ($LASTEXITCODE -ne 0) {
    Write-DtWarn "stop.ps1 exit $LASTEXITCODE; continuing if process gone"
}
Start-Sleep -Seconds 1

# 2) Backup
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
Write-DtInfo "Backing up data + binaries to backups\upgrade-$stamp ..."
$backupDir = Backup-DtUpgrade -InstallDir $InstallDir -Stamp $stamp
Write-DtOk "Backup: $backupDir"

# 3) Publish
if (-not $PublishDir) {
    $PublishDir = Join-Path $PSScriptRoot 'publish'
}
$needPublish = -not $SkipPublish
if ($needPublish) {
    Write-DtInfo "Publishing Release build..."
    & (Join-Path $PSScriptRoot 'publish.ps1') -Output $PublishDir
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} else {
    Write-DtInfo "SkipPublish: using existing $PublishDir"
}
if (-not (Test-Path (Join-Path $PublishDir 'DataTrace.Web.exe'))) {
    Write-DtFail "PublishDir missing DataTrace.Web.exe: $PublishDir"
    exit 1
}
$PublishDir = (Resolve-Path -LiteralPath $PublishDir).Path

# 4) Copy binaries WITHOUT wiping data\ / branding\ / customer.json
Write-DtInfo "Copying binaries (preserve data/branding/customer.json)..."
$rcArgs = @(
    $PublishDir, $InstallDir,
    '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NC', '/NS',
    '/XD', 'data', 'branding', 'backups',
    '/XF', 'customer.json', 'appsettings.Production.json', '.datatrace.pid',
    'console.out.log', 'console.err.log', 'version.json'
)
& robocopy @rcArgs | Out-Null
$rc = $LASTEXITCODE
if ($rc -ge 8) {
    Write-DtFail "robocopy failed, exit $rc"
    exit 1
}
Write-DtOk "Binaries updated"

# Keep Production appsettings / SiteName etc.: do NOT rewrite customer branding unless missing
$prodPath = Join-Path $InstallDir 'appsettings.Production.json'
if (-not (Test-Path -LiteralPath $prodPath)) {
    Write-DtWarn "appsettings.Production.json missing; rewriting from customer.json"
    $site = if ($meta.SiteName) { [string]$meta.SiteName } else { $cid }
    $dataRoot = if ($meta.DataRoot) { [string]$meta.DataRoot } else { 'data' }
    $sim = $false
    if ($meta.PSObject.Properties['SimulatorAutoRun']) { $sim = [bool]$meta.SimulatorAutoRun }
    $logo = $null
    if ($meta.PSObject.Properties['LogoPath'] -and $meta.LogoPath) { $logo = [string]$meta.LogoPath }
    Write-ProductionAppsettings -InstallDir $InstallDir -Port $port -DataRoot $dataRoot `
        -CustomerId $cid -SiteName $site -SimulatorAutoRun:$sim -LogoPath $logo
}

# 5) Record versions (schema runs on next app start via DatabaseSeeder)
$newExe = Get-ExePath -InstallDir $InstallDir
$newVersion = Get-DtAppVersion -ExePath $newExe
if (-not $newVersion) { $newVersion = 'unknown' }
Write-VersionMeta -InstallDir $InstallDir -AppVersion $newVersion -PreviousVersion $prevVersion
Update-CustomerMetaVersion -InstallDir $InstallDir -AppVersion $newVersion -PreviousVersion $prevVersion
Write-DtOk "Version recorded: $prevVersion -> $newVersion"

Write-DtInfo "DB schema: app startup runs EnsureCreated + SqliteSchema.AddColumnIfMissing (same as normal boot). No separate SQL required."

# 6) Start + verify
if (-not $SkipStart) {
    Write-DtInfo "Starting upgraded instance..."
    & (Join-Path $PSScriptRoot 'start.ps1') -InstallDir $InstallDir -Replace
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 4) {
        Write-DtFail "start.ps1 failed exit $LASTEXITCODE; backup at $backupDir"
        Write-DtInfo "Rollback: .\rollback.ps1 -InstallDir `"$InstallDir`" -BackupDir `"$backupDir`""
        exit $LASTEXITCODE
    }
    Start-Sleep -Seconds 2

    if (-not $SkipVerify) {
        & (Join-Path $PSScriptRoot 'verify.ps1') -InstallDir $InstallDir
        if ($LASTEXITCODE -ne 0) {
            Write-DtFail "verify failed; backup at $backupDir"
            Write-DtInfo "Rollback: .\rollback.ps1 -InstallDir `"$InstallDir`" -BackupDir `"$backupDir`""
            exit $LASTEXITCODE
        }
    }
}

# Confirm demo untouched when this upgrade is not on 5080
$demoAfter = Test-PortListening -Port $demoPort
$demoPidAfter = if ($demoAfter) { Get-ListeningPid -Port $demoPort } else { $null }
if ($port -ne $demoPort -and $demoBefore) {
    if ($demoAfter -and ($demoPidBefore -eq $demoPidAfter)) {
        Write-DtOk "Demo port $demoPort still PID=$demoPidBefore (untouched)"
    } elseif ($demoAfter) {
        Write-DtWarn "Demo port $demoPort PID changed $demoPidBefore -> $demoPidAfter"
    } else {
        Write-DtFail "Demo port $demoPort stopped unexpectedly during upgrade"
        exit 2
    }
}

Write-Host ""
Write-DtOk "Upgrade complete: $prevVersion -> $newVersion"
Write-Host "BACKUP=$backupDir"
Write-Host "INSTALL_DIR=$InstallDir"
Write-Host "APP_VERSION=$newVersion"
exit 0