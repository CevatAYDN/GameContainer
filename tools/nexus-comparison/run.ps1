param([string]$Dotnet = 'dotnet', [string]$Output = '', [switch]$Generated)
$ErrorActionPreference = 'Stop'
if (-not $Output) { $Output = Join-Path $PSScriptRoot 'comparative-results.json' }
function Invoke-Git([string[]]$Arguments) {
    & git @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git failed with exit $LASTEXITCODE" }
}
function Get-PinnedSource([string]$Name, [string]$Url, [string]$Commit, [string[]]$Folders) {
    $target = Join-Path $PSScriptRoot ('competitors/' + $Name)
    if (-not (Test-Path -LiteralPath $target)) {
        Invoke-Git @('clone', '--filter=blob:none', '--no-checkout', $Url, $target)
        Invoke-Git (@('-C', $target, 'sparse-checkout', 'set', '--cone') + $Folders)
        Invoke-Git @('-C', $target, 'checkout', '--detach', $Commit)
    }
    $actual = (& git -C $target rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -ne $Commit) { throw "Unexpected $Name checkout: $actual; expected $Commit" }
    Invoke-Git @('-C', $target, 'diff', '--exit-code', 'HEAD', '--')
}
Get-PinnedSource 'VContainer' 'https://github.com/hadashiA/VContainer.git' '5401e5a7ebc4980a2b82141ffc26391a6547edd7' @('VContainer/Assets/VContainer/Runtime', 'VContainer.SourceGenerator')
Get-PinnedSource 'Zenject' 'https://github.com/modesttree/Zenject.git' 'c2e33500a84f9408a809deca2af2c55494ab2482' @('UnityProject/Assets/Plugins/Zenject/Source')
Get-PinnedSource 'Reflex' 'https://github.com/gustavopsantos/Reflex.git' '70c9b4e46a3b08ee8a13a61b71bb264f14446a7c' @('Assets/Reflex')
$generatedArgs = @()
if ($Generated) {
    # VContainer ships its generator as source; build it from the pinned checkout (the library is not edited).
    & $Dotnet build (Join-Path $PSScriptRoot 'competitors/VContainer/VContainer.SourceGenerator/VContainer.SourceGenerator.csproj') -c Release -o (Join-Path $PSScriptRoot 'bin/VContainer.SourceGenerator') --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'VContainer source generator build failed.' }
    $generatedArgs = @('-p:Generated=true')
}
& $Dotnet run --project (Join-Path $PSScriptRoot 'ComparativeBenchmarks.csproj') -c Release @generatedArgs -- ([System.IO.Path]::GetFullPath($Output))
if ($LASTEXITCODE -ne 0) { throw "Comparison failed: $LASTEXITCODE" }
