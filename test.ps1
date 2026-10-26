[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw 'Daynote tests require Windows and .NET Framework 4.8.'
}

$sourceRoot = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "The .NET Framework C# compiler was not found: $compiler"
}

$suites = @(
    @{ Name = 'CoreTests'; Sources = @('Core.cs', 'CoreTests.cs') },
    @{ Name = 'CalendarLogicTests'; Sources = @('Core.cs', 'CalendarLogic.cs', 'CalendarLogicTests.cs') }
)
$references = @('System.dll', 'System.Core.dll', 'System.Runtime.Serialization.dll', 'System.Xml.dll') |
    ForEach-Object { '/r:' + (Join-Path $framework $_) }
foreach ($suite in $suites) {
    foreach ($file in $suite.Sources) {
        if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot $file) -PathType Leaf)) {
            throw "Required test source is missing: $file"
        }
    }
}

# Only the model and calendar logic are compiled. The application, tray, desktop
# integration, installer, and any real Daynote profile are never started.
$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$testRoot = Join-Path $temporaryParent ('Daynote-tests-' + [Guid]::NewGuid().ToString('N'))
$isolatedProfile = Join-Path $testRoot 'profile'
$isolatedTemp = Join-Path $testRoot 'temp'
$savedEnvironment = @{}
foreach ($name in @('TEMP', 'TMP', 'APPDATA', 'LOCALAPPDATA')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

$locationChanged = $false
try {
    New-Item -ItemType Directory -Path $testRoot, $isolatedProfile, $isolatedTemp | Out-Null
    [Environment]::SetEnvironmentVariable('TEMP', $isolatedTemp, 'Process')
    [Environment]::SetEnvironmentVariable('TMP', $isolatedTemp, 'Process')
    [Environment]::SetEnvironmentVariable('APPDATA', $isolatedProfile, 'Process')
    [Environment]::SetEnvironmentVariable('LOCALAPPDATA', $isolatedProfile, 'Process')
    Push-Location -LiteralPath $testRoot
    $locationChanged = $true

    foreach ($suite in $suites) {
        $name = $suite.Name
        $executable = Join-Path $testRoot ($name + '.exe')
        $sources = $suite.Sources | ForEach-Object { Join-Path $sourceRoot $_ }
        Write-Host "Building $name..."
        & $compiler /nologo /target:exe /platform:x64 /optimize+ /utf8output "/main:$name" "/out:$executable" @references @sources
        if ($LASTEXITCODE -ne 0) {
            throw "$name compilation failed (exit code $LASTEXITCODE)."
        }

        Write-Host "Running $name with isolated temporary data..."
        & $executable
        if ($LASTEXITCODE -ne 0) {
            throw "$name failed (exit code $LASTEXITCODE)."
        }
    }
    Write-Host 'PASS: both Daynote test suites completed successfully.'
}
finally {
    if ($locationChanged) { Pop-Location }
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }

    # Delete only this run's freshly generated directory, never a caller path.
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolvedTestRoot.StartsWith($temporaryParent, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolvedTestRoot).StartsWith('Daynote-tests-', [StringComparison]::Ordinal)) {
        throw 'Refusing to clean up a path outside this test run temporary directory.'
    }
    if (Test-Path -LiteralPath $resolvedTestRoot) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
