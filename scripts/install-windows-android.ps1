[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

function Get-DaynoteProgramNames { @('Daynote.exe', 'Daynote.exe.config') }
function Get-DaynoteHash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

function Get-DaynoteProcesses([string]$Executable) {
    $expected = [IO.Path]::GetFullPath($Executable)
    @(Get-Process -Name Daynote -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and [string]::Equals([IO.Path]::GetFullPath($_.Path), $expected, [StringComparison]::OrdinalIgnoreCase) }
        catch { $false }
    })
}

function Initialize-DaynoteWindowProbe {
    if ('DaynoteUpgradeWindowProbe' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public sealed class DaynoteUpgradeWindow {
    public int ProcessId;
    public string Title;
    public bool Visible;
}
public static class DaynoteUpgradeWindowProbe {
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder title, int maximum);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    public static DaynoteUpgradeWindow[] Read(int[] processIds) {
        var wanted = new HashSet<int>(processIds);
        var result = new List<DaynoteUpgradeWindow>();
        EnumWindows(delegate(IntPtr window, IntPtr ignored) {
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            if (wanted.Contains((int)processId)) {
                var title = new StringBuilder(512);
                GetWindowText(window, title, title.Capacity);
                result.Add(new DaynoteUpgradeWindow { ProcessId=(int)processId, Title=title.ToString(), Visible=IsWindowVisible(window) });
            }
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }
}
'@
}

function Assert-DaynoteEditorsClosed([string]$Executable) {
    $running = @(Get-DaynoteProcesses $Executable)
    if ($running.Count -eq 0) { return }
    Initialize-DaynoteWindowProbe
    $processIds = [int[]]@($running | ForEach-Object { $_.Id })
    $editors = @([DaynoteUpgradeWindowProbe]::Read($processIds) | Where-Object {
        $_.Title -eq '添加提醒' -or $_.Title -eq '编辑提醒'
    })
    if ($editors.Count -gt 0) {
        throw '日历中仍有打开的添加或编辑窗口。请先保存或取消该编辑，再重新运行升级；本次不会退出日历。'
    }
}

function Stop-DaynoteNormally([string]$Executable) {
    if (@(Get-DaynoteProcesses $Executable).Count -eq 0) { return }
    Assert-DaynoteEditorsClosed $Executable
    Start-Process -FilePath $Executable -ArgumentList '--quit' -WindowStyle Hidden | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (@(Get-DaynoteProcesses $Executable).Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (@(Get-DaynoteProcesses $Executable).Count -gt 0) {
        throw '日历尚未正常退出，未强制结束进程。请关闭打开的提示或从托盘退出后重试。'
    }
}

function New-DaynoteStaging([string]$SourceFolder, [string]$StagingFolder) {
    # Validate the entire package before stopping the installed calendar.
    foreach ($name in Get-DaynoteProgramNames) {
        $source = Join-Path $SourceFolder $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Item -LiteralPath $source).Length -eq 0) {
            throw ('安装文件不完整：' + $name + '。请先完整解压试用包。')
        }
    }
    try {
        [xml]$configuration = [IO.File]::ReadAllText((Join-Path $SourceFolder 'Daynote.exe.config'))
        if ($null -eq $configuration.configuration) { throw 'Missing configuration element' }
    } catch { throw 'Daynote.exe.config 无效，尚未退出或更改现有日历。' }
    New-Item -ItemType Directory -Path $StagingFolder -Force | Out-Null
    $hashes = @{}
    foreach ($name in Get-DaynoteProgramNames) {
        $source = Join-Path $SourceFolder $name
        $destination = Join-Path $StagingFolder $name
        $hashes[$name] = Get-DaynoteHash $source
        Copy-Item -LiteralPath $source -Destination $destination
        if ((Get-DaynoteHash $destination) -ne $hashes[$name]) { throw ('暂存校验失败：' + $name) }
    }
    return $hashes
}

function Backup-DaynoteProgram([string]$InstallFolder, [string]$BackupFolder) {
    $original = @{}
    foreach ($name in Get-DaynoteProgramNames) {
        $path = Join-Path $InstallFolder $name
        $exists = Test-Path -LiteralPath $path -PathType Leaf
        $hash = $null
        if ($exists) {
            $hash = Get-DaynoteHash $path
            Copy-Item -LiteralPath $path -Destination (Join-Path $BackupFolder $name)
            if ((Get-DaynoteHash (Join-Path $BackupFolder $name)) -ne $hash) { throw ('旧程序备份校验失败：' + $name) }
        } elseif (Test-Path -LiteralPath $path) { throw ('安装位置存在同名目录：' + $name) }
        $original[$name] = [pscustomobject]@{ Exists=$exists; Hash=$hash }
    }
    return $original
}

function Copy-DaynoteDataSnapshot([string]$DataFolder, [string]$BackupFolder, [string]$Prefix) {
    foreach ($name in @('reminders.json', 'reminders.json.bak', 'sync-key.dpapi')) {
        $path = Join-Path $DataFolder $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $destination = Join-Path $BackupFolder ($Prefix + $name)
            Copy-Item -LiteralPath $path -Destination $destination
            # The final snapshot is taken only after the application has exited.
            if ([string]::IsNullOrEmpty($Prefix) -and (Get-DaynoteHash $path) -ne (Get-DaynoteHash $destination)) {
                throw ('退出后的日历备份校验失败：' + $name)
            }
        }
    }
}

