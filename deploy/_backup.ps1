$script:FullBackupRoots = @('config.db', 'runtime', 'curves', 'archive', 'audit-archive', 'spool')

function Get-FullBackupManifest([string]$BackupSet) {
    $manifestPath = Join-Path $BackupSet 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "manifest.json missing: $BackupSet"
    }

    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    } catch {
        throw "manifest.json is invalid: $($_.Exception.Message)"
    }

    if ([int]$manifest.formatVersion -ne 2 -or [string]$manifest.backupType -ne 'full-data' -or $manifest.complete -ne $true) {
        throw "Unsupported or incomplete backup set; expected formatVersion=2, backupType=full-data, complete=true"
    }

    $roots = @($manifest.roots | ForEach-Object { [string]$_ } | Sort-Object)
    $expectedRoots = @($script:FullBackupRoots | Sort-Object)
    if (($roots -join '|') -cne ($expectedRoots -join '|')) {
        throw "Backup manifest does not cover the required DataRoot roots"
    }

    return $manifest
}

function Initialize-SqliteNative([string]$InstallDir) {
    if (-not ('DataTraceNativeSqlite' -as [type])) {
        $source = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class DataTraceNativeSqlite
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int OpenReadOnly = 1;
    private const int OpenReadWrite = 2;
    private const int OpenCreate = 4;
    private static IntPtr module;
    private static readonly byte[] MainDatabase = Encoding.UTF8.GetBytes("main\0");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string path);
    [DllImport("e_sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_open_v2")]
    private static extern int OpenV2(IntPtr filename, out IntPtr database, int flags, IntPtr vfs);
    [DllImport("e_sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_close_v2")]
    private static extern int CloseV2(IntPtr database);
    [DllImport("e_sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_errmsg")]
    private static extern IntPtr ErrorMessage(IntPtr database);
    [DllImport("e_sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_busy_timeout")]
    private static extern int BusyTimeout(IntPtr database, int milliseconds);
    [DllImport("e_sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_prepare_v2")]
    private static extern int PrepareV2(IntPtr database, byte[] sql, int length, out IntPtr statement, IntPtr tail);
    [DllImport("e_sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_step")]
    private static extern int Step(IntPtr statement);
    [DllImport("e_sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_text")]
    private static extern IntPtr ColumnText(IntPtr statement, int column);
    [DllImport("e_sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_finalize")]
    private static extern int Finalize(IntPtr statement);
    [DllImport("e_sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_backup_init")]
    private static extern IntPtr BackupInit(IntPtr destination, byte[] destinationName, IntPtr source, byte[] sourceName);
    [DllImport("e_sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_backup_step")]
    private static extern int BackupStep(IntPtr backup, int pages);
    [DllImport("e_sqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_backup_finish")]
    private static extern int BackupFinish(IntPtr backup);

    public static void LoadNativeLibrary(string path)
    {
        if (module == IntPtr.Zero)
        {
            module = LoadLibrary(path);
            if (module == IntPtr.Zero)
                throw new InvalidOperationException("Could not load SQLite native library: " + path);
        }
    }

    public static void QuickCheck(string path)
    {
        IntPtr database = Open(path, OpenReadOnly);
        IntPtr statement = IntPtr.Zero;
        try
        {
            byte[] sql = Encoding.UTF8.GetBytes("PRAGMA quick_check;\0");
            int result = PrepareV2(database, sql, -1, out statement, IntPtr.Zero);
            if (result != SqliteOk) throw Error(database, "Could not prepare quick_check");
            bool sawRow = false;
            while ((result = Step(statement)) == SqliteRow)
            {
                sawRow = true;
                string row = ReadUtf8(ColumnText(statement, 0));
                if (!String.Equals(row, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("SQLite quick_check failed for " + path + ": " + row);
            }
            if (result != SqliteDone || !sawRow)
                throw Error(database, "SQLite quick_check did not complete");
        }
        finally
        {
            if (statement != IntPtr.Zero) Finalize(statement);
            CloseV2(database);
        }
    }

    public static void BackupDatabase(string sourcePath, string destinationPath)
    {
        IntPtr source = Open(sourcePath, OpenReadOnly);
        IntPtr destination = IntPtr.Zero;
        IntPtr backup = IntPtr.Zero;
        try
        {
            destination = Open(destinationPath, OpenReadWrite | OpenCreate);
            BusyTimeout(source, 30000);
            BusyTimeout(destination, 30000);
            backup = BackupInit(destination, MainDatabase, source, MainDatabase);
            if (backup == IntPtr.Zero) throw Error(destination, "Could not initialize SQLite backup");

            int result;
            DateTime busyDeadline = DateTime.UtcNow.AddMinutes(1);
            do
            {
                result = BackupStep(backup, 256);
                if (result == 5 || result == 6)
                {
                    if (DateTime.UtcNow >= busyDeadline)
                        throw Error(destination, "SQLite backup remained busy for over one minute");
                    System.Threading.Thread.Sleep(50);
                }
                else if (result != SqliteOk && result != SqliteDone)
                    throw Error(destination, "SQLite backup failed");
            } while (result != SqliteDone);

            result = BackupFinish(backup);
            backup = IntPtr.Zero;
            if (result != SqliteOk) throw Error(destination, "Could not finish SQLite backup");
        }
        finally
        {
            if (backup != IntPtr.Zero) BackupFinish(backup);
            if (destination != IntPtr.Zero) CloseV2(destination);
            CloseV2(source);
        }
    }

    private static IntPtr Open(string path, int flags)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(path + "\0");
        IntPtr filename = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, filename, bytes.Length);
            IntPtr database;
            int result = OpenV2(filename, out database, flags, IntPtr.Zero);
            if (result != SqliteOk)
            {
                string message = database == IntPtr.Zero ? "SQLite open failed" : ReadUtf8(ErrorMessage(database));
                if (database != IntPtr.Zero) CloseV2(database);
                throw new InvalidOperationException("SQLite open failed for " + path + ": " + message);
            }
            return database;
        }
        finally
        {
            Marshal.FreeHGlobal(filename);
        }
    }

    private static string ReadUtf8(IntPtr value)
    {
        if (value == IntPtr.Zero) return String.Empty;
        int length = 0;
        while (Marshal.ReadByte(value, length) != 0) length++;
        byte[] bytes = new byte[length];
        Marshal.Copy(value, bytes, 0, length);
        return Encoding.UTF8.GetString(bytes);
    }

    private static Exception Error(IntPtr database, string operation)
    {
        return new InvalidOperationException(operation + ": " + ReadUtf8(ErrorMessage(database)));
    }
}
'@
        Add-Type -TypeDefinition $source -Language CSharp
    }

    $architecture = if ([IntPtr]::Size -eq 8) { 'win-x64' } else { 'win-x86' }
    $nativeDll = Join-Path $InstallDir "runtimes\$architecture\native\e_sqlite3.dll"
    if (-not (Test-Path -LiteralPath $nativeDll -PathType Leaf)) {
        throw "SQLite native library not found: $nativeDll (use the matching 64-bit or 32-bit PowerShell)"
    }
    [DataTraceNativeSqlite]::LoadNativeLibrary($nativeDll)
}

