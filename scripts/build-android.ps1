[CmdletBinding()]
param([string[]]$Tasks=@('assembleDebug','assembleRelease','testDebugUnitTest','lintRelease'))
$ErrorActionPreference='Stop'
$sourceRoot=Split-Path -Parent $PSScriptRoot
$toolRoot=Join-Path $env:LOCALAPPDATA 'DaynoteBuildTools'
if(-not $env:JAVA_HOME){
    $jdk=Get-ChildItem -LiteralPath (Join-Path $toolRoot 'jdk') -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
    if($jdk){$env:JAVA_HOME=$jdk.FullName}else{throw 'Set JAVA_HOME to JDK 17 or install it with Android Studio.'}
}
if(-not $env:ANDROID_HOME -and (Test-Path -LiteralPath (Join-Path $toolRoot 'sdk'))){$env:ANDROID_HOME=Join-Path $toolRoot 'sdk'}
$arguments=@('--console=plain')
if($env:HTTPS_PROXY){
    $proxy=[uri]$env:HTTPS_PROXY
    if($proxy.UserInfo){throw 'Configure authenticated build proxies privately in Gradle settings.'}
    $arguments+=@("-Dhttps.proxyHost=$($proxy.Host)","-Dhttps.proxyPort=$($proxy.Port)","-Dhttp.proxyHost=$($proxy.Host)","-Dhttp.proxyPort=$($proxy.Port)")
    # Use Windows' trusted roots for this machine's configured HTTPS proxy.
    $arguments+=@('-Djavax.net.ssl.trustStoreType=Windows-ROOT','-Djavax.net.ssl.trustStore=NONE')
}
$arguments+=$Tasks
$gradle=Join-Path $toolRoot 'gradle\gradle-8.13\bin\gradle.bat'
if(-not(Test-Path -LiteralPath $gradle)){$gradle=Join-Path $sourceRoot 'android\gradlew.bat'}
Push-Location (Join-Path $sourceRoot 'android')
try { & $gradle @arguments; if($LASTEXITCODE -ne 0){throw 'Android build or verification failed.'} }
finally { Pop-Location }