function Set-DaynoteProgramFile([string]$Source, [string]$Destination) {
    $temporary = $Destination + '.upgrade-' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $swapBackup = $temporary + '.previous'
    try {
        Copy-Item -LiteralPath $Source -Destination $temporary
        if ((Get-DaynoteHash $Source) -ne (Get-DaynoteHash $temporary)) { throw '待替换文件校验失败。' }
        # PowerShell 5 binds a null string argument as an empty path. Use a
        # concrete short-lived swap backup; the durable original is kept separately.
        if (Test-Path -LiteralPath $Destination -PathType Leaf) { [IO.File]::Replace($temporary, $Destination, $swapBackup, $true) }
        else { [IO.File]::Move($temporary, $Destination) }
    } finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) }
        if (Test-Path -LiteralPath $swapBackup -PathType Leaf) { [IO.File]::Delete($swapBackup) }
    }
}

function Test-DaynoteOriginalProgram([string]$InstallFolder, [hashtable]$Original) {
    foreach ($name in Get-DaynoteProgramNames) {
        $path = Join-Path $InstallFolder $name
        if ($Original[$name].Exists) {
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-DaynoteHash $path) -ne $Original[$name].Hash) { return $false }
        } elseif (Test-Path -LiteralPath $path) { return $false }
    }
    return $true
}

function Restore-DaynoteProgram([string]$InstallFolder, [string]$BackupFolder, [hashtable]$Original) {
    foreach ($name in Get-DaynoteProgramNames) {
        $destination = Join-Path $InstallFolder $name
        if ($Original[$name].Exists) {
            # If a failed replacement left this file intact, do not touch it.
            if ((Test-Path -LiteralPath $destination -PathType Leaf) -and (Get-DaynoteHash $destination) -eq $Original[$name].Hash) { continue }
            $source = Join-Path $BackupFolder $name
            if ((Get-DaynoteHash $source) -ne $Original[$name].Hash) { throw ('回滚备份校验失败：' + $name) }
            Set-DaynoteProgramFile $source $destination
        } elseif (Test-Path -LiteralPath $destination -PathType Leaf) {
            # Only these two known program files can be removed on fresh-install rollback.
            [IO.File]::Delete($destination)
        }
    }
    if (-not (Test-DaynoteOriginalProgram $InstallFolder $Original)) { throw '旧程序回滚校验失败。' }
}

function Install-DaynoteProgramFiles([string]$StagingFolder, [string]$InstallFolder,
        [string]$BackupFolder, [hashtable]$Hashes, [hashtable]$Original) {
    foreach ($name in Get-DaynoteProgramNames) {
        if ((Get-DaynoteHash (Join-Path $StagingFolder $name)) -ne $Hashes[$name]) { throw ('暂存文件已改变：' + $name) }
    }
    try {
        foreach ($name in Get-DaynoteProgramNames) {
            Set-DaynoteProgramFile (Join-Path $StagingFolder $name) (Join-Path $InstallFolder $name)
            if ((Get-DaynoteHash (Join-Path $InstallFolder $name)) -ne $Hashes[$name]) { throw ('安装后校验失败：' + $name) }
        }
    } catch {
        $copyFailure = $_.Exception.Message
        try { Restore-DaynoteProgram $InstallFolder $BackupFolder $Original }
        catch { throw ('程序替换失败，且自动回滚未完成。备份：' + $BackupFolder + '。' + $_.Exception.Message) }
        throw ('程序替换未完成，旧程序已恢复。' + $copyFailure)
    }
}

