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

# A Client folder that is a link. Test-Sdk stands in for dotnet: nothing here may reach a build.
$linkFixture = Join-Path ([IO.Path]::GetTempPath()) ('Launcher-client-link-' + [guid]::NewGuid().ToString('N'))
$sharedClient = Join-Path $linkFixture 'shared'
$script:root = Join-Path $linkFixture 'checkout'
New-Item -ItemType Directory -Path $sharedClient, (At 'data') | Out-Null
foreach ($name in 'Game.exe', 'Game.patched.exe', 'Content.ggpk') { Set-Content -LiteralPath (Join-Path $sharedClient $name) -Value 'stand-in' }
Set-Content -LiteralPath (At 'data/login-keys.json') -Value '{}'
New-Item -ItemType Junction -Path (At 'Client') -Target $sharedClient | Out-Null
function Test-Sdk { $true }
$stopped = [pscustomobject]@{ Conflicts=@(); Services=@() }
try {
    Check ((Get-ClientDetail) -eq "Linked to $sharedClient") 'a linked client says where it leads'
    Prepare-Client $stopped 6>$null
    Check ((Get-Content -LiteralPath (Join-Path $sharedClient 'Content.ggpk') -Raw).Trim() -eq 'stand-in') 'preparing a linked client that is ready does nothing, and does not fail'
    Remove-Item -LiteralPath (At 'data/login-keys.json')
    $refused = $null
    try { Prepare-Client $stopped 6>$null } catch { $refused = $_.Exception.Message }
    Check ($refused -like "Client is a link to $sharedClient,*") 'a linked client that is not ready is refused, and never changed'
    Remove-Item -LiteralPath $sharedClient -Recurse -Force
    Check ((Get-ClientDetail) -eq "Client links to $sharedClient, which has no client. Run the setup guide to replace the link.") 'a link that leads nowhere says so'

    # Nothing may run from the folder the saves come from; that is what could still be writing them.
    $SaveSource = Join-Path $linkFixture 'release'
    New-Item -ItemType Directory -Path (Join-Path $SaveSource 'data') | Out-Null
    Set-Content -LiteralPath (Join-Path $SaveSource 'data\saves.db') -Value 'stand-in'
    $script:savePath = At 'data/saves.db'
    $script:processes = @([pscustomobject]@{ ExecutablePath=(Join-Path $SaveSource 'app\GameServer.Gateway.exe') })
    $refused = $null
    try { Import-Saves $stopped 6>$null } catch { $refused = $_.Exception.Message }
    Check ($refused -eq 'Close the game and stop your current LocalExile first.') 'saves are not copied while that LocalExile runs'
} finally {
    Remove-Item -LiteralPath $linkFixture -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "$script:passed bridge checks passed."

# Preparation repairs only from an explicitly verified build fingerprint. All tool boundaries are fakes;
# these disposable files are never executable and no game, server, build, or archive writer is started.
$preparationFixture = Join-Path ([IO.Path]::GetTempPath()) ('Waypoint-prepare-' + [guid]::NewGuid().ToString('N'))
$script:root = $preparationFixture
$script:patchCalls = 0
$script:failPatch = $false
$ClientExecutableHash = ''
function Set-Stage($stage, $detail) { }
function dotnet {
    $global:LASTEXITCODE = 0
    if ($args[1] -eq 'gateway') { $script:gatewayTarget = $args[2] }
    if ($args[1] -eq 'keygen') { Set-Content -LiteralPath (At 'data/login-keys.json') -Value 'new fixture keys' }
    if ($args[1] -eq 'patch') {
        $script:patchCalls++
        if ($script:failPatch) { $global:LASTEXITCODE = 1; return }
        $bytes = [IO.File]::ReadAllBytes($args[2]); $bytes[0] = 33
        [IO.File]::WriteAllBytes($args[3], $bytes)
    }
}
try {
    New-Item -ItemType Directory -Path (At 'Client'), (At 'data') | Out-Null
    [IO.File]::WriteAllText((At 'Client/PathOfExile.exe'), 'new verified executable')
    [IO.File]::WriteAllText((At 'Client/Game.exe'), 'old executable')
    [IO.File]::WriteAllText((At 'Client/Game.patched.exe'), 'old prepared executable')
    [IO.File]::WriteAllText((At 'Client/Content.ggpk'), 'archive stays unchanged')
    [IO.File]::WriteAllText((At 'data/login-keys.json'), 'existing fixture keys')
    [IO.File]::WriteAllText((At 'data/saves.db'), 'existing fixture saves')
    $ClientExecutableHash = (Get-FileHash -LiteralPath (At 'Client/PathOfExile.exe')).Hash

    $script:failPatch = $true
    $refused = $null
    try { Prepare-Client $stopped 6>$null } catch { $refused = $_.Exception.Message }
    Check ($refused -eq 'Client preparation failed. Existing keys were preserved.') 'failed re-patching reports failure'
    Check ([IO.File]::ReadAllText((At 'Client/Game.exe')) -eq 'old executable' -and
        [IO.File]::ReadAllText((At 'Client/Game.patched.exe')) -eq 'old prepared executable') 'failed re-patching preserves both old executables'
    Check (-not @(Get-ChildItem -LiteralPath (At 'Client') -Filter '.waypoint-*.exe').Count) 'failed preparation removes only its temporary executable files'

    $script:failPatch = $false
    Prepare-Client $stopped 6>$null
    Check ((Get-FileHash -LiteralPath (At 'Client/Game.exe')).Hash -eq $ClientExecutableHash) 'verified original refreshes a stale Game.exe'
    $backup = Get-ChildItem -LiteralPath (At 'data/client-preparation-backups') -Directory | Select-Object -First 1
    Check ([IO.File]::ReadAllText((Join-Path $backup.FullName 'Game.exe')) -eq 'old executable' -and
        [IO.File]::ReadAllText((Join-Path $backup.FullName 'Game.patched.exe')) -eq 'old prepared executable') 'both previous executables are backed up before replacement'
    Check ([IO.File]::ReadAllText((At 'data/login-keys.json')) -eq 'existing fixture keys' -and
        [IO.File]::ReadAllText((At 'data/saves.db')) -eq 'existing fixture saves' -and
        [IO.File]::ReadAllText((At 'Client/Content.ggpk')) -eq 'archive stays unchanged') 'repair retains existing keys, saves and the archive outside the mocked gateway tool'
    $calls = $script:patchCalls
    Prepare-Client $stopped 6>$null
    Check ($script:patchCalls -eq $calls) 'a second preparation keeps matching up-to-date executables'

    [IO.File]::WriteAllText((At 'Client/PathOfExile.exe'), 'small launcher or unrecognized build')
    Prepare-Client $stopped 6>$null
    Check ($script:patchCalls -eq $calls -and (Get-FileHash -LiteralPath (At 'Client/Game.exe')).Hash -eq $ClientExecutableHash) 'an unrecognized original cannot replace a valid Game.exe'
    $ClientExecutableHash = '0' * 64
    Prepare-Client $stopped 6>$null
    Check ($script:patchCalls -eq $calls) 'a different requested build cannot authorize repair'
    # A hotfix keeps the build's name, so a build has several executables. Either is a valid Game.exe.
    [IO.File]::WriteAllText((At 'Client/PathOfExile.exe'), 'hotfix verified executable')
    $hotfix = (Get-FileHash -LiteralPath (At 'Client/PathOfExile.exe')).Hash
    $release = (Get-FileHash -LiteralPath (At 'Client/Game.exe')).Hash
    $ClientExecutableHash = $release + ',' + $hotfix
    Prepare-Client $stopped 6>$null
    Check ($script:patchCalls -eq $calls -and (Get-FileHash -LiteralPath (At 'Client/Game.exe')).Hash -eq $release) 'a build''s other executable does not replace a valid Game.exe'
    [IO.File]::WriteAllText((At 'Client/Game.exe'), 'executable of an older build')
    Prepare-Client $stopped 6>$null
    Check ($script:patchCalls -eq $calls + 1 -and (Get-FileHash -LiteralPath (At 'Client/Game.exe')).Hash -eq $hotfix) 'a hotfix original refreshes a stale Game.exe'

    # Fresh copies still get their missing Game.exe and patch from the selected original.
    $script:root = Join-Path $preparationFixture 'fresh'
    New-Item -ItemType Directory -Path (At 'Client'), (At 'data') | Out-Null
    [IO.File]::WriteAllText((At 'Client/PathOfExile.exe'), 'fresh verified executable')
    [IO.File]::WriteAllText((At 'Client/Content.ggpk'), 'fresh archive fixture')
    $ClientExecutableHash = (Get-FileHash -LiteralPath (At 'Client/PathOfExile.exe')).Hash
    Prepare-Client $stopped 6>$null
    Check ((Get-FileHash -LiteralPath (At 'Client/Game.exe')).Hash -eq $ClientExecutableHash -and
        (Test-Path -LiteralPath (At 'Client/Game.patched.exe'))) 'fresh setup copies the original and creates its prepared executable'
    Check (-not (Test-Path -LiteralPath (At 'data/client-preparation-backups'))) 'fresh setup does not create unnecessary executable backups'
    Check ($script:gatewayTarget -eq (At 'Client/Content.ggpk')) 'a standalone client''s login list is selected in its archive'

    # A client from Steam has no Content.ggpk: its game files are loose, its executable is PathOfExileSteam.exe, and the
    # checkout's own live.ps1 says whether its LocalExile runs one.
    $script:root = Join-Path $preparationFixture 'steam'
    New-Item -ItemType Directory -Path (At 'Client/Bundles2'), (At 'data'), (At 'scripts/dev') | Out-Null
    [IO.File]::WriteAllText((At 'Client/PathOfExile_x64Steam.exe'), 'small launcher')
    [IO.File]::WriteAllText((At 'Client/PathOfExileSteam.exe'), 'steam verified executable')
    [IO.File]::WriteAllText((At 'Client/Bundles2/_.index.bin'), 'index fixture')
    [IO.File]::WriteAllText((At 'Client/gateway_list.txt'), 'login list fixture')
    [IO.File]::WriteAllText((At 'data/login-keys.json'), 'existing steam fixture keys')
    [IO.File]::WriteAllText((At 'scripts/dev/live.ps1'), 'param([switch]$ClientOnly) # an older LocalExile: Content.ggpk only')
    $ClientExecutableHash = (Get-FileHash -LiteralPath (At 'Client/PathOfExileSteam.exe')).Hash
    Check ((Get-ClientLayout) -eq 'Steam' -and -not (Test-SteamLayoutAccepted)) 'a client from Steam is told from a standalone one'
    $refused = $null
    try { Prepare-Client $stopped 6>$null } catch { $refused = $_.Exception.Message }
    Check ($refused -like 'This LocalExile runs only a standalone client*' -and -not (Test-Path -LiteralPath (At 'Client/Game.exe'))) 'a LocalExile that runs no client from Steam leaves one unprepared'
    [IO.File]::WriteAllText((At 'scripts/dev/live.ps1'), "param([switch]`$ClientOnly)`nNeed ((Test-Path 'Client\Content.ggpk') -or (Test-Path 'Client\Bundles2\_.index.bin')) 'neither'")
    Check (Test-SteamLayoutAccepted) 'the checkout''s own live.ps1 says that it runs a client from Steam'
    $script:gatewayTarget = $null
    Prepare-Client $stopped 6>$null
    Check ((Get-FileHash -LiteralPath (At 'Client/Game.exe')).Hash -eq $ClientExecutableHash -and
        (Test-Path -LiteralPath (At 'Client/Game.patched.exe'))) 'a client from Steam gets Game.exe from PathOfExileSteam.exe and its prepared executable'
    Check ($script:gatewayTarget -eq (At 'Client')) 'a client from Steam has its login list selected in the client folder'
    Check ([IO.File]::ReadAllText((At 'data/login-keys.json')) -eq 'existing steam fixture keys' -and
        [IO.File]::ReadAllText((At 'Client/Bundles2/_.index.bin')) -eq 'index fixture' -and
        -not (Test-Path -LiteralPath (At 'Client/Content.ggpk'))) 'preparing a client from Steam keeps the keys and makes no archive'
} finally {
    # The root is the GUID-named fixture created above; refuse redirected entries before recursive cleanup.
    Assert-Physical $preparationFixture
    if (@(Get-ChildItem -LiteralPath $preparationFixture -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Unexpected link in preparation fixture.' }
    Remove-Item -LiteralPath $preparationFixture -Recurse -Force
}
Write-Host "$script:passed bridge and preparation checks passed."
