# Lightweight text triage, not a C# parser or semantic code audit.
# Scans the tracked C# files of the packages and the development project, so the snapshots under Nexus/artifacts and
# untracked copies are not counted (#198). Exit 1: a Thread.Sleep text candidate needs review; 2: scan failed.
$repo = Split-Path -Parent $PSScriptRoot

Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host "[Nexus Code Audit] Text triage only; comments and strings may match." -ForegroundColor Cyan
Write-Host "Untracked and ignored files are not scanned. Results are not a semantic correctness verdict."
Write-Host "===============================================================================" -ForegroundColor Cyan

$csFiles = git -C $repo ls-files -- "Nexus/Packages/*.cs" "Nexus/Assets/*.cs"
if ($LASTEXITCODE -ne 0) { Write-Host "[Nexus Code Audit] git ls-files failed" -ForegroundColor Red; exit 2 }

$asyncVoidMatches = 0
$threadSleepMatches = 0
$mutableSignalMatches = 0

foreach ($relative in $csFiles) {
    $path = Join-Path $repo $relative
    $content = Get-Content $path
    $lineNum = 0

    foreach ($line in $content) {
        $lineNum++

        # 1. Text matches excluding lines that mention common event names.
        if ($line -match "async\s+void" -and $line -notmatch "OnClick|OnEvent") {
            $asyncVoidMatches++
            Write-Host "[Nexus Audit] CANDIDATE async void text: ${relative}:$lineNum" -ForegroundColor Yellow
        }

        # 2. A textual occurrence under a Runtime path does not prove execution or thread affinity.
        if ($line -match "Thread\.Sleep" -and $relative -match "/Runtime/") {
            $threadSleepMatches++
            Write-Host "[Nexus Audit] CANDIDATE Thread.Sleep text: ${relative}:$lineNum" -ForegroundColor Yellow
        }

        # 3. Declaration-shaped text, not semantic mutability analysis.
        if ($line -match "public\s+struct\s+\w+Signal" -and $line -notmatch "readonly") {
            $mutableSignalMatches++
            Write-Host "[Nexus Audit] CANDIDATE mutable signal text: ${relative}:$lineNum" -ForegroundColor Gray
        }
    }
}

Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host "[Nexus Code Audit] Audit Summary ($($csFiles.Count) tracked files):" -ForegroundColor Cyan
Write-Host "      Async void text matches: $asyncVoidMatches" -ForegroundColor Yellow
Write-Host "      Thread.Sleep text matches: $threadSleepMatches" -ForegroundColor Yellow
Write-Host "      Mutable signal text matches: $mutableSignalMatches" -ForegroundColor Gray
Write-Host "Zero matches do not prove correctness. Exit 1 requests review of a Thread.Sleep candidate."
Write-Host "===============================================================================" -ForegroundColor Cyan
if ($threadSleepMatches -gt 0) { exit 1 }
exit 0
