[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputDirectory = Join-Path $sourceRoot 'dist'
$versionMatch = [regex]::Match(
    (Get-Content -LiteralPath (Join-Path $sourceRoot 'App.cs') -Raw),
    '\[assembly:\s*AssemblyVersion\("(\d+\.\d+\.\d+)\.\d+"\)\]')
if (-not $versionMatch.Success) { throw 'Cannot read the release version from App.cs.' }
$version = $versionMatch.Groups[1].Value

# Exact whitelist: no source tree, test profiles, logs, or personal Data folder.
$packageFiles = @(
    'build/Daynote.exe',
    'build/Daynote.exe.config',
    'build/Daynote.ico',
    'LICENSE',
    'README.md',
    'CHANGELOG.md',
    'CONTRIBUTING.md',
    'docs/images/month-light.png',
    'docs/images/day-dark.png',
    'install.ps1',
    'uninstall.ps1'
)
foreach ($relativePath in $packageFiles) {
    $path = Join-Path $sourceRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Package file is missing: $relativePath. Run build.ps1 first."
    }
    if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Package files must be regular files: $relativePath"
    }
}
$binaryVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $sourceRoot 'build/Daynote.exe')).Version
if ($binaryVersion.ToString(3) -ne $version) {
    throw "The application binary is version $binaryVersion, but App.cs is version $version. Rebuild first."
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$archivePath = Join-Path $outputDirectory ("Daynote-v$version-windows-x64.zip")
$temporaryArchive = $archivePath + '.tmp-' + [Guid]::NewGuid().ToString('N')
$checksumPath = Join-Path $outputDirectory 'SHA256SUMS.txt'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    $archive = [IO.Compression.ZipFile]::Open($temporaryArchive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($relativePath in $packageFiles) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, (Join-Path $sourceRoot $relativePath), $relativePath,
                [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $archive.Dispose() }

    $check = [IO.Compression.ZipFile]::OpenRead($temporaryArchive)
    try {
        $actualFiles = @($check.Entries | ForEach-Object { $_.FullName } | Sort-Object)
        if (@(Compare-Object ($packageFiles | Sort-Object) $actualFiles).Count -ne 0) {
            throw 'The ZIP contents do not match the package whitelist.'
        }
    }
    finally { $check.Dispose() }

    Move-Item -LiteralPath $temporaryArchive -Destination $archivePath -Force
    $sha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    ($sha256 + '  ' + [IO.Path]::GetFileName($archivePath)) |
        Set-Content -LiteralPath $checksumPath -Encoding ASCII
}
finally {
    if (Test-Path -LiteralPath $temporaryArchive) {
        Remove-Item -LiteralPath $temporaryArchive -Force
    }
}

Write-Host "Packaged Daynote v$version with $($packageFiles.Count) whitelisted files."
[PSCustomObject]@{
    Version = $version
    Tag = 'v' + $version
    ArchivePath = $archivePath
    ChecksumPath = $checksumPath
}
