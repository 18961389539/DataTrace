#Requires -Version 5.1
<#
.SYNOPSIS
  Install/upgrade a published DataTrace build into a per-customer directory (one customer = one deploy).
.PARAMETER CustomerId
  Customer id / folder name, e.g. ACME / CustomerA / _cfg-smoke
.PARAMETER InstallRoot
  Root folder; actual path = InstallRoot\CustomerId (default D:\Apps\DataTrace)
.PARAMETER Port
  HTTP port (default 5080)
.PARAMETER SiteName
  Factory/site display name (AppBar / login). Default = CustomerId. Preserved on upgrade if omitted.
.PARAMETER DataRootRelative
  Data directory relative to install dir, or absolute path (default data)
.PARAMETER PublishDir
  Publish output; default deploy\publish. Auto-publish if exe missing.
.PARAMETER ForceData
  DANGER: recreate data dir (backup then delete). Default preserves data.
.PARAMETER SkipPublish
  Do not auto-publish; PublishDir must already contain exe.
.EXAMPLE
  .\install.ps1 -CustomerId ACME -Port 5080 -SiteName "ACME Line 1"
  .\install.ps1 -CustomerId SiteB -Port 5082
#>
param(
    [Parameter(Mandatory)][string]$CustomerId,
    [string]$InstallRoot = 'D:\Apps\DataTrace',
    [int]$Port = 5080,
    [string]$SiteName = '',
    [string]$DataRootRelative = 'data',
    [string]$PublishDir = '',
    [switch]$ForceData,
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

if ($CustomerId -notmatch '^[A-Za-z0-9_\-]+$') {
    Write-DtFail "CustomerId must be letters/digits/_/- : $CustomerId"
    exit 1
}
if ($Port -lt 1 -or $Port -gt 65535) {
    Write-DtFail "Invalid port: $Port"
    exit 1
}

if (-not $PublishDir) {
    $PublishDir = Join-Path $PSScriptRoot 'publish'
}

$needPublish = -not (Test-Path (Join-Path $PublishDir 'DataTrace.Web.exe'))
if ($needPublish) {
    if ($SkipPublish) {
        Write-DtFail "PublishDir missing DataTrace.Web.exe: $PublishDir"
        exit 1
    }
    Write-DtInfo "Publish output missing; running publish.ps1 ..."
    & (Join-Path $PSScriptRoot 'publish.ps1') -Output $PublishDir
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$PublishDir = (Resolve-Path -LiteralPath $PublishDir).Path
$InstallDir = Join-Path $InstallRoot $CustomerId
if (-not $SiteName) { $SiteName = $CustomerId }
if (-not $DataRootRelative) { $DataRootRelative = 'data' }

$dataDir = if ([System.IO.Path]::IsPathRooted($DataRootRelative)) {
    $DataRootRelative
} else {
    Join-Path $InstallDir $DataRootRelative
}
$hadData = Test-Path -LiteralPath (Join-Path $dataDir 'config.db')

Write-DtInfo "CustomerId : $CustomerId"
Write-DtInfo "SiteName   : $SiteName"
Write-DtInfo "InstallDir : $InstallDir"
Write-DtInfo "Publish    : $PublishDir"
Write-DtInfo "HTTP Port  : $Port"
Write-DtInfo "DataRoot   : $DataRootRelative"
Write-DtInfo "Had data   : $hadData"

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

Write-DtInfo "Copying publish files (preserving customer data/config)..."
$rcArgs = @(
    $PublishDir, $InstallDir,
    '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NC', '/NS',
    '/XD', 'data', 'branding',
    '/XF', 'customer.json', 'appsettings.Production.json', '.datatrace.pid', 'console.out.log', 'console.err.log'
)
& robocopy @rcArgs | Out-Null
$rc = $LASTEXITCODE
if ($rc -ge 8) {
    Write-DtFail "robocopy failed, exit $rc"
    exit 1
}

if ($ForceData -and (Test-Path -LiteralPath $dataDir)) {
    $backup = Join-Path $InstallDir ("data-backup-" + (Get-Date).ToString('yyyyMMdd-HHmmss'))
    Write-DtWarn "ForceData: moving data to $backup then recreate"
    Move-Item -LiteralPath $dataDir -Destination $backup -Force
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
} elseif (-not (Test-Path -LiteralPath $dataDir)) {
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    Write-DtInfo "Created data dir (first start will init config.db + default users)"
} else {
    Write-DtOk "Preserved existing data directory"
}

$brandingDir = Join-Path $InstallDir 'branding'
if (-not (Test-Path -LiteralPath $brandingDir)) {
    New-Item -ItemType Directory -Path $brandingDir -Force | Out-Null
    $brandReadme = Join-Path $brandingDir 'README.txt'
    $brandLines = @(
        'Put customer logo here, e.g. logo.png',
        'Set LogoPath=logo.png in customer.json or Settings UI',
        'App serves it as /branding/logo.png'
    )
    [System.IO.File]::WriteAllLines($brandReadme, $brandLines, [System.Text.UTF8Encoding]::new($false))
    Write-DtInfo "Created branding directory"
}

Write-ProductionAppsettings -InstallDir $InstallDir -Port $Port -DataRoot $DataRootRelative `
    -CustomerId $CustomerId -SiteName $SiteName -SimulatorAutoRun:$false
Write-CustomerMeta -InstallDir $InstallDir -CustomerId $CustomerId -Port $Port -DataRoot $DataRootRelative `
    -SiteName $SiteName -SimulatorAutoRun:$false -Environment Production

$installedExe = Join-Path $InstallDir 'DataTrace.Web.exe'
$installedVer = Get-DtAppVersion -ExePath $installedExe
if ($installedVer) {
    Write-VersionMeta -InstallDir $InstallDir -AppVersion $installedVer -PreviousVersion $null
    Update-CustomerMetaVersion -InstallDir $InstallDir -AppVersion $installedVer -PreviousVersion $null
    Write-DtInfo "AppVersion=$installedVer"
}

$probe = Join-Path $dataDir '.write-probe'
try {
    [System.IO.File]::WriteAllText($probe, (Get-Date).ToString('o'))
    Remove-Item -LiteralPath $probe -Force
    Write-DtOk "data directory writable"
} catch {
    Write-DtFail "data directory not writable: $dataDir — $($_.Exception.Message)"
    exit 1
}

$exe = Get-ExePath -InstallDir $InstallDir
Write-DtOk "Install complete: $exe"
Write-Host ""
Write-Host "Next:"
Write-Host "  .\start.ps1 -InstallDir `"$InstallDir`""
Write-Host "  .\verify.ps1 -InstallDir `"$InstallDir`""
Write-Host "  Open http://127.0.0.1:$Port/"
Write-Host ""
if (-not $hadData) {
    Write-Host "Default accounts (change password after first login):"
    Write-Host "  (Admin@123 meets Identity password policy: len>=8, upper, lower, digit)"
    Write-Host "  admin / Admin@123"
    Write-Host "  engineer / Engineer@123"
    Write-Host "  operator / Operator@123"
    Write-Host "  viewer / Viewer@123"
}
Write-Host "INSTALL_DIR=$InstallDir"
exit 0