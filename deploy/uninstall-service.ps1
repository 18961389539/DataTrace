#Requires -Version 5.1
param(
    [Parameter(Mandatory)][string]$InstallDir
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

if (-not (Test-DtElevated)) {
    Write-DtFail "当前未以管理员身份运行，无法卸载 Windows 服务。"
    Write-DtInfo "请以管理员打开 PowerShell 后执行本脚本。"
    exit 5
}

$meta = Read-CustomerMeta -InstallDir $InstallDir
$name = [string]$meta.ServiceName
$svc = Get-Service -Name $name -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-DtOk "服务不存在，无需卸载: $name"
    exit 0
}

if ($svc.Status -ne 'Stopped') {
    Write-DtInfo "停止服务 $name ..."
    Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

Write-DtInfo "删除服务 $name ..."
sc.exe delete $name | Out-Null
Start-Sleep -Seconds 1
Write-DtOk "已卸载服务（data 目录未删除）"
exit 0
