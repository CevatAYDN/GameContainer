param(
    [string]$Dotnet = 'dotnet',
    [string]$UnityCli = (Join-Path $env:LOCALAPPDATA 'Unity/bin/unity.exe'),
    [string]$RunName = 'mono',
    [string]$PackagePath = '',
    [string]$ArtifactFolder = 'performance-20261007',
    [switch]$Android,
    [string]$AdbSerial = '',
    [switch]$Generated,
    # Launch only: install and run this APK on the device, without the host run or any build. Its launches can then
    # be interleaved with other builds' launches, so drift between launches is told apart from a difference between builds.
    [string]$Apk = ''
)
$ErrorActionPreference = 'Stop'
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunName must be a simple folder name.' }
if ($Apk) {
    $Apk = [IO.Path]::GetFullPath($Apk)
    if (-not (Test-Path -LiteralPath $Apk)) { throw "APK not found: $Apk" }
    $Android = $true
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$evidence = Join-Path $repo ('Nexus/artifacts/' + $ArtifactFolder + '/' + $RunName)
$project = Join-Path $repo ('Nexus/artifacts/' + $ArtifactFolder + '/UnityComparison')
$result = Join-Path $evidence 'unity-results.json'
$player = Join-Path $evidence 'player/Comparison.exe'
if (Test-Path -LiteralPath $result) { throw "Refusing to overwrite evidence: $result" }
if (Test-Path -LiteralPath (Join-Path $evidence 'android-results.json')) { throw "Refusing to overwrite evidence: $evidence" }
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$oldPlayer = $env:NEXUS_COMPARE_PLAYER
$oldResult = $env:NEXUS_COMPARE_RESULT
try {
    if (-not $Apk) {
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
        $apk = if ($Apk) { $Apk } else { Join-Path $evidence 'player-android/Comparison.apk' }
        $androidResult = Join-Path $evidence 'android-results.json'
        if (-not $Apk) {
            $env:NEXUS_COMPARE_PLAYER = $apk
            & $UnityCli run $project --non-interactive --json -- -buildTarget Android -nographics -executeMethod ComparisonBuild.BuildAndroidIl2cpp -logFile (Join-Path $evidence 'build-android-editor.log') *> (Join-Path $evidence 'build-android-cli.log')
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $apk)) { throw 'Unity Android IL2CPP build failed; inspect build-android logs.' }
        }
        $adbArgs = @(); if ($AdbSerial) { $adbArgs = @('-s', $AdbSerial) }
        $devices = @((& adb devices) | Where-Object { $_ -match "`tdevice$" })
        if (-not $AdbSerial -and $devices.Count -ne 1) { throw "Expected exactly one adb device, found $($devices.Count); pass -AdbSerial." }
        $package = 'com.nexus.comparison'
        $deviceFile = "/sdcard/Android/data/$package/files/unity-results.json"
        & adb @adbArgs install -r $apk | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'adb install failed.' }
        & adb @adbArgs shell am force-stop $package | Out-Null
        & adb @adbArgs shell rm -f $deviceFile | Out-Null
        & adb @adbArgs logcat -c | Out-Null
        $component = ((& adb @adbArgs shell cmd package resolve-activity --brief -c android.intent.category.LAUNCHER $package) | Where-Object { $_ -match '/' } | Select-Object -Last 1).Trim()
        if (-not $component) { throw 'Could not resolve the comparison app launcher activity.' }
        & adb @adbArgs shell am start -n $component | Out-Null
        $deadline = (Get-Date).AddSeconds(240); $done = $false
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 3
            $log = (& adb @adbArgs logcat -d -s Unity:I 2>$null) -join "`n"
            if ($log -match 'NEXUS_COMPARE_DONE') { $done = $true; break }
        }
        (& adb @adbArgs logcat -d -s Unity:I 2>$null) | Set-Content -LiteralPath (Join-Path $evidence 'android-logcat.log') -Encoding utf8
        & adb @adbArgs shell am force-stop $package | Out-Null
        if (-not $done) { throw 'The device benchmark did not report completion; see android-logcat.log.' }
        $json = (& adb @adbArgs exec-out cat $deviceFile) -join "`n"
        if (-not $json -or $json -notmatch 'Measurements') { throw 'Could not read the device result file.' }
        Set-Content -LiteralPath $androidResult -Value $json -Encoding utf8
        $androidReport = $json | ConvertFrom-Json
        if ($androidReport.Backend -ne 'IL2CPP' -or $androidReport.DevelopmentBuild -or $androidReport.NexusDebug -or $androidReport.Measurements.Count -ne 12 -or [bool]$androidReport.Generated -ne [bool]$Generated) {
            throw 'Unexpected Android benchmark configuration/results.'
        }
        $model = (& adb @adbArgs shell getprop ro.product.model).Trim(); $release = (& adb @adbArgs shell getprop ro.build.version.release).Trim()
        @{ model = $model; androidRelease = $release; abi = (& adb @adbArgs shell getprop ro.product.cpu.abi).Trim(); apk = $apk; apkSha256 = (Get-FileHash -LiteralPath $apk).Hash; launchOnly = [bool]$Apk } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'android-device.json') -Encoding utf8
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
                [IO.File]::WriteAllText($log.FullName, $redacted)
                break
            } catch [IO.IOException] { Start-Sleep -Seconds 3 }
        }
    }
}
