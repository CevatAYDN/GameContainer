if (Test-Path 'C:\Program Files\Unity\Hub\Editor') {
    Get-ChildItem 'C:\Program Files\Unity\Hub\Editor' -Name
} else {
    Write-Output 'NO_UNITY_DIR'
}
$d = Get-Command dotnet -ErrorAction SilentlyContinue
if ($d) { Write-Output ('dotnet: ' + $d.Source) } else { Write-Output 'NO_DOTNET' }
$csc = Get-ChildItem 'C:\Program Files\Unity\Hub\Editor\*\Editor\Data\Tools\Roslyn\csc.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($csc) { Write-Output ('csc: ' + $csc.FullName) } else { Write-Output 'NO_CSC' }
