param(
    [string]$Apk = (Join-Path $PSScriptRoot '../../Nexus/builds/android-il2cpp-dev.apk'),
    [string]$RunName = ('phone-services-' + (Get-Date -Format 'yyyyMMdd-HHmm')),
    [string]$AdbSerial = '',
    [string]$Package = 'com.UnityTechnologies.com.unity.template.urpblank'
)
# Phone check of the share sheet (issue #134), local notifications (#160) and the debug menu (#270) over the
# development player that NexusVerify.NexusVerifyBuild.BuildAndroidIL2CPPDevelopment builds; its ServicesProbe runs
# only when the player starts with the intent extra nexusProbe=services. Launched twice, without and with the
# notification permission. Each launch: the share sheet for a text, then a file (each must cover the game; Back
# closes it), a notification scheduled 20 s ahead, the debug menu opened and closed with a held Backquote key, then
# HOME and a wait past the notification's time. Without the permission nothing may be posted; with it the
# notification must be posted, and a tap on it must bring the game back with TakeOpenedWith = probe-data and remove it.
# The permission is restored afterwards. Evidence in Nexus/artifacts/<RunName>/. Exit codes: 0 passed, 1 failed,
# 2 could not run.
$ErrorActionPreference = 'Stop'
trap { Write-Host "Services probe COULD NOT RUN: $_"; exit 2 }
$Text = 'Scheduled 20 s ahead'   # ServicesProbe's notification text
$Permission = 'android.permission.POST_NOTIFICATIONS'
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunName must be a simple folder name.' }
$evidence = Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))) ('Nexus/artifacts/' + $RunName)
if (Test-Path -LiteralPath $evidence) { throw "Refusing to reuse evidence folder: $evidence" }
if (-not (Test-Path -LiteralPath $Apk)) { throw "APK not found: $Apk" }
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$adbArgs = @(); if ($AdbSerial) { $adbArgs = @('-s', $AdbSerial) }
function Adb([string[]]$a) { $out = & adb.exe @adbArgs @a 2>&1; return ($out | ForEach-Object { "$_" }) }
# The phone's logcat buffer drops the probe's lines within a minute, so every read keeps what it saw.
$seen = [Collections.Generic.List[string]]::new()
function ProbeLines {
    foreach ($line in Adb @('logcat', '-d', '-s', 'Unity')) {
        if ($line -match '\[Probe\] (services|share|notification|menu|resumed openedWith)' -and -not $seen.Contains($line)) { $seen.Add($line) }
    }
    return $seen
}
function WaitFor([string]$pattern, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (ProbeLines | Where-Object { $_ -match $pattern }) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}
function TopActivity { return (@(Adb @('shell', 'dumpsys', 'activity', 'activities') | Where-Object { $_ -match 'topResumedActivity=' })[0]).Trim() }
function Key([string]$code) { Adb @('shell', 'input', 'keyevent', $code) | Out-Null }
# adb's plain key event releases at once and was missed at 2 of 8 presses (likely press and release in one frame).
function HeldBackquote { Adb @('shell', 'input', 'keyevent', '--longpress', 'KEYCODE_GRAVE') | Out-Null }
function OwnNotification { # the dumpsys block of this app's notification records
    $dump = (Adb @('shell', 'dumpsys', 'notification', '--noredact')) -join "`n"
    return @([regex]::Matches($dump, "NotificationRecord\(0x[0-9a-f]+: pkg=$([regex]::Escape($Package)) [\s\S]*?(?=NotificationRecord\(|\z)") | ForEach-Object { $_.Value }) -join "`n"
}

& adb.exe start-server 2>&1 | Out-Null   # its banner would otherwise land in the first reply
$model = (Adb @('shell', 'getprop', 'ro.product.model')) -join ''
$android = (Adb @('shell', 'getprop', 'ro.build.version.release')) -join ''
Write-Host "device $model, Android $android"
$install = Adb @('install', '-r', $Apk)
$install | Set-Content -LiteralPath (Join-Path $evidence 'install.txt')
if (-not ($install -match '^Success')) { throw "adb install failed: $($install -join ' ')" }   # else an older player of the same package would run
$activity = @(Adb @('shell', 'cmd', 'package', 'resolve-activity', '--brief', $Package))[-1].Trim()
if ($activity -notmatch '/') { throw "no launcher activity for $Package" }
$granted = (Adb @('shell', 'dumpsys', 'package', $Package)) -match "${Permission}: granted=true"

