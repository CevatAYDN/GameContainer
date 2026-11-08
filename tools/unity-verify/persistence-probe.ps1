param(
    [Parameter(Mandatory = $true)][ValidatePattern('\A[A-Za-z0-9_.:-]+\z')][string]$AdbSerial,
    [Parameter(Mandatory = $true)][ValidatePattern('\A[A-Fa-f0-9]{64}\z')][string]$ExpectedApkSha256,
    [Parameter(Mandatory = $true)][string]$ExpectedCommit,
    [Parameter(Mandatory = $true)][string]$Aapt,
    [string]$Apk = (Join-Path $PSScriptRoot '../../Nexus/builds/android-il2cpp-dev.apk'),
    [ValidatePattern('\A[A-Za-z0-9]{8,64}\z')][string]$RunToken = ([guid]::NewGuid().ToString('N')),
    [string]$RunName = ('persistence-' + $RunToken),
    [ValidateRange(5, 300)][int]$TimeoutSeconds = 60
)
# #338: 12 launches, bool/outcome x wallet/ack x arm/recover/verify.
# Only the exact framework development APK may be installed with -r; no game APKs, clears or uninstalls.
# Real force-stop/restart, not power failure, a live ad SDK, or a multi-key transaction proof.
# The caller supplies the frozen build identity, including the .commit dirty annotation when present.
# Exit 0: all restart checks observed, 1: failed/incomplete phases, 2: preflight could not run.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Package = 'com.UnityTechnologies.com.unity.template.urpblank'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$evidence = $null
$exitCode = 2
$result = [ordered]@{
    runToken = $RunToken; serial = $AdbSerial; package = $Package
    expectedApkSha256 = $ExpectedApkSha256; expectedCommit = $ExpectedCommit
    apkSha256 = ''; playerCommit = ''; phases = @(); passed = $false; failure = ''
    scope = 'process restart; simulator; single wallet JSON blob; not power loss or real ad SDK'
}

function Invoke-ProbeAdb([string[]]$a) {
    $out = @(& adb.exe -s $AdbSerial @a 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) { throw "adb failed ($LASTEXITCODE): $($a -join ' '): $($out -join ' ')" }
    return $out
}

function Get-ProbePid {
    # pidof exits 1 for absence; a device-side marker distinguishes absence from a dropped transport.
    $marker = "nexus-pid-$RunToken"
    $command = "pidof $Package; rc=`$?; echo ${marker}:`$rc"
    $out = @(Invoke-ProbeAdb @('shell', $command))
    if ($out.Count -eq 0 -or $out[-1] -notmatch "^$([regex]::Escape($marker)):([01])$") {
        throw 'No trustworthy device-side PID query answer.'
    }
    $status = [int]$Matches[1]
    $value = (($out | Select-Object -SkipLast 1) -join ' ').Trim()
    if ($status -eq 1 -and $value -eq '') { return '' }
    if ($status -ne 0 -or $value -notmatch '^[1-9][0-9]*$') { throw "Expected one app PID, got '$value' (status $status)." }
    return $value
}

