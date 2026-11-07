#Requires -Version 5.1
<#
.SYNOPSIS
  桌面快捷方式入口：后端没在听端口就先拉起，再打开页面。
.PARAMETER EdgeAppId
  Edge 已安装应用的 app-id。有的话用应用窗口打开，没有就交给默认浏览器。
#>
param(
    [string]$EdgeAppId = ""
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$exe = Join-Path $repo "src\DataTrace.Web\bin\Debug\net8.0\DataTrace.Web.exe"
$url = "http://localhost:5080/"
$health = "http://127.0.0.1:5080/healthz"

function Show-LaunchError([string]$Message) {
    $shell = New-Object -ComObject WScript.Shell
    $shell.Popup($Message, 0, "DataTrace", 16) | Out-Null
}

function Test-BackendReady {
    # 工站故障时 /healthz 会回 503，服务本身已经在听。只要收到 HTTP 响应就算拉起来了。
    try {
        Invoke-WebRequest -Uri $health -UseBasicParsing -TimeoutSec 2 | Out-Null
        return $true
    }
    catch {
        return $null -ne $_.Exception.Response
    }
}

function Wait-BackendReady([System.Diagnostics.Process]$Process, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-BackendReady) {
            return $true
        }
        if ($Process -and $Process.HasExited) {
            return $false
        }
        Start-Sleep -Milliseconds 400
    }
    return (Test-BackendReady)
}

function Open-DataTraceWindow {
    $edge = Join-Path ${env:ProgramFiles(x86)} "Microsoft\Edge\Application\msedge_proxy.exe"
    if ($EdgeAppId -and (Test-Path -LiteralPath $edge)) {
        Start-Process -FilePath $edge -ArgumentList @(
            "--profile-directory=Default",
            "--app-id=$EdgeAppId",
            "--app-url=$url",
            "--app-run-on-os-login-mode=windowed"
        )
        return
    }

    Start-Process $url
}

function Start-Backend {
    if (-not (Test-Path -LiteralPath $exe)) {
        Show-LaunchError "找不到后端程序：$exe`n请先在源码目录生成一次。"
        exit 1
    }

    $running = Get-CimInstance Win32_Process -Filter "Name = 'DataTrace.Web.exe'" |
        Where-Object { $_.ExecutablePath -and ($_.ExecutablePath -eq $exe) }
    if ($running) {
        return $null
    }

    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $exe
    $start.WorkingDirectory = Split-Path -Parent $exe
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Development"
    return [System.Diagnostics.Process]::Start($start)
}

$mutex = New-Object System.Threading.Mutex($false, "Local\DataTrace.OpenDesktop")
$ownsMutex = $false
try {
    try {
        $ownsMutex = $mutex.WaitOne(0)
    }
    catch [System.Threading.AbandonedMutexException] {
        # 上次启动被强行结束时锁会留下来，这次接着用即可。
        $ownsMutex = $true
    }
    if (-not $ownsMutex) {
        if (-not (Wait-BackendReady -Process $null -Seconds 60)) {
            Show-LaunchError "另一个启动还没完成，页面暂时打不开。"
            exit 1
        }
    }
    elseif (-not (Test-BackendReady)) {
        $process = Start-Backend
        if (-not (Wait-BackendReady -Process $process -Seconds 60)) {
            $code = if ($process -and $process.HasExited) { $process.ExitCode } else { "仍在运行但端口未开" }
            Show-LaunchError "后端没有启动成功（$code）。详情在数据目录的 logs 里。"
            exit 1
        }
    }

    Open-DataTraceWindow
}
finally {
    if ($ownsMutex) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}
