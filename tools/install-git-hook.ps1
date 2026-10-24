# PowerShell Script to install local Git pre-commit hook for Nexus Framework verification
$hookPath = ".git/hooks/pre-commit"

if (-not (Test-Path ".git")) {
    Write-Host '[Nexus Hook] Error: .git directory not found. Run this script from repo root.' -ForegroundColor Red
    exit 1
}

# Without dotnet the harness cannot run, so the commit stops (CLAUDE.md: a commit that fails the harness is not committed).
$hookContent = @'
#!/bin/sh
# Nexus Framework Pre-Commit Verification Hook
echo "[Nexus Hook] Running local benchmark & zero-GC verification..."
if ! dotnet --info >/dev/null 2>&1; then
    echo "[Nexus Hook] ERROR: 'dotnet' is not available, so the host harness (tools/nexus-benchmark, net10.0) cannot run. Commit aborted."
    echo "[Nexus Hook] Install the .NET 10 SDK or put dotnet on PATH, then commit again."
    exit 1
fi
dotnet run --project tools/nexus-benchmark
if [ $? -ne 0 ]; then
    echo "[Nexus Hook] ERROR: Pre-commit verification failed! Commit aborted."
    exit 1
fi
echo "[Nexus Hook] All local tests passed."
'@

# sh reads the hook: LF line endings and UTF-8 without a BOM (a BOM before #! breaks the shebang).
[System.IO.File]::WriteAllText((Join-Path (Get-Location) $hookPath), $hookContent.Replace("`r`n", "`n") + "`n", (New-Object System.Text.UTF8Encoding $false))
Write-Host '[Nexus Hook] Pre-commit hook installed successfully.' -ForegroundColor Green
Write-Host '[Nexus Hook] Every git commit will now run local zero-GC and benchmark tests automatically.' -ForegroundColor Cyan
