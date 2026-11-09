param([switch]$NativeReply, [string]$Reply = '', [int]$Code = 0)
if ($NativeReply) {
    $lines = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Reply)) | ConvertFrom-Json
    foreach ($line in $lines) { [Console]::Out.WriteLine($line) }
    if ($Code) { [Console]::Error.WriteLine('native transport "failed"') }
    exit $Code
}
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'run-unity.ps1'))
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw "Runner parse errors: $($errors -join '; ')" }
$repoAssignment = $ast.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -ceq '$repo'
} | Select-Object -First 1
if (-not $repoAssignment) { throw 'Could not isolate the runner filesystem root.' }
# Run the entire launch-only entry point, redirecting only its repository root into this test's temp directory.
$sandboxSource = $source.Remove($repoAssignment.Extent.StartOffset, $repoAssignment.Extent.Text.Length).
    Insert($repoAssignment.Extent.StartOffset, '$repo = $testRepo')
$runner = [scriptblock]::Create($sandboxSource)
$temp = Join-Path ([IO.Path]::GetTempPath()) ('comparison-evidence-test-' + [guid]::NewGuid().ToString('N') + ' with spaces')
New-Item -ItemType Directory -Path $temp | Out-Null
$script:checks = 0
function Assert-Test($condition, [string]$message) {
    if (-not $condition) { throw $message }
    $script:checks++
}
function fake-aapt {
    $script:aaptCalls++
    $global:LASTEXITCODE = if ($script:scenario -eq 'failed-aapt') { 7 } else { 0 }
    if ($script:scenario -eq 'empty-aapt') { return }
    $name = if ($script:scenario -eq 'wrong-package') { 'com.example.game' } else { 'com.nexus.comparison' }
    "package: name='$name' versionCode='1'"
}
function adb.exe {
    $original = @($args)
    $a = @($args)
    if ($a.Count -ge 2 -and $a[0] -eq '-s') { $a = @($a | Select-Object -Skip 2) }
    $script:calls.Add(($a -join ' '))
    $script:arguments.Add($original)
    $command = $a -join ' '
    $global:LASTEXITCODE = if ($script:scenario -eq 'missing-native-exit') { $null } else { 0 }
    $failCommands = @{
        'failed-devices' = '^devices$'; 'failed-device' = '^get-state$'
        'failed-start-server' = '^start-server$'
        'failed-install' = '^install '; 'failed-path' = '^shell pm path '
        'failed-hash' = '^shell sha256sum '; 'failed-clear' = '^shell rm '
        'failed-readlink' = '^shell readlink '
        'failed-absence' = '^shell test -e '; 'failed-log-clear' = '^logcat -c$'
        'failed-resolve' = '^shell cmd package '; 'failed-start' = '^shell am start '
        'failed-log' = '^logcat -d '; 'failed-result' = '^exec-out cat '
        'failed-model' = '^shell getprop ro.product.model'
        'failed-release' = '^shell getprop ro.build.version.release'
        'failed-abi' = '^shell getprop ro.product.cpu.abi'
        'failed-stop' = '^shell am force-stop '; 'failed-proc' = '^shell test ! -d '
        'failed-pid' = '^shell pidof '
    }
    if ($failCommands.ContainsKey($script:scenario) -and $command -match $failCommands[$script:scenario]) {
        $global:LASTEXITCODE = 7
        'native transport "failed"'
        return
    }
    if ($script:scenario -eq 'failed-termination-pid' -and $command -match '^shell pidof ' -and $script:reads) {
        $global:LASTEXITCODE = 7
        'transport lost after completion'; return
    }
    if ($script:terminated -and
        (($script:scenario -eq 'failed-post-path' -and $command -match '^shell pm path ') -or
         ($script:scenario -eq 'failed-post-hash' -and $command -match '^shell sha256sum '))) {
        $global:LASTEXITCODE = 7
        'post-run identity transport failed'; return
    }
    if ($script:scenario -eq 'transcript-write-failed') {
        New-Item -ItemType Directory -Path (Join-Path $script:caseEvidence 'adb-commands.jsonl') -Force | Out-Null
    }
    $output = @(& { switch -Regex ($command) {
        '^devices$' { "List of devices attached"; "fake-serial`tdevice"; return }
        '^start-server$' {
            if ($script:scenario -eq 'cold-daemon' -and -not $script:daemonWarm) {
                '* daemon not running; starting now at tcp:5037'
                '* daemon started successfully'
            }
            $script:daemonWarm = $true
            return
        }
        '^get-state$' {
            if ($script:scenario -eq 'cold-daemon' -and -not $script:daemonWarm) {
                '* daemon not running; starting now at tcp:5037'
                '* daemon started successfully'
                $script:daemonWarm = $true
            }
            'device'; return
        }
        '^install -r ' {
            $script:installs++
            if ($script:scenario -eq 'mutated-input') { [IO.File]::AppendAllText($script:inputApk, 'changed') }
            'Success'; return
        }
        '^shell pidof ' {
            $marker = [regex]::Match($a[-1], 'echo ([^: ]+):').Groups[1].Value
            if ($script:scenario -eq 'empty-pid') { return }
            if ($script:scenario -eq 'no-new-pid') { "${marker}:1"; return }
            if ($script:scenario -eq 'multiple-pids') { '1234 5678'; "${marker}:0"; return }
            if ($script:scenario -eq 'foreign-owner' -or ($script:started -and
                (-not $script:completed -or $script:scenario -in @('success-stop', 'failed-stop')))) { '1234'; "${marker}:0" }
            elseif ($script:scenario -eq 'changed-owner' -and $script:completed) { '9999'; "${marker}:0" }
            else { "${marker}:1" }
            return
        }
        '^shell test -e ' {
            $marker = [regex]::Match($a[-1], 'echo ([^: ]+):').Groups[1].Value
            "${marker}:$(if ($script:stale) { 0 } else { 1 })"; return
        }
        '^shell rm -f ' {
            if ($script:scenario -ne 'stale-remains') { $script:stale = $false }
            return
        }
        '^logcat -c$' { return }
        '^shell readlink -f ' {
            if ($a[-1] -eq '/sdcard') {
                $script:rootQueries++
                if ($script:scenario -eq 'wrong-readlink') { '/sdcard/another' }
                else { '/storage/emulated/0' }
            }
            elseif ($script:scenario -eq 'clean-first-install') {
                $global:LASTEXITCODE = 1
                'result parent does not exist'; return
            }
            elseif ($script:scenario -eq 'wrong-readlink') { '/sdcard/another-result.json' }
            else { '/storage/emulated/0/Android/data/com.nexus.comparison/files/unity-results.json' }
            return
        }
        '^shell cmd package resolve-activity ' { 'com.nexus.comparison/com.unity3d.player.UnityPlayerActivity'; return }
        '^shell am start ' {
            $script:started = $true
            if ($script:scenario -eq 'start-error') { 'Error: activity not started'; return }
            'Starting: Intent'; 'Status: ok'; return
        }
        '^logcat -d ' {
            if ($script:scenario -eq 'no-marker-running') { 'fixture without completion'; return }
            $script:completed = $true
            if ($script:scenario -eq 'empty-log') { return }
            $pidValue = if ($script:scenario -eq 'wrong-pid') { '9876' } else { '1234' }
            $path = if ($script:scenario -eq 'wrong-marker-path') { '/sdcard/another-result.json' } else {
                '/storage/emulated/0/Android/data/com.nexus.comparison/files/unity-results.json'
            }
            "11-09 00:01:02.003  $pidValue  5678 I Unity   : NEXUS_COMPARE_DONE $path"
            if ($script:scenario -eq 'duplicate-marker') {
                "11-09 00:01:02.004  $pidValue  5678 I Unity   : NEXUS_COMPARE_DONE $path"
            }
            return
        }
        '^shell am force-stop ' { $script:started = $false; return }
        '^exec-out cat ' {
            $script:reads++
            if ($script:scenario -ne 'empty-result') { $script:json }
            return
        }
        '^shell pm path ' {
            $script:identityReads++
            if ($script:terminated -and $script:scenario -eq 'empty-post-path') { return }
            $script:installedPath = if ($script:terminated -and $script:scenario -eq 'success-post-path') {
                '/data/app/newfixture==/com.nexus.comparison-b==/base.apk'
            } else { '/data/app/fixture==/com.nexus.comparison-a==/base.apk' }
            "package:$script:installedPath"; return
        }
        '^shell sha256sum ' {
            if ($script:terminated -and $script:scenario -eq 'empty-post-hash') { return }
            $hash = if ($script:scenario -eq 'wrong-installed-hash' -or
                ($script:terminated -and $script:scenario -eq 'wrong-post-hash')) { '0' * 64 } else { $script:apkHash }
            "$hash  $script:installedPath"; return
        }
        '^shell test ! -d ' { $script:terminated = $true; return }
        '^shell getprop ' { 'fake-device'; return }
        default { throw "Unexpected fake command: $command" }
    } })
    $emptyCommands = @{
        'empty-device' = '^get-state$'; 'empty-install' = '^install '
        'empty-path' = '^shell pm path '; 'empty-hash' = '^shell sha256sum '
        'empty-absence' = '^shell test -e '; 'empty-resolve' = '^shell cmd package '
        'empty-start' = '^shell am start '; 'empty-model' = '^shell getprop ro.product.model'
        'empty-readlink' = '^shell readlink '
    }
    if ($emptyCommands.ContainsKey($script:scenario) -and $command -match $emptyCommands[$script:scenario]) { return }
    if ($script:scenario -in @('native-success', 'native-failure') -and $command -eq 'get-state') {
        $lines = if ($script:scenario -eq 'native-success') { @('device') } else {
            @('quote:" path:C:\fixture\save', "tabs`tand spaces  ", '')
        }
        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $lines -Compress)))
        $nativeHost = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
        & $nativeHost -NoProfile -File $script:testFile -NativeReply -Reply $encoded -Code $(if ($script:scenario -eq 'native-failure') { 7 } else { 0 })
        return
    }
    $output
}
function Start-Sleep { param($Seconds, $Milliseconds) }
function Get-Date {
    $script:clock = $script:clock.AddSeconds(60)
    $script:clock
}
function Add-Content {
    param($LiteralPath, [Parameter(ValueFromPipeline = $true)]$Value, $Encoding)
    process {
        Microsoft.PowerShell.Management\Add-Content -LiteralPath $LiteralPath -Value $Value -Encoding $Encoding -ErrorAction Stop
        $global:LASTEXITCODE = 99 # Evidence writes must not overwrite the already captured native exit.
    }
}
function Set-Content {
    param($LiteralPath, [Parameter(ValueFromPipeline = $true)]$Value, $Encoding)
    process {
        if ($script:scenario -eq 'metadata-write-failed' -and $LiteralPath -like '*android-device.json') { throw 'fake metadata write failed' }
        Microsoft.PowerShell.Management\Set-Content -LiteralPath $LiteralPath -Value $Value -Encoding $Encoding -ErrorAction Stop
    }
}
function Get-ChildItem {
    param($LiteralPath, $Filter)
    if ($script:scenario -eq 'escape-move-source') {
        Set-Variable -Name androidCandidate -Value $script:escapeSource -Scope 1
    }
    if ($script:scenario -eq 'escape-move-target') {
        Set-Variable -Name androidResult -Value $script:escapeDestination -Scope 1
    }
    Microsoft.PowerShell.Management\Get-ChildItem -LiteralPath $LiteralPath -Filter $Filter -ErrorAction Stop
}
function Move-Item {
    param($LiteralPath, $Destination)
    $script:moves++
    Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination -ErrorAction Stop
}
try {
    $script:testFile = $PSCommandPath
    $testRepo = Join-Path $temp 'repo'
    New-Item -ItemType Directory -Path $testRepo | Out-Null
    $inputApk = Join-Path $temp 'input with spaces.apk'
    $script:inputApk = $inputApk
    $script:escapeSource = Join-Path $temp 'outside-candidate.json'
    $script:escapeDestination = Join-Path $temp 'outside-result.json'
    [IO.File]::WriteAllText($script:escapeSource, 'unrelated evidence sentinel')
    $report = @{
        Backend = 'IL2CPP'; DevelopmentBuild = $false; NexusDebug = $false; Generated = $false
        Measurements = @(1..12 | ForEach-Object { @{ Library = 'fake'; Scenario = 'fake'; MedianNsPerOperation = 1 } })
    }
    $cases = @('escape-artifact', 'escape-artifact-sibling', 'absolute-artifact', 'escape-move-source', 'escape-move-target', 'nested-evidence',
        'clean-first-install', 'wrong-post-hash', 'failed-post-path', 'failed-post-hash', 'empty-post-path', 'empty-post-hash', 'success-post-path',
        'cold-daemon', 'failed-start-server',
        'success', 'success-stop', 'success-generated', 'native-success', 'native-failure', 'wrong-package', 'failed-aapt', 'empty-aapt',
        'failed-devices', 'failed-device', 'failed-install', 'failed-path', 'failed-hash', 'failed-clear', 'failed-absence',
        'failed-log-clear', 'failed-resolve', 'failed-start', 'failed-log', 'failed-result', 'failed-model', 'failed-release',
        'failed-abi', 'failed-stop', 'failed-proc', 'failed-pid', 'failed-termination-pid', 'failed-readlink',
        'empty-device', 'empty-install', 'empty-path', 'empty-hash', 'empty-absence', 'empty-resolve', 'empty-start',
        'empty-model', 'empty-pid', 'empty-log', 'empty-result', 'foreign-owner', 'changed-owner',
        'stale-remains', 'wrong-pid', 'wrong-marker-path', 'wrong-installed-hash', 'mutated-input',
        'wrong-config', 'transcript-write-failed', 'metadata-write-failed', 'empty-readlink', 'wrong-readlink',
        'missing-native-exit', 'start-error', 'multiple-pids', 'no-new-pid', 'no-marker-running', 'duplicate-marker')
    foreach ($case in $cases) {
        $script:scenario = $case
        [IO.File]::WriteAllText($inputApk, 'fake comparison APK')
        $script:apkHash = (Get-FileHash -LiteralPath $inputApk).Hash
        $report.Generated = $case -in @('wrong-config', 'success-generated')
        $script:json = $report | ConvertTo-Json -Depth 4
        $script:started = $script:completed = $false
        $script:terminated = $false
        $script:daemonWarm = $false
        $script:identityReads = 0
        $script:moves = 0
        $script:stale = $case -ne 'clean-first-install'
        $script:rootQueries = 0
        $script:installs = $script:reads = $script:aaptCalls = 0
        $script:calls = [Collections.Generic.List[string]]::new()
        $script:arguments = [Collections.Generic.List[object]]::new()
        $script:clock = [DateTime]::UtcNow
        $failure = ''
        $parameters = @{ Apk = $inputApk; RunName = $case; ArtifactFolder = 'fake'; AdbSerial = 'fake-serial'; Aapt = 'fake-aapt' }
        if ($case -in @('success', 'failed-devices')) { $parameters.AdbSerial = '' }
        if ($case -eq 'success-generated') { $parameters.Generated = $true }
        if ($case -eq 'escape-artifact') { $parameters.ArtifactFolder = '../outside' }
        if ($case -eq 'escape-artifact-sibling') { $parameters.ArtifactFolder = '../artifacts-other' }
        if ($case -eq 'absolute-artifact') { $parameters.ArtifactFolder = Join-Path $temp 'outside' }
        if ($case -eq 'nested-evidence') { $parameters.ArtifactFolder = 'nested/proof' }
        $script:caseEvidence = Join-Path $testRepo "Nexus/artifacts/fake/$case"
        if ($case -ne 'absolute-artifact') {
            $script:caseEvidence = [IO.Path]::GetFullPath((Join-Path $testRepo "Nexus/artifacts/$($parameters.ArtifactFolder)/$case"))
        }
        try { & $runner @parameters | Out-Null } catch { $failure = $_.Exception.Message }
        $evidence = $script:caseEvidence
        if ($case -in @('success', 'success-stop', 'success-generated', 'native-success', 'success-post-path', 'clean-first-install', 'nested-evidence', 'cold-daemon')) {
            Assert-Test ($failure -eq '') "$case failed: $failure"
            if ($case -eq 'cold-daemon') {
                Assert-Test ($script:calls.IndexOf('start-server') -ge 0 -and
                    $script:calls.IndexOf('start-server') -lt $script:calls.IndexOf('get-state')) 'Daemon initialization did not precede strict device-state validation.'
            }
            $device = Get-Content -LiteralPath (Join-Path $evidence 'android-device.json') -Raw | ConvertFrom-Json
            Assert-Test (Test-Path -LiteralPath (Join-Path $evidence 'android-results.json')) "$case did not publish results."
            Assert-Test ($device.serial -ceq 'fake-serial' -and $device.pid -ceq '1234' -and $device.tokenScope -ceq 'host-only') 'Missing actual process/device identity or host-only token scope.'
            Assert-Test ($device.termination.pidEnded -and $device.termination.forceStopRequested -eq ($case -eq 'success-stop')) 'Termination outcome not corroborated.'
            Assert-Test ($device.apkSha256 -ceq $script:apkHash -and $device.installedApkSha256 -ceq $script:apkHash -and
                (Get-FileHash -LiteralPath $device.apk).Hash -ceq $script:apkHash) 'Snapshot/installed hash chain failed.'
            Assert-Test ($script:identityReads -eq 2 -and $device.installedBefore.sha256 -ceq $script:apkHash -and
                $device.installedAfter.sha256 -ceq $script:apkHash -and
                [DateTimeOffset]$device.installedAfter.checkedUtc -ge [DateTimeOffset]$device.termination.stoppedUtc) 'Before/after installed identity was not checked after termination.'
            Assert-Test (($device.installedBefore.path -cne $device.installedAfter.path) -eq ($case -eq 'success-post-path')) 'Actual before/after installed paths were not retained.'
            Assert-Test ($script:rootQueries -eq 1) 'Existing storage root was not resolved independently of the result parent.'
            Assert-Test ((Get-FileHash -LiteralPath (Join-Path $evidence 'android-results.json')).Hash -ceq $device.resultSha256) 'Published result hash mismatch.'
            Assert-Test ($script:moves -eq 1 -and $device.apk -ceq [IO.Path]::GetFullPath($device.apk)) 'Evidence publication did not use normalized absolute paths.'
            $records = @(Get-Content -LiteralPath (Join-Path $evidence 'adb-commands.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
            Assert-Test ($records.Count -eq $script:calls.Count) 'Native invocations missing from JSONL.'
            Assert-Test (@($records | Where-Object { @($_.outputLines).Count -eq 0 }).Count -gt 0) 'Empty successful native replies were not retained.'
            for ($i = 0; $i -lt $records.Count; $i++) {
                $r = $records[$i]
                Assert-Test ((ConvertTo-Json -InputObject @($r.arguments) -Compress) -ceq
                    (ConvertTo-Json -InputObject @($script:arguments[$i]) -Compress)) 'Recorded argument boundaries changed.'
                Assert-Test ($r.nativeExit -eq 0 -and $r.runToken -ceq $device.runToken -and
                    [DateTimeOffset]$r.startedUtc -le [DateTimeOffset]$r.endedUtc -and
                    ([DateTimeOffset]$r.startedUtc).Offset -eq [TimeSpan]::Zero -and
                    ([DateTimeOffset]$r.endedUtc).Offset -eq [TimeSpan]::Zero) 'Command exit/token/UTC interval invalid.'
                if ($script:calls[$i] -ne 'devices') {
                    Assert-Test ($r.serial -ceq 'fake-serial' -and $r.arguments[0] -ceq '-s' -and
                        $r.arguments[1] -ceq 'fake-serial') 'Resolved serial not explicitly used for a device command.'
                }
            }
            continue
        }
        Assert-Test ($failure -ne '') "$case was accepted."
        Assert-Test (-not (Test-Path -LiteralPath (Join-Path $evidence 'android-results.json'))) "$case wrote successful results."
        if ($case -notin @('failed-model', 'failed-release', 'failed-abi', 'failed-stop', 'failed-proc',
            'failed-termination-pid', 'empty-model', 'empty-result', 'changed-owner', 'wrong-config', 'metadata-write-failed',
            'wrong-post-hash', 'failed-post-path', 'failed-post-hash', 'empty-post-path', 'empty-post-hash',
            'escape-move-source', 'escape-move-target')) {
            Assert-Test ($script:reads -eq 0) "$case read JSON after a failed freshness boundary."
        }
        if ($case -in @('escape-artifact', 'escape-artifact-sibling', 'absolute-artifact')) {
            Assert-Test ($script:installs -eq 0 -and -not (Test-Path -LiteralPath $evidence)) 'Escaping artifact path reached evidence writes or install.'
        }
        if ($case -in @('escape-move-source', 'escape-move-target')) {
            Assert-Test ($script:moves -eq 0 -and -not (Test-Path -LiteralPath $script:escapeDestination) -and
                [IO.File]::ReadAllText($script:escapeSource) -ceq 'unrelated evidence sentinel') 'Escaping final move touched an unrelated path.'
        }
        if ($case -in @('wrong-package', 'failed-aapt', 'empty-aapt', 'foreign-owner')) {
            Assert-Test ($script:installs -eq 0) "$case attempted installation."
        }
        if ($case -eq 'foreign-owner' -or $case -eq 'changed-owner') {
            Assert-Test (-not ($script:calls -match '^shell am force-stop ')) "$case stopped a foreign owner."
        }
        if ($case -in @('failed-stop', 'failed-proc', 'failed-termination-pid', 'changed-owner')) {
            $ended = Get-Content -LiteralPath (Join-Path $evidence 'android-termination.json') -Raw | ConvertFrom-Json
            Assert-Test (-not $ended.pidEnded -and $ended.failure -ne '') 'Failed termination evidence was lost or labelled ended.'
        }
        if ($case -eq 'native-failure') {
            $r = Get-Content -LiteralPath (Join-Path $evidence 'adb-commands.jsonl') | Select-Object -Last 1 | ConvertFrom-Json
            Assert-Test ($r.nativeExit -eq 7 -and $r.outputLines[0] -ceq 'quote:" path:C:\fixture\save' -and
                $r.outputLines[1] -ceq "tabs`tand spaces  " -and $r.outputLines[2] -ceq '' -and
                @($r.outputLines | Where-Object { $_ -ceq 'native transport "failed"' }).Count -eq 1) "Native failure/stderr/escaping not retained: $($r | ConvertTo-Json -Compress -Depth 4)"
        }
    }
    $preserved = Join-Path $testRepo 'Nexus/artifacts/fake/success/android-results.json'
    $preservedHash = (Get-FileHash -LiteralPath $preserved).Hash
    $script:scenario = 'evidence-reuse'
    $script:calls.Clear()
    $failure = ''
    try { & $runner -Apk $inputApk -RunName success -ArtifactFolder fake -Aapt fake-aapt | Out-Null }
    catch { $failure = $_.Exception.Message }
    Assert-Test ($failure -ne '') 'Previously successful evidence folder was reused.'
    Assert-Test ($script:calls.Count -eq 0) 'Evidence reuse reached device commands.'
    Assert-Test ((Get-FileHash -LiteralPath $preserved).Hash -ceq $preservedHash) 'Existing result evidence changed.'
    "PASS checks=$script:checks scenarios=$($cases.Count + 1) deviceCalls=0"
}
finally {
    $resolved = [IO.Path]::GetFullPath($temp)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'comparison-evidence-test-*') { throw 'Unsafe regression cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
