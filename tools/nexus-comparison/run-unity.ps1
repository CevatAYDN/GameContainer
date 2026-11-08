param(
    [string]$Dotnet = 'dotnet',
    [string]$UnityCli = (Join-Path $env:LOCALAPPDATA 'Unity/bin/unity.exe'),
    [string]$RunName = 'mono',
    [string]$PackagePath = '',
    [string]$ArtifactFolder = 'performance-20261007',
    [switch]$Android,
    [ValidatePattern('\A(?:[A-Za-z0-9_.:-]+)?\z')][string]$AdbSerial = '',
    [string]$Aapt = '',
    [switch]$Generated,
    # Launch only: install and run this APK on the device, without the host run or any build. Its launches can then
    # be interleaved with other builds' launches, so drift between launches is told apart from a difference between builds.
    [string]$Apk = ''
)
$ErrorActionPreference = 'Stop'
function Invoke-ComparisonAdb([string[]]$a) {
    [string[]]$arguments = if ($AdbSerial) { @('-s', $AdbSerial) + $a } else { $a }
    $startedUtc = [DateTime]::UtcNow.ToString('o')
    # Capture native stderr and exit before writing evidence (including on Windows PowerShell).
    $ErrorActionPreference = 'Continue'
    $global:LASTEXITCODE = $null
    $out = @(& adb.exe @arguments 2>&1)
    $nativeExit = $global:LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $out = @($out | ForEach-Object { "$_" })
    [ordered]@{
        arguments = $arguments; serial = $AdbSerial; runToken = $runToken; tokenScope = 'host-only'
        startedUtc = $startedUtc; endedUtc = [DateTime]::UtcNow.ToString('o')
        nativeExit = $nativeExit; outputLines = $out
    } | ConvertTo-Json -Depth 4 -Compress |
        Add-Content -LiteralPath (Join-Path $evidence 'adb-commands.jsonl') -Encoding utf8 -ErrorAction Stop
    if ($null -eq $nativeExit -or $nativeExit -ne 0) { throw "adb failed ($nativeExit): $($a -join ' '): $($out -join ' ')" }
    return $out
}
function Get-ComparisonPid {
    # pidof's exit 1 means absence only when the shell confirms it actually ran.
    $marker = "nexus-pid-$runToken"
    $out = @(Invoke-ComparisonAdb @('shell', "pidof com.nexus.comparison; rc=`$?; echo ${marker}:`$rc"))
    if ($out.Count -eq 0 -or $out[-1] -notmatch "\A$marker`:([01])\z") { throw 'No trustworthy device-side PID answer.' }
    $status = [int]$Matches[1]
    $value = (($out | Select-Object -SkipLast 1) -join ' ').Trim()
    if ($status -eq 1 -and $value -eq '') { return '' }
    if ($status -ne 0 -or $value -notmatch '\A[1-9][0-9]*\z') { throw "Expected one comparison PID, got '$value'." }
    return $value
}
function Assert-ComparisonApk {
    foreach ($path in @($sourceApk, $frozenApk)) {
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $apkHash) {
            throw "Input APK changed during the run: $path"
        }
    }
}
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunName must be a simple folder name.' }
# PowerShell names are case-insensitive: $apk below would overwrite the parameter, so it is read once here.
$launchOnly = [bool]$Apk
if ($launchOnly) {
    $existingApk = [IO.Path]::GetFullPath($Apk)
    if (-not (Test-Path -LiteralPath $existingApk)) { throw "APK not found: $existingApk" }
    $Android = $true
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ($ArtifactFolder -notmatch '\A[A-Za-z0-9_.-]+(?:[/\\][A-Za-z0-9_.-]+)*\z' -or
    @($ArtifactFolder -split '[/\\]' | Where-Object { $_.EndsWith('.') }).Count) {
    throw 'ArtifactFolder must contain relative folder names without traversal or trailing dots.'
}
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repo 'Nexus/artifacts'))
$artifactPrefix = $artifactRoot + [IO.Path]::DirectorySeparatorChar
$artifactPath = [IO.Path]::GetFullPath((Join-Path $artifactRoot $ArtifactFolder))
$evidence = [IO.Path]::GetFullPath((Join-Path $artifactPath $RunName))
$project = [IO.Path]::GetFullPath((Join-Path $artifactPath 'UnityComparison'))
foreach ($path in @($artifactPath, $evidence, $project)) {
    if (-not $path.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Evidence/project path escapes this repo's Nexus/artifacts: $path"
    }
}
$result = Join-Path $evidence 'unity-results.json'
$player = Join-Path $evidence 'player/Comparison.exe'
if ($Android -and (Test-Path -LiteralPath $evidence)) { throw "Refusing to reuse Android evidence folder: $evidence" }
if (Test-Path -LiteralPath $result) { throw "Refusing to overwrite evidence: $result" }
if (Test-Path -LiteralPath (Join-Path $evidence 'android-results.json')) { throw "Refusing to overwrite evidence: $evidence" }
New-Item -ItemType Directory -Force:(-not $Android) -Path $evidence | Out-Null
$oldPlayer = $env:NEXUS_COMPARE_PLAYER
$oldResult = $env:NEXUS_COMPARE_RESULT
$androidCandidate = ''
try {
    if (-not $launchOnly) {
        & (Join-Path $PSScriptRoot 'run.ps1') -Dotnet $Dotnet -Output (Join-Path $evidence 'host-results.json') -Generated:$Generated *> (Join-Path $evidence 'host.log')
        if ($LASTEXITCODE -ne 0) { throw 'Pinned competitor build/host comparison failed.' }
        $assets = Join-Path $project 'Assets'
        foreach ($folder in @('Runtime', 'Editor', 'Plugins')) {
            New-Item -ItemType Directory -Force -Path (Join-Path $assets $folder) | Out-Null
        }
        New-Item -ItemType Directory -Force -Path (Join-Path $project 'Packages'), (Join-Path $project 'ProjectSettings') | Out-Null
        Set-Content -LiteralPath (Join-Path $assets 'NexusComparison.marker') -Value 'Isolated core DI benchmark' -Encoding utf8
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ComparativeBenchmarks.cs') -Destination (Join-Path $assets 'Runtime')
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'unity/ComparisonBuild.cs') -Destination (Join-Path $assets 'Editor')
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin/VContainer/netstandard2.1/VContainer.dll') -Destination (Join-Path $assets 'Plugins')
        foreach ($dll in @('Zenject.dll', 'Zenject-usage.dll')) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('bin/Zenject/netstandard2.1/' + $dll)) -Destination (Join-Path $assets 'Plugins')
        }
        if (-not $PackagePath) { $PackagePath = Join-Path $repo 'Nexus/Packages/com.nexus.core' }
        $package = [IO.Path]::GetFullPath($PackagePath).Replace('\', '/')
        $sourceHash = (Get-FileHash -LiteralPath (Join-Path $package 'Runtime/Core/NexusDI.cs')).Hash
        # Reflex is a Unity package: Unity compiles its pinned source (ENABLE_MONO defined by the player) instead of a host DLL.
        $reflex = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'competitors/Reflex/Assets/Reflex')).Replace('\', '/')
        @{ dependencies = @{ 'com.nexus.core' = ('file:' + $package); 'com.gustavopsantos.reflex' = ('file:' + $reflex) } } | ConvertTo-Json -Depth 4 |
            Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') -Encoding utf8
        Copy-Item -LiteralPath (Join-Path $repo 'Nexus/ProjectSettings/ProjectVersion.txt') -Destination (Join-Path $project 'ProjectSettings')
        $env:NEXUS_COMPARE_PLAYER = $player
        $env:NEXUS_COMPARE_RESULT = $result
        $env:NEXUS_COMPARE_GENERATED = if ($Generated) { '1' } else { '' }
        if ($Generated) {
            # Use a separate -ArtifactFolder for generated runs: the analyzer and the define stay in the project.
            New-Item -ItemType Directory -Force -Path (Join-Path $assets 'Analyzers') | Out-Null
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin/VContainer.SourceGenerator/VContainer.SourceGenerator.dll') -Destination (Join-Path $assets 'Analyzers')
            & $UnityCli run $project --non-interactive --json -- -nographics -executeMethod ComparisonBuild.PrepareGenerated -logFile (Join-Path $evidence 'prepare-generated-editor.log') *> (Join-Path $evidence 'prepare-generated-cli.log')
            if ($LASTEXITCODE -ne 0) { throw 'Generated-configuration preparation failed; inspect prepare-generated logs.' }
        }
        & $UnityCli run $project --non-interactive --json -- -nographics -executeMethod ComparisonBuild.BuildMono -logFile (Join-Path $evidence 'build-editor.log') *> (Join-Path $evidence 'build-cli.log')
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $player)) { throw 'Unity Mono player build failed; inspect build logs.' }
        $playerLog = Join-Path $evidence 'player.log'
        $process = Start-Process -FilePath $player -ArgumentList @('-batchmode', '-nographics', '-logFile', ('"' + $playerLog + '"')) -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Comparison player exceeded 60 seconds.' }
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $result)) { throw 'Unity comparison did not complete successfully.' }
        $report = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
        if ($report.Backend -ne 'Mono' -or $report.DevelopmentBuild -or $report.NexusDebug -or $report.Measurements.Count -ne 12 -or [bool]$report.Generated -ne [bool]$Generated) {
            throw 'Unexpected benchmark configuration/results.'
        }
        $head = (& git -C $repo rev-parse HEAD).Trim()
        if ((Get-FileHash -LiteralPath (Join-Path $package 'Runtime/Core/NexusDI.cs')).Hash -ne $sourceHash) {
            throw 'NexusDI source changed during the comparison; evidence is not accepted.'
        }
        @{ sourceCommit = $head; nexusPackagePath = $package; nexusDiSha256 = $sourceHash;
           workingTreeDiff = ((& git -C $repo diff --stat) -join "`n"); result = $result } | ConvertTo-Json |
            Set-Content -LiteralPath (Join-Path $evidence 'source-identity.json') -Encoding utf8
        $report.Measurements | Select-Object Library, Scenario, MedianNsPerOperation, BytesPerOperation | Format-Table -AutoSize
    }

    if ($Android) {
        # Same benchmark as an Android ARM64 IL2CPP non-development player, run on the connected device.
        $apk = if ($launchOnly) { $existingApk } else { Join-Path $evidence 'player-android/Comparison.apk' }
        $androidResult = Join-Path $evidence 'android-results.json'
        if (-not $launchOnly) {
            $env:NEXUS_COMPARE_PLAYER = $apk
            & $UnityCli run $project --non-interactive --json -- -buildTarget Android -nographics -executeMethod ComparisonBuild.BuildAndroidIl2cpp -logFile (Join-Path $evidence 'build-android-editor.log') *> (Join-Path $evidence 'build-android-cli.log')
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $apk)) { throw 'Unity Android IL2CPP build failed; inspect build-android logs.' }
        }
        $runToken = [guid]::NewGuid().ToString('N') # Host transcript identity, never sent to/emitted by the player.
        $sourceApk = $apk
        $apkHash = (Get-FileHash -LiteralPath $sourceApk -Algorithm SHA256).Hash
        $frozenApk = Join-Path $evidence 'comparison-input.apk'
        Copy-Item -LiteralPath $sourceApk -Destination $frozenApk -ErrorAction Stop
        Assert-ComparisonApk
        if (-not $Aapt) {
            $buildTools = 'C:\Program Files\Unity\Hub\Editor\6000.6.8f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\build-tools'
            $Aapt = Get-ChildItem -LiteralPath $buildTools -Directory | Sort-Object Name -Descending |
                ForEach-Object { Join-Path $_.FullName 'aapt.exe' } |
                Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
            if (-not $Aapt) { throw 'aapt.exe not found; pass -Aapt.' }
        }
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = $null
        $badging = @(& $Aapt dump badging $frozenApk 2>&1 | ForEach-Object { "$_" })
        $badgingExit = $global:LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        @{ arguments = @('dump', 'badging', $frozenApk); nativeExit = $badgingExit; outputLines = $badging } |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'aapt-badging.json') -Encoding utf8
        $package = 'com.nexus.comparison'
        $packages = [regex]::Matches(($badging -join "`n"), "(?m)^package: name='([^']+)'")
        if ($null -eq $badgingExit -or $badgingExit -ne 0 -or $packages.Count -ne 1 -or
            $packages[0].Groups[1].Value -cne $package) { throw "Frozen APK must be EXACT package $package; aapt must succeed." }
        Assert-ComparisonApk
        if (-not $AdbSerial) {
            $devices = @(Invoke-ComparisonAdb @('devices') | Where-Object { $_ -match '\A[A-Za-z0-9_.:-]+\tdevice\z' })
            if ($devices.Count -ne 1) { throw "Expected exactly one adb device, found $($devices.Count); pass -AdbSerial." }
            $AdbSerial = ($devices[0] -split "`t")[0]
        }
        if ((@(Invoke-ComparisonAdb @('get-state')) -join '').Trim() -cne 'device') { throw 'Serial is not an authorized connected device.' }
        if (Get-ComparisonPid) { throw 'Comparison app already running; no automatic preflight kill.' }
        Assert-ComparisonApk
        $install = @(Invoke-ComparisonAdb @('install', '-r', $frozenApk))
        if (@($install | Where-Object { $_.Trim() -ceq 'Success' }).Count -ne 1) { throw 'Install did not confirm Success.' }
        Assert-ComparisonApk
        $paths = @(Invoke-ComparisonAdb @('shell', 'pm', 'path', $package))
        if ($paths.Count -ne 1 -or $paths[0] -notmatch '\Apackage:(/data/app/[A-Za-z0-9_./+=@~-]+/base\.apk)\z') {
            throw 'Expected exactly one installed comparison base.apk path.'
        }
        $installedApk = $Matches[1]
        $hashReply = @(Invoke-ComparisonAdb @('shell', "sha256sum '$installedApk'"))
        if ($hashReply.Count -ne 1 -or $hashReply[0] -notmatch "\A([A-Fa-f0-9]{64})\s+$([regex]::Escape($installedApk))\z") {
            throw 'Could not read the installed base.apk SHA256.'
        }
        $installedHash = $Matches[1].ToUpperInvariant()
        if ($installedHash -cne $apkHash) { throw 'Installed base.apk SHA256 differs from the snapshot.' }
        $installedBefore = @{ path = $installedApk; sha256 = $installedHash; checkedUtc = [DateTime]::UtcNow.ToString('o') }
        if (Get-ComparisonPid) { throw 'Comparison app started before owned launch; refusing to continue.' }
        $resultSuffix = "/Android/data/$package/files/unity-results.json"
        $deviceFile = '/sdcard' + $resultSuffix
        # Only the storage root must exist on a first install; Unity creates the result's parent on launch.
        $canonical = @(Invoke-ComparisonAdb @('shell', 'readlink', '-f', '/sdcard'))
        if ($canonical.Count -ne 1 -or $canonical[0] -cnotmatch '\A/storage/emulated/[0-9]+\z') {
            throw 'Could not confirm the canonical device storage root.'
        }
        $markerPath = $canonical[0] + $resultSuffix
        Invoke-ComparisonAdb @('shell', 'rm', '-f', $deviceFile) | Out-Null
        $absentMarker = "nexus-file-$runToken"
        $absence = @(Invoke-ComparisonAdb @('shell', "test -e $deviceFile; rc=`$?; echo ${absentMarker}:`$rc"))
        if ($absence.Count -ne 1 -or $absence[0] -cne "${absentMarker}:1") { throw 'Old benchmark result is not confirmed absent.' }
        Invoke-ComparisonAdb @('logcat', '-c') | Out-Null
        $activity = @(Invoke-ComparisonAdb @('shell', 'cmd', 'package', 'resolve-activity', '--brief', '-c', 'android.intent.category.LAUNCHER', $package))
        $components = @($activity | Where-Object { $_ -cmatch "\A$([regex]::Escape($package))/[A-Za-z0-9_.$]+\z" })
        if ($components.Count -ne 1 -or ($activity -match 'Error:|Exception')) { throw 'Could not resolve one comparison launcher activity.' }
        $component = $components[0]
        $ownedPid = ''
        $termination = [ordered]@{ serial = $AdbSerial; runToken = $runToken; pid = ''; forceStopRequested = $false; pidEnded = $false; stoppedUtc = ''; failure = '' }
        try {
            if (Get-ComparisonPid) { throw 'App appeared before launch; refusing to launch over another owner.' }
            $launch = @(Invoke-ComparisonAdb @('shell', 'am', 'start', '-W', '-n', $component))
            if (-not ($launch -cmatch '\AStatus: ok\z') -or ($launch -match 'Error:|Exception|Warning: Activity not started')) { throw 'Activity launch did not confirm success.' }
            $deadline = (Get-Date).AddSeconds(10)
            do {
                $ownedPid = Get-ComparisonPid
                if ($ownedPid) { break }
                Start-Sleep -Milliseconds 200
            } while ((Get-Date) -lt $deadline)
            if (-not $ownedPid) { throw 'No new comparison PID observed after launch.' }
            $termination.pid = $ownedPid
            $deadline = (Get-Date).AddSeconds(240)
            do {
                $log = @(Invoke-ComparisonAdb @('logcat', '-d', "--pid=$ownedPid", '-v', 'threadtime', '-s', 'Unity:I'))
                $log | Set-Content -LiteralPath (Join-Path $evidence 'android-logcat.log') -Encoding utf8
                $markers = @($log | Where-Object {
                    $_ -cmatch "\A\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d+\s+$ownedPid\s+\d+\s+I\s+Unity\s*:\s+NEXUS_COMPARE_DONE $([regex]::Escape($markerPath))\z"
                })
                if ($markers.Count -eq 1) { break }
                if ($markers.Count -gt 1) { throw 'Duplicate comparison completion markers.' }
                if ((Get-ComparisonPid) -cne $ownedPid) { throw 'Owned process ended/changed without its exact completion marker.' }
                Start-Sleep -Milliseconds 200
            } while ((Get-Date) -lt $deadline)
            if ($markers.Count -ne 1) { throw 'Device benchmark did not report scoped completion.' }
            $json = (@(Invoke-ComparisonAdb @('exec-out', 'cat', $deviceFile)) -join "`n")
            if (-not $json) { throw 'Empty device result file.' }
            $androidReport = $json | ConvertFrom-Json
            if ($androidReport.Backend -ne 'IL2CPP' -or $androidReport.DevelopmentBuild -or $androidReport.NexusDebug -or $androidReport.Measurements.Count -ne 12 -or [bool]$androidReport.Generated -ne [bool]$Generated) {
                throw 'Unexpected Android benchmark configuration/results.'
            }
        } finally {
            try {
                if ($ownedPid) {
                    $currentPid = Get-ComparisonPid
                    if ($currentPid) {
                        if ($currentPid -cne $ownedPid) { throw 'Process ownership changed; refusing force-stop.' }
                        $termination.forceStopRequested = $true
                        Invoke-ComparisonAdb @('shell', 'am', 'force-stop', $package) | Out-Null
                        if (Get-ComparisonPid) { throw 'App still running after checked force-stop.' }
                    }
                    Invoke-ComparisonAdb @('shell', 'test', '!', '-d', "/proc/$ownedPid") | Out-Null
                    $termination.pidEnded = $true
                    $termination.stoppedUtc = [DateTime]::UtcNow.ToString('o')
                }
            } catch {
                $termination.failure = "$_"
                throw
            } finally {
                $termination | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'android-termination.json') -Encoding utf8
            }
        }
        $postPaths = @(Invoke-ComparisonAdb @('shell', 'pm', 'path', $package))
        if ($postPaths.Count -ne 1 -or $postPaths[0] -notmatch '\Apackage:(/data/app/[A-Za-z0-9_./+=@~-]+/base\.apk)\z') {
            throw 'Expected exactly one post-run installed comparison base.apk path.'
        }
        $postInstalledApk = $Matches[1]
        $postHashReply = @(Invoke-ComparisonAdb @('shell', "sha256sum '$postInstalledApk'"))
        if ($postHashReply.Count -ne 1 -or $postHashReply[0] -notmatch "\A([A-Fa-f0-9]{64})\s+$([regex]::Escape($postInstalledApk))\z") {
            throw 'Could not read the post-run installed base.apk SHA256.'
        }
        $postInstalledHash = $Matches[1].ToUpperInvariant()
        if ($postInstalledHash -cne $installedHash) { throw 'Post-run installed base.apk SHA256 differs from the snapshot.' }
        $installedAfter = @{ path = $postInstalledApk; sha256 = $postInstalledHash; checkedUtc = [DateTime]::UtcNow.ToString('o') }
        $model = (@(Invoke-ComparisonAdb @('shell', 'getprop', 'ro.product.model')) -join '').Trim()
        $release = (@(Invoke-ComparisonAdb @('shell', 'getprop', 'ro.build.version.release')) -join '').Trim()
        $abi = (@(Invoke-ComparisonAdb @('shell', 'getprop', 'ro.product.cpu.abi')) -join '').Trim()
        if (-not $model -or -not $release -or -not $abi) { throw 'Missing device identity reply.' }
        Assert-ComparisonApk
        # The raw candidate is retained separately; android-results.json is published last, after every native check.
        $candidate = Join-Path $evidence 'android-result-candidate.json'
        Set-Content -LiteralPath $candidate -Value $json -Encoding utf8
        @{ model = $model; androidRelease = $release; abi = $abi; serial = $AdbSerial; pid = $ownedPid
           package = $package; component = $component; runToken = $runToken; tokenScope = 'host-only'
           apk = $frozenApk; sourceApk = $sourceApk; apkSha256 = $apkHash; installedApk = $installedApk
           installedApkSha256 = $installedHash; installedBefore = $installedBefore; installedAfter = $installedAfter
           resultSha256 = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
           resultDevicePath = $markerPath; completionMarker = $markers[0]; termination = $termination; launchOnly = $launchOnly } |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'android-device.json') -Encoding utf8
        $androidCandidate = $candidate
        Write-Host "Android ($model, Android $release, IL2CPP):"
        $androidReport.Measurements | Select-Object Library, Scenario, MedianNsPerOperation | Format-Table -AutoSize
    }
} finally {
    $env:NEXUS_COMPARE_PLAYER = $oldPlayer
    $env:NEXUS_COMPARE_RESULT = $oldResult
    foreach ($log in Get-ChildItem -LiteralPath $evidence -Filter '*.log') {
        # The editor can still hold its log for a moment after the CLI returns.
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            try {
                $raw = [IO.File]::ReadAllText($log.FullName)
                $redacted = [regex]::Replace($raw, '-accessToken\s+\S+', '-accessToken [REDACTED]')
                if ($raw -cne $redacted) { [IO.File]::WriteAllText($log.FullName, $redacted) }
                break
            } catch [IO.IOException] {
                if ($attempt -eq 19) { throw }
                Start-Sleep -Seconds 3
            }
        }
    }
}
if ($androidCandidate) {
    $moveSource = [IO.Path]::GetFullPath($androidCandidate)
    $moveDestination = [IO.Path]::GetFullPath($androidResult)
    $moveDirectory = [IO.Path]::GetFullPath($evidence)
    if (-not $moveDirectory.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Final evidence directory escapes this repo artifact root.'
    }
    foreach ($path in @($moveSource, $moveDestination)) {
        if (-not [string]::Equals([IO.Path]::GetDirectoryName($path), $moveDirectory, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Final result move must stay in its verified evidence directory: $path"
        }
    }
    Move-Item -LiteralPath $moveSource -Destination $moveDestination -ErrorAction Stop
}
