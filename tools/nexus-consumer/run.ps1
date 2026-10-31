param(
    [string]$RunName = ('consumer-' + (Get-Date -Format 'yyyyMMdd-HHmm')),
    [string]$UnityEditor = 'C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe',
    [string]$UnityCli = (Join-Path $env:LOCALAPPDATA 'Unity/bin/unity.exe'),
    [switch]$BuildPlayer,
    [switch]$BuildAndroid,
    [switch]$ValidatePerformance
)
# Clean UPM consumer gate: pack the embedded package into a tarball, create a fresh project that
# installs it, import the Code-first sample, run the smoke tests, optionally build and run a
# Windows Mono player and build an Android IL2CPP player. Evidence lands under
# Nexus/artifacts/<RunName>/ (git-ignored). The development project is only read.
$ErrorActionPreference = 'Stop'
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunName must be a simple folder name.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$devProject = Join-Path $repo 'Nexus'
$evidence = Join-Path $repo ('Nexus/artifacts/' + $RunName)
if (Test-Path -LiteralPath $evidence) { throw "Refusing to reuse evidence folder: $evidence" }
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$steps = [System.Collections.Generic.List[object]]::new()
function Save-Steps { $steps | ConvertTo-Json -Depth 6 -AsArray | Set-Content -LiteralPath (Join-Path $evidence 'consumer-results.json') -Encoding utf8 }

# 1. Pack the package as a game pinned to this commit receives it: git archive of the tracked files at HEAD.
# Uncommitted changes and git-ignored build outputs (SourceGenerator~/bin, obj) are not in a git URL
# install, so the gate refuses to run with uncommitted changes under the package instead of testing other content.
$packDir = Join-Path $evidence 'package'
New-Item -ItemType Directory -Path $packDir -Force | Out-Null
$packagePath = 'Nexus/Packages/com.nexus.core'
$dirty = & git -C $repo status --porcelain -- $packagePath
if ($LASTEXITCODE -ne 0) { throw 'git status failed.' }
if ($dirty) { throw ("Uncommitted changes under ${packagePath}; the gate packs HEAD. Commit or stash them first:`n" + ($dirty -join "`n")) }
$commit = (& git -C $repo rev-parse HEAD).Trim()
$version = ((& git -C $repo show "HEAD:$packagePath/package.json") -join "`n" | ConvertFrom-Json).version
$archivePath = Join-Path $packDir ('com.nexus.core-' + $version + '.tgz')
& git -C $repo archive --format=tar.gz --prefix=package/ -o $archivePath "HEAD:$packagePath"
$steps.Add([ordered]@{ stage = 'pack'; exitCode = $LASTEXITCODE; commit = $commit; archive = $archivePath }); Save-Steps
if ($LASTEXITCODE -ne 0) { throw "git archive failed (exit $LASTEXITCODE)." }
$archive = Get-Item -LiteralPath $archivePath

# 2. Fresh consumer project.
$prepare = @('--archive', $archive.FullName, '--output', $evidence)
if ($ValidatePerformance) { $prepare += '--testables' }
& python -I (Join-Path $PSScriptRoot 'prepare_clean_project.py') @prepare
if ($LASTEXITCODE -ne 0) { throw 'prepare_clean_project.py failed.' }
$consumer = Join-Path $evidence 'CleanConsumer'

function Invoke-UnityStage([string]$name, [string[]]$cliArguments) {
    $commandLog = Join-Path $evidence ($name + '-cli.log')
    & $UnityCli @cliArguments *> $commandLog
    $stageExit = $LASTEXITCODE
    $steps.Add([ordered]@{ stage = $name; exitCode = $stageExit; cliLog = $commandLog }); Save-Steps
    Get-Content -LiteralPath $commandLog -Tail 8
    if ($stageExit -ne 0) { throw "Stage '$name' did not pass (exit $stageExit); see $commandLog" }
}
function Read-EvidenceLog([string]$path) {
    # The last stage's editor can still hold its log for a moment after the Unity CLI returns (#108).
    for ($attempt = 1; ; $attempt++) {
        try { return [IO.File]::ReadAllText($path) }
        catch [IO.IOException] { if ($attempt -ge 30) { throw }; Start-Sleep -Seconds 2 }
    }
}
function Invoke-EditorMethod([string]$name, [string]$method, [string[]]$extra = @()) {
    Invoke-UnityStage $name (@('run', $consumer, '--non-interactive', '--json', '--') + $extra +
        @('-nographics', '-executeMethod', $method, '-logFile', (Join-Path $evidence ($name + '-editor.log'))))
}
function Invoke-ConsumerTests([string]$name, [string]$mode, [string]$filter) {
    $report = Join-Path $evidence ($name + '.xml')
    Invoke-UnityStage $name @('test', $consumer, '--mode', $mode, '--filter', $filter, '--timeout', '600',
        '--output', $report, '--non-interactive', '--json', '--', '-nographics', '-logFile', (Join-Path $evidence ($name + '-editor.log')))
    if (-not (Test-Path -LiteralPath $report)) { throw 'Test process did not write a result XML.' }
    [xml]$tests = Get-Content -LiteralPath $report -Raw
    if ([int]$tests.'test-run'.total -lt 1 -or $tests.'test-run'.result -ne 'Passed') { throw "No passing tests in $report" }
    Write-Host ("{0}: {1} passed, {2} failed" -f $name, $tests.'test-run'.passed, $tests.'test-run'.failed)
}

