#Requires -Version 5.1
param(
    [string]$InstallDir = '',
    [string]$CustomerId = '',
    [string]$InstallRoot = 'D:\Apps\DataTrace'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

if (-not $InstallDir) {
    if (-not $CustomerId) { Write-DtFail "请指定 -InstallDir 或 -CustomerId"; exit 1 }
    $InstallDir = Join-Path $InstallRoot $CustomerId
}

if (-not (Test-Path -LiteralPath $InstallDir)) {
    Write-DtFail "安装目录不存在: $InstallDir"
    exit 1
}

$meta = $null
try { $meta = Read-CustomerMeta -InstallDir $InstallDir } catch { Write-DtWarn $_.Exception.Message }

$pidFile = Get-PidFilePath -InstallDir $InstallDir
$procPid = $null
if (Test-Path $pidFile) {
    $procPid = [int](Get-Content $pidFile | Select-Object -First 1)
}

$port = if ($meta) { [int]$meta.Port } else { 0 }
$alive = $false
if ($procPid) {
    $alive = [bool](Get-Process -Id $procPid -ErrorAction SilentlyContinue)
}

$listening = $false
$listenPid = $null
if ($port -gt 0) {
    $listening = Test-PortListening -Port $port
    $listenPid = Get-ListeningPid -Port $port
}

Write-Host "InstallDir : $InstallDir"
if ($meta) {
    Write-Host "CustomerId : $($meta.CustomerId)"
    Write-Host "Port       : $port"
    Write-Host "ServiceName: $($meta.ServiceName)"
}
Write-Host "PidFile    : $(if ($procPid) { $procPid } else { '(无)' })  alive=$alive"
Write-Host "Listening  : $listening  listenPid=$listenPid"

if ($alive -and $listening) {
    Write-DtOk "运行中"
    exit 0
} elseif ($alive -and -not $listening) {
    Write-DtWarn "进程在但端口未监听（启动中或异常）"
    exit 4
} elseif (-not $alive -and $listening) {
    Write-DtWarn "端口被其它进程占用（listenPid=$listenPid），本安装 PID 文件未指向它"
    exit 3
} else {
    Write-DtInfo "未运行"
    exit 1
}
