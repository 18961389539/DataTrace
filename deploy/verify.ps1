#Requires -Version 5.1
<#
.SYNOPSIS
  Self-check an installed DataTrace instance. Does not touch other ports (e.g. demo 5080).
.PARAMETER StartIfNeeded
  If not running, call start.ps1 (pass -Replace only when you intend to).
.PARAMETER Replace
  Forwarded to start when StartIfNeeded is set.
#>
param(
    [string]$InstallDir = '',
    [string]$CustomerId = '',
    [string]$InstallRoot = 'D:\Apps\DataTrace',
    [switch]$StartIfNeeded,
    [switch]$Replace
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

if (-not $InstallDir) {
    if (-not $CustomerId) { Write-DtFail "Specify -InstallDir or -CustomerId"; exit 1 }
    $InstallDir = Join-Path $InstallRoot $CustomerId
}

$failCount = 0
function Assert-Check {
    param([bool]$Ok, [string]$Name, [string]$Detail)
    if ($Ok) {
        Write-DtOk "$Name — $Detail"
    } else {
        Write-DtFail "$Name — $Detail"
        $script:failCount++
    }
}

Write-Host "======== DataTrace verify ========"
Write-Host "InstallDir: $InstallDir"
Write-Host ""

Assert-Check (Test-Path -LiteralPath $InstallDir) "Install dir exists" $InstallDir

$exePath = Join-Path $InstallDir 'DataTrace.Web.exe'
$exeOk = Test-Path -LiteralPath $exePath
Assert-Check $exeOk "DataTrace.Web.exe" $exePath

$metaOk = Test-Path (Get-CustomerMetaPath -InstallDir $InstallDir)
Assert-Check $metaOk "customer.json" (Get-CustomerMetaPath -InstallDir $InstallDir)

$port = 0
$meta = $null
if ($metaOk) {
    $meta = Read-CustomerMeta -InstallDir $InstallDir
    $port = [int]$meta.Port
    Assert-Check ($port -gt 0) "Port configured" "$port"
}

$prodSettings = Join-Path $InstallDir 'appsettings.Production.json'
Assert-Check (Test-Path $prodSettings) "appsettings.Production.json" $prodSettings

# Peidekai: branding + readable key values
$brandingDir = Join-Path $InstallDir 'branding'
Assert-Check (Test-Path -LiteralPath $brandingDir) "branding directory" $brandingDir

# AppVersion check (升得了)
$exeForVer = Join-Path $InstallDir 'DataTrace.Web.exe'
$verFromExe = if (Test-Path $exeForVer) { Get-DtAppVersion -ExePath $exeForVer } else { $null }
$verMetaPath = Get-VersionMetaPath -InstallDir $InstallDir
$verFromFile = $null
if (Test-Path -LiteralPath $verMetaPath) {
    try { $verFromFile = [string](Read-VersionMeta -InstallDir $InstallDir).AppVersion } catch {}
}
if ($verFromExe) {
    Write-DtOk "AppVersion (exe) = $verFromExe"
} else {
    Write-DtWarn "AppVersion not readable from exe ProductVersion"
}
if ($verFromFile) {
    Write-DtOk "version.json AppVersion = $verFromFile"
} elseif ($metaOk -and $meta -and $meta.PSObject.Properties['AppVersion'] -and $meta.AppVersion) {
    Write-DtOk "customer.json AppVersion = $([string]$meta.AppVersion)"
} else {
    Write-DtWarn "version.json / customer.json AppVersion not set yet (run upgrade.ps1 or re-install)"
}

if ($metaOk -and $meta) {
    $siteNameOk = -not [string]::IsNullOrWhiteSpace([string]$meta.SiteName)
    Assert-Check $siteNameOk "SiteName" ([string]$meta.SiteName)
    $simProp = $meta.PSObject.Properties['SimulatorAutoRun']
    if ($simProp) {
        $simVal = [bool]$meta.SimulatorAutoRun
        if ($simVal) {
            Write-DtWarn "SimulatorAutoRun=true (field use should prefer false; demo customers may enable)"
        } else {
            Write-DtOk "SimulatorAutoRun=false (safe for field)"
        }
    } else {
        Write-DtWarn "customer.json missing SimulatorAutoRun (old install); re-run install.ps1"
    }
    $envName = [string]$meta.Environment
    if ($envName) {
        Assert-Check ($envName -eq 'Production') "Environment=Production" $envName
    }
}

if (Test-Path $prodSettings) {
    try {
        $prod = Get-Content -LiteralPath $prodSettings -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($prod.Customer -and ($prod.Customer.PSObject.Properties['SiteName'])) {
            Assert-Check (-not [string]::IsNullOrWhiteSpace([string]$prod.Customer.SiteName)) "Production SiteName" ([string]$prod.Customer.SiteName)
        }
        if ($prod.Customer -and ($prod.Customer.PSObject.Properties['SimulatorAutoRun'])) {
            $prodSim = [bool]$prod.Customer.SimulatorAutoRun
            if ($prodSim) { Write-DtWarn "appsettings.Production.json Customer.SimulatorAutoRun=true" }
            else { Write-DtOk "Production Customer.SimulatorAutoRun=false" }
        } else {
            Write-DtWarn "appsettings.Production.json missing Customer.SimulatorAutoRun"
        }
        $urlOk = $prod.Kestrel -and $prod.Kestrel.Endpoints -and $prod.Kestrel.Endpoints.Http -and $prod.Kestrel.Endpoints.Http.Url
        Assert-Check ([bool]$urlOk) "Kestrel bind URL readable" $(if ($urlOk) { [string]$prod.Kestrel.Endpoints.Http.Url } else { 'missing' })
        $dr = [string]$prod.DataRoot
        Assert-Check (-not [string]::IsNullOrWhiteSpace($dr)) "DataRoot readable" $dr
    } catch {
        Assert-Check $false "appsettings.Production.json parseable" $_.Exception.Message
    }
}

$dataDir = Join-Path $InstallDir 'data'
Assert-Check (Test-Path $dataDir) "data directory" $dataDir

$writable = $false
try {
    $probe = Join-Path $dataDir '.verify-write'
    [System.IO.File]::WriteAllText($probe, 'ok')
    Remove-Item $probe -Force
    $writable = $true
} catch {}
Assert-Check $writable "data writable" $dataDir

$pidFile = Get-PidFilePath -InstallDir $InstallDir
$running = $false
if (Test-Path $pidFile) {
    $p = [int](Get-Content $pidFile | Select-Object -First 1)
    $running = [bool](Get-Process -Id $p -ErrorAction SilentlyContinue)
}

if (-not $running -and $StartIfNeeded) {
    Write-DtInfo "Not running; StartIfNeeded — starting (Replace=$Replace)..."
    $startArgs = @{ InstallDir = $InstallDir }
    if ($Replace) { $startArgs['Replace'] = $true }
    & (Join-Path $PSScriptRoot 'start.ps1') @startArgs
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 4) {
        Assert-Check $false "start instance" "start.ps1 exit $LASTEXITCODE"
    } else {
        Start-Sleep -Seconds 2
        $running = $true
    }
}

if ($port -gt 0) {
    $listening = Test-PortListening -Port $port
    Assert-Check $listening "Port listening" "TCP $port"

    if ($listening) {
        $http = Test-HttpReachable -Port $port -TimeoutSec 10
        Assert-Check ([bool]$http.Ok) "HTTP reachable" "$($http.Detail)  url=http://127.0.0.1:$port/"
    } else {
        Assert-Check $false "HTTP reachable" "port not listening"
    }
}

$configDb = Join-Path $dataDir 'config.db'
if (Test-Path $configDb) {
    Assert-Check $true "config.db present" $configDb
} else {
    if ($running) {
        Write-DtWarn "config.db not yet present (may still be initializing): $configDb"
    } else {
        Write-DtWarn "config.db missing; start once to create DB and default users"
    }
}

Write-Host ""
Write-Host "======== result ========"
if ($failCount -eq 0) {
    Write-DtOk "ALL PASS"
    exit 0
} else {
    Write-DtFail "$failCount check(s) failed (FAIL)"
    exit 1
}