function Get-SqliteQuickCheck([string]$DatabasePath, [string]$InstallDir) {
    Initialize-SqliteNative -InstallDir $InstallDir
    [DataTraceNativeSqlite]::QuickCheck($DatabasePath)
}

function Test-FullBackupPayload([string]$PayloadRoot, $Manifest, [string]$InstallDir) {
    $payloadRoot = (Resolve-Path -LiteralPath $PayloadRoot).Path
    $listed = @{}
    $fileEntries = @($Manifest.files)
    if ($fileEntries.Count -eq 0) {
        throw "Backup manifest has no files"
    }

    $configFound = $false
    foreach ($entry in $fileEntries) {
        $relative = ([string]$entry.file).Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($relative) -or $relative.StartsWith('/') -or $relative -match '^[A-Za-z]:' -or $relative -match '(^|/)\.\.?(/|$)') {
            throw "Unsafe path in backup manifest: $relative"
        }

        $root = ($relative -split '/', 2)[0]
        if ($root -notin $script:FullBackupRoots -or ($root -eq 'config.db' -and $relative -ne 'config.db')) {
            throw "Path is outside the supported full backup roots: $relative"
        }

        $key = $relative.ToLowerInvariant()
        if ($listed.ContainsKey($key)) {
            throw "Duplicate path in backup manifest: $relative"
        }
        $listed[$key] = $true

        $fullPath = [IO.Path]::GetFullPath((Join-Path $payloadRoot ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))))
        $prefix = $payloadRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Manifest path escapes backup set: $relative"
        }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Backup file missing: $relative"
        }

        $item = Get-Item -LiteralPath $fullPath
        if ([long]$entry.sizeBytes -ne $item.Length) {
            throw "Size mismatch for $relative (manifest=$($entry.sizeBytes), actual=$($item.Length))"
        }
        $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -cne ([string]$entry.sha256).ToLowerInvariant()) {
            throw "SHA-256 mismatch for $relative"
        }
        if ([string]$entry.integrity -cne 'ok') {
            throw "Backup entry is marked invalid: $relative ($($entry.integrity))"
        }

        if ($relative -ceq 'config.db') {
            $configFound = $true
            if ([string]$entry.kind -cne 'sqlite') {
                throw "config.db must be marked as a SQLite database"
            }
        }
        if ([string]$entry.kind -ceq 'sqlite') {
            if ($relative -ne 'config.db' -and $relative -notmatch '^runtime/data_.+\.db$') {
                throw "Unexpected SQLite database path: $relative"
            }
            Get-SqliteQuickCheck -DatabasePath $fullPath -InstallDir $InstallDir
        } elseif ([string]$entry.kind -cne 'file') {
            throw "Unknown backup file kind for $relative"
        }
    }

    if (-not $configFound) {
        throw "Backup manifest does not contain required config.db"
    }

    $actualFiles = Get-ChildItem -LiteralPath $payloadRoot -File -Recurse |
        Where-Object { $_.FullName -ine (Join-Path $payloadRoot 'manifest.json') }
    if ($actualFiles.Count -ne $listed.Count) {
        throw "Backup payload has unlisted or missing files (manifest=$($listed.Count), actual=$($actualFiles.Count))"
    }
    foreach ($actual in $actualFiles) {
        $relative = (Get-PathRelativeToRoot -Root $payloadRoot -Path $actual.FullName).Replace('\', '/').ToLowerInvariant()
        if (-not $listed.ContainsKey($relative)) {
            throw "Unlisted file in backup payload: $relative"
        }
    }

    return [pscustomobject]@{
        FileCount = $fileEntries.Count
        DatabaseCount = @($fileEntries | Where-Object { [string]$_.kind -ceq 'sqlite' }).Count
        TotalBytes = [long](($fileEntries | Measure-Object -Property sizeBytes -Sum).Sum)
    }
}

