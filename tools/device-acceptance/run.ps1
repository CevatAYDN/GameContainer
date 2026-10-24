param(
    [string]$Apk = (Join-Path $PSScriptRoot '../../Nexus/builds/android-il2cpp'),
    [string]$RunName = ('device-' + (Get-Date -Format 'yyyyMMdd-HHmm')),
    [int]$SoakSeconds = 300,
    [int]$SampleSeconds = 30,
    [int]$MaxPssGrowthPercent = 50,
    [string]$AdbSerial = '',
    [string]$Aapt = ''
)
# Physical-device acceptance of an Android player: cold start, soak with memory samples, background and
# resume, kill and restart, logcat scan. Evidence lands under Nexus/artifacts/<RunName>/ (git-ignored).
# Out of scope: storage interruption and gameplay-specific checks; see README.md.
$ErrorActionPreference = 'Stop'
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunName must be a simple folder name.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$evidence = Join-Path $repo ('Nexus/artifacts/' + $RunName)
if (Test-Path -LiteralPath $evidence) { throw "Refusing to reuse evidence folder: $evidence" }
if (-not (Test-Path -LiteralPath $Apk)) { throw "APK not found: $Apk (build it with NexusVerify.NexusVerifyBuild.BuildAndroidIL2CPP)" }
if (-not $Aapt) {
    $buildTools = 'C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\build-tools'
    $Aapt = Get-ChildItem -LiteralPath $buildTools -Directory | Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'aapt.exe' } | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Aapt) { throw 'aapt.exe not found; pass -Aapt.' }
}
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
# adb installs only files named *.apk; NexusVerifyBuild writes the player without an extension.
$Apk = (Resolve-Path -LiteralPath $Apk).Path  # a relative -Apk resolves against the PowerShell location, not the process directory
if ([IO.Path]::GetExtension($Apk) -ne '.apk') { $copy = Join-Path $evidence 'player.apk'; Copy-Item -LiteralPath $Apk -Destination $copy; $Apk = $copy }
$adbArgs = @(); if ($AdbSerial) { $adbArgs = @('-s', $AdbSerial) }
function Invoke-Adb([string[]]$a) { $out = & adb.exe @adbArgs @a 2>&1; return ($out | ForEach-Object { "$_" }) }
function Get-AppPid([string]$package) { $tokens = ((Invoke-Adb @('shell', 'pidof', $package)) -join ' ') -split '\s+'; return ($tokens | Where-Object { $_ -match '^\d+$' } | Select-Object -Last 1) }
function PssKb([string]$package) {
    $lines = Invoke-Adb @('shell', 'dumpsys', 'meminfo', $package)
    foreach ($line in $lines) { if ($line -match 'TOTAL PSS:\s+(\d+)') { return [int]$Matches[1] } }
    foreach ($line in $lines) { if ($line -match '^\s*TOTAL\s+(\d+)') { return [int]$Matches[1] } }
    return -1
}

$badging = & $Aapt dump badging $Apk 2>&1
$package = ([regex]::Match(($badging -join "`n"), "package: name='([^']+)'")).Groups[1].Value
$activity = ([regex]::Match(($badging -join "`n"), "launchable-activity: name='([^']+)'")).Groups[1].Value
if (-not $package -or -not $activity) { throw 'Could not read package/activity from the APK.' }
$component = "$package/$activity"
$devices = @((& adb.exe devices) | Where-Object { $_ -match "`tdevice$" })
if (-not $AdbSerial -and $devices.Count -ne 1) { throw "Expected exactly one adb device, found $($devices.Count); pass -AdbSerial." }
$model = ((Invoke-Adb @('shell', 'getprop', 'ro.product.model')) -join '').Trim()
$release = ((Invoke-Adb @('shell', 'getprop', 'ro.build.version.release')) -join '').Trim()
Write-Host "Device: $model (Android $release); app: $component"

$result = [ordered]@{
    package = $package; component = $component; apk = $Apk; apkSha256 = (Get-FileHash -LiteralPath $Apk).Hash
    model = $model; androidRelease = $release; soakSeconds = $SoakSeconds; phases = [ordered]@{}; pssSamplesKb = @(); passed = $false; failures = @()
}
function Fail([string]$why) { $result.failures += $why; Write-Host "FAIL: $why" }

$install = Invoke-Adb @('install', '-r', $Apk)
if (($install -join "`n") -notmatch 'Success') { throw "adb install failed: $($install -join ' ')" }
Invoke-Adb @('shell', 'am', 'force-stop', $package) | Out-Null
Invoke-Adb @('logcat', '-c') | Out-Null

# 1. Cold start.
Invoke-Adb @('shell', 'am', 'start', '-n', $component) | Out-Null
Start-Sleep -Seconds 15
$pid0 = Get-AppPid $package
$result.phases.coldStart = [ordered]@{ pid = $pid0; ok = [bool]$pid0 }
if (-not $pid0) { Fail 'the app did not start (no process after 15 s)' }

