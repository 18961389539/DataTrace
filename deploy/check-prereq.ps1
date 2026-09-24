#Requires -Version 5.1
<#
.SYNOPSIS
  检查 DataTrace 运行前置条件（.NET 8 ASP.NET Core Runtime / SDK）。
.EXAMPLE
  .\check-prereq.ps1
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$DotNetDownloadUrl = 'https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0'
$failed = $false

Write-DtInfo "检查 .NET / ASP.NET Core 8 运行环境..."

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-DtFail "未找到 dotnet 命令。请安装 .NET 8 SDK 或 ASP.NET Core Runtime (Hosting Bundle)。"
    Write-DtInfo "下载地址: $DotNetDownloadUrl"
    Write-DtInfo "推荐：若仅运行已发布程序，安装「ASP.NET Core 8.0 Runtime - Windows Hosting Bundle」。"
    Write-DtInfo "推荐：若本机还要发布/编译，安装「.NET 8 SDK」。"
    exit 2
}

Write-DtOk "已找到 dotnet: $($dotnet.Source)"

$runtimes = & dotnet --list-runtimes 2>&1 | Out-String
$sdks = & dotnet --list-sdks 2>&1 | Out-String

$hasAspNet8 = $runtimes -match 'Microsoft\.AspNetCore\.App\s+8\.'
$hasNet8 = $runtimes -match 'Microsoft\.NETCore\.App\s+8\.'
$hasSdk8orHigher = $sdks -match '^\s*8\.|^\s*9\.|^\s*1[0-9]\.'

if ($hasAspNet8) {
    Write-DtOk "已安装 Microsoft.AspNetCore.App 8.x"
} else {
    Write-DtFail "缺少 Microsoft.AspNetCore.App 8.x（ASP.NET Core 运行时）"
    $failed = $true
}

if ($hasNet8) {
    Write-DtOk "已安装 Microsoft.NETCore.App 8.x"
} else {
    Write-DtWarn "未检测到 Microsoft.NETCore.App 8.x（通常随 ASP.NET Core / SDK 一并安装）"
}

if ($hasSdk8orHigher) {
    Write-DtOk "已安装可用于 publish 的 SDK（8+）"
} else {
    Write-DtWarn "未检测到 .NET SDK 8+。若只需运行已发布包可忽略；若要执行 publish.ps1 请安装 SDK。"
    Write-DtInfo "下载地址: $DotNetDownloadUrl"
}

Write-Host ""
Write-Host "---- 当前 runtimes ----"
Write-Host $runtimes.Trim()
Write-Host "---- 当前 sdks ----"
Write-Host $sdks.Trim()

if ($failed) {
    Write-DtFail "前置检查未通过。请安装 ASP.NET Core 8.0 Runtime / Hosting Bundle 后重试。"
    Write-DtInfo $DotNetDownloadUrl
    exit 2
}

Write-DtOk "前置检查通过。"
exit 0