function Wait-DaynoteReady([Diagnostics.Process]$Process, [string]$DataFolder, [DateTime]$StartedUtc) {
    Initialize-DaynoteWindowProbe
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $readySince = $null
    $statusPath = Join-Path $DataFolder 'desktop-status.txt'
    $expectedData = [IO.Path]::GetFullPath((Join-Path $DataFolder 'reminders.json'))
    while ([DateTime]::UtcNow -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) { return $false }
        $visibleCalendar = @([DaynoteUpgradeWindowProbe]::Read([int[]]@($Process.Id)) | Where-Object {
            $_.Visible -and $_.Title -eq '朝夕 · 日历'
        }).Count -gt 0
        $freshStatus = $false
        if (Test-Path -LiteralPath $statusPath -PathType Leaf) {
            try {
                if ((Get-Item -LiteralPath $statusPath).LastWriteTimeUtc -ge $StartedUtc) {
                    $status = @{}
                    foreach ($line in [IO.File]::ReadAllLines($statusPath)) {
                        $pair = $line -split '=', 2
                        if ($pair.Count -eq 2) { $status[$pair[0]] = $pair[1] }
                    }
                    $freshStatus = $status['processId'] -eq [string]$Process.Id -and
                        $status.ContainsKey('error') -and [string]::IsNullOrEmpty($status['error']) -and
                        $status.ContainsKey('dataFile') -and
                        [string]::Equals([IO.Path]::GetFullPath($status['dataFile']), $expectedData, [StringComparison]::OrdinalIgnoreCase)
                }
            } catch { $freshStatus = $false }
        }
        if ($visibleCalendar -or $freshStatus) {
            if ($null -eq $readySince) { $readySince = [DateTime]::UtcNow }
            if (([DateTime]::UtcNow - $readySince).TotalSeconds -ge 2) { return $true }
        } else { $readySince = $null }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Invoke-DaynoteUpgrade {
    $packageRoot = Split-Path -Parent $PSScriptRoot
    $sourceFolder = Join-Path $packageRoot 'Windows'
    if (-not (Test-Path -LiteralPath (Join-Path $sourceFolder 'Daynote.exe'))) { $sourceFolder = Join-Path $packageRoot 'build' }
    $installFolder = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\Daynote'))
    $installedExe = Join-Path $installFolder 'Daynote.exe'
    $dataFolder = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('UserProfile')) 'Daynote\Data'))
    if (Test-Path -LiteralPath $installFolder) {
        $folder = Get-Item -LiteralPath $installFolder -Force
        if (-not $folder.PSIsContainer -or ($folder.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '安装位置不是普通文件夹，已停止升级。' }
    }
    # A separate installer lock prevents two double-clicks from interleaving files.
    $lock = New-Object Threading.Mutex($false, 'Local\Daynote.AndroidUpgrade')
    $locked = $false
    try {
        try { $locked = $lock.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
        if (-not $locked) { throw '另一份朝夕升级正在进行，请等待其完成。' }
        Assert-DaynoteEditorsClosed $installedExe
        $wasRunning = @(Get-DaynoteProcesses $installedExe).Count -gt 0
        $backupFolder = Join-Path ([Environment]::GetFolderPath('UserProfile')) ('Daynote\Backups\before-android-sync-' +
            (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
        New-Item -ItemType Directory -Path $backupFolder -Force | Out-Null
        $stagingFolder = Join-Path $backupFolder 'new-program'
        $hashes = New-DaynoteStaging $sourceFolder $stagingFolder
        $original = Backup-DaynoteProgram $installFolder $backupFolder
        Copy-DaynoteDataSnapshot $dataFolder $backupFolder 'before-exit-'
        $oldStopped = $false
        $programReplaced = $false
        $newProcess = $null
        try {
            Stop-DaynoteNormally $installedExe
            $oldStopped = $true
            Copy-DaynoteDataSnapshot $dataFolder $backupFolder ''
            New-Item -ItemType Directory -Path $installFolder -Force | Out-Null
            Install-DaynoteProgramFiles $stagingFolder $installFolder $backupFolder $hashes $original
            $programReplaced = $true
            $startedUtc = [DateTime]::UtcNow
            # This is the interactive calendar requested by the user.
            $newProcess = Start-Process -FilePath $installedExe -WorkingDirectory $installFolder -PassThru
            if (-not (Wait-DaynoteReady $newProcess $dataFolder $startedUtc)) {
                throw '新版未能确认正常启动，正在恢复旧程序。'
            }
            Write-Output ('Windows 同步版已安装并确认启动。更新前备份：' + $backupFolder)
            Write-Output '在日历菜单“账号与同步”中连接自己的配对码。'
        } catch {
            $failure = $_.Exception.Message
            try {
                if ($programReplaced) {
                    Stop-DaynoteNormally $installedExe
                    Restore-DaynoteProgram $installFolder $backupFolder $original
                }
                if (-not (Test-DaynoteOriginalProgram $installFolder $original)) { throw '旧程序文件尚未完全恢复。' }
                if ($oldStopped -and $wasRunning -and $original['Daynote.exe'].Exists) {
                    $restartTime = [DateTime]::UtcNow
                    $oldProcess = Start-Process -FilePath $installedExe -WorkingDirectory $installFolder -PassThru
                    if (-not (Wait-DaynoteReady $oldProcess $dataFolder $restartTime)) { throw '旧程序已恢复，但未能确认重新启动。' }
                }
            } catch {
                throw ($failure + ' 自动恢复未完成：' + $_.Exception.Message + ' 日程文件没有被安装脚本覆盖，备份：' + $backupFolder)
            }
            throw ($failure + ' 已恢复旧程序；日程文件没有被安装脚本覆盖。备份：' + $backupFolder)
        }
    } finally {
        if ($locked) { $lock.ReleaseMutex() }
        $lock.Dispose()
    }
}

# Dot-sourcing loads only helpers, allowing isolated transaction checks.
if ($MyInvocation.InvocationName -ne '.') { Invoke-DaynoteUpgrade }
