# The preview has its own profile. Existing Daynote data is copied once and never modified here.
$ErrorActionPreference = 'Stop'
$packageRoot = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $packageRoot 'Windows\Daynote.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw '没有找到 Windows\Daynote.exe，请先解压完整安装包。' }
$previewRoot = Join-Path $env:USERPROFILE 'Daynote\NativePreview\Data'
$originalPath = Join-Path $env:USERPROFILE 'Daynote\Data\reminders.json'
$previewPath = Join-Path $previewRoot 'reminders.json'
New-Item -ItemType Directory -Path $previewRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath $previewPath) -and (Test-Path -LiteralPath $originalPath)) {
    Copy-Item -LiteralPath $originalPath -Destination $previewPath
    Copy-Item -LiteralPath $originalPath -Destination (Join-Path $previewRoot 'before-native-preview.json')
}
Start-Process -FilePath $executable -ArgumentList @('--data-dir', ('"' + $previewRoot + '"'))
