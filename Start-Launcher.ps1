param([string]$RepositoryPath = '')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src/Launcher.Desktop/Launcher.Desktop.csproj'
& dotnet build $project --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed. See output above.' }
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSScriptRoot 'src/Launcher.Desktop/bin/Debug/net10.0-windows/Launcher.Desktop.exe'))
$start.UseShellExecute = $false
if ($RepositoryPath) {
    # This script works in Windows PowerShell 5.1 as well as PowerShell 7.
    if ($RepositoryPath.Contains('"')) { throw 'RepositoryPath cannot contain a double quote.' }
    $start.Arguments = '--repo "' + $RepositoryPath.TrimEnd('\') + '"'
}
[Diagnostics.Process]::Start($start) | Out-Null
