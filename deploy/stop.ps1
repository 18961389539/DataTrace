#Requires -Version 5.1
<#
.SYNOPSIS
  停止本安装目录对应的控制台进程（按 PID 文件），不误杀其它端口实例。
#>
param(
    [string]$InstallDir = '',
    [string]$CustomerId = '',
    [string]$InstallRoot = 'D:\Apps\DataTrace',
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

if (-not $InstallDir) {
    if (-not $CustomerId) { Write-DtFail "请指定 -InstallDir 或 -CustomerId"; exit 1 }
    $InstallDir = Join-Path $InstallRoot $CustomerId
}

$pidFile = Get-PidFilePath -InstallDir $InstallDir
if (-not (Test-Path -LiteralPath $pidFile)) {
    Write-DtWarn "无 PID 文件，可能未以 start.ps1 启动: $pidFile"
    # 尝试按 customer.json 端口匹配本目录 exe
    if (Test-Path (Get-CustomerMetaPath -InstallDir $InstallDir)) {
        $meta = Read-CustomerMeta -InstallDir $InstallDir
        $listenPid = Get-ListeningPid -Port ([int]$meta.Port)
        if ($listenPid) {
            $p = Get-Process -Id $listenPid -ErrorAction SilentlyContinue
            $exe = Get-ExePath -InstallDir $InstallDir
            if ($p -and $p.Path -and ($p.Path -ieq $exe)) {
                Write-DtInfo "按端口+路径匹配到本实例 PID=$listenPid，停止中..."
                Stop-DtProcessByPid -ProcessId $listenPid -Force:$Force | Out-Null
                Write-DtOk "已停止 PID=$listenPid"
                exit 0
            }
        }
    }
    Write-DtOk "无需停止（未发现本实例进程）"
    exit 0
}

$oldPid = [int](Get-Content -LiteralPath $pidFile | Select-Object -First 1)
$alive = Get-Process -Id $oldPid -ErrorAction SilentlyContinue
if (-not $alive) {
    Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
    Write-DtOk "进程已不存在，已清理 PID 文件"
    exit 0
}

Write-DtInfo "停止 PID=$oldPid ..."
Stop-DtProcessByPid -ProcessId $oldPid -Force:$Force | Out-Null
Start-Sleep -Milliseconds 500
Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
Write-DtOk "已停止"
exit 0
