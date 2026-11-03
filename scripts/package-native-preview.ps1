[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $sourceRoot 'deliverables'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$packageRoot = Join-Path $releaseRoot ('Daynote-Native-Preview-' + $stamp)
New-Item -ItemType Directory -Path $packageRoot,(Join-Path $packageRoot 'Windows'),(Join-Path $packageRoot 'scripts') -Force | Out-Null
foreach ($file in @('Daynote.exe','Daynote.exe.config')) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot ('build\' + $file)) -Destination (Join-Path $packageRoot 'Windows')
}
Copy-Item -LiteralPath (Join-Path $sourceRoot 'scripts\start-sync-preview.ps1') -Destination (Join-Path $packageRoot 'scripts')
@'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start-sync-preview.ps1"
if errorlevel 1 pause
'@ | Set-Content -LiteralPath (Join-Path $packageRoot '启动 Windows 预览版.cmd') -Encoding ASCII
$appleRoot = Join-Path $sourceRoot 'apple'
$appleOut = Join-Path $packageRoot 'Apple'
New-Item -ItemType Directory -Path $appleOut -Force | Out-Null
Get-ChildItem -LiteralPath $appleRoot -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/](\.build|build|DerivedData|xcuserdata|\.swiftpm|\.private|qa)[\\/]' -and $_.Name -notlike '.env*' } | ForEach-Object {
    $relative = $_.FullName.Substring($appleRoot.Length).TrimStart('\','/')
    $destination = Join-Path $appleOut $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination
}
Copy-Item -LiteralPath (Join-Path $sourceRoot 'docs\NATIVE_PREVIEW_GUIDE.html') -Destination (Join-Path $packageRoot '先读我.html')
Copy-Item -LiteralPath (Join-Path $sourceRoot 'LICENSE') -Destination $packageRoot
$zip = $packageRoot + '.zip'
Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zip
& node (Join-Path $PSScriptRoot 'fix-mac-zip-permissions.mjs') $zip
if ($LASTEXITCODE -ne 0) { throw 'Failed to preserve Mac script permissions.' }
Get-FileHash -LiteralPath $zip -Algorithm SHA256 | Select-Object Path,Hash
Write-Output ('PACKAGE=' + $packageRoot)
Write-Output ('ZIP=' + $zip)
