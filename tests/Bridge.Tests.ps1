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
$parent.CreationDate = $started.AddSeconds(-1)
$parent.CommandLine = "cmd.exe /c dotnet watch run --project src\GameServer.Gateway --non-interactive -- 1>`"$log`" 2>`"$err`""
$watcher = [pscustomobject]@{ ProcessId=100; ParentProcessId=80; Name='dotnet.exe'; CreationDate=$started; CommandLine='dotnet watch run --project src\GameServer.Gateway --non-interactive --' }
$script:byId[80] = $parent; $script:byId[100] = $watcher
Check (Owned-Watcher $watcher) 'default upstream Gateway watch command has verified checkout ownership'
$hostProcess = [pscustomobject]@{ ProcessId=101; ParentProcessId=100; CreationDate=$started.AddSeconds(1) }
$app = [pscustomobject]@{ ProcessId=102; ParentProcessId=101; CreationDate=$started.AddSeconds(2); ExecutablePath=(At 'src\GameServer.Gateway\bin\Debug\net10.0\GameServer.Gateway.exe') }
$script:byId[101] = $hostProcess
Check (Owned-WatchedGateway $app $watcher) 'watched Gateway child requires exact executable and verified ancestry'
$app.ExecutablePath = 'C:\Other\GameServer.Gateway.exe'
Check (-not (Owned-WatchedGateway $app $watcher)) 'same app name from another build is not adopted'
$app.ExecutablePath = At 'src\GameServer.Gateway\bin\Debug\net10.0\GameServer.Gateway.exe'
$hostProcess.CreationDate = $app.CreationDate.AddSeconds(1)
Check (-not (Owned-WatchedGateway $app $watcher)) 'watch ancestry rejects recycled parent identities'
$watcher.CommandLine += ' --unexpected'
Check (-not (Owned-Watcher $watcher)) 'watch ownership rejects extra arguments'
$watcher.CommandLine = 'dotnet watch run --project src\GameServer.Gateway --non-interactive --'
$parent.CommandLine = $parent.CommandLine.Replace('C:\Checkout with spaces\LocalExile','C:\Other')
Check (-not (Owned-Watcher $watcher)) 'watcher in another checkout cannot be signalled'
Check ((Server-Command 'Auth' $false) -eq 'dotnet src\GameServer.Auth\bin\Debug\net10.0\GameServer.Auth.dll serve') 'Auth startup preserves the DLL and serve argument boundary'
Check ((Server-Command 'Gateway' $true) -eq 'dotnet watch run --project src\GameServer.Gateway --non-interactive --') 'watched startup uses the verified upstream command'
Check ((Server-Command 'Gateway' $false) -eq 'dotnet src\GameServer.Gateway\bin\Debug\net10.0\GameServer.Gateway.dll') 'disabled/older hot reload uses the normal Gateway DLL'
Write-Host "$script:passed bridge checks passed."