function Assert-ProbeIdentity {
    $hash = (Get-FileHash -LiteralPath $script:frozenApk -Algorithm SHA256).Hash
    $commit = (Get-Content -LiteralPath "$script:frozenApk.commit" -Raw).Trim()
    if (-not [string]::Equals($hash, $ExpectedApkSha256, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($commit, $ExpectedCommit, [StringComparison]::Ordinal)) {
        throw 'Frozen APK SHA256 or build .commit differs from the requested identity.'
    }
}

function Read-ProbeLines([string]$ProcessId, [string]$Variant, [string]$Scenario, [string]$Phase) {
    $prefix = "[PersistenceProbe] run=$RunToken variant=$Variant scenario=$Scenario phase=$Phase pid=$ProcessId event="
    # Both logcat's PID filter and the threadtime prefix are checked; only this process/run/phase is retained.
    foreach ($line in Invoke-ProbeAdb @('logcat', '-d', "--pid=$ProcessId", '-v', 'threadtime', '-s', 'Unity')) {
        if ($line -match "^\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d+\s+$ProcessId\s+\d+\s+[VDIWEF]\s+Unity\s*:\s+" -and
            $line.Contains($prefix) -and -not $script:seen.Contains($line)) {
            $script:seen.Add($line)
        }
    }
}

function Invoke-ProbePhase([string]$Variant, [string]$Scenario, [string]$Phase, [string]$Receipt, [string]$PreviousPid) {
    Assert-ProbeIdentity
    if (Get-ProbePid) { throw 'Refusing to launch over an already running process.' }
    $record = [ordered]@{
        runToken = $RunToken; variant = $Variant; scenario = $Scenario; phase = $Phase
        pid = ''; previousPid = $PreviousPid; checks = 0; receipt = ''; marker = ''
        committedBalance = -1; pending = -1; acknowledgements = -1; passed = $false
        forceStopRequested = $false; pidEnded = $false; failure = ''
        startedUtc = (Get-Date).ToUniversalTime().ToString('o'); stoppedUtc = ''
    }
    $result.phases += $record
    $script:seen = [Collections.Generic.List[string]]::new()
    $logPath = Join-Path $evidence "$Variant-$Scenario-$Phase.txt"
    try {
        $a = @('shell', 'am', 'start', '-W', '-n', $script:component,
            '--es', 'nexusProbe', 'persistence', '--es', 'nexusRunToken', $RunToken,
            '--es', 'nexusCallback', $Variant, '--es', 'nexusFault', $Scenario, '--es', 'nexusPhase', $Phase)
        if ($Receipt) { $a += @('--es', 'nexusReceiptId', $Receipt) }
        $launch = @(Invoke-ProbeAdb $a)
        if ($launch -match 'Error:|Exception|Warning: Activity not started') { throw "Launch failed: $($launch -join ' ')" }
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        do {
            $record.pid = Get-ProbePid
            if ($record.pid) { break }
            Start-Sleep -Milliseconds 200
        } while ((Get-Date) -lt $deadline)
        if (-not $record.pid -or $record.pid -eq $PreviousPid) { throw 'No distinct new process PID.' }
        $marker = switch ($Phase) { 'arm' { 'ARMED' } 'recover' { 'RECOVERED' } 'verify' { 'VERIFIED' } }
        $terminal = @()
        do {
            Read-ProbeLines $record.pid $Variant $Scenario $Phase
            if (@($script:seen | Where-Object { $_ -match ' event=FAILED ' }).Count) { throw 'Probe recorded a failure.' }
            $terminal = @($script:seen | Where-Object { $_ -match " event=$marker " })
            if ($terminal.Count) { break }
            if ((Get-ProbePid) -ne $record.pid) { throw 'Process ended or changed before the terminal marker.' }
            Start-Sleep -Milliseconds 200
        } while ((Get-Date) -lt $deadline)
        if ($terminal.Count -ne 1) { throw "Expected one $marker marker, found $($terminal.Count)." }
        if (@($script:seen | Where-Object { $_ -match ' event=START il2cpp=1 development=1$' }).Count -ne 1) {
            throw 'Missing unique Android IL2CPP development START marker.'
        }
        $pattern = " event=$marker checks=([1-9][0-9]*) committedBalance=(100|110) receipt=([A-Za-z0-9-]{1,80}) pending=([01]) acks=([01])$"
        if ($terminal[0] -notmatch $pattern) { throw "Malformed $marker marker." }
        $record.checks = [int]$Matches[1]
        $record.committedBalance = [int]$Matches[2]
        $record.receipt = $Matches[3]
        $record.pending = [int]$Matches[4]
        $record.acknowledgements = [int]$Matches[5]
        $record.marker = $marker
        $heardChecks = @($script:seen | Where-Object { $_ -match ' event=CHECK name=[a-z0-9-]+ index=[1-9][0-9]*$' })
        if ($heardChecks.Count -eq 0 -or $heardChecks.Count -ne $record.checks) { throw 'Missing checks or terminal/check count mismatch.' }
        for ($i = 0; $i -lt $heardChecks.Count; $i++) {
            if ($heardChecks[$i] -notmatch ' index=([1-9][0-9]*)$' -or [int]$Matches[1] -ne $i + 1) {
                throw 'Check sequence is incomplete or duplicated.'
            }
        }
        $balance = if ($Phase -eq 'arm' -and $Scenario -eq 'wallet') { 100 } else { 110 }
        $pending = if ($Phase -eq 'arm') { 1 } else { 0 }
        $acks = if ($Phase -eq 'recover') { 1 } else { 0 }
        if ($record.committedBalance -ne $balance -or $record.pending -ne $pending -or
            $record.acknowledgements -ne $acks -or ($Receipt -and $record.receipt -cne $Receipt)) {
            throw 'Wallet, pending receipt, identity or acknowledgement result differs from the phase contract.'
        }
        if ($Phase -eq 'arm' -and
            @($script:seen | Where-Object { $_ -match " event=EXPECTED_FAILURE stage=$Scenario type=IOException$" }).Count -ne 1) {
            throw 'The requested persistence fault was not observed exactly once.'
        }
        if ($Phase -eq 'arm' -and
            @($script:seen | Where-Object { $_ -match ' event=BASELINE committedBalance=100 pending=0$' }).Count -ne 1) {
            throw 'Missing unique confirmed baseline marker.'
        }
        $reopenedBalance = if ($Scenario -eq 'wallet') { 100 } else { 110 }
        if ($Phase -eq 'recover' -and
            @($script:seen | Where-Object {
                $_ -match " event=REOPENED committedBalance=$reopenedBalance receipt=$([regex]::Escape($Receipt)) pending=1$"
            }).Count -ne 1) { throw 'Missing unique reopened wallet/receipt marker.' }
        # Kill only after the validated terminal marker, and only while that same process owns the app.
        if ((Get-ProbePid) -ne $record.pid) { throw 'Process changed before force-stop; refusing to stop it.' }
        $record.forceStopRequested = $true
        Invoke-ProbeAdb @('shell', 'am', 'force-stop', $Package) | Out-Null
        $deadline = (Get-Date).AddSeconds(10)
        do {
            if (-not (Get-ProbePid)) { break }
            Start-Sleep -Milliseconds 200
        } while ((Get-Date) -lt $deadline)
        if (Get-ProbePid) { throw 'App still has a PID after force-stop.' }
        Invoke-ProbeAdb @('shell', 'test', '!', '-d', "/proc/$($record.pid)") | Out-Null
        $record.pidEnded = $true
        $record.stoppedUtc = (Get-Date).ToUniversalTime().ToString('o')
        $record.passed = $true
        Write-Host "$Variant/$Scenario/$Phase pid=$($record.pid) checks=$($record.checks) receipt=$($record.receipt) ended=True"
        return $record
    }
    catch {
        $record.failure = "$_"
        # Do not remove a collision, repair a failed store, or kill an unmarked/unidentified process on failure.
        throw
    }
    finally {
        $script:seen | Set-Content -LiteralPath $logPath -Encoding utf8
        $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'persistence-probe.json') -Encoding utf8
    }
}

