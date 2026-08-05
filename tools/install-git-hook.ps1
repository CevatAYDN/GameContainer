# PowerShell Script to install local Git pre-commit hook for Nexus Framework verification
$hookPath = ".git/hooks/pre-commit"

if (-not (Test-Path ".git")) {
    Write-Host '[Nexus Hook] Error: .git directory not found. Run this script from repo root.' -ForegroundColor Red
    exit 1
}

$hookContent = @'
#!/bin/sh
# Nexus Framework Pre-Commit Verification Hook
echo "[Nexus Hook] Running local benchmark & zero-GC verification..."
dotnet run --project tools/nexus-benchmark
if [ $? -ne 0 ]; then
    echo "[Nexus Hook] ERROR: Pre-commit verification failed! Commit aborted."
    exit 1
fi
echo "[Nexus Hook] All local tests passed ✓"
'@

Set-Content -Path $hookPath -Value $hookContent -Encoding ASCII
Write-Host '[Nexus Hook] Pre-commit hook installed successfully.' -ForegroundColor Green
Write-Host '[Nexus Hook] Every git commit will now run local zero-GC and benchmark tests automatically.' -ForegroundColor Cyan
