$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'persistence-probe.ps1')).Replace("`r`n", "`n")
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw "Driver parse errors: $($errors -join '; ')" }
$helper = $ast.Find({ param($n)
    $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-ProbeAdb'
}, $true)
$phase = $ast.Find({ param($n)
    $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-ProbePhase'
}, $true)
$startup = @($ast.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.AssignmentStatementAst] -and
    $_.Left.Extent.Text -cmatch '^\$script:current(Variant|Scenario|Phase)$'
} | ForEach-Object { $_.Extent.Text }) -join "`n"
$temp = Join-Path ([IO.Path]::GetTempPath()) ('persistence-evidence-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$session = [PowerShell]::Create()
try {
    # Only trusted AST-extracted functions run; the phase stops at its first identity check.
    $test = {
        param($evidence, $helperSource, $phaseSource, $startupSource)
        $ErrorActionPreference = 'Stop'
        Set-StrictMode -Version Latest
        $AdbSerial = 'fake-explicit-serial'
        $RunToken = 'testtoken1234'
        $script:currentVariant = $script:currentScenario = $script:currentPhase = $null
        . ([scriptblock]::Create($startupSource))
        . ([scriptblock]::Create($helperSource))
        . ([scriptblock]::Create($phaseSource))
        $script:checks = 0
        function Assert-Test($condition, $message) {
            if (-not $condition) { throw $message }
            $script:checks++
        }
        $script:fakeConfig = Join-Path $evidence 'fake-reply.json'
        $script:fakeNative = Join-Path $evidence 'fake-native.ps1'
        $script:nativeHost = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
        [IO.File]::WriteAllText($script:fakeNative, @'
param($Config)
$reply = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
for ($i = 0; $i -lt $reply.lines.Count; $i++) {
    if ($reply.code -ne 0 -and $i -eq $reply.lines.Count - 1) {
        [Console]::Error.WriteLine($reply.lines[$i])
    } else { [Console]::Out.WriteLine($reply.lines[$i]) }
}
exit $reply.code
'@)
        # Shadow adb.exe with a local native fixture; it has no device or network code.
        function adb.exe {
            $script:invokedArguments = @($args)
            @{ code = $script:fakeExit; lines = $script:fakeOutput } |
                ConvertTo-Json | Set-Content -LiteralPath $script:fakeConfig -Encoding utf8
            & $script:nativeHost -NoProfile -File $script:fakeNative -Config $script:fakeConfig
            $global:LASTEXITCODE = $LASTEXITCODE
        }
        function Add-Content {
            param($LiteralPath, [Parameter(ValueFromPipeline = $true)]$Value, $Encoding)
            Microsoft.PowerShell.Management\Add-Content -LiteralPath $LiteralPath -Value $Value -Encoding $Encoding -ErrorAction Stop
            $global:LASTEXITCODE = 99 # Logging must not replace the captured native exit status.
        }
        function Assert-ProbeIdentity { throw 'test-phase-entry-only' }
        $path = Join-Path $evidence 'adb-commands.jsonl'
        $script:fakeExit = 0
        $script:fakeOutput = @('quote:" path:C:\probe\save', "tabs`tand trailing spaces  ", '', 'nexus-pid-testtoken1234:1')
        $arguments = @('shell', 'echo "quoted"', 'C:\path with spaces\save')
        $before = [DateTimeOffset]::UtcNow
        $reply = @(Invoke-ProbeAdb $arguments)
        $after = [DateTimeOffset]::UtcNow
        Assert-Test (Test-Path -LiteralPath $path -PathType Leaf) 'Missing raw command evidence.'
        $records = @(Get-Content -LiteralPath $path | ForEach-Object { $_ | ConvertFrom-Json })
        Assert-Test ($records.Count -eq 1) 'Expected one JSONL record per invocation.'
        $r = $records[0]
        Assert-Test (($reply | ConvertTo-Json -Compress) -ceq ($script:fakeOutput | ConvertTo-Json -Compress)) 'Caller output changed.'
        Assert-Test (($r.outputLines | ConvertTo-Json -Compress) -ceq ($reply | ConvertTo-Json -Compress)) 'Output escaping, blank lines or whitespace changed.'
        Assert-Test ($r.exitCode -eq 0) 'Success exit code not captured immediately.'
        Assert-Test ($r.serial -ceq $AdbSerial -and $r.runToken -ceq $RunToken) 'Serial or run token missing.'
        Assert-Test (($r.arguments | ConvertTo-Json -Compress) -ceq ($script:invokedArguments | ConvertTo-Json -Compress)) 'Recorded arguments differ from the invocation.'
        Assert-Test (($r.arguments | ConvertTo-Json -Compress) -ceq (@('-s', $AdbSerial) + $arguments | ConvertTo-Json -Compress)) 'Explicit serial/argument array changed.'
        Assert-Test ($null -eq $r.variant -and $null -eq $r.scenario -and $null -eq $r.phase) 'Preflight metadata must be null.'
        # PowerShell 7 may deserialize ISO timestamps as DateTime instead of strings.
        $start = [DateTimeOffset]$r.startedUtc
        $end = [DateTimeOffset]$r.endedUtc
        Assert-Test ($start.Offset -eq [TimeSpan]::Zero -and $end.Offset -eq [TimeSpan]::Zero -and
            $before -le $start -and $start -le $end -and $end -le $after) "UTC command interval is invalid: before=$($before.ToString('o')) start=$($start.ToString('o')) end=$($end.ToString('o')) after=$($after.ToString('o'))."
        Assert-Test (([regex]::Matches($startupSource, '\$null')).Count -eq 3) 'Driver must initialize all preflight metadata to null.'
        $failure = ''
        try { Invoke-ProbePhase 'outcome' 'ack' 'verify' '' '' | Out-Null }
        catch { $failure = $_.Exception.Message }
        Assert-Test ($failure -ceq 'test-phase-entry-only') 'Phase test executed beyond the identity boundary.'
        $script:fakeOutput = @()
        $reply = @(Invoke-ProbeAdb @('shell', 'test', '!', '-d', '/proc/123'))
        $records = @(Get-Content -LiteralPath $path | ForEach-Object { $_ | ConvertFrom-Json })
        Assert-Test ($records.Count -eq 2 -and $reply.Count -eq 0 -and @($records[1].outputLines).Count -eq 0) 'Empty native reply not retained as an empty array.'
        Assert-Test ($records[1].variant -ceq 'outcome' -and $records[1].scenario -ceq 'ack' -and
            $records[1].phase -ceq 'verify') 'Phase metadata was not set before the first identity check.'
        $script:fakeExit = 7
        $script:fakeOutput = @('transport "failed"', '  detail\unchanged  ')
        $arguments = @('shell', 'am', 'force-stop', 'fake.package')
        $failure = ''
        $recordedBeforeThrow = $false
        try { Invoke-ProbeAdb $arguments | Out-Null }
        catch {
            $failure = $_.Exception.Message
            $records = @(Get-Content -LiteralPath $path | ForEach-Object { $_ | ConvertFrom-Json })
            $recordedBeforeThrow = $records.Count -eq 3 -and $records[-1].exitCode -eq 7
        }
        Assert-Test $recordedBeforeThrow 'Nonzero exit was not recorded before propagation.'
        Assert-Test ($failure -ceq "adb failed (7): $($arguments -join ' '): $($script:fakeOutput -join ' ')") 'Native failure propagation changed.'
        Assert-Test (($records[-1].outputLines | ConvertTo-Json -Compress) -ceq ($script:fakeOutput | ConvertTo-Json -Compress)) 'Failure reply changed.'
        # A directory at the log path is a deterministic write failure, without permission changes.
        $evidence = Join-Path $evidence 'blocked'
        New-Item -ItemType Directory -Path (Join-Path $evidence 'adb-commands.jsonl') -Force | Out-Null
        $script:fakeExit = 0
        $failure = ''
        try { Invoke-ProbeAdb @('get-state') | Out-Null }
        catch { $failure = $_.Exception.Message }
        Assert-Test ($failure -ne '') 'Logging failure silently returned success.'
        "PASS checks=$script:checks records=$($records.Count) deviceCalls=0"
    }
    $null = $session.AddScript($test.ToString()).AddArgument($temp).AddArgument($helper.Extent.Text).AddArgument($phase.Extent.Text).AddArgument($startup)
    $output = $session.Invoke()
    if ($session.Streams.Error.Count) { throw "Isolated regression failed: $($session.Streams.Error -join '; ')" }
    if (@($output | Where-Object { "$_" -match '^PASS checks=\d+ records=3 deviceCalls=0$' }).Count -ne 1) {
        throw 'Regression did not reach its final check.'
    }
    $output
}
finally {
    $session.Dispose()
    # Delete only this regression's verified, uniquely named temporary directory.
    $resolved = [IO.Path]::GetFullPath($temp)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'persistence-evidence-test-*') { throw 'Unsafe regression cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
