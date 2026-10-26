param([switch]$NoLaunch)

$ErrorActionPreference = 'Stop'
$sourceDirectory = Join-Path $PSScriptRoot 'build'
$programsDirectory = Join-Path $env:LOCALAPPDATA 'Programs'
$installDirectory = [IO.Path]::GetFullPath((Join-Path $programsDirectory 'Daynote'))
$executable = Join-Path $installDirectory 'Daynote.exe'
$shortcutName = '朝夕提醒.lnk'
$applicationName = '朝夕提醒'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Daynote'
$wasInstalled = Test-Path -LiteralPath $uninstallKey

function Get-InstalledProcess {
    @(Get-Process -Name Daynote -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and [string]::Equals([IO.Path]::GetFullPath($_.Path), $executable, [StringComparison]::OrdinalIgnoreCase) }
        catch { $false }
    })
}

# Validate the complete package before touching an installed version.
$binaryFiles = @('Daynote.exe', 'Daynote.exe.config', 'Daynote.ico')
foreach ($name in $binaryFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory $name) -PathType Leaf)) {
        throw "安装文件不完整：build\$name。请先运行 build.ps1。"
    }
}
foreach ($name in @('README.md', 'uninstall.ps1', 'LICENSE')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $name) -PathType Leaf)) {
        throw "安装文件不完整：$name。"
    }
}
if (Test-Path -LiteralPath $installDirectory) {
    $targetItem = Get-Item -LiteralPath $installDirectory -Force
    if (-not $targetItem.PSIsContainer -or ($targetItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw '安装位置不是普通文件夹，已停止安装。'
    }
}

if (@(Get-InstalledProcess).Count -gt 0) {
    Start-Process -FilePath $executable -ArgumentList '--quit' -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (@(Get-InstalledProcess).Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (@(Get-InstalledProcess).Count -gt 0) {
        throw '朝夕提醒尚未退出。请从托盘菜单退出后重新安装；没有强制结束进程。'
    }
}

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
foreach ($name in $binaryFiles) {
    Copy-Item -LiteralPath (Join-Path $sourceDirectory $name) -Destination (Join-Path $installDirectory $name) -Force
}
foreach ($name in @('README.md', 'uninstall.ps1', 'LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $installDirectory $name) -Force
}

$shell = New-Object -ComObject WScript.Shell
try {
    $desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) $shortcutName
    $startMenuDirectory = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'
    New-Item -ItemType Directory -Path $startMenuDirectory -Force | Out-Null
    $startMenuShortcut = Join-Path $startMenuDirectory $shortcutName
    foreach ($shortcutPath in @($desktopShortcut, $startMenuShortcut)) {
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $executable
        $shortcut.WorkingDirectory = $installDirectory
        $shortcut.IconLocation = (Join-Path $installDirectory 'Daynote.ico') + ',0'
        $shortcut.Description = '中文桌面提醒：日期、星期、时间和备注'
        $shortcut.Save()
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null
    }
} finally {
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
}

if (-not (Test-Path -LiteralPath $runKey)) { New-Item -Path $runKey | Out-Null }
if (-not $wasInstalled -or (Get-ItemProperty -LiteralPath $runKey -Name Daynote -ErrorAction SilentlyContinue)) { New-ItemProperty -Path $runKey -Name 'Daynote' -Value ('"' + $executable + '"') -PropertyType String -Force | Out-Null }
if (-not (Test-Path -LiteralPath $uninstallKey)) { New-Item -Path $uninstallKey | Out-Null }
$powerShellExecutable = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$uninstallCommand = '"' + $powerShellExecutable + '" -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $installDirectory 'uninstall.ps1') + '"'
$registryValues = @{
    DisplayName = $applicationName
    DisplayVersion = '2.2.0'
    Publisher = 'Daynote contributors'
    InstallLocation = $installDirectory
    DisplayIcon = (Join-Path $installDirectory 'Daynote.ico')
    UninstallString = $uninstallCommand
    InstallDate = (Get-Date -Format 'yyyyMMdd')
}
foreach ($entry in $registryValues.GetEnumerator()) {
    New-ItemProperty -Path $uninstallKey -Name $entry.Key -Value $entry.Value -PropertyType String -Force | Out-Null
}
New-ItemProperty -Path $uninstallKey -Name 'NoModify' -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name 'NoRepair' -Value 1 -PropertyType DWord -Force | Out-Null

if (-not $NoLaunch) { Start-Process -FilePath $executable -WorkingDirectory $installDirectory -WindowStyle Normal }
Write-Output "已安装：$installDirectory"
Write-Output '已创建桌面和开始菜单快捷方式。首次安装启用开机启动，升级保留原设置。'
Write-Output ('数据保存在：' + (Join-Path $env:LOCALAPPDATA 'Daynote\Data'))