function Test-LiveDataAgainstManifest([string]$DataRoot, $Manifest, [string]$InstallDir) {
    $dataRoot = (Resolve-Path -LiteralPath $DataRoot).Path
    $listed = @{}
    foreach ($entry in @($Manifest.files)) {
        $relative = ([string]$entry.file).Replace('\', '/')
        $fullPath = [IO.Path]::GetFullPath((Join-Path $dataRoot ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))))
        $prefix = $dataRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Manifest path escapes live DataRoot: $relative"
        }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Restored file missing from DataRoot: $relative"
        }

        $item = Get-Item -LiteralPath $fullPath
        if ([long]$entry.sizeBytes -ne $item.Length) {
            throw "Restored file size mismatch for $relative"
        }
        $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -cne ([string]$entry.sha256).ToLowerInvariant()) {
            throw "Restored file SHA-256 mismatch for $relative"
        }
        if ([string]$entry.integrity -cne 'ok') {
            throw "Restored manifest entry is marked invalid: $relative"
        }
        if ([string]$entry.kind -ceq 'sqlite') {
            Get-SqliteQuickCheck -DatabasePath $fullPath -InstallDir $InstallDir
        }
        $listed[$relative.ToLowerInvariant()] = $true
    }

    $actual = @{}
    $configPath = Join-Path $dataRoot 'config.db'
    if (Test-Path -LiteralPath $configPath -PathType Leaf) {
        $actual['config.db'] = $true
    }
    foreach ($root in @('runtime', 'curves', 'archive', 'audit-archive', 'spool')) {
        $rootPath = Join-Path $dataRoot $root
        if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) { continue }
        foreach ($file in (Get-ChildItem -LiteralPath $rootPath -File -Force -Recurse)) {
            $relative = (Get-PathRelativeToRoot -Root $dataRoot -Path $file.FullName).Replace('\', '/')
            if ($root -eq 'runtime' -and $relative -match '^runtime/data_.+\.db-(wal|shm)$') { continue }
            $actual[$relative.ToLowerInvariant()] = $true
        }
    }

    if ($actual.Count -ne $listed.Count) {
        throw "Restored DataRoot has an unexpected file count (manifest=$($listed.Count), actual=$($actual.Count))"
    }
    foreach ($relative in $actual.Keys) {
        if (-not $listed.ContainsKey($relative)) {
            throw "Unexpected file appeared in restored DataRoot: $relative"
        }
    }

    return [pscustomobject]@{
        FileCount = $listed.Count
        DatabaseCount = @($Manifest.files | Where-Object { [string]$_.kind -ceq 'sqlite' }).Count
    }
}

function Get-PathRelativeToRoot([string]$Root, [string]$Path) {
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $prefix = $fullRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the backup root: $Path"
    }
    return $fullPath.Substring($prefix.Length)
}

function Assert-FullBackupSet([string]$BackupSet, [string]$InstallDir) {
    $BackupSet = (Resolve-Path -LiteralPath $BackupSet).Path
    $manifest = Get-FullBackupManifest -BackupSet $BackupSet
    $summary = Test-FullBackupPayload -PayloadRoot $BackupSet -Manifest $manifest -InstallDir $InstallDir
    return [pscustomobject]@{
        Path = $BackupSet
        Manifest = $manifest
        Summary = $summary
    }
}

function Get-LatestFullBackupSet([string]$BackupRoot) {
    if (-not (Test-Path -LiteralPath $BackupRoot -PathType Container)) {
        throw "No backups folder: $BackupRoot"
    }

    foreach ($directory in (Get-ChildItem -LiteralPath $BackupRoot -Directory | Sort-Object Name -Descending)) {
        if ($directory.Name -like 'pre-restore-*' -or $directory.Name -like 'upgrade-*' -or $directory.Name -like '.incomplete-*') {
            continue
        }
        try {
            $null = Get-FullBackupManifest -BackupSet $directory.FullName
            return $directory.FullName
        } catch {
            continue
        }
    }

    throw "No complete full-data backup sets under $BackupRoot"
}
