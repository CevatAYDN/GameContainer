# PowerShell Script for Nexus Static Code Quality & Anti-Pattern Audit
# Scans the tracked C# files of the packages and the development project, so the snapshots under Nexus/artifacts and
# untracked copies are not counted (#198). Exit code 1 when a FAIL line was written, 0 otherwise.
$repo = Split-Path -Parent $PSScriptRoot

Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host "[Nexus Code Audit] Running Static Anti-Pattern & Code Quality Audit..." -ForegroundColor Cyan
Write-Host "===============================================================================" -ForegroundColor Cyan

$csFiles = git -C $repo ls-files -- "Nexus/Packages/*.cs" "Nexus/Assets/*.cs"
if ($LASTEXITCODE -ne 0) { Write-Host "[Nexus Code Audit] git ls-files failed" -ForegroundColor Red; exit 2 }

$asyncVoidIssues = 0
$threadSleepIssues = 0
$mutableSignals = 0

foreach ($relative in $csFiles) {
    $path = Join-Path $repo $relative
    $content = Get-Content $path
    $lineNum = 0

    foreach ($line in $content) {
        $lineNum++

        # 1. Check async void (excluding Unity event methods like OnClick)
        if ($line -match "async\s+void" -and $line -notmatch "OnClick|OnEvent") {
            $asyncVoidIssues++
            Write-Host "[Nexus Audit] WARN  Async Void detected: ${relative}:$lineNum" -ForegroundColor Yellow
        }

        # 2. Check Thread.Sleep in runtime code
        if ($line -match "Thread\.Sleep" -and $relative -match "/Runtime/") {
            $threadSleepIssues++
            Write-Host "[Nexus Audit] FAIL  Thread.Sleep blocking call detected: ${relative}:$lineNum" -ForegroundColor Red
        }

        # 3. Check mutable signal structs
        if ($line -match "public\s+struct\s+\w+Signal" -and $line -notmatch "readonly") {
            $mutableSignals++
            Write-Host "[Nexus Audit] INFO  Mutable signal struct (consider 'readonly struct'): ${relative}:$lineNum" -ForegroundColor Gray
        }
    }
}

Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host "[Nexus Code Audit] Audit Summary ($($csFiles.Count) tracked files):" -ForegroundColor Cyan
Write-Host "      Async Void Methods: $asyncVoidIssues" -ForegroundColor Yellow
Write-Host "      Main-Thread Sleep Calls: $threadSleepIssues" -ForegroundColor Red
Write-Host "      Mutable Signal Structs: $mutableSignals" -ForegroundColor Gray
Write-Host "===============================================================================" -ForegroundColor Cyan
if ($threadSleepIssues -gt 0) { exit 1 }
exit 0