$results = @()
try {
    foreach ($allowed in @($false, $true)) {
        $run = if ($allowed) { 'permission' } else { 'no-permission' }
        Adb @('shell', 'pm', $(if ($allowed) { 'grant' } else { 'revoke' }), $Package, $Permission) | Out-Null
        Adb @('shell', 'am', 'force-stop', $Package) | Out-Null
        Adb @('logcat', '-c') | Out-Null
        $seen.Clear()
        Adb @('shell', 'am', 'start', '-n', $activity, '--es', 'nexusProbe', 'services') | Out-Null
        $sheets = @()
        foreach ($share in @('share text opened', 'share file opened')) {
            if (WaitFor $share 60) { Start-Sleep -Seconds 2; $sheets += TopActivity; Key 'KEYCODE_BACK' }
        }
        $scheduled = WaitFor 'notification scheduled' 30
        HeldBackquote; $menuOpened = WaitFor 'menu open=True' 10
        HeldBackquote; $menuClosed = WaitFor 'menu open=False' 10
        Key 'KEYCODE_HOME'
        # Android posts an inexact alarm late (15 s on the 2311DRK48G), so wait well past the notification's 20 s.
        $deadline = (Get-Date).AddSeconds(90)
        do { ProbeLines | Out-Null; Start-Sleep -Seconds 2; $record = OwnNotification } while ((Get-Date) -lt $deadline -and -not ($allowed -and $record -match [regex]::Escape($Text)))
        $record | Set-Content -LiteralPath (Join-Path $evidence "notification-$run.txt")
        $tapped = ''; $removed = $false
        if ($allowed -and $record -match [regex]::Escape($Text)) {
            Adb @('shell', 'cmd', 'statusbar', 'expand-notifications') | Out-Null
            Start-Sleep -Seconds 2
            Adb @('shell', 'uiautomator', 'dump', '/sdcard/nexus-services-ui.xml') | Out-Null
            $ui = Join-Path $evidence "ui-$run.xml"
            Adb @('pull', '/sdcard/nexus-services-ui.xml', $ui) | Out-Null
            if ((Get-Content -LiteralPath $ui -Raw) -match "text=`"$([regex]::Escape($Text))`"[^>]*bounds=`"\[(\d+),(\d+)\]\[(\d+),(\d+)\]`"") {
                Adb @('shell', 'input', 'tap', [string](([int]$Matches[1] + [int]$Matches[3]) / 2 -as [int]), [string](([int]$Matches[2] + [int]$Matches[4]) / 2 -as [int])) | Out-Null
                WaitFor 'resumed openedWith=probe-data' 15 | Out-Null
                $tapped = TopActivity
                $removed = -not (OwnNotification)
            }
            Adb @('shell', 'cmd', 'statusbar', 'collapse') | Out-Null
        }
        $lines = ProbeLines
        $lines | Set-Content -LiteralPath (Join-Path $evidence "probe-$run.txt")
        Adb @('logcat', '-d', '-v', 'time') | Set-Content -LiteralPath (Join-Path $evidence "logcat-$run.txt")
        $has = { param($p) @($lines | Where-Object { $_ -match $p }).Count -gt 0 }
        $result = [ordered]@{
            run = $run
            shareText = & $has 'share text opened=True'; shareFile = & $has 'share file opened=True'
            sheetsCoveredTheGame = $sheets.Count -eq 2 -and @($sheets | Where-Object { $_ -match [regex]::Escape("$Package/") }).Count -eq 0
            sheets = $sheets
            permission = & $has "notification permission=$(if ($allowed) { 'Allowed' } else { 'NeedsPermission' })"
            scheduled = $scheduled; posted = $record -match [regex]::Escape($Text)
            postedLateSeconds = if ($record -match 'when=(\d+)' -and ($w = [long]$Matches[1]) -and $record -match 'mCreationTimeMs=(\d+)') { [math]::Round(([long]$Matches[1] - $w) / 1000.0, 1) } else { $null }
            menu = $menuOpened -and $menuClosed -and (& $has 'menu open=True theme=True buttons=2')
            failed = @($lines | Where-Object { $_ -match 'FAILED' }) -join ' '
        }
        if ($allowed) {
            $result.openedWith = & $has 'resumed openedWith=probe-data'
            $result.gameOnTopAfterTap = $tapped -match [regex]::Escape("$Package/"); $result.removedAfterTap = $removed
        }
        $result.passed = $result.shareText -and $result.shareFile -and $result.sheetsCoveredTheGame -and $result.permission `
            -and $result.scheduled -and $result.menu -and -not $result.failed -and $(if ($allowed) {
                $result.posted -and $result.openedWith -and $result.gameOnTopAfterTap -and $result.removedAfterTap
            } else { -not $result.posted })
        Write-Host ("$run " + ($(if ($result.passed) { 'passed' } else { 'FAILED' })) + ': ' + (($result.Keys | Where-Object { $_ -notin 'run', 'sheets', 'passed' } | ForEach-Object { "$_=$($result[$_])" }) -join ' '))
        $results += [pscustomobject]$result
    }
}
finally {
    Adb @('shell', 'pm', $(if ($granted) { 'grant' } else { 'revoke' }), $Package, $Permission) | Out-Null
}

$ok = $results.Count -eq 2 -and @($results | Where-Object { -not $_.passed }).Count -eq 0
[ordered]@{ device = $model; android = $android; runs = $results } | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $evidence 'services-probe.json')
Write-Host ("Services probe " + ($(if ($ok) { 'PASSED' } else { 'FAILED' })) + " on $model, Android $android")
if ($ok) { exit 0 } else { exit 1 }
