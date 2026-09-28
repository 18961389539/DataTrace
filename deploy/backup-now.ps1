#Requires -Version 5.1
<#
.SYNOPSIS
  Create a complete DataRoot backup set with per-file SHA-256 and SQLite checks.
.DESCRIPTION
  The in-app button uses SQLite Online Backup API while collection is running.
  Use -StopApp only for this offline helper; the set is published only after all
  required data has been copied and its manifest has been written.
#>
param(
    [Parameter(Mandatory)][string]$InstallDir,
    [switch]$StopApp
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')
. (Join-Path $PSScriptRoot '_backup.ps1')

$staging = $null
$stoppedByScript = $false
try {
    $InstallDir = (Resolve-Path -LiteralPath $InstallDir).Path
    if (-not (Test-Path -LiteralPath (Join-Path $InstallDir 'DataTrace.Web.exe'))) {
        throw "Not an install dir: $InstallDir"
    }

    $meta = Read-CustomerMeta -InstallDir $InstallDir
    $configuredRoot = if ($meta.DataRoot) { [string]$meta.DataRoot } else { 'data' }
    $dataRoot = if ([IO.Path]::IsPathRooted($configuredRoot)) { $configuredRoot } else { Join-Path $InstallDir $configuredRoot }
    $dataRoot = [IO.Path]::GetFullPath($dataRoot)
    $backupRoot = Join-Path $dataRoot 'backups'
    $configDb = Join-Path $dataRoot 'config.db'
    if (-not (Test-Path -LiteralPath $configDb -PathType Leaf)) {
        throw "Required config.db is missing: $configDb"
    }

    Initialize-SqliteNative -InstallDir $InstallDir

    if ($StopApp) {
        & (Join-Path $PSScriptRoot 'stop.ps1') -InstallDir $InstallDir
        if ($LASTEXITCODE -ne 0) { throw "stop.ps1 failed with exit code $LASTEXITCODE" }
        $stoppedByScript = $true
    }

    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    $stamp = Get-Date -Format 'yyyy-MM-dd_HHmm'
    $setDir = Join-Path $backupRoot $stamp
    $suffix = 1
    while (Test-Path -LiteralPath $setDir) {
        $setDir = Join-Path $backupRoot ($stamp + "_$suffix")
        $suffix++
    }
    $staging = Join-Path $backupRoot ('.incomplete-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    foreach ($root in $script:FullBackupRoots | Where-Object { $_ -ne 'config.db' }) {
        New-Item -ItemType Directory -Path (Join-Path $staging $root) -Force | Out-Null
    }

    $entries = New-Object 'System.Collections.Generic.List[object]'
    function Add-BackupFile([string]$Source, [string]$RelativePath, [string]$Kind) {
        $destination = Join-Path $staging ($RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar))
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        if (-not [string]::Equals([IO.Path]::GetFullPath($Source), [IO.Path]::GetFullPath($destination), [StringComparison]::OrdinalIgnoreCase)) {
            Copy-Item -LiteralPath $Source -Destination $destination -Force
        }
        $item = Get-Item -LiteralPath $destination
        $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        $entries.Add([ordered]@{
            file = $RelativePath.Replace('\', '/')
            kind = $Kind
            sizeBytes = [long]$item.Length
            sha256 = $hash
            integrity = 'ok'
        }) | Out-Null
    }
    function Backup-OneDatabase([string]$Source, [string]$RelativePath) {
        $destination = Join-Path $staging ($RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar))
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        [DataTraceNativeSqlite]::BackupDatabase($Source, $destination)
        [DataTraceNativeSqlite]::QuickCheck($destination)
        Add-BackupFile -Source $destination -RelativePath $RelativePath -Kind 'sqlite'
    }

    Backup-OneDatabase -Source $configDb -RelativePath 'config.db'
    $runtimeDir = Join-Path $dataRoot 'runtime'
    if (Test-Path -LiteralPath $runtimeDir -PathType Container) {
        foreach ($db in (Get-ChildItem -LiteralPath $runtimeDir -Filter 'data_*.db' -File | Sort-Object FullName)) {
            Backup-OneDatabase -Source $db.FullName -RelativePath ("runtime/" + $db.Name)
        }
    }

    foreach ($root in @('runtime', 'curves', 'archive', 'audit-archive', 'spool')) {
        $sourceRoot = Join-Path $dataRoot $root
        if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) { continue }
        foreach ($entry in (Get-ChildItem -LiteralPath $sourceRoot -Force -Recurse)) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "DataRoot contains an unsupported link/reparse point: $($entry.FullName)"
            }
        }
        foreach ($item in (Get-ChildItem -LiteralPath $sourceRoot -File -Force -Recurse | Sort-Object FullName)) {
            $full = [IO.Path]::GetFullPath($item.FullName)
            $backupPrefix = [IO.Path]::GetFullPath($backupRoot).TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar
            if ($full.StartsWith($backupPrefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
            $relativeWithinRoot = $full.Substring([IO.Path]::GetFullPath($sourceRoot).TrimEnd([char[]]@('\', '/')).Length).TrimStart([char[]]@('\', '/'))
            if ($root -eq 'runtime' -and $relativeWithinRoot -notmatch '[\\/]' -and $item.Name -match '^data_.+\.db(?:-wal|-shm)?$') { continue }
            $relative = ($root + '/' + $relativeWithinRoot).Replace('\', '/')
            Add-BackupFile -Source $item.FullName -RelativePath $relative -Kind 'file'
        }
    }

    $manifest = [ordered]@{
        formatVersion = 2
        backupType = 'full-data'
        complete = $true
        appVersion = ''
        createdAt = (Get-Date).ToString('o')
        finishedAt = (Get-Date).ToString('o')
        dataRoot = $dataRoot
        roots = $script:FullBackupRoots
        files = @($entries.ToArray())
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $staging 'manifest.json') -Encoding UTF8
    [IO.Directory]::Move($staging, $setDir)
    $staging = $null
    Write-DtOk "Full-data backup set: $setDir"
    Write-Host "BACKUPSET=$setDir"
    Write-Host "FILES=$($entries.Count)"
    Write-Host "DATABASES=$(@($entries | Where-Object { $_.kind -eq 'sqlite' }).Count)"
} catch {
    Write-DtFail "Full-data backup failed: $($_.Exception.Message)"
    if ($staging -and (Test-Path -LiteralPath $staging)) {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
    throw
} finally {
    if ($StopApp -and $stoppedByScript) {
        & (Join-Path $PSScriptRoot 'start.ps1') -InstallDir $InstallDir
        if ($LASTEXITCODE -ne 0) { Write-DtFail "start.ps1 failed with exit code $LASTEXITCODE" }
    }
}
