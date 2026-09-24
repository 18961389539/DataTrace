# DataTrace deploy shared helpers. Dot-source from other scripts.
#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

function Get-DataTraceRepoRoot {
    param([string]$ScriptDir)
    if (-not $ScriptDir) { $ScriptDir = $PSScriptRoot }
    return (Resolve-Path (Join-Path $ScriptDir '..')).Path
}

function Get-DataTraceWebProject {
    param([string]$RepoRoot)
    return (Join-Path $RepoRoot 'src\DataTrace.Web\DataTrace.Web.csproj')
}

function Write-DtInfo([string]$Message)  { Write-Host "[INFO] $Message" -ForegroundColor Cyan }
function Write-DtOk([string]$Message)    { Write-Host "[PASS] $Message" -ForegroundColor Green }
function Write-DtWarn([string]$Message)  { Write-Host "[WARN] $Message" -ForegroundColor Yellow }
function Write-DtFail([string]$Message)  { Write-Host "[FAIL] $Message" -ForegroundColor Red }

function Test-DtElevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-CustomerMetaPath {
    param([Parameter(Mandatory)][string]$InstallDir)
    return (Join-Path $InstallDir 'customer.json')
}

function Read-CustomerMeta {
    param([Parameter(Mandatory)][string]$InstallDir)
    $path = Get-CustomerMetaPath -InstallDir $InstallDir
    if (-not (Test-Path $path)) {
        throw "customer.json not found: $path (run install.ps1 first)"
    }
    return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Write-CustomerMeta {
    param(
        [Parameter(Mandatory)][string]$InstallDir,
        [Parameter(Mandatory)][string]$CustomerId,
        [Parameter(Mandatory)][int]$Port,
        [string]$DataRoot = 'data',
        [string]$ServiceName = $null,
        [string]$SiteName = $null,
        [bool]$SimulatorAutoRun = $false,
        [string]$Environment = 'Production',
        [string]$LogoPath = $null
    )
    if (-not $ServiceName) { $ServiceName = "DataTrace-$CustomerId" }
    $metaPath = Get-CustomerMetaPath -InstallDir $InstallDir
    $existing = $null
    if (Test-Path -LiteralPath $metaPath) {
        try { $existing = Get-Content -LiteralPath $metaPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $existing = $null }
    }
    if (-not $SiteName) {
        if ($existing -and $existing.SiteName) { $SiteName = [string]$existing.SiteName }
        else { $SiteName = $CustomerId }
    }
    if (-not $LogoPath -and $existing -and $existing.LogoPath) {
        $LogoPath = [string]$existing.LogoPath
    }
    # Upgrade merge: keep SiteName/Logo if already customized; Port/DataRoot from this install.
    $obj = [ordered]@{
        CustomerId         = $CustomerId
        SiteName           = $SiteName
        Port               = $Port
        InstallPath        = $InstallDir
        DataRoot           = $DataRoot
        ServiceName        = $ServiceName
        Environment        = $Environment
        SimulatorAutoRun   = $SimulatorAutoRun
        Backup             = [ordered]@{
            Enabled          = $true
            DailyTime        = '02:30'
            BackupDirectory  = ''
            RetentionDays    = 30
            MaxBackups       = 60
        }
        UpdatedAt          = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    }
    if ($LogoPath) { $obj['LogoPath'] = $LogoPath }
    # Preserve AppVersion / soft license caps across install re-runs
    if ($existing) {
        if ($existing.PSObject.Properties['AppVersion'] -and $existing.AppVersion) {
            $obj['AppVersion'] = [string]$existing.AppVersion
        }
        if ($existing.PSObject.Properties['PreviousVersion'] -and $existing.PreviousVersion) {
            $obj['PreviousVersion'] = [string]$existing.PreviousVersion
        }
        if ($existing.PSObject.Properties['UpgradedAt'] -and $existing.UpgradedAt) {
            $obj['UpgradedAt'] = [string]$existing.UpgradedAt
        }
        if ($existing.PSObject.Properties['MaxStations'] -and $null -ne $existing.MaxStations) {
            $obj['MaxStations'] = [int]$existing.MaxStations
        }
        if ($existing.PSObject.Properties['MaxPlcs'] -and $null -ne $existing.MaxPlcs) {
            $obj['MaxPlcs'] = [int]$existing.MaxPlcs
        }
    }
    $json = $obj | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($metaPath, $json, [System.Text.UTF8Encoding]::new($false))
}

function Get-PidFilePath {
    param([Parameter(Mandatory)][string]$InstallDir)
    return (Join-Path $InstallDir '.datatrace.pid')
}

function Get-ExePath {
    param([Parameter(Mandatory)][string]$InstallDir)
    $exe = Join-Path $InstallDir 'DataTrace.Web.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Executable not found: $exe"
    }
    return $exe
}

