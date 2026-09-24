#Requires -Version 5.1
<#
.SYNOPSIS
  可选：为客户端口添加入站防火墙规则（需管理员）。非管理员则跳过并提示。
#>
param(
    [Parameter(Mandatory)][string]$InstallDir
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$meta = Read-CustomerMeta -InstallDir $InstallDir
$port = [int]$meta.Port
$ruleName = "DataTrace-$($meta.CustomerId)-$port"

if (-not (Test-DtElevated)) {
    Write-DtWarn "未提升权限，跳过防火墙规则。局域网访问若被拦，请管理员执行本脚本。"
    Write-DtInfo "规则名: $ruleName  端口: $port/TCP"
    exit 0
}

$existing = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
if ($existing) {
    Write-DtOk "防火墙规则已存在: $ruleName"
    exit 0
}

New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow -Profile Any | Out-Null
Write-DtOk "已添加入站规则: $ruleName (TCP $port)"
exit 0
