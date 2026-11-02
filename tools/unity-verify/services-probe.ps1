param(
    [string]$Apk = (Join-Path $PSScriptRoot '../../Nexus/builds/android-il2cpp-dev.apk'),
    [string]$RunName = ('phone-services-' + (Get-Date -Format 'yyyyMMdd-HHmm')),
    [string]$AdbSerial = '',
    [string]$Package = 'com.UnityTechnologies.com.unity.template.urpblank',
    [switch]$ThreeFingers
)
# Phone check of the share sheet (issue #134), local notifications (#160) and the debug menu (#270) over the
# development player that NexusVerify.NexusVerifyBuild.BuildAndroidIL2CPPDevelopment builds; its ServicesProbe runs
# only when the player starts with the intent extra nexusProbe. Launches, each of which must pass:
# - no-permission, permission: the share sheet for a text, then a file (each must cover the game; Back closes it),
#   the shared file read by a second process (adb's shell, through the URI the probe grants it), a notification
#   scheduled 20 s ahead, the debug menu opened and closed with a held Backquote key, then HOME and a wait. Without
#   the permission nothing may be posted; with it the notification must be posted, and a tap on it must bring the game
#   back with TakeOpenedWith = probe-data and remove it.
# - cold: the notification is scheduled, the process is killed in the background (force-stop would cancel the alarm),
#   and the tap must start a new process whose TakeOpenedWith returns probe-data.
# - ask-deny, ask-deny-again, ask-after-two-denials, ask-allow: RequestPermissionAsync after `pm clear` (a first
#   session: no permission, no flags, no saved answer); the script answers Android's dialog by its button ids. Two
#   refusals end the dialog: the third request must answer NeedsPermission without one, as INotificationService documents.
# -ThreeFingers runs only the three-fingers launch, attended: someone at the phone opens the debug menu with three
# fingers, lifts them, and closes it with three fingers again.
# The permission and its flags are restored afterwards. Evidence in Nexus/artifacts/<RunName>/. Exit codes: 0 passed,
# 1 failed, 2 could not run.
$ErrorActionPreference = 'Stop'
trap { Write-Host "Services probe COULD NOT RUN: $_"; exit 2 }
$Text = 'Scheduled 20 s ahead'   # ServicesProbe's notification text
$FileText = 'Nexus probe file'   # and its shared file's
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
        if ($line -match '\[Probe\] (services|share|notification|menu|resumed openedWith|cold|ask)' -and -not $seen.Contains($line)) { $seen.Add($line) }
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
function Has([string]$pattern) { return @($seen | Where-Object { $_ -match $pattern }).Count -gt 0 }
function TopActivity { return (@(Adb @('shell', 'dumpsys', 'activity', 'activities') | Where-Object { $_ -match 'topResumedActivity=' })[0]).Trim() }
function GameOnTop { return (TopActivity) -match [regex]::Escape("$Package/") }
function Pid { return ((Adb @('shell', 'pidof', $Package)) -join '').Trim() }
function Key([string]$code) { Adb @('shell', 'input', 'keyevent', $code) | Out-Null }
# adb's plain key event releases at once and was missed at 1 of 12 presses (likely press and release in one frame).
function HeldBackquote { Adb @('shell', 'input', 'keyevent', '--longpress', 'KEYCODE_GRAVE') | Out-Null }
function OwnNotification { # the dumpsys block of this app's notification records
    $dump = (Adb @('shell', 'dumpsys', 'notification', '--noredact')) -join "`n"
    return @([regex]::Matches($dump, "NotificationRecord\(0x[0-9a-f]+: pkg=$([regex]::Escape($Package)) [\s\S]*?(?=NotificationRecord\(|\z)") | ForEach-Object { $_.Value }) -join "`n"
}
function UiDump([string]$name, [switch]$Discard) { # -Discard: the screen shows other apps' content (the notification shade)
    Adb @('shell', 'uiautomator', 'dump', '/sdcard/nexus-services-ui.xml') | Out-Null
    $file = Join-Path $evidence "ui-$name.xml"
    Adb @('pull', '/sdcard/nexus-services-ui.xml', $file) | Out-Null
    Adb @('shell', 'rm', '/sdcard/nexus-services-ui.xml') | Out-Null
    $ui = Get-Content -LiteralPath $file -Raw
    if ($Discard) { Remove-Item -LiteralPath $file }
    return $ui
}
function TapNode([string]$ui, [string]$attribute) { # taps the centre of the first node whose attributes match; false when none does
    if ($ui -notmatch "<node [^>]*$attribute[^>]*bounds=`"\[(\d+),(\d+)\]\[(\d+),(\d+)\]`"") { return $false }
    Adb @('shell', 'input', 'tap', [string][int](([int]$Matches[1] + [int]$Matches[3]) / 2), [string][int](([int]$Matches[2] + [int]$Matches[4]) / 2)) | Out-Null
    return $true
}
# Android posts an inexact alarm late (15 s on the 2311DRK48G), so wait well past the notification's 20 s.
function WaitForNotification([int]$seconds, [bool]$untilPosted) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do { ProbeLines | Out-Null; Start-Sleep -Seconds 2; $record = OwnNotification } while ((Get-Date) -lt $deadline -and -not ($untilPosted -and $record -match [regex]::Escape($Text)))
    return $record
}
function LateSeconds([string]$record) {
    if ($record -match 'when=(\d+)') { $when = [long]$Matches[1] } else { return $null }
    if ($record -match 'mCreationTimeMs=(\d+)') { return [math]::Round(([long]$Matches[1] - $when) / 1000.0, 1) }
    return $null
}
function TapNotification([string]$name) {
    Adb @('shell', 'cmd', 'statusbar', 'expand-notifications') | Out-Null
    Start-Sleep -Seconds 2
    $hit = TapNode (UiDump $name -Discard) "text=`"$([regex]::Escape($Text))`""
    if (-not $hit) { Adb @('shell', 'cmd', 'statusbar', 'collapse') | Out-Null }
    return $hit
}
function SetPermission([bool]$allowed, [bool]$clearFlags) {
    Adb @('shell', 'pm', $(if ($allowed) { 'grant' } else { 'revoke' }), $Package, $Permission) | Out-Null
    if ($clearFlags) { Adb @('shell', 'pm', 'clear-permission-flags', $Package, $Permission, 'user-set', 'user-fixed') | Out-Null }
}
function Launch([string]$mode, [switch]$KeepRunning) {
    if (-not $KeepRunning) { Adb @('shell', 'am', 'force-stop', $Package) | Out-Null }
    Adb @('logcat', '-c') | Out-Null
    $seen.Clear()
    Adb @('shell', 'am', 'start', '-n', $activity, '--es', 'nexusProbe', $mode) | Out-Null
    return WaitFor "services bound mode=$mode " 60
}
function Finish([string]$name, [System.Collections.Specialized.OrderedDictionary]$result, [bool]$passed) {
    $lines = ProbeLines
    $lines | Set-Content -LiteralPath (Join-Path $evidence "probe-$name.txt")
    Adb @('logcat', '-d', '-v', 'time') | Set-Content -LiteralPath (Join-Path $evidence "logcat-$name.txt")
    $failed = @($lines | Where-Object { $_ -match 'FAILED' }) -join ' '
    $result.failed = $failed
    $result.passed = $passed -and -not $failed
    Write-Host ("$name " + ($(if ($result.passed) { 'passed' } else { 'FAILED' })) + ': ' + (($result.Keys | Where-Object { $_ -notin 'run', 'sheets', 'passed', 'failed' } | ForEach-Object { "$_=$($result[$_])" }) -join ' '))
    $script:results += [pscustomobject]$result
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
$granted = [bool]((Adb @('shell', 'dumpsys', 'package', $Package)) -match "${Permission}: granted=true")

$results = @()
try {
    if (-not $ThreeFingers) {
        foreach ($allowed in @($false, $true)) {
            $name = if ($allowed) { 'permission' } else { 'no-permission' }
            SetPermission $allowed $false
            $bound = Launch 'services'
            $sheets = @()
            foreach ($share in @('share text opened', 'share file opened')) {
                if (WaitFor $share 60) { Start-Sleep -Seconds 2; $sheets += TopActivity; Key 'KEYCODE_BACK' }
            }
            $uri = if (WaitFor 'share file uri=content://' 10) { (@($seen | Where-Object { $_ -match 'share file uri=(\S+)' })[0] -replace '.*share file uri=(\S+).*', '$1') } else { '' }
            $read = if ($uri) { (Adb @('shell', 'content', 'read', '--uri', $uri)) -join "`n" } else { '' }
            $read | Set-Content -LiteralPath (Join-Path $evidence "file-read-$name.txt")
            $scheduled = WaitFor 'notification scheduled' 30
            HeldBackquote; $menuOpened = WaitFor 'menu open=True theme=True buttons=2' 10
            HeldBackquote; $menuClosed = WaitFor 'menu open=False' 10
            Key 'KEYCODE_HOME'
            $record = WaitForNotification 90 $allowed
            $record | Set-Content -LiteralPath (Join-Path $evidence "notification-$name.txt")
            $result = [ordered]@{
                run = $name; bound = $bound
                shareText = Has 'share text opened=True'; shareFile = Has 'share file opened=True'
                sheetsCoveredTheGame = $sheets.Count -eq 2 -and @($sheets | Where-Object { $_ -match [regex]::Escape("$Package/") }).Count -eq 0
                sheets = $sheets
                fileReadByShell = $read.Trim() -eq $FileText
                permission = Has "notification permission=$(if ($allowed) { 'Allowed' } else { 'NeedsPermission' })"
                scheduled = $scheduled; posted = $record -match [regex]::Escape($Text); postedLateSeconds = LateSeconds $record
                menu = $menuOpened -and $menuClosed
            }
            $passed = $bound -and $result.shareText -and $result.shareFile -and $result.sheetsCoveredTheGame -and $result.fileReadByShell `
                -and $result.permission -and $result.scheduled -and $result.menu
            if ($allowed) {
                $tapped = TapNotification $name
                $result.openedWith = $tapped -and (WaitFor 'resumed openedWith=probe-data' 15)
                $result.gameOnTopAfterTap = GameOnTop
                $result.removedAfterTap = -not (OwnNotification)
                $passed = $passed -and $result.posted -and $result.openedWith -and $result.gameOnTopAfterTap -and $result.removedAfterTap
            }
            else { $passed = $passed -and -not $result.posted }
            Finish $name $result $passed
        }

        # A tap that starts the app: the process is killed in the background, which keeps the scheduled alarm.
        SetPermission $true $false
        $bound = Launch 'services-cold'
        $armed = WaitFor 'cold armed' 30
        Key 'KEYCODE_HOME'; Start-Sleep -Seconds 2
        $pidBefore = Pid
        # The debuggable player's own uid kills it: `am kill` left it running on the 2311DRK48G, and force-stop cancels alarms.
        if ($pidBefore) { Adb @('shell', 'run-as', $Package, 'kill', '-9', $pidBefore) | Out-Null }
        Start-Sleep -Seconds 2
        $pidAfterKill = Pid
        $record = WaitForNotification 90 $true
        $record | Set-Content -LiteralPath (Join-Path $evidence 'notification-cold.txt')
        Adb @('logcat', '-c') | Out-Null
        $seen.Clear()
        $tapped = TapNotification 'cold'
        $result = [ordered]@{
            run = 'cold'; bound = $bound; armed = $armed; pidBefore = $pidBefore; killed = -not $pidAfterKill
            posted = $record -match [regex]::Escape($Text); postedLateSeconds = LateSeconds $record; tapped = $tapped
            openedWith = $tapped -and (WaitFor 'services bound mode=cold-start notifications=\S+ openedWith=probe-data' 60)
        }
        $result.pidAfterTap = Pid
        $result.newProcess = $result.pidAfterTap -and $result.pidAfterTap -ne $pidBefore
        $result.gameOnTop = GameOnTop
        $result.removedAfterTap = -not (OwnNotification)
        Finish 'cold' $result ($bound -and $armed -and $result.killed -and $result.posted -and $result.openedWith -and $result.newProcess `
            -and $result.gameOnTop -and $result.removedAfterTap)

        # RequestPermissionAsync: Android's dialog (permission controller button ids; the second refusal's button is
        # permission_deny_and_dont_ask_again_button), then what two refusals leave.
        Adb @('shell', 'pm', 'clear', $Package) | Out-Null   # a first session: no permission, no flags, no saved answer
        foreach ($ask in @(
                @{ name = 'ask-deny'; button = 'permission_deny(?:_and_dont_ask_again)?_button'; answer = 'NeedsPermission' },
                @{ name = 'ask-deny-again'; button = 'permission_deny(?:_and_dont_ask_again)?_button'; answer = 'NeedsPermission' },
                @{ name = 'ask-after-two-denials'; button = $null; answer = 'NeedsPermission' })) {
            $bound = Launch 'services-ask'
            $before = WaitFor 'ask before permission=' 30
            Start-Sleep -Seconds 3
            $ui = UiDump $ask.name
            $dialog = $ui -match 'permissioncontroller:id/permission_(allow|deny)'
            $tapped = if ($ask.button) { TapNode $ui "resource-id=`"com.android.permissioncontroller:id/$($ask.button)`"" } else { $false }
            $answered = WaitFor 'ask answered=' 15
            $result = [ordered]@{
                run = $ask.name; bound = $bound; before = $before; dialog = $dialog; tapped = $tapped; answered = $answered
                answer = Has "ask answered=$($ask.answer) permission=$($ask.answer)"
            }
            $expectDialog = [bool]$ask.button
            Finish $ask.name $result ($bound -and $before -and $dialog -eq $expectDialog -and $tapped -eq $expectDialog -and $answered -and $result.answer)
        }
        Adb @('shell', 'pm', 'clear', $Package) | Out-Null   # a first session: no permission, no flags, no saved answer
        $bound = Launch 'services-ask'
        $before = WaitFor 'ask before permission=NeedsPermission' 30
        Start-Sleep -Seconds 3
        $tapped = TapNode (UiDump 'ask-allow') 'resource-id="com.android.permissioncontroller:id/permission_allow_button"'
        $result = [ordered]@{
            run = 'ask-allow'; bound = $bound; before = $before; tapped = $tapped
            answer = $tapped -and (WaitFor 'ask answered=Allowed permission=Allowed' 15)
        }
        Finish 'ask-allow' $result ($bound -and $before -and $tapped -and $result.answer)

    }
    else {
        $bound = Launch 'services-menu'
        # One window for both: the person at the phone cannot see this script's prompts.
        Write-Host 'THREE FINGERS: open the menu with three fingers, lift them, close it with three fingers (300 s)'
        $deadline = (Get-Date).AddSeconds(300)
        $opened = WaitFor 'menu open=True theme=True buttons=2 touches=[3-9]' 300
        $closed = WaitFor 'menu open=False theme=True buttons=0 touches=[3-9]' ([math]::Max(1, ($deadline - (Get-Date)).TotalSeconds))
        $result = [ordered]@{ run = 'three-fingers'; bound = $bound; opened = $opened; closed = $closed }
        Finish 'three-fingers' $result ($bound -and $opened -and $closed)
    }
}
finally {
    # A cold launch that never came would otherwise run at the next plain launch (Application.persistentDataPath).
    Adb @('shell', 'rm', '-f', "/sdcard/Android/data/$Package/files/nexus-services-probe-cold") | Out-Null
    Adb @('shell', 'run-as', $Package, 'rm', '-f', 'files/nexus-services-probe-cold') | Out-Null
    SetPermission $granted $true
}

$expected = if ($ThreeFingers) { 1 } else { 7 }
$ok = $results.Count -eq $expected -and @($results | Where-Object { -not $_.passed }).Count -eq 0
[ordered]@{ device = $model; android = $android; apk = (Split-Path -Leaf $Apk); runs = $results } | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $evidence 'services-probe.json')
Write-Host ("Services probe " + ($(if ($ok) { 'PASSED' } else { 'FAILED' })) + " on $model, Android $android ($($results.Count) of $expected launches ran)")
if ($ok) { exit 0 } else { exit 1 }
