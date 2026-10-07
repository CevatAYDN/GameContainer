param(
    [string]$Dotnet = 'dotnet',
    [string]$UnityCli = (Join-Path $env:LOCALAPPDATA 'Unity/bin/unity.exe'),
    [string]$RunName = 'mono',
    [string]$PackagePath = ''
)
$ErrorActionPreference = 'Stop'
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunName must be a simple folder name.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$evidence = Join-Path $repo ('Nexus/artifacts/performance-20261007/' + $RunName)
$project = Join-Path $repo 'Nexus/artifacts/performance-20261007/UnityComparison'
$result = Join-Path $evidence 'unity-results.json'
$player = Join-Path $evidence 'player/Comparison.exe'
if (Test-Path -LiteralPath $result) { throw "Refusing to overwrite evidence: $result" }
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$oldPlayer = $env:NEXUS_COMPARE_PLAYER
$oldResult = $env:NEXUS_COMPARE_RESULT
try {
    & (Join-Path $PSScriptRoot 'run.ps1') -Dotnet $Dotnet -Output (Join-Path $evidence 'host-results.json') *> (Join-Path $evidence 'host.log')
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
    @{ dependencies = @{ 'com.nexus.core' = ('file:' + $package) } } | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $repo 'Nexus/ProjectSettings/ProjectVersion.txt') -Destination (Join-Path $project 'ProjectSettings')
    $env:NEXUS_COMPARE_PLAYER = $player
    $env:NEXUS_COMPARE_RESULT = $result
    & $UnityCli run $project --non-interactive --json -- -nographics -executeMethod ComparisonBuild.BuildMono -logFile (Join-Path $evidence 'build-editor.log') *> (Join-Path $evidence 'build-cli.log')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $player)) { throw 'Unity Mono player build failed; inspect build logs.' }
    $playerLog = Join-Path $evidence 'player.log'
    $process = Start-Process -FilePath $player -ArgumentList @('-batchmode', '-nographics', '-logFile', ('"' + $playerLog + '"')) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Comparison player exceeded 60 seconds.' }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $result)) { throw 'Unity comparison did not complete successfully.' }
    $report = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
    if ($report.Backend -ne 'Mono' -or $report.DevelopmentBuild -or $report.NexusDebug -or $report.Measurements.Count -ne 9) {
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
} finally {
    $env:NEXUS_COMPARE_PLAYER = $oldPlayer
    $env:NEXUS_COMPARE_RESULT = $oldResult
    foreach ($log in Get-ChildItem -LiteralPath $evidence -Filter '*.log') {
        $raw = [IO.File]::ReadAllText($log.FullName)
        $redacted = [regex]::Replace($raw, '-accessToken\s+\S+', '-accessToken [REDACTED]')
        [IO.File]::WriteAllText($log.FullName, $redacted)
    }
}
