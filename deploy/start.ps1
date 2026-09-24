#Requires -Version 5.1
<#
.SYNOPSIS
  Start an installed DataTrace as a background console process; write PID file.
.PARAMETER InstallDir
  Customer install directory, e.g. D:\Apps\DataTrace\ACME
.PARAMETER CustomerId
  If InstallDir omitted, path = InstallRoot\CustomerId
.PARAMETER Replace
  If already running under this install, stop and restart. Default: leave running and exit 0.
.EXAMPLE
  .\start.ps1 -InstallDir D:\Apps\DataTrace\ACME
#>
param(
    [string]$InstallDir = '',
    [string]$CustomerId = '',
    [string]$InstallRoot = 'D:\Apps\DataTrace',
    [switch]$Replace
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

$meta = Read-CustomerMeta -InstallDir $InstallDir
$port = [int]$meta.Port
$exe = Get-ExePath -InstallDir $InstallDir
$pidFile = Get-PidFilePath -InstallDir $InstallDir
$dataRootEnv = 'data'
if ($meta.DataRoot) { $dataRootEnv = [string]$meta.DataRoot }

if (Test-Path -LiteralPath $pidFile) {
    $oldPid = [int](Get-Content -LiteralPath $pidFile -ErrorAction SilentlyContinue | Select-Object -First 1)
    $alive = Get-Process -Id $oldPid -ErrorAction SilentlyContinue
    if ($alive) {
        if ($Replace) {
            Write-DtWarn "Instance running (PID=$oldPid); -Replace stopping it"
            Stop-DtProcessByPid -ProcessId $oldPid -Force | Out-Null
            Start-Sleep -Seconds 1
            Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
        } else {
            Write-DtOk "Already running (PID=$oldPid) port $($meta.Port); skip start"
            exit 0
        }
    } else {
        Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
    }
}

if (Test-PortListening -Port $port) {
    $listenPid = Get-ListeningPid -Port $port
    if ($Replace) {
        Write-DtWarn "Port $port held by PID=$listenPid; -Replace stopping that process"
        Stop-DtProcessByPid -ProcessId $listenPid -Force | Out-Null
        Start-Sleep -Seconds 1
    } else {
        Write-DtFail "Port $port already in use by PID=$listenPid. Use another port install or -Replace."
        Write-DtInfo "Do not auto-kill unknown processes (may be demo on 5080)."
        exit 3
    }
}

$logOut = Join-Path $InstallDir 'console.out.log'
$logErr = Join-Path $InstallDir 'console.err.log'
$dataDir = if ([System.IO.Path]::IsPathRooted($dataRootEnv)) { $dataRootEnv } else { Join-Path $InstallDir $dataRootEnv }
New-Item -ItemType Directory -Path $dataDir -Force | Out-Null

Remove-Item -LiteralPath $logOut, $logErr -Force -ErrorAction SilentlyContinue

Write-DtInfo "Starting: $exe"
Write-DtInfo "Port: $port  Env: Production  DataRoot: $dataRootEnv"

$keys = @('ASPNETCORE_ENVIRONMENT', 'Kestrel__Endpoints__Http__Url', 'DataRoot')
$saved = @{}
foreach ($k in $keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k, 'Process') }

try {
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Production', 'Process')
    [Environment]::SetEnvironmentVariable('Kestrel__Endpoints__Http__Url', "http://0.0.0.0:$port", 'Process')
    [Environment]::SetEnvironmentVariable('DataRoot', $dataRootEnv, 'Process')

    $proc = Start-Process -FilePath $exe -WorkingDirectory $InstallDir `
        -RedirectStandardOutput $logOut -RedirectStandardError $logErr `
        -WindowStyle Hidden -PassThru
} finally {
    foreach ($k in $keys) {
        [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process')
    }
}

if (-not $proc) {
    Write-DtFail "Start-Process failed"
    exit 1
}

$newPid = $proc.Id
[System.IO.File]::WriteAllText($pidFile, "$newPid")

$deadline = (Get-Date).AddSeconds(60)
$ready = $false
while ((Get-Date) -lt $deadline) {
    $still = Get-Process -Id $newPid -ErrorAction SilentlyContinue
    if (-not $still) {
        Write-DtFail "Process exited early; see $logErr / $logOut and data\logs\"
        if (Test-Path $logErr) { Get-Content $logErr -Tail 40 | ForEach-Object { Write-Host $_ } }
        if (Test-Path $logOut) { Get-Content $logOut -Tail 40 | ForEach-Object { Write-Host $_ } }
        Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
        exit 1
    }
    if (Test-PortListening -Port $port) {
        $ready = $true
        break
    }
    Start-Sleep -Milliseconds 500
}

if (-not $ready) {
    Write-DtWarn "Process alive (PID=$newPid) but port $port not listening yet; check status/verify/logs"
    exit 4
}

Write-DtOk "Started PID=$newPid  listen http://0.0.0.0:$port/"
Write-Host "PID=$newPid"
exit 0