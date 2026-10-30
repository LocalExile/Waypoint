# Exercise production ownership/profile logic with fake process inventory. No running processes are stopped.
$ErrorActionPreference = 'Stop'
$bridge = Join-Path $PSScriptRoot '../scripts/RuntimeBridge.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($bridge, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($function in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    . ([scriptblock]::Create($function.Extent.Text))
}
$nativeCommand = $ast.Find({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Add-Type' }, $true)
Add-Type -TypeDefinition $nativeCommand.CommandElements[2].Value
$script:root = 'C:\Checkout with spaces\LocalExile'
$script:byId = @{}
$started = Get-Date
$relative = 'src\GameServer.Gateway\bin\Debug\net10.0\GameServer.Gateway.dll'
$log = At 'logs\live-gateway.log'
$err = At 'logs\live-gateway.err'
$parent = [pscustomobject]@{ ProcessId=80; Name='cmd.exe'; CreationDate=$started.AddSeconds(-1); CommandLine="C:\Windows\system32\cmd.exe /c dotnet $relative 1>`"$log`" 2>`"$err`"" }
$process = [pscustomobject]@{ ProcessId=90; ParentProcessId=80; Name='dotnet.exe'; CreationDate=$started; CommandLine="dotnet $relative" }
$script:byId[80] = $parent
$script:passed = 0
function Check($value, $message) { if (-not $value) { throw "FAIL: $message" }; $script:passed++; Write-Host "PASS: $message" }
Check (Owned-Server $process 'Gateway') 'upstream dedicated console and checkout-specific logs establish ownership'
$parent.CommandLine += ' '
Check (Owned-Server $process 'Gateway') 'ShellExecute trailing command-line whitespace is accepted'
$parent.CommandLine = $parent.CommandLine.Replace('C:\Checkout with spaces\LocalExile','C:\Other')
Check (-not (Owned-Server $process 'Gateway')) 'same relative DLL in another checkout is foreign'
$parent.CommandLine = "cmd.exe /c dotnet $relative 1>`"$log`" 2>`"$err`""
$process.CommandLine = 'dotnet C:\Other\GameServer.Gateway.dll'
Check (-not (Owned-Server $process 'Gateway')) 'another absolute build is foreign'
$process.CommandLine = "dotnet $relative --unexpected"
Check (-not (Owned-Server $process 'Gateway')) 'extra runtime arguments are not silently accepted'
$process.CommandLine = "dotnet $relative"
$parent.Name = 'powershell.exe'
Check (-not (Owned-Server $process 'Gateway')) 'shared interactive terminals cannot be signalled'
$parent.Name = 'cmd.exe'; $parent.CreationDate = $started.AddSeconds(1)
Check (-not (Owned-Server $process 'Gateway')) 'reused parent PIDs cannot establish ownership'
$script:byId.Clear()
Check (-not (Owned-Server $process 'Gateway')) 'orphaned relative commands do not establish ownership'
Check (Same-Path 'C:\Checkout with spaces\LocalExile\Client\Game.patched.exe' 'c:\checkout with spaces\localexile\Client\Game.patched.exe') 'Windows path identity ignores case'
Write-Host "$script:passed bridge checks passed."
