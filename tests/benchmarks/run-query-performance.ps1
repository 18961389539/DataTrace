param(
    [string]$DotnetPath = "$env:LOCALAPPDATA\DataTrace\dotnet8\dotnet.exe",
    [int]$RowsPerMonth = 10000,
    [int]$PageSize = 500,
    [int]$DeepSkip = 10000
)

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if (-not (Test-Path -LiteralPath $DotnetPath)) { $DotnetPath = "dotnet" }
$artifactPath = Join-Path $repoRoot ("artifacts\query-benchmark-build-" + [guid]::NewGuid().ToString("N"))
$resultPath = Join-Path $repoRoot ("artifacts\query-benchmark-result-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".txt")
New-Item -ItemType Directory -Path (Split-Path -Parent $resultPath) -Force | Out-Null

Push-Location $repoRoot
try {
    & $DotnetPath build tests/DataTrace.QueryBenchmarks/DataTrace.QueryBenchmarks.csproj `
        --configuration Release `
        --artifacts-path $artifactPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $benchmarkDll = Join-Path $artifactPath "bin\DataTrace.QueryBenchmarks\release\DataTrace.QueryBenchmarks.dll"
    & $DotnetPath $benchmarkDll --rows-per-month $RowsPerMonth --page-size $PageSize --deep-skip $DeepSkip 2>&1 |
        Tee-Object -FilePath $resultPath
    $exitCode = $LASTEXITCODE
    Write-Host "Benchmark record: $resultPath"
    exit $exitCode
}
finally {
    Pop-Location
}