# 3. Stages.
Invoke-EditorMethod 'consumer-import' 'CleanConsumerSetup.ImportSample'
Invoke-EditorMethod 'consumer-prepare' 'CleanConsumerSetup.PrepareScene'
Invoke-ConsumerTests 'consumer-editmode' 'EditMode' 'UpmConsumerTests'
Invoke-ConsumerTests 'consumer-playmode' 'PlayMode' 'CodeFirstConsumerTests'
if ($ValidatePerformance) {
    Invoke-ConsumerTests 'consumer-release-performance' 'PlayMode' 'PerformanceTests'
    Invoke-ConsumerTests 'consumer-release-trace-stripping' 'PlayMode' 'DebugStrippingTests'
    Invoke-ConsumerTests 'consumer-hardening' 'EditMode' 'HardeningRegressionTests'
    Invoke-ConsumerTests 'consumer-readiness' 'EditMode' 'IntegrationReadinessTests'
    Invoke-ConsumerTests 'consumer-services' 'EditMode' 'LatestServiceRegressionTests'
    Invoke-ConsumerTests 'consumer-reentrancy' 'EditMode' 'ReadyReentrancyRegressionTests'
    Invoke-ConsumerTests 'consumer-lifecycle' 'PlayMode' 'LatestLifecycleRegressionTests'
}
if ($BuildPlayer) {
    Invoke-EditorMethod 'consumer-build-mono' 'CleanConsumerSetup.BuildWindowsMono'
    $player = Join-Path $evidence 'player-mono/NexusSmoke.exe'
    $result = Join-Path $evidence 'player-mono-result.json'
    $process = Start-Process -FilePath $player -ArgumentList @('-batchmode', '-nographics', '-logFile', ('"' + (Join-Path $evidence 'player-mono.log') + '"'), '--nexus-result', ('"' + $result + '"')) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Smoke player exceeded 60 seconds.' }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $result)) { throw 'Player did not complete successfully.' }
    if (-not (Get-Content -LiteralPath $result -Raw | ConvertFrom-Json).passed) { throw 'Player startup/teardown failed.' }
    $steps.Add([ordered]@{ stage = 'consumer-player-mono'; exitCode = $process.ExitCode; result = $result }); Save-Steps
}
if ($BuildAndroid) {
    Invoke-EditorMethod 'consumer-build-android-il2cpp' 'CleanConsumerSetup.BuildAndroidIl2Cpp' @('-buildTarget', 'Android')
    # The installed packages' link.xml files reach the linker only through the core's NexusPackagesLinkXml (#110, #115).
    $linker = [regex]::Match((Read-EvidenceLog (Join-Path $evidence 'consumer-build-android-il2cpp-editor.log')), '\[Nexus\] (\d+) package link\.xml files handed to the linker')
    if (-not $linker.Success -or [int]$linker.Groups[1].Value -lt 1) { throw 'The Android build handed no Nexus package link.xml to the linker; see consumer-build-android-il2cpp-editor.log' }
    $steps[-1].packageLinkXmlFiles = [int]$linker.Groups[1].Value; Save-Steps
}
$head = (& git -C $repo rev-parse HEAD).Trim()
@{ sourceCommit = $head; archive = $archive.FullName; archiveSha256 = (Get-FileHash -LiteralPath $archive.FullName).Hash
   workingTreeDiff = ((& git -C $repo diff --stat) -join "`n") } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'source-identity.json') -Encoding utf8
foreach ($log in Get-ChildItem -LiteralPath $evidence -Filter '*.log' -Recurse) {
    $raw = Read-EvidenceLog $log.FullName
    [IO.File]::WriteAllText($log.FullName, [regex]::Replace($raw, '-accessToken\s+\S+', '-accessToken [REDACTED]'))
}
Write-Host "Clean consumer gate passed: $evidence"