# 2. Soak with memory samples.
$start = Get-Date; $alive = [bool]$pid0
while ($alive -and ((Get-Date) - $start).TotalSeconds -lt $SoakSeconds) {
    Start-Sleep -Seconds $SampleSeconds
    $p = Get-AppPid $package
    if (-not $p) { $alive = $false; break }
    $pss = PssKb $package
    $result.pssSamplesKb += [ordered]@{ t = [int]((Get-Date) - $start).TotalSeconds; pssKb = $pss }
    Write-Host ("  t={0,4}s pid={1} pss={2} kB" -f [int]((Get-Date) - $start).TotalSeconds, $p, $pss)
}
$samples = @($result.pssSamplesKb | Where-Object { $_.pssKb -gt 0 })
$growth = if ($samples.Count -ge 2) { [math]::Round(100.0 * ($samples[-1].pssKb - $samples[0].pssKb) / $samples[0].pssKb, 1) } else { 0 }
$result.phases.soak = [ordered]@{ aliveAtEnd = $alive; samples = $samples.Count; pssGrowthPercent = $growth }
if (-not $alive) { Fail 'the process died during the soak' }
if ($growth -gt $MaxPssGrowthPercent) { Fail "PSS grew $growth% over the soak (limit $MaxPssGrowthPercent%)" }

# 3. Background and resume.
if ($alive) {
    Invoke-Adb @('shell', 'input', 'keyevent', 'KEYCODE_HOME') | Out-Null
    Start-Sleep -Seconds 10
    $pidBackground = Get-AppPid $package
    Invoke-Adb @('shell', 'am', 'start', '-n', $component) | Out-Null
    Start-Sleep -Seconds 10
    $pidResumed = Get-AppPid $package
    $result.phases.backgroundResume = [ordered]@{ pidBeforeHome = $pid0; pidInBackground = $pidBackground; pidAfterResume = $pidResumed; samePid = ($pidResumed -eq $pid0); ok = [bool]$pidResumed }
    if (-not $pidResumed) { Fail 'the app did not come back after HOME and relaunch' }
}

# 4. Kill and restart (what a user sees after the OS reclaimed the app).
Invoke-Adb @('shell', 'am', 'force-stop', $package) | Out-Null
Start-Sleep -Seconds 3
Invoke-Adb @('shell', 'am', 'start', '-n', $component) | Out-Null
Start-Sleep -Seconds 15
$pidRestart = Get-AppPid $package
$result.phases.killRestart = [ordered]@{ pid = $pidRestart; ok = [bool]$pidRestart }
if (-not $pidRestart) { Fail 'the app did not start again after force-stop' }
Invoke-Adb @('shell', 'am', 'force-stop', $package) | Out-Null

# 5. Logcat scan.
$log = Invoke-Adb @('logcat', '-d')
$pids = @($pid0, $pidRestart) | Where-Object { $_ }
$own = @($log | Where-Object { $line = $_; @($pids | Where-Object { $line -match ('^\S+\s+\S+\s+' + $_ + '\s') }).Count -gt 0 })
$log | Set-Content -LiteralPath (Join-Path $evidence 'logcat.log') -Encoding utf8
$fatal = @($own | Where-Object { $_ -match 'FATAL EXCEPTION' }).Count
$anr = @($log | Where-Object { $_ -match ('ANR in ' + [regex]::Escape($package)) }).Count
$unityExceptions = @($own | Where-Object { $_ -match '\bUnity\b' -and $_ -match '(^|\s)[A-Za-z.]+Exception(:|\s)' -and $_ -notmatch 'ExceptionHelpers|Exceptions/' -and $_ -notmatch 'AssetPackManager' }).Count  # Unity's own Play Asset Delivery probe logs ClassNotFoundException on every APK without Play Core
$adsViolations = @($own | Where-Object { $_ -match '\[Nexus Ads\] PolicyViolation' }).Count
$adsLines = @($own | Where-Object { $_ -match '\[Nexus Ads\]' }).Count
$result.phases.logcat = [ordered]@{ pids = $pids; ownLines = $own.Count; fatalExceptions = $fatal; anr = $anr; unityExceptionLines = $unityExceptions; adsLines = $adsLines; adsPolicyViolations = $adsViolations }
if ($fatal -gt 0) { Fail "$fatal FATAL EXCEPTION line(s) in logcat" }
if ($anr -gt 0) { Fail "$anr ANR line(s) in logcat" }
if ($unityExceptions -gt 0) { Fail "$unityExceptions Unity exception line(s) in logcat" }
if ($adsViolations -gt 0) { Fail "$adsViolations Nexus Ads policy violation line(s) in logcat" }

$result.passed = ($result.failures.Count -eq 0)
$result.sourceCommit = (& git -C $repo rev-parse HEAD).Trim()
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'device-acceptance.json') -Encoding utf8
Write-Host ("Device acceptance {0}: {1}" -f ($(if ($result.passed) { 'PASSED' } else { 'FAILED' }), $evidence))
if (-not $result.passed) { exit 1 }
