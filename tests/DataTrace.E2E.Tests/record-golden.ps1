#Requires -Version 7.0
<# Run one mode per process. Update, review every PNG, then invoke a separate compare run. #>
param(
    [Parameter(Mandatory)][ValidateSet('update', 'compare')][string]$Mode,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$assembly = Join-Path $repo "tests\DataTrace.E2E.Tests\bin\$Configuration\net8.0\DataTrace.E2E.Tests.dll"
$app = Join-Path $repo "src\DataTrace.Web\bin\$Configuration\net8.0\DataTrace.Web.dll"
if (-not (Test-Path -LiteralPath $assembly) -or -not (Test-Path -LiteralPath $app)) {
    throw "Build first: dotnet build DataTrace.sln -c $Configuration"
}
$env:DATATRACE_APP_DLL = $app
$env:DATATRACE_E2E_BROWSER = 'chromium'
$env:DATATRACE_E2E_GOLDEN = $Mode
dotnet test (Join-Path $repo 'tests\DataTrace.E2E.Tests\DataTrace.E2E.Tests.csproj') -c $Configuration --no-build --no-restore --filter 'FullyQualifiedName~ScreenshotsMatchTheirBaseline'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