function Write-ProductionAppsettings {
    param(
        [Parameter(Mandatory)][string]$InstallDir,
        [Parameter(Mandatory)][int]$Port,
        [string]$DataRoot = 'data',
        [string]$CustomerId = '',
        [string]$SiteName = '',
        [bool]$SimulatorAutoRun = $false,
        [string]$LogoPath = $null
    )
    $path = Join-Path $InstallDir 'appsettings.Production.json'
    $customer = [ordered]@{
        SimulatorAutoRun = $SimulatorAutoRun
    }
    if ($CustomerId) { $customer['CustomerId'] = $CustomerId }
    if ($SiteName) { $customer['SiteName'] = $SiteName }
    if ($LogoPath) { $customer['LogoPath'] = $LogoPath }
    $obj = [ordered]@{
        DataRoot = $DataRoot
        Customer = $customer
        Backup = [ordered]@{
            Enabled = $true
            DailyTime = '02:30'
            BackupDirectory = ''
            RetentionDays = 30
            MaxBackups = 60
        }
        Kestrel  = @{
            Endpoints = @{
                Http = @{
                    Url = "http://0.0.0.0:$Port"
                }
            }
        }
    }
    $json = $obj | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($path, $json, [System.Text.UTF8Encoding]::new($false))
}

function Test-PortListening {
    param([Parameter(Mandatory)][int]$Port)
    $lines = netstat -ano | Select-String -Pattern ":$Port\s+.*LISTENING"
    return [bool]$lines
}

function Get-ListeningPid {
    param([Parameter(Mandatory)][int]$Port)
    $m = netstat -ano | Select-String -Pattern ":$Port\s+.*LISTENING"
    if (-not $m) { return $null }
    $parts = ($m[0].ToString() -split '\s+') | Where-Object { $_ }
    return [int]$parts[-1]
}

function Test-HttpReachable {
    param(
        [Parameter(Mandatory)][int]$Port,
        [int]$TimeoutSec = 5
    )
    $url = "http://127.0.0.1:$Port/"
    try {
        $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -MaximumRedirection 0 -TimeoutSec $TimeoutSec -ErrorAction Stop
        return @{ Ok = $true; StatusCode = [int]$resp.StatusCode; Detail = "HTTP $($resp.StatusCode)" }
    } catch {
        $ex = $_.Exception
        if ($ex.Response -and $ex.Response.StatusCode) {
            $code = [int]$ex.Response.StatusCode
            if ($code -ge 200 -and $code -lt 400) {
                return @{ Ok = $true; StatusCode = $code; Detail = "HTTP $code (redirect/ok)" }
            }
            return @{ Ok = $false; StatusCode = $code; Detail = "HTTP $code" }
        }
        if ($_.ErrorDetails -or $ex.Message -match '302|301|307|308') {
            return @{ Ok = $true; StatusCode = 302; Detail = "HTTP redirect (login)" }
        }
        return @{ Ok = $false; StatusCode = 0; Detail = $ex.Message }
    }
}

function Stop-DtProcessByPid {
    param([int]$ProcessId, [switch]$Force)
    if ($ProcessId -le 0) { return $false }
    $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $p) { return $false }
    if ($Force) {
        Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
    } else {
        try {
            $p.CloseMainWindow() | Out-Null
            if (-not $p.WaitForExit(8000)) {
                Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
            }
        } catch {
            Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
        }
    }
    return $true
}

function Get-DtAppVersion {
    param([string]$ExePath = '')
    if ($ExePath -and (Test-Path -LiteralPath $ExePath)) {
        $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($ExePath)
        if ($vi.ProductVersion) {
            $pv = [string]$vi.ProductVersion
            $plus = $pv.IndexOf('+')
            if ($plus -gt 0) { $pv = $pv.Substring(0, $plus) }
            return $pv.Trim()
        }
        if ($vi.FileVersion) { return ([string]$vi.FileVersion).Trim() }
    }
    return $null
}

function Get-VersionMetaPath {
    param([Parameter(Mandatory)][string]$InstallDir)
    return (Join-Path $InstallDir 'version.json')
}

function Read-VersionMeta {
    param([Parameter(Mandatory)][string]$InstallDir)
    $path = Get-VersionMetaPath -InstallDir $InstallDir
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try {
        return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json)
    } catch {
        return $null
    }
}