try {
    if ($RunName -notmatch '\A[A-Za-z0-9_-]+\z') { throw 'RunName must be a simple folder name.' }
    if ($ExpectedCommit -notmatch '\A[A-Fa-f0-9]{40}( \(Packages or Assets modified\))?\z') { throw 'ExpectedCommit must match the build .commit format.' }
    if (-not (Test-Path -LiteralPath $Apk -PathType Leaf) -or -not (Test-Path -LiteralPath "$Apk.commit" -PathType Leaf)) {
        throw 'The APK and its build .commit are both required.'
    }
    $Apk = (Resolve-Path -LiteralPath $Apk).Path
    $Aapt = (Get-Command $Aapt -CommandType Application).Source
    Get-Command adb.exe -CommandType Application | Out-Null
    $candidate = Join-Path $repo ('Nexus/artifacts/' + $RunName)
    if (Test-Path -LiteralPath $candidate) { throw "Refusing to reuse evidence folder: $candidate" }
    # Compare before making a copy, then again on the frozen snapshot to catch a concurrent build.
    $result.apkSha256 = (Get-FileHash -LiteralPath $Apk -Algorithm SHA256).Hash
    $result.playerCommit = (Get-Content -LiteralPath "$Apk.commit" -Raw).Trim()
    if (-not [string]::Equals($result.apkSha256, $ExpectedApkSha256, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($result.playerCommit, $ExpectedCommit, [StringComparison]::Ordinal)) {
        throw 'APK SHA256 or build .commit differs from the requested identity.'
    }
    New-Item -ItemType Directory -Path $candidate | Out-Null
    $evidence = $candidate
    $script:frozenApk = Join-Path $evidence 'probe.apk'
    Copy-Item -LiteralPath $Apk -Destination $script:frozenApk
    Copy-Item -LiteralPath "$Apk.commit" -Destination "$script:frozenApk.commit"
    Assert-ProbeIdentity
    $badging = @(& $Aapt dump badging $script:frozenApk 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) { throw 'aapt could not inspect the APK.' }
    $packages = [regex]::Matches(($badging -join "`n"), "(?m)^package: name='([^']+)'")
    $activities = [regex]::Matches(($badging -join "`n"), "(?m)^launchable-activity: name='([^']+)'")
    if ($packages.Count -ne 1 -or -not [string]::Equals($packages[0].Groups[1].Value, $Package, [StringComparison]::Ordinal)) {
        throw "Refusing APK: only EXACT package $Package is permitted."
    }
    if ($activities.Count -ne 1 -or $activities[0].Groups[1].Value -notmatch '\A[A-Za-z0-9_.$]+\z' -or
        -not ($badging -match '^application-debuggable')) { throw 'A single launcher activity and debuggable APK are required.' }
    $script:component = "$Package/$($activities[0].Groups[1].Value)"
    $result.component = $script:component
    if ((@(Invoke-ProbeAdb @('get-state')) -join '').Trim() -ne 'device') { throw 'The explicit serial is not a connected authorized device.' }
    if (Get-ProbePid) { throw 'Probe app is already running; stop it explicitly before this run. No automatic preflight kill.' }
    Assert-ProbeIdentity
    $install = @(Invoke-ProbeAdb @('install', '-r', $script:frozenApk))
    $install | Set-Content -LiteralPath (Join-Path $evidence 'install.txt') -Encoding utf8
    if (@($install | Where-Object { $_.Trim() -ceq 'Success' }).Count -ne 1) { throw 'Install did not confirm Success; no older APK may be tested.' }
    Assert-ProbeIdentity
    $exitCode = 1
    foreach ($variant in @('bool', 'outcome')) {
        foreach ($scenario in @('wallet', 'ack')) {
            $armed = Invoke-ProbePhase $variant $scenario 'arm' '' ''
            $recovered = Invoke-ProbePhase $variant $scenario 'recover' $armed.receipt $armed.pid
            $null = Invoke-ProbePhase $variant $scenario 'verify' $armed.receipt $recovered.pid
        }
    }
    Assert-ProbeIdentity
    $result.passed = $result.phases.Count -eq 12 -and @($result.phases | Where-Object {
        -not $_.passed -or -not $_.pidEnded -or $_.checks -le 0
    }).Count -eq 0
    if (-not $result.passed) { throw 'Incomplete phase set or failed restart checks.' }
    $exitCode = 0
}
catch {
    $result.failure = "$_"
    Write-Host "Persistence probe incomplete: $_"
}
finally {
    if ($evidence -and (Test-Path -LiteralPath $evidence -PathType Container)) {
        $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'persistence-probe.json') -Encoding utf8
    }
}
Write-Host "Persistence probe observed=$($result.passed) phases=$($result.phases.Count)/12 evidence=$evidence"
exit $exitCode
