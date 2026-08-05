# PowerShell Script for Nexus Static Code Quality & Anti-Pattern Audit
$targetDir = "Nexus"

Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host "[Nexus Code Audit] Running Static Anti-Pattern & Code Quality Audit..." -ForegroundColor Cyan
Write-Host "===============================================================================" -ForegroundColor Cyan

$csFiles = Get-ChildItem -Path $targetDir -Recurse -Filter "*.cs" | Where-Object { $_.FullName -notmatch "Library|obj|bin" }

$asyncVoidIssues = 0
$threadSleepIssues = 0
$mutableSignals = 0

foreach ($file in $csFiles) {
    $content = Get-Content $file.FullName
    $lineNum = 0
    
    foreach ($line in $content) {
        $lineNum++
        
        # 1. Check async void (excluding Unity event methods like OnClick)
        if ($line -match "async\s+void" -and $line -notmatch "OnClick|OnEvent") {
            $asyncVoidIssues++
            Write-Host "[Nexus Audit] WARN  Async Void detected: $($file.Name):$lineNum" -ForegroundColor Yellow
        }
        
        # 2. Check Thread.Sleep in runtime code
        if ($line -match "Thread\.Sleep" -and $file.FullName -match "Runtime") {
            $threadSleepIssues++
            Write-Host "[Nexus Audit] FAIL  Thread.Sleep blocking call detected: $($file.Name):$lineNum" -ForegroundColor Red
        }

        # 3. Check mutable signal structs
        if ($line -match "public\s+struct\s+\w+Signal" -and $line -notmatch "readonly") {
            $mutableSignals++
            Write-Host "[Nexus Audit] INFO  Mutable signal struct (consider 'readonly struct'): $($file.Name):$lineNum" -ForegroundColor Gray
        }
    }
}

Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host "[Nexus Code Audit] Audit Summary:" -ForegroundColor Cyan
Write-Host "      Async Void Methods: $asyncVoidIssues" -ForegroundColor Yellow
Write-Host "      Main-Thread Sleep Calls: $threadSleepIssues" -ForegroundColor Red
Write-Host "      Mutable Signal Structs: $mutableSignals" -ForegroundColor Gray
Write-Host "===============================================================================" -ForegroundColor Cyan