function Write-VersionMeta {
    param(
        [Parameter(Mandatory)][string]$InstallDir,
        [Parameter(Mandatory)][string]$AppVersion,
        [string]$PreviousVersion = $null,
        [string]$Note = 'Schema applied on app startup (EnsureCreated + AddColumnIfMissing)'
    )
    $path = Get-VersionMetaPath -InstallDir $InstallDir
    $obj = [ordered]@{
        AppVersion      = $AppVersion
        PreviousVersion = $PreviousVersion
        UpgradedAt      = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        SchemaNote      = $Note
    }
    $json = $obj | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($path, $json, [System.Text.UTF8Encoding]::new($false))
}

function Update-CustomerMetaVersion {
    param(
        [Parameter(Mandatory)][string]$InstallDir,
        [Parameter(Mandatory)][string]$AppVersion,
        [string]$PreviousVersion = $null
    )
    $metaPath = Get-CustomerMetaPath -InstallDir $InstallDir
    if (-not (Test-Path -LiteralPath $metaPath)) {
        Write-DtWarn "customer.json missing; skip AppVersion merge: $metaPath"
        return
    }
    $raw = Get-Content -LiteralPath $metaPath -Raw -Encoding UTF8
    $obj = $raw | ConvertFrom-Json
    $ht = [ordered]@{}
    foreach ($p in $obj.PSObject.Properties) {
        $ht[$p.Name] = $p.Value
    }
    if ($PreviousVersion) { $ht['PreviousVersion'] = $PreviousVersion }
    $ht['AppVersion'] = $AppVersion
    $ht['UpgradedAt'] = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    $ht['UpdatedAt'] = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    $json = $ht | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($metaPath, $json, [System.Text.UTF8Encoding]::new($false))
}

function Backup-DtUpgrade {
    <#
    .SYNOPSIS
      Backup data\, customer.json, and binaries (excluding data/branding/backups) before upgrade.
    #>
    param(
        [Parameter(Mandatory)][string]$InstallDir,
        [string]$Stamp = ''
    )
    if (-not $Stamp) { $Stamp = (Get-Date).ToString('yyyyMMdd-HHmmss') }
    $backupRoot = Join-Path $InstallDir 'backups'
    $dest = Join-Path $backupRoot ("upgrade-" + $Stamp)
    New-Item -ItemType Directory -Path $dest -Force | Out-Null

    $dataSrc = Join-Path $InstallDir 'data'
    if (Test-Path -LiteralPath $dataSrc) {
        $dataDest = Join-Path $dest 'data'
        New-Item -ItemType Directory -Path $dataDest -Force | Out-Null
        & robocopy $dataSrc $dataDest /E /NFL /NDL /NJH /NJS /NC /NS | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Backup data robocopy failed: $LASTEXITCODE" }
    }

    $cust = Join-Path $InstallDir 'customer.json'
    if (Test-Path -LiteralPath $cust) {
        Copy-Item -LiteralPath $cust -Destination (Join-Path $dest 'customer.json') -Force
    }
    $ver = Join-Path $InstallDir 'version.json'
    if (Test-Path -LiteralPath $ver) {
        Copy-Item -LiteralPath $ver -Destination (Join-Path $dest 'version.json') -Force
    }
    $prod = Join-Path $InstallDir 'appsettings.Production.json'
    if (Test-Path -LiteralPath $prod) {
        Copy-Item -LiteralPath $prod -Destination (Join-Path $dest 'appsettings.Production.json') -Force
    }

    $binDest = Join-Path $dest 'binaries'
    New-Item -ItemType Directory -Path $binDest -Force | Out-Null
    $rcArgs = @(
        $InstallDir, $binDest,
        '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NC', '/NS',
        '/XD', 'data', 'branding', 'backups',
        '/XF', 'customer.json', 'appsettings.Production.json', '.datatrace.pid',
        'console.out.log', 'console.err.log', 'version.json'
    )
    & robocopy @rcArgs | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Backup binaries robocopy failed: $LASTEXITCODE" }

    $manifest = [ordered]@{
        Stamp       = $Stamp
        InstallDir  = $InstallDir
        CreatedAt   = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        AppVersion  = (Get-DtAppVersion -ExePath (Join-Path $InstallDir 'DataTrace.Web.exe'))
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $dest 'backup-manifest.json'),
        ($manifest | ConvertTo-Json -Depth 5),
        [System.Text.UTF8Encoding]::new($false))

    return $dest
}
