#Requires -Version 5.1
<#
.SYNOPSIS
  端到端冒烟：发布 → 安装到独立目录 → 在备用端口启动 → 自检 → 停止。
  默认客户目录 D:\Apps\DataTrace\_smoke-test，端口 5081。
  绝不触碰 5080 上已有实例（除非其恰好就是本 smoke 目录且加了 -Replace）。
#>
param(
    [string]$CustomerId = '_smoke-test',
    [string]$InstallRoot = 'D:\Apps\DataTrace',
    [int]$Port = 5081,
    [switch]$KeepRunning
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$demoPort = 5080
$demoBefore = Test-PortListening -Port $demoPort
$demoPidBefore = if ($demoBefore) { Get-ListeningPid -Port $demoPort } else { $null }

Write-Host "======== DataTrace 装得快 冒烟测试 ========"
Write-Host "目标客户 : $CustomerId"
Write-Host "目标端口 : $Port"
Write-Host "演示端口 : $demoPort 监听中=$demoBefore PID=$demoPidBefore"
Write-Host ""

if ($Port -eq $demoPort) {
    Write-DtFail "冒烟端口不能与演示端口 $demoPort 相同。"
    exit 1
}

& (Join-Path $PSScriptRoot 'check-prereq.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$publishDir = Join-Path $PSScriptRoot 'publish'
& (Join-Path $PSScriptRoot 'publish.ps1') -Output $publishDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $PSScriptRoot 'install.ps1') -CustomerId $CustomerId -InstallRoot $InstallRoot -Port $Port -PublishDir $publishDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$installDir = Join-Path $InstallRoot $CustomerId

# 若上次冒烟残留，允许对本安装 Replace
& (Join-Path $PSScriptRoot 'start.ps1') -InstallDir $installDir -Replace
if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 4) { exit $LASTEXITCODE }

Start-Sleep -Seconds 3

& (Join-Path $PSScriptRoot 'verify.ps1') -InstallDir $installDir
$verifyCode = $LASTEXITCODE

# 确认 5080 未被破坏
$demoAfter = Test-PortListening -Port $demoPort
$demoPidAfter = if ($demoAfter) { Get-ListeningPid -Port $demoPort } else { $null }

Write-Host ""
Write-Host "---- 演示实例保护检查 ----"
if ($demoBefore) {
    if ($demoAfter -and ($demoPidBefore -eq $demoPidAfter)) {
        Write-DtOk "端口 $demoPort 仍由原 PID=$demoPidBefore 监听（未触动）"
    } elseif ($demoAfter) {
        Write-DtWarn "端口 $demoPort 仍在监听，但 PID 变化: $demoPidBefore -> $demoPidAfter"
    } else {
        Write-DtFail "端口 $demoPort 在冒烟后不再监听！原 PID=$demoPidBefore"
        $verifyCode = 1
    }
} else {
    Write-DtInfo "冒烟前 $demoPort 本就未监听"
}

if (-not $KeepRunning) {
    Write-DtInfo "停止冒烟实例..."
    & (Join-Path $PSScriptRoot 'stop.ps1') -InstallDir $installDir -Force
}

Write-Host ""
if ($verifyCode -eq 0) {
    Write-DtOk "冒烟测试 PASS"
} else {
    Write-DtFail "冒烟测试 FAIL (verify=$verifyCode)"
}
exit $verifyCode
