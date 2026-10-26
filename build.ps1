$ErrorActionPreference = 'Stop'
$base = $PSScriptRoot
$out = Join-Path $base 'build'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll') | ForEach-Object { '/r:' + (Join-Path $framework $_) }
$refs += '/r:' + (Join-Path $framework 'WPF\WindowsBase.dll')
$refs += '/r:' + (Join-Path $framework 'WPF\PresentationCore.dll')
$refs += '/r:' + (Join-Path $framework 'WPF\PresentationFramework.dll')
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 64,64
$g = [System.Drawing.Graphics]::FromImage($bitmap)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::White)
$red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,59,48))
$ink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(28,28,30))
$g.FillRectangle($red,0,0,64,18)
$font = New-Object System.Drawing.Font 'Segoe UI',26,([System.Drawing.FontStyle]::Bold),([System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('31',$font,$ink,12,23)
$icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
$iconPath = Join-Path $out 'Daynote.ico'
$stream = [System.IO.File]::Create($iconPath)
$icon.Save($stream)
$stream.Dispose(); $icon.Dispose(); $red.Dispose(); $ink.Dispose(); $font.Dispose(); $g.Dispose(); $bitmap.Dispose()
$sources = @('App.cs','Core.cs','CalendarLogic.cs','CalendarWindow.cs','DayView.cs','TaskEditor.cs','DesktopHost.cs','DisplayLayout.cs') | ForEach-Object { Join-Path $base $_ }
& (Join-Path $framework 'csc.exe') /nologo /target:winexe /platform:x64 /optimize+ /utf8output "/out:$out\Daynote.exe" "/win32icon:$iconPath" "/win32manifest:$base\app.manifest" @refs @sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
@'
<?xml version="1.0" encoding="utf-8"?>
<configuration><startup useLegacyV2RuntimeActivationPolicy="true"><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" /></startup><runtime><AppContextSwitchOverrides value="Switch.System.Windows.DoNotScaleForDpiChanges=false" /></runtime></configuration>
'@ | Set-Content -LiteralPath (Join-Path $out 'Daynote.exe.config') -Encoding UTF8
Write-Output "Built $out\Daynote.exe"
