param(
    [Parameter(Mandatory)][string]$AdbSerial,
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\6000.6.8f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$project = Join-Path $root 'Nexus'
$evidence = Join-Path $project ('artifacts/release-injection-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidence | Out-Null
$binder = Join-Path $project 'Assets/Scripts/Nexus/NexusGeneratedBinder.g.cs'
$oldDisable = $env:NEXUS_DISABLE_AUTOGEN
$oldSkip = $env:NEXUS_SKIP_BINDER_GUARD
function RunEditor($method, $log) {
    $args = "-batchmode -nographics -quit -projectPath `"$project`" -buildTarget Android -executeMethod NexusVerify.NexusVerifyBuild.$method -logFile `"$evidence/$log`""
    $p = Start-Process $Unity -ArgumentList $args -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "Unity failed ($($p.ExitCode)): $evidence/$log" }
}
function Adb([string[]]$Arguments) {
    $result = & adb.exe -s $AdbSerial @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "adb $Arguments failed: $result" }
    return $result
}
try {
    RunEditor 'DisableProbeAutoGeneration' 'prepare.log'
    foreach ($suffix in @('', '.meta')) {
        if (Test-Path "$binder$suffix") { Move-Item "$binder$suffix" "$evidence/binder$suffix" }
    }
    $env:NEXUS_DISABLE_AUTOGEN = '1'
    $env:NEXUS_SKIP_BINDER_GUARD = '1'
    RunEditor 'BuildAndroidReleaseInjectionProbe' 'build.log'
    Copy-Item "$project/builds/android-release-injection.apk" "$evidence/player.apk"
    Copy-Item "$project/builds/android-release-injection.apk.commit" "$evidence/player.apk.commit"
    Get-FileHash "$evidence/player.apk" | ConvertTo-Json | Set-Content "$evidence/apk-hash.json"
    & pwsh -NoProfile -File "$PSScriptRoot/check-stripped.ps1" -Out "$evidence/stripped.json"
    if ($LASTEXITCODE -ne 0) { throw 'Stripped-assembly audit failed.' }
    $package = 'com.UnityTechnologies.com.unity.template.urpblank'
    Adb @('install', '-r', "$evidence/player.apk") | Set-Content "$evidence/install.log"
    Adb @('shell', 'am', 'force-stop', $package) | Out-Null
    $capture = Start-Process adb -ArgumentList "-s $AdbSerial logcat -v threadtime -s Unity" -RedirectStandardOutput "$evidence/device.log" -PassThru -NoNewWindow
    try {
        Adb @('shell', 'am', 'start', '-n', "$package/com.unity3d.player.UnityPlayerGameActivity") | Out-Null
        Start-Sleep -Seconds 12
        $devicePid = (Adb @('shell', 'pidof', $package) | Out-String).Trim()
    } finally { Stop-Process -Id $capture.Id -ErrorAction SilentlyContinue }
    $lines = Get-Content "$evidence/device.log" | Where-Object { $_ -match "\s$devicePid\s" -and $_ -match '\[ReleaseInjectionProbe\]' }
    if (-not ($lines -match 'PASS development=false services=8/8 Context\+SignalBus=16/16 mediator=1/1 reflection=true') -or $lines -match 'FAIL') {
        throw "Release injection probe failed or produced no evidence: $evidence/device.log"
    }
    $lines
} finally {
    foreach ($suffix in @('', '.meta')) {
        if (Test-Path "$evidence/binder$suffix") { Move-Item "$evidence/binder$suffix" "$binder$suffix" }
    }
    $env:NEXUS_DISABLE_AUTOGEN = $oldDisable
    $env:NEXUS_SKIP_BINDER_GUARD = $oldSkip
    RunEditor 'RestoreProbeAutoGeneration' 'restore.log'
}
