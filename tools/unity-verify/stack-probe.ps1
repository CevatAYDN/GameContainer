param(
    [string]$Apk = (Join-Path $PSScriptRoot '../../Nexus/builds/android-il2cpp-stackprobe.apk'),
    [string]$RunName = ('stack-probe-' + (Get-Date -Format 'yyyyMMdd-HHmm')),
    [string]$AdbSerial = '',
    [string]$Package = 'com.UnityTechnologies.com.unity.template.urpblank'
)
# Phone check of the screen stack (issue #76, ADR-0003 verification plan item 4) over the player that
# NexusVerify.NexusVerifyBuild.BuildAndroidStackProbe builds, launched once for each UI input setup of ADR-0003
# decision 7 (the intent extra nexusInputMode). Each launch: 10 rounds of three KEYCODE_BACK presses (the sheet
# closes, then the modal, then the root raises the press-again hint), a 60 s capture of the managed allocation per
# frame with a 30 s drag after 15 s without a touch, and a double press that must quit the app.
# Evidence in Nexus/artifacts/<RunName>/. Exit codes: 0 passed, 1 failed, 2 could not run.
$ErrorActionPreference = 'Stop'
trap { Write-Host "Stack probe COULD NOT RUN: $_"; exit 2 }
$Rounds = 10   # StackProbe.Rounds: the player runs that many, so it is no parameter here
$Modes = [ordered]@{ 'eventsystem' = 'yes'; 'eventsystem-once' = 'yes'; 'inputforui' = 'no' }   # mode -> EventSystem present
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunName must be a simple folder name.' }
$evidence = Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))) ('Nexus/artifacts/' + $RunName)
if (Test-Path -LiteralPath $evidence) { throw "Refusing to reuse evidence folder: $evidence" }
if (-not (Test-Path -LiteralPath $Apk)) { throw "APK not found: $Apk" }
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$adbArgs = @(); if ($AdbSerial) { $adbArgs = @('-s', $AdbSerial) }
function Adb([string[]]$a) { $out = & adb.exe @adbArgs @a 2>&1; return ($out | ForEach-Object { "$_" }) }
function ProbeLines { return @(Adb @('logcat', '-d', '-s', 'Unity') | Where-Object { $_ -match '\[StackProbe\]' }) }
function WaitFor([string]$pattern, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (ProbeLines | Where-Object { $_ -match $pattern }) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}
function Back { Adb @('shell', 'input', 'keyevent', '4') | Out-Null }

& adb.exe start-server 2>&1 | Out-Null   # its banner would otherwise land in the first reply
$model = (Adb @('shell', 'getprop', 'ro.product.model')) -join ''
$android = (Adb @('shell', 'getprop', 'ro.build.version.release')) -join ''
Write-Host "device $model, Android $android"
$install = Adb @('install', '-r', $Apk)
$install | Set-Content -LiteralPath (Join-Path $evidence 'install.txt')
if (-not ($install -match '^Success')) { throw "adb install failed: $($install -join ' ')" }   # else an older player of the same package would run
$activity = @(Adb @('shell', 'cmd', 'package', 'resolve-activity', '--brief', $Package))[-1].Trim()
if ($activity -notmatch '/') { throw "no launcher activity for $Package" }
$size = ((Adb @('shell', 'wm', 'size')) -join '') -replace '.*?(\d+)x(\d+).*', '$1 $2'
$w, $h = $size -split ' ' | ForEach-Object { [int]$_ }

$results = @()
foreach ($mode in $Modes.Keys) {
    Adb @('shell', 'am', 'force-stop', $Package) | Out-Null
    Adb @('logcat', '-c') | Out-Null
    Adb @('shell', 'am', 'start', '-n', $activity, '--es', 'nexusInputMode', $mode) | Out-Null
    $sent = 0
    if (WaitFor 'ready round=1 ' 60) {
        for ($round = 1; $round -le $Rounds; $round++) {
            if (-not (WaitFor "ready round=$round " 15)) { break }
            for ($press = 0; $press -lt 3; $press++) { Start-Sleep -Milliseconds 1200; Back; $sent++ }
        }
        if (WaitFor 'capture start' 15) {
            Start-Sleep -Seconds 15
            $dragStart = Get-Date
            while (((Get-Date) - $dragStart).TotalSeconds -lt 30) {
                Adb @('shell', 'input', 'swipe', [int]($w * 0.2), [int]($h * 0.5), [int]($w * 0.8), [int]($h * 0.5), '1000') | Out-Null
            }
            WaitFor 'capture done' 60 | Out-Null
        }
        Start-Sleep -Seconds 3   # longer than the probe's PressAgainSeconds (2 s), so the first press only arms the hint
        Back; Start-Sleep -Milliseconds 400; Back; $sent += 2
        Start-Sleep -Seconds 4
    }
    $alive = ((Adb @('shell', 'pidof', $Package)) -join '').Trim()
    Adb @('logcat', '-d', '-v', 'time') | Set-Content -LiteralPath (Join-Path $evidence "logcat-$mode.txt")
    $lines = ProbeLines
    $lines | Set-Content -LiteralPath (Join-Path $evidence "probe-$mode.txt")
    $count = { param($p) @($lines | Where-Object { $_ -match $p }).Count }
    $capture = @($lines | Where-Object { $_ -match 'capture mode=' }) -join ''
    $result = [ordered]@{
        mode = $mode; presses = $sent
        sheet = & $count 'back on Sheet'; modal = & $count 'back on Modal'; home = & $count 'back on Home'
        hints = & $count 'press-again hint'; rounds = & $count 'ready round=\d+ top=Sheet depth=3'
        eventSystem = (@($lines | Where-Object { $_ -match "mode=$mode eventSystem=(\w+)" }) -join '') -replace '.*eventSystem=(\w+).*', '$1'
        pointerMoves = if ($capture -match 'pointerMoves=(\d+)') { [int]$Matches[1] } else { 0 }
        capture = $capture; failed = @($lines | Where-Object { $_ -match 'FAILED' }) -join ' '
        quitLogged = (& $count '\] quit') -gt 0; processGone = -not $alive
    }
    $heard = $result.sheet + $result.modal + $result.home
    $result.passed = $sent -gt 0 -and $heard -eq $sent -and $result.rounds -eq $Rounds -and $result.sheet -eq $Rounds `
        -and $result.modal -eq $Rounds -and $result.home -eq $Rounds + 2 -and $result.hints -eq $Rounds + 1 `
        -and $result.eventSystem -eq $Modes[$mode] -and $result.pointerMoves -gt 0 -and -not $result.failed `
        -and $result.quitLogged -and $result.processGone
    Write-Host ("$mode " + ($(if ($result.passed) { 'passed' } else { 'FAILED' })) + ": $heard of $sent presses heard; $capture")
    $results += [pscustomobject]$result
}

$ok = @($results | Where-Object { -not $_.passed }).Count -eq 0
[ordered]@{ device = $model; android = $android; modes = $results } | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $evidence 'stack-probe.json')
Write-Host ("Stack probe " + ($(if ($ok) { 'PASSED' } else { 'FAILED' })) + " on $model, Android $android")
if ($ok) { exit 0 } else { exit 1 }
