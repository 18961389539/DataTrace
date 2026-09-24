#Requires -Version 5.1
<#
.SYNOPSIS
  将已安装实例注册为 Windows 服务（需要管理员）。失败时给出中文说明，不中断其它实例。
#>
param(
    [Parameter(Mandatory)][string]$InstallDir,
    [string]$DisplayName = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

if (-not (Test-DtElevated)) {
    Write-DtFail "当前未以管理员身份运行，无法注册 Windows 服务。"
    Write-DtInfo "请右键 PowerShell「以管理员身份运行」，然后执行:"
    Write-DtInfo "  cd `"$PSScriptRoot`""
    Write-DtInfo "  .\install-service.ps1 -InstallDir `"$InstallDir`""
    Write-DtInfo "或继续使用 .\start.ps1 以控制台方式运行（无需管理员）。"
    exit 5
}

$meta = Read-CustomerMeta -InstallDir $InstallDir
$exe = Get-ExePath -InstallDir $InstallDir
$name = [string]$meta.ServiceName
if (-not $DisplayName) { $DisplayName = "DataTrace ($($meta.CustomerId))" }

$existing = Get-Service -Name $name -ErrorAction SilentlyContinue
if ($existing) {
    Write-DtWarn "服务已存在: $name ($($existing.Status))"
    Write-DtInfo "如需重装请先 .\uninstall-service.ps1 -InstallDir `"$InstallDir`""
    exit 0
}

# 确保 Production 配置含端口
Write-ProductionAppsettings -InstallDir $InstallDir -Port ([int]$meta.Port) -DataRoot 'data'

# binPath：引号包裹；环境变量通过服务 registry Environment 写入
$binPath = "`"$exe`""
Write-DtInfo "创建服务 $name ..."
New-Service -Name $name -BinaryPathName $binPath -DisplayName $DisplayName -StartupType Automatic -Description "DataTrace 一客户一套部署 ($($meta.CustomerId))" | Out-Null

# 写入服务环境变量（ASPNETCORE_ENVIRONMENT / URL）
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$name"
$envValues = @(
    "ASPNETCORE_ENVIRONMENT=Production",
    "Kestrel__Endpoints__Http__Url=http://0.0.0.0:$($meta.Port)",
    "DataRoot=data"
)
New-ItemProperty -Path $regPath -Name Environment -PropertyType MultiString -Value $envValues -Force | Out-Null

# 服务工作目录：.NET 已用 BaseDirectory 作 ContentRoot，一般足够；仍设置 AppDirectory 习惯
Write-DtOk "服务已创建: $name"
Write-DtInfo "启动: Start-Service -Name $name"
Write-DtInfo "或: sc.exe start $name"
exit 0
