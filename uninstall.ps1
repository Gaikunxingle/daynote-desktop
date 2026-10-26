$ErrorActionPreference = 'Stop'
$programsDirectory = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
$installDirectory = [IO.Path]::GetFullPath((Join-Path $programsDirectory 'Daynote'))
$executable = Join-Path $installDirectory 'Daynote.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Daynote'

function Get-InstalledProcess {
    @(Get-Process -Name Daynote -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and [string]::Equals([IO.Path]::GetFullPath($_.Path), $executable, [StringComparison]::OrdinalIgnoreCase) }
        catch { $false }
    })
}

# The only recursive removal target is this exact per-user application directory.
if (-not [string]::Equals([IO.Path]::GetDirectoryName($installDirectory), $programsDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($installDirectory) -ne 'Daynote') {
    throw '卸载路径检查失败，未删除任何文件。'
}
if (Test-Path -LiteralPath $installDirectory) {
    $targetItem = Get-Item -LiteralPath $installDirectory -Force
    if (-not $targetItem.PSIsContainer -or ($targetItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw '安装目录不是普通文件夹，未删除任何文件。'
    }
    if (-not [string]::Equals($targetItem.FullName.TrimEnd('\'), $installDirectory.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw '卸载路径检查失败，未删除任何文件。'
    }
}

if (@(Get-InstalledProcess).Count -gt 0) {
    Start-Process -FilePath $executable -ArgumentList '--quit' -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (@(Get-InstalledProcess).Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (@(Get-InstalledProcess).Count -gt 0) {
        throw '朝夕提醒尚未退出。请从托盘菜单退出后再次卸载；没有强制结束进程。'
    }
}

if (Test-Path -LiteralPath $installDirectory) {
    # Reject links inside the package instead of following them during deletion.
    $linkedItem = Get-ChildItem -LiteralPath $installDirectory -Force -Recurse |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1
    if ($linkedItem) { throw '安装文件夹中有链接目录或文件，未执行删除。请先检查安装文件夹。' }
    Remove-Item -LiteralPath $installDirectory -Recurse -Force
}

$shell = New-Object -ComObject WScript.Shell
try {
    $shortcutPaths = @(
        (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) '朝夕提醒.lnk'),
        (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\朝夕提醒.lnk')
    )
    foreach ($shortcutPath in $shortcutPaths) {
        if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
            $shortcut = $shell.CreateShortcut($shortcutPath)
            $matches = [string]::Equals($shortcut.TargetPath, $executable, [StringComparison]::OrdinalIgnoreCase)
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null
            if ($matches) { Remove-Item -LiteralPath $shortcutPath -Force }
        }
    }
} finally {
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
}

$runValue = Get-ItemProperty -Path $runKey -Name Daynote -ErrorAction SilentlyContinue
if ($runValue -and [string]::Equals([string]$runValue.Daynote, ('"' + $executable + '"'), [StringComparison]::OrdinalIgnoreCase)) {
    Remove-ItemProperty -Path $runKey -Name Daynote -ErrorAction SilentlyContinue
}
$registered = Get-ItemProperty -Path $uninstallKey -ErrorAction SilentlyContinue
if ($registered -and [string]::Equals([string]$registered.InstallLocation, $installDirectory, [StringComparison]::OrdinalIgnoreCase)) {
    Remove-Item -LiteralPath $uninstallKey -Recurse -Force
}
Write-Output '朝夕提醒已卸载。'
Write-Output ('你的提醒数据仍保存在：' + (Join-Path $env:LOCALAPPDATA 'Daynote\Data'))
