param(
    [string]$Dir = (Join-Path $PSScriptRoot '../../Nexus/Library/Bee/artifacts/Android/ManagedStripped'),
    [string]$Out = '',
    # the assemblies whose classes are checked; interfaces and base classes may come from any assembly in $Dir
    [string]$Include = '^(com\.nexus\.|Nexus\.|Assembly-CSharp)',
    [string]$UnityEditor = 'C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor'
)
# Reads the managed assemblies a player build left after UnityLinker (issue #113). For every class in the included
# assemblies, each method the linker kept on an interface the class implements needs its implementation on the class
# or a base class: same name, generic arity and parameter count, or an explicit override. IL2CPP throws
# EntryPointNotFoundException at the call when it is missing. Parameter types are not compared, so an overload of the
# same name, arity and parameter count hides a missing one. Exit 0: none missing, 1: missing found, 2: could not run.
$ErrorActionPreference = 'Stop'
try {
    $Dir = (Resolve-Path -LiteralPath $Dir).Path
    Add-Type -LiteralPath (Join-Path $UnityEditor 'Data\il2cpp\build\deploy\Mono.Cecil.dll')
    $resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
    $resolver.AddSearchDirectory($Dir)
    $readerParameters = New-Object Mono.Cecil.ReaderParameters
    $readerParameters.AssemblyResolver = $resolver

    $assemblies = @(Get-ChildItem -LiteralPath $Dir -Filter *.dll | Where-Object { $_.BaseName -match $Include })
    if ($assemblies.Count -eq 0) { throw "no assembly in $Dir matches $Include" }
    $missing = New-Object System.Collections.Generic.List[string]
    $classes = 0
    foreach ($file in $assemblies) {
        $module = [Mono.Cecil.ModuleDefinition]::ReadModule($file.FullName, $readerParameters)
        foreach ($type in $module.GetTypes()) {
            if ($type.IsInterface -or $type.IsAbstract) { continue }
            $classes++
            $methods = New-Object System.Collections.Generic.List[object]
            $explicit = New-Object System.Collections.Generic.HashSet[string]
            $contracts = New-Object System.Collections.Generic.List[object]
            for ($current = $type; $current; $current = if ($current.BaseType) { $current.BaseType.Resolve() } else { $null }) {
                foreach ($method in $current.Methods) {
                    $methods.Add($method)
                    foreach ($override in $method.Overrides) { [void]$explicit.Add("$($override.DeclaringType.Resolve().FullName)::$($override.Name)``$($override.GenericParameters.Count)") }
                }
                foreach ($implemented in $current.Interfaces) { $contracts.Add($implemented.InterfaceType.Resolve()) }
            }
            foreach ($contract in $contracts) {
                if (-not $contract) { continue }
                foreach ($declared in $contract.Methods) {
                    if ($declared.HasBody -or $declared.IsStatic) { continue }  # default interface method, static member
                    if ($explicit.Contains("$($contract.FullName)::$($declared.Name)``$($declared.GenericParameters.Count)")) { continue }
                    $found = $methods | Where-Object {
                        ($_.Name -eq $declared.Name -or $_.Name.EndsWith('.' + $declared.Name)) -and
                        $_.GenericParameters.Count -eq $declared.GenericParameters.Count -and
                        $_.Parameters.Count -eq $declared.Parameters.Count } | Select-Object -First 1
                    if (-not $found) { $missing.Add("$($file.Name): $($type.FullName) has no $($contract.FullName)::$($declared.Name)<$($declared.GenericParameters.Count)>($($declared.Parameters.Count))") }
                }
            }
        }
    }
    $result = [ordered]@{
        passed     = ($missing.Count -eq 0)
        directory  = $Dir
        assemblies = @($assemblies | ForEach-Object { $_.Name })
        classes    = $classes
        missing    = @($missing)
    }
    if ($Out) { $result | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $Out -Encoding utf8 }
    Write-Output "[check-stripped] $($assemblies.Count) assemblies, $classes classes, missing implementations: $($missing.Count)"
    $missing | ForEach-Object { Write-Output "[check-stripped]   $_" }
    if ($missing.Count -gt 0) { exit 1 }
    exit 0
}
catch {
    Write-Output "[check-stripped] could not run: $($_.Exception.Message)"
    exit 2
}
