#Requires -Version 5.1
<#
.SYNOPSIS
  发布 DataTrace.Web（Release）到输出目录。
.PARAMETER Output
  发布输出目录，默认 <repo>\deploy\publish
.EXAMPLE
  .\publish.ps1
  .\publish.ps1 -Output D:\Temp\dt-publish
#>
param(
    [string]$Output = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$repoRoot = Get-DataTraceRepoRoot -ScriptDir $PSScriptRoot
$project = Get-DataTraceWebProject -RepoRoot $repoRoot
if (-not $Output) {
    $Output = Join-Path $PSScriptRoot 'publish'
}

Write-DtInfo "前置检查..."
& (Join-Path $PSScriptRoot 'check-prereq.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not (Test-Path -LiteralPath $project)) {
    Write-DtFail "未找到项目: $project"
    exit 1
}

New-Item -ItemType Directory -Path $Output -Force | Out-Null
$Output = (Resolve-Path -LiteralPath $Output).Path

Write-DtInfo "发布项目: $project"
Write-DtInfo "输出目录: $Output"

& dotnet publish $project -c Release -o $Output --nologo
if ($LASTEXITCODE -ne 0) {
    Write-DtFail "dotnet publish 失败（退出码 $LASTEXITCODE）。已停止，未改动客户安装目录。"
    exit $LASTEXITCODE
}

$exe = Join-Path $Output 'DataTrace.Web.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    Write-DtFail "发布完成但未找到 DataTrace.Web.exe: $exe"
    exit 1
}

Write-DtOk "发布成功: $exe"
Write-Host "OUTPUT=$Output"
exit 0
