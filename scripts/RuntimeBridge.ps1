# Adapter v1: Windows PowerShell 5.1, upstream LocalExile 3.29.3b / .NET 10 Debug layout.
# All operations use result files; live.ps1 never runs in a pipeline or with captured stdout.
param(
    [Parameter(Mandatory=$true)][ValidateSet('Inspect','Start','Play','Stop','CloseGame','Restart','Build','PrepareClient','Dependencies','Register','Switch','Fetch','ReloadData','Backup','RestoreBackup','ImportSaves','DeleteBackup','BuildMod','BuildMods','EnableMod','DisableMod','ReloadMod','QueryMods','RestoreModChoices','QueryAccounts','ManageAccount','CheckCapture','SetupCapture','StartCapture','StopCapture','FreshSaves')][string]$Action,
    [Parameter(Mandatory=$true)][string]$RepoRoot,
    [ValidateSet('All','Auth','Patch','Gateway')][string]$Service = 'All',
    [Parameter(Mandatory=$true)][string]$ResultPath,
    [switch]$UseLocalProfile,
    [switch]$NoHotReload,
    [switch]$NoBackup,
    [switch]$CaptureAreaLayouts,
    [switch]$StartFresh,
    [ValidateSet('Branch','Remote','PR')][string]$SourceKind = 'Branch',
    [string]$SourceValue = '',
    [string]$BackupId = '',
    [string]$SaveSource = '',
    [string]$ModId = '',
    [string]$ModProject = ''
)
. (Join-Path $PSScriptRoot 'ModBridge.ps1')
. (Join-Path $PSScriptRoot 'AccountBridge.ps1')
. (Join-Path $PSScriptRoot 'CaptureBridge.ps1')
$ErrorActionPreference = 'Stop'
$result = $null
$transcribing = $false
$mutex = $null
$hasLock = $false
$script:accountData = $null
$script:stage = 'Checking setup'
$names = @('Patch','Gateway','Auth')
$portMap = @{ Patch = @(12995,8080); Gateway = @(6112); Auth = @(20493,20495) }

function Write-Result($value) {
    $value | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
}
function Require($condition, [string]$message) { if (-not $condition) { throw $message } }
function At([string]$relative) { Join-Path $script:root $relative }
function Set-Stage([string]$stage, [string]$detail = '') {
    # Waypoint asks an operation to stop with this file: between stages, so what a stage writes is never cut short.
    if ($stage -ne 'Completed' -and (Test-Path -LiteralPath (Join-Path (Split-Path $ResultPath) 'stop.request'))) { throw 'Stopped by the player.' }
    $script:stage = $stage
    if ($Action -eq 'Inspect') { return }
    $progress = @{ Stage=$stage; Detail=$detail; LogPath=(Join-Path (Split-Path $ResultPath) $(if ($Action -eq 'Register') { 'account-service.log' } else { 'operation.log' })) }
    $path = Join-Path (Split-Path $ResultPath) 'progress.json'
    $progress | ConvertTo-Json | Set-Content -LiteralPath ($path + '.tmp') -Encoding UTF8
    Move-Item -LiteralPath ($path + '.tmp') -Destination $path -Force
}
function Same-Path([string]$a, [string]$b) {
    if (-not $a -or -not $b) { return $false }
    try { return [IO.Path]::GetFullPath($a).TrimEnd('\') -ieq [IO.Path]::GetFullPath($b).TrimEnd('\') } catch { return $false }
}
function Import-RuntimeProfile {
    $script:profile = 'Upstream live.ps1'
    if ($UseLocalProfile -and (Test-Path -LiteralPath (At 'scripts/dev/live.local.ps1'))) {
        # Inspection does not execute arbitrary profile code. Adapter v1 accepts constant Game_* assignments only.
        $script:profile = 'Local runtime wrapper'
        $path = At 'config/runtime.local.ps1'
        Require (Test-Path -LiteralPath $path) 'Local runtime wrapper exists but config/runtime.local.ps1 is missing.'
        $tokens = $null; $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
        Require (-not $errors.Count) 'The local runtime profile has PowerShell syntax errors.'
        foreach ($statement in $ast.EndBlock.Statements) {
            Require ($statement -is [System.Management.Automation.Language.AssignmentStatementAst]) 'Adapter v1 supports only constant Game_* assignments in the local profile.'
            $variable = $statement.Left.Extent.Text
            Require ($variable -match '^\$env:(Game_[A-Za-z0-9_]+)$') 'Only Game_* environment assignments are supported in the local profile.'
            $key = $Matches[1]
            $expression = $statement.Right
            if ($expression -is [System.Management.Automation.Language.CommandExpressionAst]) { $expression = $expression.Expression }
            Require ($expression -is [System.Management.Automation.Language.StringConstantExpressionAst]) 'Local runtime values must be literal strings; complex profiles need a newer adapter.'
            [Environment]::SetEnvironmentVariable($key, $expression.Value, 'Process')
        }
    }
    foreach ($entry in @{
        Game_BIND='127.0.0.1'; Game_LOGIN_PORT='20493'; Game_GATEWAY_PORT='6112'; Game_AUTH_CONTROL_PORT='20495';
        Game_PATCH_TCP_PORT='12995'; Game_PATCH_URL='http://127.0.0.1:8080/'; Game_CLIENT_EXE='Client/Game.exe'; Game_GGPK='Client/Content.ggpk'
    }.GetEnumerator()) {
        $value = [Environment]::GetEnvironmentVariable($entry.Key)
        Require (-not $value -or $value -ieq $entry.Value) "Adapter v1 requires the standard local value for $($entry.Key)."
    }
    Require (-not $env:Game_TLS_PFX -and -not $env:Game_CDN_PASSTHROUGH -and -not $env:Game_PATCH_WEB_ROOT -and -not $env:Game_PATCH_BACKUP_WEB_ROOT) 'Custom TLS/CDN patch settings are not supported by adapter v1.'
    $script:savePath = if ($env:Game_SAVES_DB) { [IO.Path]::GetFullPath((Join-Relative $env:Game_SAVES_DB)) } else { At 'data/saves.db' }
}
function Join-Relative([string]$path) { if ([IO.Path]::IsPathRooted($path)) { $path } else { At $path } }
function Get-Inventory {
    # Failure to inspect listeners or processes must fail closed, never report an empty/free machine.
    $script:processes = @(Get-CimInstance Win32_Process)
    $script:byId = @{}
    foreach ($p in $script:processes) { $script:byId[[int]$p.ProcessId] = $p }
    $script:listeners = @(Get-NetTCPConnection -State Listen)
}
function Owned-Server($process, [string]$name) {
    # A release runs each server's own executable from app\, under LocalExile.exe.
    if ($script:isRelease) { return $process -and (Same-Path $process.ExecutablePath (At "app\GameServer.$name.exe")) }
    if (-not $process -or $process.Name -ine 'dotnet.exe') { return $false }
    $relative = "src\GameServer.$name\bin\Debug\net10.0\GameServer.$name.dll"
    $arguments = Split-Arguments $process.CommandLine
    if ($arguments.Count -lt 2) { return $false }
    $expectedCount = if ($name -eq 'Auth') { 3 } else { 2 }
    if ($arguments.Count -ne $expectedCount) { return $false }
    if ($name -eq 'Auth' -and $arguments[2] -ine 'serve') { return $false }
    # An absolute DLL alone doesn't establish the working directory (and hence save/config identity).
    # Upstream starts a dedicated cmd with checkout-specific absolute log destinations: verify that parent too.
    if ($arguments[1] -ine $relative -and -not (Same-Path $arguments[1] (At $relative))) { return $false }
    $parent = $script:byId[[int]$process.ParentProcessId]
    if (-not $parent -or $parent.Name -ine 'cmd.exe' -or $parent.CreationDate -gt $process.CreationDate) { return $false }
    $tail = 'dotnet ' + $relative + $(if ($name -eq 'Auth') { ' serve' }) + ' 1>"' + (At "logs\live-$($name.ToLower()).log") + '" 2>"' + (At "logs\live-$($name.ToLower()).err") + '"'
    return $parent.CommandLine.TrimEnd() -imatch ('/c\s+' + [regex]::Escape($tail) + '$')
}
function Split-Arguments([string]$line) { [NativeRuntime]::Arguments($line) }
function Owned-Watcher($process) {
    if (-not $process -or $process.Name -ine 'dotnet.exe') { return $false }
    $args = @(Split-Arguments $process.CommandLine)
    if ($args.Count -ne 7 -or ($args[1..6] -join ' ') -cne 'watch run --project src\GameServer.Gateway --non-interactive --') { return $false }
    $parent = $script:byId[[int]$process.ParentProcessId]
    if (-not $parent -or $parent.Name -ine 'cmd.exe' -or $parent.CreationDate -gt $process.CreationDate) { return $false }
    $tail = 'dotnet watch run --project src\GameServer.Gateway --non-interactive -- 1>"' + (At 'logs\live-gateway.log') + '" 2>"' + (At 'logs\live-gateway.err') + '"'
    return $parent.CommandLine.TrimEnd() -imatch ('/c\s+' + [regex]::Escape($tail) + '$')
}
function Descends-From($process, $ancestor) {
    if (-not $process -or -not $ancestor) { return $false }
    $current = $process
    for ($i=0; $i -lt 8; $i++) {
        if ($current.ProcessId -eq $ancestor.ProcessId) { return $current.CreationDate -eq $ancestor.CreationDate }
        $parent = $script:byId[[int]$current.ParentProcessId]
        if (-not $parent -or $parent.CreationDate -gt $current.CreationDate) { return $false }
        $current = $parent
    }
    return $false
}
function Owned-WatchedGateway($process, $watcher) {
    return $process -and (Same-Path $process.ExecutablePath (At 'src\GameServer.Gateway\bin\Debug\net10.0\GameServer.Gateway.exe')) -and (Descends-From $process $watcher)
}
function Get-Snapshot {
    Get-Inventory
    $conflicts = [Collections.Generic.List[string]]::new()
    $services = @()
    $watchers = @($script:processes | Where-Object { Owned-Watcher $_ })
    if ($watchers.Count -gt 1) { $conflicts.Add('Multiple Gateway watchers belong to this checkout.') }
    foreach ($name in $names) {
        $owned = @($script:processes | Where-Object { Owned-Server $_ $name })
        $watcher = $null
        if ($name -eq 'Gateway' -and $watchers.Count) {
            $watcher = $watchers[0]
            $owned += @($script:processes | Where-Object { Owned-WatchedGateway $_ $watcher })
        }
        if ($owned.Count -gt 1) { $conflicts.Add("Multiple $name processes belong to this checkout.") }
        $process = $owned | Select-Object -First 1
        $ready = $null -ne $process
        foreach ($port in $portMap[$name]) {
            $onPort = @($script:listeners | Where-Object { $_.LocalPort -eq $port })
            if (-not $onPort.Count) { $ready = $false }
            foreach ($connection in $onPort) {
                if (-not $process -or $connection.OwningProcess -ne $process.ProcessId) {
                    $conflicts.Add("Port $port is occupied by another process (PID $($connection.OwningProcess)); $name is not owned by this checkout.")
                    $ready = $false
                }
            }
        }
        $status = if ($ready) { 'Ready' } elseif ($process -or $watcher) { 'Starting' } else { 'Stopped' }
        $detail = if ($ready) { 'Owned process; all required ports listening' } elseif ($process -or $watcher) { 'Starting, rebuilding or restarting; inspect Gateway output for errors' } else { 'No verified process for this checkout' }
        if ($watcher) { $detail += " | hot reload watcher PID $($watcher.ProcessId)" }
        $row = New-Service $name $status $process $detail $portMap[$name]
        $row.WatchProcessId = if ($watcher) { [int]$watcher.ProcessId } else { 0 }
        $services += $row
    }
    # Upstream live.ps1 stops processes by name before launch, even before they hold a port.
    # Block foreign server/watch processes as well as foreign listeners so upstream cannot kill them.
    foreach ($p in $script:processes) {
        if ($p.Name -notin @('dotnet.exe','cmd.exe','GameServer.Gateway.exe','GameServer.Auth.exe','GameServer.Patch.exe')) { continue }
        if ($p.CommandLine -notmatch 'GameServer\.(Gateway|Auth|Patch)(\.dll|\.exe|[\\\s"]|$)' -or $p.CommandLine -match '\s(build|test)\s|GameServer\.(Gateway|Auth|Patch)\.Tests') { continue }
        $ownedProcess = $false
        foreach ($s in $services) {
            if ($p.ProcessId -eq $s.ProcessId) { $ownedProcess = $true }
            if ($s.ProcessId -and $script:byId[$s.ProcessId].ParentProcessId -eq $p.ProcessId) { $ownedProcess = $true }
        }
        foreach ($w in $watchers) { if ((Descends-From $p $w) -or $p.ProcessId -eq $w.ParentProcessId) { $ownedProcess = $true } }
        if (-not $ownedProcess) { $conflicts.Add("Another server or watcher is running (PID $($p.ProcessId)). Stop it from its own installation before launching here.") }
    }
    $gamePath = At 'Client/Game.patched.exe'
    $games = @($script:processes | Where-Object { $_.Name -ieq 'Game.patched.exe' -or $_.Name -like 'PathOfExile*.exe' -or $_.Name -ieq 'Game.exe' })
    $ownedGame = @($games | Where-Object { Same-Path $_.ExecutablePath $gamePath })
    if ($games.Count -gt $ownedGame.Count -or $ownedGame.Count -gt 1) { $conflicts.Add('Another game client is running. Close it before launching or restarting services.') }
    $game = $ownedGame | Select-Object -First 1
    $services += New-Service 'Game' $(if ($game) { 'Running' } else { 'Stopped' }) $game $(if ($game) { 'Prepared client for this checkout' } else { 'Launches only through live.ps1 -ClientOnly' }) @()
    $requirements = @(
        @{ Name='.NET 10 SDK'; Ready=(Test-Sdk); Detail='Install .NET 10 SDK; check with dotnet --list-sdks' },
        @{ Name='Launch adapter'; Ready=((Test-Path -LiteralPath (At 'scripts/dev/live.ps1')) -and ((Get-Content -LiteralPath (At 'scripts/dev/live.ps1') -Raw) -match '\$ClientOnly')); Detail='scripts/dev/live.ps1 must support -ClientOnly and -Only' },
        @{ Name='Original client'; Ready=(Test-Path -LiteralPath (At 'Client/Game.exe')); Detail=(Get-ClientDetail) },
        @{ Name='Prepared client'; Ready=(Test-Path -LiteralPath $gamePath); Detail='Follow Agents/GettingStarted.md steps 4-5 to prepare Game.patched.exe' },
        @{ Name='Client archive'; Ready=(Test-Path -LiteralPath (At 'Client/Content.ggpk')); Detail='Standalone Content.ggpk is required; Steam layout is unsupported' },
        @{ Name='Login keys'; Ready=(Test-Path -LiteralPath (At 'data/login-keys.json')); Detail='data/login-keys.json must match the prepared client; never regenerate existing keys casually' },
        @{ Name='Python'; Ready=([bool](Get-Command python -ErrorAction SilentlyContinue)); Detail='Python 3.9+ is required for data extraction' },
        @{ Name='Node.js'; Ready=([bool](Get-Command node -ErrorAction SilentlyContinue)); Detail='Node.js is required for bundle decompression' },
        @{ Name='Bundle decompressor'; Ready=(Test-Path -LiteralPath (At 'Tools/ooz/node_modules/ooz-wasm')); Detail='From Tools/ooz run npm install' }
    )
    if ($script:profile -eq 'Local runtime wrapper') {
        $requirements += @{ Name='Existing save database'; Ready=(Test-Path -LiteralPath $script:savePath); Detail="Local runtime uses $script:savePath" }
    }
    $commit = ''; $branch = ''
    if ((Test-Path -LiteralPath (At '.git')) -and (Get-Command git -ErrorAction SilentlyContinue)) {
        $commit = [string](& git -C $script:root rev-parse HEAD 2>$null)
        $branch = [string](& git -C $script:root branch --show-current 2>$null)
    }
    $builds = @($names | ForEach-Object {
        $dll = At "src/GameServer.$_/bin/Debug/net10.0/GameServer.$_.dll"
        if (Test-Path -LiteralPath $dll) { "$_=" + (Get-Item -LiteralPath $dll).VersionInfo.ProductVersion } else { "$_=not built" }
    })
    $sourceLabel = $branch
    $selectionPath = Get-SourceSelectionPath
    if (-not $branch -and (Test-Path -LiteralPath $selectionPath)) {
        Assert-Physical $selectionPath
        try {
            if ((Get-Item -LiteralPath $selectionPath).Length -lt 32768) {
                $selection = Get-Content -LiteralPath $selectionPath -Raw | ConvertFrom-Json
                if ($selection.Commit -eq $commit -and $selection.Kind -eq 'PR' -and $selection.Value -match '^[1-9][0-9]{0,8}$') { $sourceLabel = "PR #$($selection.Value)" }
                elseif ($selection.Commit -eq $commit -and $selection.Kind -eq 'Remote' -and $selection.Value.StartsWith('refs/remotes/')) { $sourceLabel = $selection.Value.Substring('refs/remotes/'.Length) }
            }
        } catch { $sourceLabel = '' }
    }
    $accountBlock = ''
    try { Assert-AccountsStopped ([pscustomobject]@{ Conflicts=$conflicts.ToArray(); Services=$services }) }
    catch { $accountBlock = $_.Exception.Message }
    @{ Root=$script:root; Profile=$script:profile; SaveDatabase=$script:savePath; SourceCommit=$commit; SourceBranch=$branch; AccountWriteBlockedReason=$accountBlock;
       SourceLabel=$sourceLabel; BuildCommit=($builds -join '; '); Requirements=@($requirements); Services=@($services); Conflicts=@($conflicts.ToArray()); ModEnvironment=(Get-ModEnvironment $services); Capture=(Get-CaptureSnapshot $services) }
}
function Get-SourceSelectionPath {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $key = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($script:root.ToLowerInvariant())))).Replace('-','') }
    finally { $sha.Dispose() }
    return Join-Path (Join-Path $env:LOCALAPPDATA 'EmulatorWorkspaceLauncher/source-selections') ($key + '.json')
}
function Test-Sdk {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    $sdks = @(& dotnet --list-sdks)
    return [bool]($sdks | Where-Object { $_ -match '^10\.' })
}
function New-Service($name, $status, $process, $detail, $ports) {
    $memory = 0; $cpu = 0; $uptime = ''; $started = ''; $processId = 0
    if ($process) {
        $processId = [int]$process.ProcessId
        $started = $process.CreationDate.ToUniversalTime().ToString('o')
        $uptime = ((Get-Date) - $process.CreationDate).ToString('dd\.hh\:mm\:ss')
        try { $p = Get-Process -Id $processId; $memory = [math]::Round($p.WorkingSet64 / 1MB,1); $cpu = $p.TotalProcessorTime.TotalSeconds } catch {}
    }
    @{ Name=$name; Status=$status; ProcessId=$processId; StartedUtc=$started; Detail=$detail; Ports=@($ports); MemoryMb=$memory; CpuSeconds=$cpu; Uptime=$uptime }
}
function Assert-CanStart($snapshot) {
    Require (-not $snapshot.Conflicts.Count) ($snapshot.Conflicts -join "`n")
    $missing = @($snapshot.Requirements | Where-Object { -not $_.Ready } | ForEach-Object { "$($_.Name): $($_.Detail)" })
    Require (-not $missing.Count) ($missing -join "`n")
}
function Start-Services($snapshot, [string[]]$selected) {
    Assert-CanStart $snapshot
    if ($CaptureAreaLayouts -and 'Gateway' -in $selected) {
        $null = Check-CaptureDependencies
        $existing = @($snapshot.Services | Where-Object { $_.Name -eq 'Gateway' -and ($_.ProcessId -or $_.WatchProcessId) })
        Require (-not $existing.Count -or (Test-CaptureGatewayEnabled $snapshot.Services)) 'Close the game, then restart Gateway to enable capture.'
    }
    $missing = @($selected | Where-Object { $n = $_; -not ($snapshot.Services | Where-Object { $_.Name -eq $n -and $_.Status -eq 'Ready' }) })
    if (-not $missing.Count) { return }
    if ('Gateway' -in $missing) { Backup-Saves $snapshot }
    # live.ps1 rebuilds dependencies. Do not overwrite assemblies while any owned server is running.
    $running = @($snapshot.Services | Where-Object { $_.Name -ne 'Game' -and ($_.ProcessId -or $_.WatchProcessId) })
    if ($running.Count) {
        # A selected service that is alive but not ready should not be silently restarted.
        Require (-not @($running | Where-Object { $_.Name -in $missing }).Count) 'A selected service is still starting or unhealthy. Inspect its log, then stop it explicitly.'
        foreach ($n in $missing) {
            $dll = At "src/GameServer.$n/bin/Debug/net10.0/GameServer.$n.dll"
            Require (Test-Path -LiteralPath $dll) "Build $n first: stop servers, then click Build checkout."
            Require ($snapshot.SourceCommit -and (Get-Item -LiteralPath $dll).VersionInfo.ProductVersion.Contains($snapshot.SourceCommit)) 'Existing binaries do not match the selected commit. Stop servers, then Build checkout.'
        }
        & git -C $script:root diff --quiet HEAD --
        Require ($LASTEXITCODE -eq 0) 'The checkout has uncommitted changes. Stop servers and build before starting additional services.'
    }
    Set-Stage 'Extracting data' 'Checking the upstream extraction manifest; missing or outdated data will be generated.'
    & (At 'scripts/dev/extract-data.ps1')
    if (-not $running.Count) {
        Set-Stage 'Building services' ($missing -join ', ')
        foreach ($name in $missing) {
            & dotnet build (At "src/GameServer.$name") --nologo -v minimal
            Require ($LASTEXITCODE -eq 0) "Build failed for $name. Open this operation's log."
        }
    }
    if ('Gateway' -in $missing) { Build-GatewayMods -NoDependencies:([bool]$running.Count) -AllowRetire -SourceCommit $snapshot.SourceCommit }
    $latest = Get-Snapshot
    Require (-not $latest.Conflicts.Count) ($latest.Conflicts -join "`n")
    Require (-not @($latest.Services | Where-Object { $_.Name -in $missing -and ($_.ProcessId -or $_.WatchProcessId) }).Count) 'A selected service started during preparation. Refresh and retry without restarting it.'
    Set-Stage 'Starting services' ($missing -join ', ')
    $liveText = Get-Content -LiteralPath (At 'scripts/dev/live.ps1') -Raw
    $watched = -not $NoHotReload -and $liveText.Contains('$NoHotReload') -and $liveText.Contains('watch')
    # Start only verified missing services, using upstream's standard commands and dedicated consoles.
    # Do not invoke live.ps1's broad name/port cleanup: it can force-kill a different checkout or a
    # restarting watch tree. Client launch still goes exclusively through its -ClientOnly workflow.
    $before = @{}
    foreach ($key in @('Game_GATEWAY_ENTER','Game_PACKET_DUMPS','Game_HOT_RELOAD_LOG','Game_WALKMAPS')) { $before[$key] = [Environment]::GetEnvironmentVariable($key) }
    try {
        if (-not $env:Game_GATEWAY_ENTER) { $env:Game_GATEWAY_ENTER = '1' }
        if (-not $env:Game_PACKET_DUMPS) { $env:Game_PACKET_DUMPS = 'logs\packets' }
        $env:Game_WALKMAPS = if ($CaptureAreaLayouts) { '1' } else { '0' }
        $env:Game_HOT_RELOAD_LOG = if ($watched) { At 'logs\live-gateway.err' } else { $null }
        $archive = At ('logs/archive/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-launcher-' + [guid]::NewGuid().ToString('N').Substring(0,8))
        New-Item -ItemType Directory -Force -Path (At 'logs') | Out-Null
        foreach ($name in $missing) {
            $latest = Get-Snapshot
            Require (-not $latest.Conflicts.Count) ($latest.Conflicts -join "`n")
            Require (-not @($latest.Services | Where-Object { $_.Name -eq $name -and ($_.ProcessId -or $_.WatchProcessId) }).Count) "$name started during preparation. Refresh before retrying."
            foreach ($extension in @('log','err')) {
                $oldLog = At "logs/live-$($name.ToLower()).$extension"
                if (Test-Path -LiteralPath $oldLog) {
                    New-Item -ItemType Directory -Force -Path $archive | Out-Null
                    Move-Item -LiteralPath $oldLog -Destination (Join-Path $archive "live-$($name.ToLower()).$extension")
                }
            }
            Set-Stage 'Starting services' $name
            $command = Server-Command $name ($watched -and $name -eq 'Gateway')
            $line = '/c ' + $command + ' 1>"' + (At "logs\live-$($name.ToLower()).log") + '" 2>"' + (At "logs\live-$($name.ToLower()).err") + '"'
            Start-Process cmd.exe -ArgumentList $line -WorkingDirectory $script:root -WindowStyle Hidden
        }
    } finally {
        foreach ($key in $before.Keys) { [Environment]::SetEnvironmentVariable($key, $before[$key]) }
    }
    Set-Stage 'Waiting for services' 'Verifying ownership of every required listening port.'
    $deadline = (Get-Date).AddSeconds(60)
    do {
        $latest = Get-Snapshot
        Require (-not $latest.Conflicts.Count) ($latest.Conflicts -join "`n")
        $pending = @($latest.Services | Where-Object { $_.Name -in $selected -and $_.Status -ne 'Ready' })
        if (-not $pending.Count) {
            if ('Gateway' -in $missing) { Record-CaptureGateway $latest.Services }
            return
        }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    throw 'Servers did not become ready within 60 seconds. Review logs/live-*.log and logs/live-*.err. Partial starts remain visible; use Stop servers to clean up.'
}
function Build-GatewayMods([switch]$NoDependencies, [switch]$AllowRetire, [string]$SourceCommit = '') {
    # Match upstream's pre-Gateway mod build without invoking live.ps1's broad process cleanup.
    # Older revisions have no mod source folder. Binary-only mods and mod-state.txt are not modified here.
    $source = At 'Mods/Source'
    if ($AllowRetire) { Retire-RemovedManagedMods }
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { return }
    Assert-Physical $source
    $projects = @(Get-ChildItem -LiteralPath $source -Filter *.csproj -File -Recurse | Sort-Object FullName)
    foreach ($project in $projects) {
        Assert-Physical $project.FullName
        Set-Stage 'Building mods' $project.BaseName
        $arguments = @('build', $project.FullName, '--nologo', '-v', 'minimal')
        # Beside existing services, reuse the already-built API instead of rebuilding shared assemblies.
        # If an input is missing, the build fails and the user can stop services and build the checkout.
        if ($NoDependencies) { $arguments += '--no-dependencies' }
        & dotnet @arguments
        Require ($LASTEXITCODE -eq 0) "Build failed for mod $($project.BaseName). Gateway was not started; older mod binaries were not accepted as a fallback. Open this operation's log, fix the error, then retry. If dependencies need rebuilding, stop servers and use Build checkout first."
        Record-ModBuild $project.FullName $SourceCommit
    }
}
function Server-Command([string]$name, [bool]$watched) {
    Require ($name -in @('Patch','Gateway','Auth')) 'Unknown service command.'
    if ($name -eq 'Gateway' -and $watched) { return 'dotnet watch run --project src\GameServer.Gateway --non-interactive --' }
    return "dotnet src\GameServer.$name\bin\Debug\net10.0\GameServer.$name.dll" + $(if ($name -eq 'Auth') { ' serve' })
}
function Stop-Services($snapshot, [string[]]$selected) {
    Require (-not @($snapshot.Services | Where-Object { $_.Name -eq 'Game' -and $_.ProcessId }).Count) 'Close the game first using Close game, then stop or restart services.'
    if ('Gateway' -in $selected) { Stop-Capture }
    foreach ($name in @('Gateway','Auth','Patch')) {
        if ($name -notin $selected) { continue }
        Get-Inventory
        $targets = @($script:processes | Where-Object { Owned-Server $_ $name })
        if ($name -eq 'Gateway') { $targets += @($script:processes | Where-Object { Owned-Watcher $_ }) }
        foreach ($target in $targets) {
            Set-Stage 'Stopping services' $name
            $tree = @($script:processes | Where-Object { Descends-From $_ $target })
            # Attach only to the dedicated cmd console verified by Owned-Server. Ctrl+C lets Gateway save.
            # Console attachment must happen in a disposable helper: detaching this PowerShell would invalidate
            # its host handles and break transcript/output handling for the remainder of this operation.
            $helper = Join-Path $PSScriptRoot 'ConsoleSignal.ps1'
            $signalArguments = @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',('"' + $helper + '"'),
                '-TargetId', [string]$target.ProcessId, '-ParentId', [string]$target.ParentProcessId,
                '-StartedUtc', $target.CreationDate.ToUniversalTime().ToString('o'))
            $signal = Start-Process (Join-Path $PSHOME 'powershell.exe') -ArgumentList $signalArguments -WindowStyle Hidden -PassThru
            $null = $signal.Handle
            $signal.WaitForExit()
            Require ($signal.ExitCode -eq 0) "Could not signal $name gracefully. No force termination was attempted."
            $deadline = (Get-Date).AddSeconds(20)
            do {
                $remaining = @($tree | Where-Object {
                    $p = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
                    $p -and [math]::Abs(($p.StartTime.ToUniversalTime() - $_.CreationDate.ToUniversalTime()).TotalMilliseconds) -lt 1
                })
                if (-not $remaining.Count) { break }
                Start-Sleep -Milliseconds 250
            } while ((Get-Date) -lt $deadline)
            Require (-not $remaining.Count) "$name's process tree did not stop in 20 seconds. No force termination was attempted."
            Write-Host "$name stopped."
        }
    }
}

function Assert-SetupStopped($snapshot) {
    Require (-not $snapshot.Conflicts.Count) ($snapshot.Conflicts -join "`n")
    Require (-not @($snapshot.Services | Where-Object { $_.ProcessId -or $_.WatchProcessId }).Count) 'Close the game and stop services before preparing the installation or creating an account.'
}
function Assert-Physical([string]$path) {
    $current = [IO.Path]::GetFullPath($path)
    while ($current) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        Require (-not $item -or -not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) "Refusing a redirected save/backup path: $current"
        $current = Split-Path -Parent $current
    }
}
function Get-Python {
    $venv = At '.venv/Scripts/python.exe'
    if (Test-Path -LiteralPath $venv) { return $venv }
    $bundled = At 'Tools/python/python.exe'
    if ($script:isRelease -and (Test-Path -LiteralPath $bundled)) { return $bundled }
    $command = Get-Command python.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    Require ([bool]$command) 'Python is required to make a consistent save backup. Install Python or disable automatic backups explicitly.'
    return $command.Source
}
function Backup-Saves($snapshot) {
    if ($NoBackup -and $Action -ne 'Backup') { Write-Host 'Automatic save backup is disabled.'; return }
    if (-not (Test-Path -LiteralPath $script:savePath)) { Write-Host 'No save database exists yet; nothing to back up.'; return }
    Assert-Physical $script:savePath
    $destination = Join-Path (Get-BackupDirectory) ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
    Assert-Physical $destination
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Set-Stage 'Backing up saves' 'Creating a consistent SQLite snapshot and checking its integrity.'
    & (Get-Python) (Join-Path $PSScriptRoot 'SaveBackup.py') $script:savePath (Join-Path $destination 'saves.db')
    Require ($LASTEXITCODE -eq 0) 'Save backup failed. The requested change was not started. Read the operation log.'
    $lastBuild = ''
    $historyPath = Join-Path (Get-BackupDirectory) 'last-launch.json'
    if (Test-Path -LiteralPath $historyPath) {
        Assert-Physical $historyPath
        try { $lastBuild = (Get-Content -LiteralPath $historyPath -Raw | ConvertFrom-Json).GatewayCommit } catch {}
    }
    $metadata = @{ Repository=$script:root; SaveDatabase=$script:savePath; SourceCommit=$snapshot.SourceCommit; SourceBranch=$snapshot.SourceBranch; SourceLabel=$snapshot.SourceLabel;
       LastBuildCommit=$lastBuild; RequestedKind=$SourceKind; RequestedSource=$SourceValue; StartFresh=[bool]$StartFresh; CreatedUtc=[datetime]::UtcNow.ToString('o'); Mods=(Get-ModBackupMetadata $snapshot) } |
        ConvertTo-Json -Depth 9
    Require ($metadata.Length -lt 4 * 1024 * 1024) 'Backup metadata exceeds its size limit; the requested change was not started.'
    $metadata | Set-Content -LiteralPath (Join-Path $destination 'backup.json') -Encoding UTF8
    Write-Host "Save backup verified: $destination"
}
function Get-BackupDirectory {
    $base = Join-Path $env:LOCALAPPDATA 'EmulatorWorkspaceLauncher/backups'
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $key = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($script:savePath.ToLowerInvariant())))).Replace('-','').Substring(0,16) }
    finally { $sha.Dispose() }
    return Join-Path $base $key
}
function Record-SaveLaunch($snapshot) {
    $dll = At 'src/GameServer.Gateway/bin/Debug/net10.0/GameServer.Gateway.dll'
    $commit = ''
    if ((Test-Path -LiteralPath $dll) -and (Get-Item -LiteralPath $dll).VersionInfo.ProductVersion -match '\b([0-9a-f]{40}([0-9a-f]{24})?)\b') { $commit = $Matches[1] }
    $directory = Get-BackupDirectory
    Assert-Physical $directory
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $path = Join-Path $directory 'last-launch.json'
    Assert-Physical $path
    @{ GatewayCommit=$commit; SourceCommit=$snapshot.SourceCommit; LaunchedUtc=[datetime]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath $path -Encoding UTF8
}
function Get-SelectedBackup {
    Require ($BackupId -match '^\d{8}-\d{6}-[0-9a-f]{8}$') 'Select a backup from this installation.'
    $path = Join-Path (Get-BackupDirectory) $BackupId
    Assert-Physical $path
    Assert-Physical (Join-Path $path 'backup.json')
    Assert-Physical (Join-Path $path 'saves.db')
    Require (Test-Path -LiteralPath (Join-Path $path 'backup.json')) 'Backup metadata is missing.'
    Require ((Get-Item -LiteralPath (Join-Path $path 'backup.json')).Length -lt 4 * 1024 * 1024) 'Backup metadata exceeds its size limit.'
    $meta = Get-Content -LiteralPath (Join-Path $path 'backup.json') -Raw | ConvertFrom-Json
    Require (Same-Path $meta.SaveDatabase $script:savePath) 'This backup belongs to a different save database.'
    Require (Test-Path -LiteralPath (Join-Path $path 'saves.db')) 'Backup database is missing.'
    return $path
}
function Restore-SaveBackup($snapshot) {
    $path = Get-SelectedBackup
    Require (-not $snapshot.Conflicts.Count) ($snapshot.Conflicts -join "`n")
    Require (-not @($snapshot.Services | Where-Object { $_.Name -eq 'Game' -and $_.ProcessId }).Count) 'Close the game before restoring saves. Owned servers will be stopped gracefully.'
    Stop-Services $snapshot $names
    Assert-SetupStopped (Get-Snapshot)
    # Stage and validate the selected snapshot before touching current saves.
    Set-Stage 'Checking restore' 'Verifying the selected SQLite backup.'
    $staged = Join-Path (Split-Path $ResultPath) 'restore.db'
    & (Get-Python) (Join-Path $PSScriptRoot 'SaveBackup.py') (Join-Path $path 'saves.db') $staged
    Require ($LASTEXITCODE -eq 0) 'The selected backup could not be validated. Current saves were not changed.'
    Backup-Saves $snapshot
    Set-Stage 'Restoring saves' 'Replacing only this installation''s saves; source, client and keys are unchanged.'
    Move-PreviousSaves
    New-Item -ItemType Directory -Force -Path (Split-Path $script:savePath) | Out-Null
    Copy-Item -LiteralPath $staged -Destination $script:savePath
    # This database has not yet been launched with the currently selected build.
    $history = Join-Path (Get-BackupDirectory) 'last-launch.json'
    Assert-Physical $history
    if (Test-Path -LiteralPath $history) { Remove-Item -LiteralPath $history }
    Write-Host 'Save restored. Source was not changed; choose the matching branch/PR before Play if needed.'
}
function Import-Saves($snapshot) {
    # Another LocalExile's saves into this checkout. That folder is only read: an online backup holds what is still in its WAL.
    Require ([IO.Path]::IsPathRooted($SaveSource)) 'Choose the LocalExile folder to bring the saves from.'
    $source = [IO.Path]::GetFullPath($SaveSource).TrimEnd('\')
    $database = Join-Path $source 'data\saves.db'
    Require (Test-Path -LiteralPath $database -PathType Leaf) 'That LocalExile folder has no saves.'
    Require (-not (Same-Path $database $script:savePath)) 'Those are this LocalExile''s own saves.'
    Assert-SetupStopped $snapshot
    $inside = $source + '\'
    Require (-not @($script:processes | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($inside, [StringComparison]::OrdinalIgnoreCase) }).Count) 'Close the game and stop your current LocalExile first.'
    Set-Stage 'Checking your saves' 'Making a consistent copy of your saves and checking it. The originals are not changed.'
    $staged = Join-Path (Split-Path $ResultPath) 'import.db'
    & (Get-Python) (Join-Path $PSScriptRoot 'SaveBackup.py') $database $staged
    Require ($LASTEXITCODE -eq 0) 'Your saves could not be read. Nothing was changed.'
    Backup-Saves $snapshot
    Set-Stage 'Bringing your characters' 'Copying them into this LocalExile; anything already here is set aside.'
    Move-PreviousSaves
    New-Item -ItemType Directory -Force -Path (Split-Path $script:savePath) | Out-Null
    Move-Item -LiteralPath $staged -Destination $script:savePath
    $history = Join-Path (Get-BackupDirectory) 'last-launch.json'
    Assert-Physical $history
    if (Test-Path -LiteralPath $history) { Remove-Item -LiteralPath $history }
    $choices = Join-Path $source 'Mods\Binaries\mod-state.txt'
    if (Test-Path -LiteralPath $choices -PathType Leaf) {
        New-Item -ItemType Directory -Force -Path (At 'Mods/Binaries') | Out-Null
        Copy-Item -LiteralPath $choices -Destination (At 'Mods/Binaries/mod-state.txt') -Force
    }
    # Last, and what says it is done: a guide run again must not copy over the progress made here since.
    @{ From=$source; CopiedUtc=[datetime]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path (Split-Path $script:savePath) 'saves-brought.json') -Encoding UTF8
    Write-Host "Saves copied from $source."
}
function Move-PreviousSaves {
    $recovery = Join-Path (Split-Path $ResultPath) 'previous-saves'
    New-Item -ItemType Directory -Path $recovery | Out-Null
    foreach ($suffix in @('','-wal','-shm')) { Assert-Physical ($script:savePath + $suffix) }
    foreach ($suffix in @('','-wal','-shm')) {
        $source = $script:savePath + $suffix
        if (Test-Path -LiteralPath $source) { Move-Item -LiteralPath $source -Destination (Join-Path $recovery ('saves.db' + $suffix)) }
    }
    Write-Host "Previous database retained for recovery: $recovery"
}
function Reload-GameData($snapshot) {
    Require (-not $snapshot.Conflicts.Count) ($snapshot.Conflicts -join "`n")
    Require ([bool]@($snapshot.Services | Where-Object { $_.Name -eq 'Game' -and $_.ProcessId }).Count) 'Enter the game with this installation before reloading game-data overrides.'
    Require ([bool]@($snapshot.Services | Where-Object { $_.Name -eq 'Gateway' -and $_.Status -eq 'Ready' }).Count) 'Gateway must be ready before reloading game data.'
    Require (Test-Path -LiteralPath (At 'scripts/dev/client_control.py')) 'This revision has no supported native client-control helper. Use /game_data reload in game if supported.'
    Backup-Saves $snapshot
    Set-Stage 'Reloading game-data overrides' 'Sending /game_data reload through the selected checkout''s native client helper; waiting for Gateway confirmation.'
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = Get-Python
    $info.Arguments = '"' + (At 'scripts/dev/client_control.py') + '" command "/game_data reload"'
    $info.WorkingDirectory = $script:root; $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $p = [Diagnostics.Process]::Start($info)
    try {
        $output = $p.StandardOutput.ReadToEndAsync(); $errorOutput = $p.StandardError.ReadToEndAsync()
        if (-not $p.WaitForExit(30000)) { $p.Kill(); throw 'Native helper timed out; only the disposable helper was stopped. Check Gateway logs to see whether the command applied.' }
        Require ($p.ExitCode -eq 0) 'Native client helper failed. Check that this checkout''s Python has Frida installed, the supported client is in game, and native control is available. You can enter /game_data reload in game.'
        $reply = $output.Result | ConvertFrom-Json
        $confirmed = @($reply.result.replies | Where-Object { $_ -match '^Game data reloaded: revision [0-9]+\.?$' })
        Require ($reply.ok -eq $true -and $confirmed.Count -gt 0) 'Command sent, but Gateway did not confirm a game-data revision. Check Gateway logs; no success was assumed.'
        Write-Host ($confirmed -join "`n")
    } finally { $p.Dispose() }
}
function Invoke-Git([string[]]$arguments) {
    & git -c core.hooksPath=NUL -c core.fsmonitor=false -c submodule.recurse=false -C $script:root @arguments
    Require ($LASTEXITCODE -eq 0) 'Git operation failed. Nothing was forced or discarded. Read the operation output.'
}
function Switch-Source($snapshot) {
    Require (-not $snapshot.Conflicts.Count) ($snapshot.Conflicts -join "`n")
    Require (-not @($snapshot.Services | Where-Object { $_.Name -eq 'Game' -and $_.ProcessId }).Count) 'Close the game before switching source. The launcher will stop owned servers gracefully.'
    Require (Test-Sdk) 'Install .NET 10 SDK first.'
    # Refuse tracked edits and untracked source. Never force checkout, reset, clean or stash owner work.
    & git -c core.fsmonitor=false -C $script:root diff --no-ext-diff --no-textconv --quiet --cached HEAD --
    Require ($LASTEXITCODE -eq 0) 'This checkout has local changes. Commit or move them before switching; the launcher will not discard or stash them.'
    & git -c core.fsmonitor=false -C $script:root diff --no-ext-diff --no-textconv --quiet --
    Require ($LASTEXITCODE -eq 0) 'This checkout has local changes. Commit or move them before switching; the launcher will not discard or stash them.'
    # Pre-mod revisions do not gitignore Mods/Binaries. Preserve those runtime files across a round trip.
    # Tracked changes there are still caught above; every other untracked source path remains a blocker.
    $untracked = @(& git -c core.fsmonitor=false -C $script:root ls-files --others --exclude-standard -- . ':(top,icase,exclude)Mods/Binaries/**')
    Require ($LASTEXITCODE -eq 0 -and -not $untracked.Count) 'This checkout has local changes. Commit or move them before switching; the launcher will not discard or stash them.'
    $target = ''
    if ($SourceKind -eq 'PR') {
        Require ($SourceValue -match '^[1-9][0-9]{0,8}$') 'Enter a positive PR number.'
        Set-Stage 'Fetching pull request' "PR #$SourceValue from LocalExile/LocalExile"
        Invoke-Git @('-c','credential.helper=','fetch','--no-tags','--no-recurse-submodules','https://github.com/LocalExile/LocalExile.git',("+refs/pull/$SourceValue/head:refs/launcher/pr/$SourceValue"))
        $target = "refs/launcher/pr/$SourceValue"
    } else {
        $prefix = if ($SourceKind -eq 'Branch') { 'refs/heads/' } else { 'refs/remotes/' }
        Require ($SourceValue.StartsWith($prefix) -and -not $SourceValue.EndsWith('/HEAD')) 'Select a local or fetched remote branch from the list.'
        $null = & git check-ref-format $SourceValue
        Require ($LASTEXITCODE -eq 0) 'Invalid Git branch reference.'
        $target = $SourceValue
    }
    $commit = [string](& git -C $script:root rev-parse --verify ($target + '^{commit}') 2>$null)
    Require ($LASTEXITCODE -eq 0 -and $commit -match '^[0-9a-f]{40}([0-9a-f]{24})?$') 'The selected revision is unavailable. Refresh branches first.'
    # Reject unrelated repositories before stopping services or altering saves/source.
    foreach ($path in @('GameServer.slnx','scripts/dev/live.ps1','src/GameServer.Gateway/GameServer.Gateway.csproj')) {
        $null = & git -C $script:root cat-file -e ($commit + ':' + $path) 2>$null
        Require ($LASTEXITCODE -eq 0) 'The selected revision is not a supported LocalExile source tree.'
    }
    $profileChanges = @(& git -C $script:root diff --name-only --no-ext-diff --no-textconv HEAD $commit -- 'config/runtime.local.ps1' 'scripts/dev/live.local.ps1')
    Require ($LASTEXITCODE -eq 0 -and -not $profileChanges.Count) 'This revision changes runtime-profile files. Review/select that profile separately before activation; save/profile migration is not automatic.'
    Stop-Services $snapshot $names
    $stopped = Get-Snapshot
    Assert-SetupStopped $stopped
    Backup-Saves $stopped
    Set-Stage 'Switching source' "$SourceKind $SourceValue"
    if ($SourceKind -eq 'Branch') { Invoke-Git @('switch','--no-guess','--', $SourceValue.Substring('refs/heads/'.Length)) }
    else { Invoke-Git @('switch','--detach',$commit) }
    # A local branch may move externally between selection and checkout. Show failure rather than build the wrong revision.
    $actual = [string](& git -C $script:root rev-parse HEAD)
    Require ($LASTEXITCODE -eq 0 -and $actual -eq $commit) 'The branch changed during activation. Refresh and retry; source is switched but services remain stopped.'
    $selectionPath = Get-SourceSelectionPath
    Assert-Physical $selectionPath
    Assert-Physical ($selectionPath + '.tmp')
    New-Item -ItemType Directory -Force -Path (Split-Path $selectionPath) | Out-Null
    @{ Kind=$SourceKind; Value=$SourceValue; Commit=$commit } | ConvertTo-Json | Set-Content -LiteralPath ($selectionPath + '.tmp') -Encoding UTF8
    Move-Item -LiteralPath ($selectionPath + '.tmp') -Destination $selectionPath -Force
    Retire-RemovedManagedMods
    Set-Stage 'Building selected revision' 'Services remain stopped. A failed build preserves the save database.'
    & dotnet build (At 'GameServer.slnx') --nologo -v minimal
    Require ($LASTEXITCODE -eq 0) 'Source switched, but the new revision did not build. Saves were preserved and services remain stopped. Review the build output or switch back.'
    if ($StartFresh -and (Test-Path -LiteralPath $script:savePath)) {
        # A stopped SQLite database can still have committed WAL pages. Move the complete set aside together;
        # recovery files are retained locally even when automatic backup was disabled. No owner data is deleted.
        Move-PreviousSaves
        & (Get-Python) -c 'import sqlite3,sys; sqlite3.connect(sys.argv[1]).close()' $script:savePath
        Require ($LASTEXITCODE -eq 0) 'Could not create the fresh save database. Previous saves remain in the recovery folder shown above.'
        $history = Join-Path (Get-BackupDirectory) 'last-launch.json'
        Assert-Physical $history
        if (Test-Path -LiteralPath $history) { Remove-Item -LiteralPath $history }
    }
    Write-Host 'Revision prepared. Press Play when ready. Existing client files and login keys were reused.'
}
function Start-FreshSaves($snapshot) {
    Assert-SetupStopped $snapshot
    if (-not (Test-Path -LiteralPath $script:savePath)) { Write-Host 'No save database exists yet; the next start creates one.'; return }
    Backup-Saves $snapshot
    Set-Stage 'Starting fresh' 'Moving this installation''s saves aside; nothing is deleted.'
    Move-PreviousSaves
    & (Get-Python) -c 'import sqlite3,sys; sqlite3.connect(sys.argv[1]).close()' $script:savePath
    Require ($LASTEXITCODE -eq 0) 'Could not create the fresh save database. Previous saves remain in the recovery folder shown above.'
    $history = Join-Path (Get-BackupDirectory) 'last-launch.json'
    Assert-Physical $history
    if (Test-Path -LiteralPath $history) { Remove-Item -LiteralPath $history }
}
function Get-ClientDetail {
    $client = Get-Item -LiteralPath (At 'Client') -Force -ErrorAction SilentlyContinue
    if (-not $client -or -not ($client.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return 'Standalone 3.29.3b executable at Client/Game.exe' }
    $target = @($client.Target)[0]
    if (Test-Path -LiteralPath (At 'Client/Game.exe')) { return "Linked to $target" }
    return "Client links to $target, which has no client. Run the setup guide to replace the link."
}
function Prepare-Client($snapshot) {
    Assert-SetupStopped $snapshot
    Require (Test-Sdk) 'Install .NET 10 SDK first.'
    # A linked client is used as it is. Other checkouts may share it, and its patched executable trusts one set of keys, so
    # preparing it here could break them.
    $client = Get-Item -LiteralPath (At 'Client') -Force -ErrorAction SilentlyContinue
    if ($client -and ($client.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        $target = @($client.Target)[0]
        Require ((Test-Path -LiteralPath (At 'Client/Game.patched.exe')) -and (Test-Path -LiteralPath (At 'data/login-keys.json'))) "Client is a link to $target, and other copies of LocalExile may share that folder, so Waypoint will not change it. Prepare it there, or run the setup guide to put a client here."
        Write-Host "Client is a link to $target and is already prepared: nothing to do."
        return
    }
    Require (Test-Path -LiteralPath (At 'Client/Content.ggpk')) 'Copy the complete standalone 3.29.3b client into Client first.'
    foreach ($relative in @('Client/Content.ggpk','Client/Game.exe','Client/PathOfExile.exe','Client/Game.patched.exe','data','data/login-keys.json')) {
        $item = Get-Item -LiteralPath (At $relative) -ErrorAction SilentlyContinue
        Require (-not $item -or -not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) "Preparation refuses redirected files or folders: $relative"
    }
    $keys = Test-Path -LiteralPath (At 'data/login-keys.json')
    $patched = Test-Path -LiteralPath (At 'Client/Game.patched.exe')
    Require ($keys -or -not $patched) 'A prepared executable exists without keys. Preserve it and recover its matching keys, or prepare a new client copy.'
    if (-not (Test-Path -LiteralPath (At 'Client/Game.exe'))) {
        Require (Test-Path -LiteralPath (At 'Client/PathOfExile.exe')) 'Client/PathOfExile.exe or Client/Game.exe is required.'
        Copy-Item -LiteralPath (At 'Client/PathOfExile.exe') -Destination (At 'Client/Game.exe')
    }
    Set-Stage 'Building preparation tools' 'Auth and Patch'
    foreach ($name in @('Auth','Patch')) {
        & dotnet build (At "src/GameServer.$name") --nologo -v minimal
        Require ($LASTEXITCODE -eq 0) "Build failed for $name."
    }
    Set-Stage 'Preparing client' 'Retaining existing keys and prepared executable; creating only missing files.'
    $auth = At 'src/GameServer.Auth/bin/Debug/net10.0/GameServer.Auth.dll'
    if (-not $keys) {
        & dotnet $auth keygen (At 'Client/Game.exe')
        Require ($LASTEXITCODE -eq 0) 'Key generation failed. Check that the original executable is standalone 3.29.3b.'
    }
    if (-not $patched) {
        & dotnet $auth patch (At 'Client/Game.exe') (At 'Client/Game.patched.exe')
        Require ($LASTEXITCODE -eq 0) 'Client preparation failed. Existing keys were preserved.'
    }
    Set-Stage 'Selecting local gateway' 'Only the copied client archive is updated.'
    & dotnet (At 'src/GameServer.Patch/bin/Debug/net10.0/GameServer.Patch.dll') gateway (At 'Client/Content.ggpk') --login '127.0.0.1:20493'
    Require ($LASTEXITCODE -eq 0) 'Local gateway setup failed. Close all clients and inspect the operation log.'
}
function Register-Account($snapshot) {
    Assert-AccountsStopped $snapshot
    Require ($script:isRelease -or (Test-Sdk)) 'Install .NET 10 SDK first.'
    Require (-not ($ResultPath -match '[&|<>^%!"\r\n]')) 'Account setup needs a launcher data path without command-shell metacharacters.'
    Require ((-not $env:Game_DEV_PORT -or $env:Game_DEV_PORT -eq '7000') -and (-not $env:Game_DEV_BIND -or $env:Game_DEV_BIND -eq '127.0.0.1')) 'Account setup requires the default localhost account-service address.'
    Require (-not @($script:listeners | Where-Object LocalPort -eq 7000).Count) 'Port 7000 is in use. Stop the existing account service first; it will not be adopted.'
    # This request arrives through stdin, never a command argument or file. Do not transcribe this action.
    [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
    try { $request = [Console]::In.ReadLine() | ConvertFrom-Json } catch { throw 'Could not read local account request.' }
    Require ($request.Name -is [string] -and $request.Name -match '^[A-Za-z0-9_-]{3,32}$') 'Account name must be 3-32 letters, digits, underscores or hyphens.'
    Require ($request.Password -is [string] -and $request.Password.Length -ge 4 -and $request.Password.Length -le 1024) 'Password must contain 4-1024 characters.'
    Assert-AccountDatabase $request $false
    Backup-Saves $snapshot
    if ($script:isRelease) {
        # LocalExile.exe reads standard input in the console's code page: anything but ASCII could become another password.
        Require ($request.Password -cmatch '^[\x20-\x7E]+$') 'The password can only use English letters, digits and common symbols.'
        Set-Stage 'Creating local account' 'Using this installation only. Credentials are not saved by the launcher.'
        $OutputEncoding = [Text.ASCIIEncoding]::new()
        $lines = @($request.Password | & (At 'LocalExile.exe') account create $request.Name --password-stdin --json)
        $code = $LASTEXITCODE
        $failure = @($lines | ForEach-Object { try { $_ | ConvertFrom-Json } catch { $null } } | Where-Object { $_.event -eq 'error' } | Select-Object -First 1)
        Require ($code -eq 0) $(if ($failure.Count) { [string]$failure[0].message } else { "LocalExile.exe could not create the account (exit $code)." })
        return
    }
    Set-Stage 'Building account service' 'Preparing the local account service for this checkout.'
    & dotnet build (At 'src/GameServer.Host') --nologo -v minimal | Out-Null
    Require ($LASTEXITCODE -eq 0) 'Account service build failed. Run Build checkout to inspect compiler output.'
    Assert-AccountsStopped (Get-Snapshot)
    $log = Join-Path (Split-Path $ResultPath) 'account-service.log'
    $err = Join-Path (Split-Path $ResultPath) 'account-service.err'
    $line = '/c dotnet src\GameServer.Host\bin\Debug\net10.0\GameServer.Host.dll 1>"' + $log + '" 2>"' + $err + '"'
    $parent = Start-Process cmd.exe -ArgumentList $line -WorkingDirectory $script:root -WindowStyle Hidden -PassThru
    $target = $null; $client = $null; $registered = $false; $failureStage = $null
    try {
        Set-Stage 'Creating local account' 'Using this installation only. Credentials are not saved by the launcher.'
        $deadline = (Get-Date).AddSeconds(20)
        do {
            Start-Sleep -Milliseconds 200
            Get-Inventory
            $targets = @($script:processes | Where-Object { $_.Name -ieq 'dotnet.exe' -and $_.ParentProcessId -eq $parent.Id -and $_.CommandLine -match '^dotnet\s+src\\GameServer.Host\\bin\\Debug\\net10.0\\GameServer.Host.dll\s*$' })
            if ($targets.Count -eq 1) { $target = $targets[0] }
            $listeners = @($script:listeners | Where-Object LocalPort -eq 7000)
            Require (-not @($listeners | Where-Object { -not $target -or $_.OwningProcess -ne $target.ProcessId }).Count) 'Another process took account-service port 7000.'
            if ($target -and $listeners.Count) { break }
        } while ((Get-Date) -lt $deadline)
        Require ($target -and $listeners.Count) 'The temporary account service did not become ready.'
        $client = [Net.Sockets.TcpClient]::new()
        $client.ReceiveTimeout = 15000; $client.SendTimeout = 15000
        $client.Connect('127.0.0.1',7000)
        $stream = $client.GetStream()
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false), 4096, $true)
        $writer.AutoFlush = $true
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $false, 4096, $true)
        try {
            $payload = @{op='register';name=$request.Name;password=$request.Password} | ConvertTo-Json -Compress
            $writer.WriteLine($payload)
            $reply = $reader.ReadLine() | ConvertFrom-Json
            Require ($reply.ok -eq $true) 'The account could not be created. The name may already exist, or it may not meet the server rules. No password was changed.'
            $registered = $true
        } finally { $reader.Dispose(); $writer.Dispose(); $payload = $null }
    } catch {
        $failureStage = $script:stage
        throw
    } finally {
        $request = $null
        if ($client) { $client.Dispose() }
        # Always stop this action's dedicated account service, including failed registrations.
        if (-not $target) {
            $target = Get-CimInstance Win32_Process | Where-Object { $_.Name -ieq 'dotnet.exe' -and $_.ParentProcessId -eq $parent.Id -and $_.CommandLine -match '^dotnet\s+src\\GameServer.Host\\bin\\Debug\\net10.0\\GameServer.Host.dll\s*$' } | Select-Object -First 1
        }
        if ($target) {
            Set-Stage 'Closing account service' 'Allowing the temporary local service to shut down.'
            $signalArgs = @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',('"' + (Join-Path $PSScriptRoot 'ConsoleSignal.ps1') + '"'),'-TargetId',[string]$target.ProcessId,'-ParentId',[string]$parent.Id,'-StartedUtc',$target.CreationDate.ToUniversalTime().ToString('o'))
            $signal = Start-Process (Join-Path $PSHOME 'powershell.exe') -ArgumentList $signalArgs -WindowStyle Hidden -PassThru
            $null = $signal.Handle; $signal.WaitForExit()
            Require ($signal.ExitCode -eq 0) "Account setup could not stop its temporary service (PID $($target.ProcessId)). No force termination was attempted."
            $deadline = (Get-Date).AddSeconds(20)
            while ((Get-Process -Id $target.ProcessId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
            Require (-not (Get-Process -Id $target.ProcessId -ErrorAction SilentlyContinue)) "The temporary account service (PID $($target.ProcessId)) is still running."
        }
        if ($failureStage) { $script:stage = $failureStage }
    }
    Require $registered 'Account registration did not finish.'
}

# After every function above: LinuxBridge.ps1 replaces those that cannot work on Linux. Windows PowerShell has no $IsLinux.
if ($IsLinux) { . (Join-Path $PSScriptRoot 'LinuxBridge.ps1') }

try {
    Require ([IO.Path]::IsPathRooted($ResultPath)) 'ResultPath must be absolute.'
    $script:root = (Resolve-Path -LiteralPath $RepoRoot).Path.TrimEnd('\')
    Require (-not ($script:root -match '[&|<>^%!"\r\n]')) 'This checkout path contains characters unsupported by upstream live.ps1 command handling. Use a plain path (spaces are supported).'
    # A release install has no source and no live.ps1: its saves, mods and accounts are where a checkout has them.
    $script:isRelease = (Test-Path -LiteralPath (At $(if ($IsLinux) { 'LocalExile' } else { 'LocalExile.exe' }))) -and (Test-Path -LiteralPath (At 'release.json')) -and -not (Test-Path -LiteralPath (At 'GameServer.slnx'))
    if (-not $script:isRelease) {
        Require (Test-Path -LiteralPath (At 'GameServer.slnx')) 'Choose the LocalExile repository root containing GameServer.slnx.'
        Require (Test-Path -LiteralPath (At 'scripts/dev/live.ps1')) 'The checkout has no scripts/dev/live.ps1.'
    }
    Set-Location -LiteralPath $script:root
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
public static class NativeRuntime {
  [DllImport("shell32.dll", SetLastError=true)] static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string line, out int count);
  [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern uint GetFinalPathNameByHandle(IntPtr handle, System.Text.StringBuilder path, uint size, uint flags);
  public static string FinalPath(string path) {
    using (var stream = System.IO.File.OpenRead(path)) {
      var buffer = new System.Text.StringBuilder(32768);
      uint size = GetFinalPathNameByHandle(stream.SafeFileHandle.DangerousGetHandle(), buffer, (uint)buffer.Capacity, 0);
      if (size == 0 || size >= buffer.Capacity) throw new System.IO.IOException("Could not resolve capture executable identity.");
      string value = buffer.ToString();
      if (value.StartsWith(@"\\?\UNC\")) return @"\\" + value.Substring(8);
      return value.StartsWith(@"\\?\") ? value.Substring(4) : value;
    }
  }
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool AttachConsole(uint pid);
  [DllImport("kernel32.dll")] static extern bool FreeConsole();
  [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);
  [DllImport("kernel32.dll", SetLastError=true)] static extern uint GetConsoleProcessList([Out] uint[] list, uint count);
  public static string[] Arguments(string line) {
    if (String.IsNullOrEmpty(line)) return new string[0];
    int count; var memory = CommandLineToArgvW(line, out count);
    if (memory == IntPtr.Zero) return new string[0];
    try { var args = new string[count]; for (int i=0;i<count;i++) args[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory,i*IntPtr.Size)); return args; }
    finally { LocalFree(memory); }
  }
  public static bool Interrupt(int pid, int parent) { return Interrupt(pid, parent, new int[] { pid, parent }); }
  public static bool Interrupt(int pid, int parent, int[] allowed) {
    FreeConsole(); if (!AttachConsole((uint)pid)) return false;
    try {
      // A dedicated upstream console contains cmd + dotnet + this helper. Do not broadcast into a shared terminal.
      var ids = new uint[256]; uint count = GetConsoleProcessList(ids, (uint)ids.Length);
      uint helper = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
      if (count < 3 || count > ids.Length || Array.IndexOf(ids,(uint)pid)<0 || Array.IndexOf(ids,(uint)parent)<0 || Array.IndexOf(ids,helper)<0) return false;
      for (int i=0; i<count; i++) if (ids[i]!=helper && Array.IndexOf(allowed,(int)ids[i])<0) return false;
      SetConsoleCtrlHandler(IntPtr.Zero, true);
      bool sent = GenerateConsoleCtrlEvent(0,0); Thread.Sleep(300); return sent;
    } finally { FreeConsole(); }
  }
}
'@
    Import-RuntimeProfile
    if ($Action -notin @('Inspect','QueryAccounts')) {
        # One writer across all launcher instances/checkouts: upstream has fixed shared ports.
        $mutex = [Threading.Mutex]::new($false, 'Local\EmulatorWorkspaceLauncher.Runtime.v1')
        try { $hasLock = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $hasLock = $true }
        Require $hasLock 'Another launcher operation is in progress. Wait for it to finish.'
        if ($Action -notin @('Register','ManageAccount')) {
            Start-Transcript -LiteralPath (Join-Path (Split-Path $ResultPath) 'operation.log') -Force | Out-Null
            $transcribing = $true
        }
    }
    Set-Stage 'Checking setup' 'Checking prerequisites, process ownership and selected installation.'
    $snapshot = Get-Snapshot
    $selected = if ($Service -eq 'All') { $names } else { @($Service) }
    switch ($Action) {
        'CheckCapture' { $null = Check-CaptureDependencies }
        'SetupCapture' { Setup-CaptureDependencies }
        'StartCapture' { Start-Capture $snapshot }
        'StopCapture' { Stop-Capture }
        'QueryAccounts' { $script:accountData = Invoke-AccountData @{ Action='List' } }
        'ManageAccount' { Manage-Account $snapshot }
        { $_ -in @('BuildMod','BuildMods','EnableMod','DisableMod','ReloadMod','QueryMods','RestoreModChoices') } { Invoke-ModOperation $snapshot }
        'Switch' { Switch-Source $snapshot }
        'FreshSaves' { Start-FreshSaves $snapshot }
        'Fetch' {
            Set-Stage 'Refreshing remote branches' 'Fetching Git objects only; source files, saves and running services are unchanged.'
            $remotes = @(& git -C $script:root remote)
            Require ($LASTEXITCODE -eq 0 -and $remotes.Count) 'No Git remotes are configured.'
            foreach ($remote in $remotes) { Invoke-Git @('fetch','--no-tags','--no-recurse-submodules','--',$remote) }
        }
        'ReloadData' { Reload-GameData $snapshot }
        'Backup' { Backup-Saves $snapshot }
        'RestoreBackup' { Restore-SaveBackup $snapshot }
        'ImportSaves' { Import-Saves $snapshot }
        'DeleteBackup' {
            $path = Get-SelectedBackup
            Set-Stage 'Deleting backup' $BackupId
            Require (@(Get-ChildItem -LiteralPath $path -Force).Count -eq 2) 'The backup folder contains extra files. Open the folder and review them before deleting this backup.'
            # Only the two known files and an empty, physically verified backup directory are removed.
            Remove-Item -LiteralPath (Join-Path $path 'saves.db')
            Remove-Item -LiteralPath (Join-Path $path 'backup.json')
            Remove-Item -LiteralPath $path
        }
        'PrepareClient' { Prepare-Client $snapshot }
        'Dependencies' {
            Assert-SetupStopped $snapshot
            Require ([bool](Get-Command npm.cmd -ErrorAction SilentlyContinue)) 'Install Node.js first, then reopen the launcher.'
            Require (Test-Path -LiteralPath (At 'Tools/ooz/package-lock.json')) 'The checkout has no locked extraction dependency.'
            Set-Stage 'Installing extraction dependency' 'npm ci in this checkout''s Tools/ooz directory'
            Push-Location -LiteralPath (At 'Tools/ooz')
            try { & npm.cmd ci --ignore-scripts --no-audit --no-fund; Require ($LASTEXITCODE -eq 0) 'Dependency installation failed. Read the operation log.' }
            finally { Pop-Location }
        }
        'Register' { Register-Account $snapshot }
        'Build' {
            Require (-not $script:isRelease) 'This folder is a LocalExile release: it comes built, and has no source code to build.'
            Require (-not @($snapshot.Services | Where-Object { $_.Name -ne 'Game' -and ($_.ProcessId -or $_.WatchProcessId) }).Count) 'Stop this checkout''s services before rebuilding their assemblies.'
            Require (Test-Sdk) 'Install the .NET 10 SDK first.'
            Backup-Saves $snapshot
            Set-Stage 'Building checkout' 'Building the selected LocalExile solution.'
            & dotnet build (At 'GameServer.slnx') --nologo
            Require ($LASTEXITCODE -eq 0) 'LocalExile build failed. Read the operation output.'
        }
        'Start' { Start-Services $snapshot $selected; if ('Gateway' -in $selected) { Record-SaveLaunch (Get-Snapshot); if ($CaptureAreaLayouts) { Start-Capture (Get-Snapshot) } } }
        'Play' {
            Assert-CanStart $snapshot
            if (-not @($snapshot.Services | Where-Object { $_.Name -eq 'Game' -and $_.ProcessId }).Count) {
                Start-Services $snapshot $names
                if ($CaptureAreaLayouts) { Start-Capture (Get-Snapshot) }
                if (-not @($snapshot.Services | Where-Object { $_.Name -eq 'Gateway' -and ($_.ProcessId -or $_.WatchProcessId) }).Count) { Record-SaveLaunch (Get-Snapshot) }
                $entry = if ($script:profile -eq 'Local runtime wrapper') { At 'scripts/dev/live.local.ps1' } else { At 'scripts/dev/live.ps1' }
                Set-Stage 'Opening game' 'Launching the prepared client through live.ps1 -ClientOnly.'
                & $entry -ClientOnly
                Start-Sleep -Milliseconds 700
                $after = Get-Snapshot
                Require ([bool]@($after.Services | Where-Object { $_.Name -eq 'Game' -and $_.ProcessId }).Count) 'The game exited during startup. Review Client/logs/Client.txt.'
            }
        }
        'Stop' { Stop-Services $snapshot $selected }
        'Restart' {
            # Validate optional dependencies before stopping a working Gateway.
            if ($CaptureAreaLayouts -and 'Gateway' -in $selected) { $null = Check-CaptureDependencies }
            Stop-Services $snapshot $selected; Start-Services (Get-Snapshot) $selected
            if ('Gateway' -in $selected) { Record-SaveLaunch (Get-Snapshot); if ($CaptureAreaLayouts) { Start-Capture (Get-Snapshot) } }
        }
        'CloseGame' {
            $game = $snapshot.Services | Where-Object { $_.Name -eq 'Game' -and $_.ProcessId }
            if ($game) {
                Set-Stage 'Closing game' 'Waiting for the game window to close without force termination.'
                $p = Get-Process -Id $game.ProcessId
                Require (Same-Path $p.Path (At 'Client/Game.patched.exe')) 'Game identity changed. Refresh before closing it.'
                $null = $p.Handle
                # CIM timestamps have microsecond precision; Process.StartTime retains 100 ns ticks.
                Require ([math]::Abs(($p.StartTime.ToUniversalTime() - [datetime]::Parse($game.StartedUtc).ToUniversalTime()).TotalMilliseconds) -lt 1) 'Game process identity changed.'
                Require ($p.CloseMainWindow()) 'The game did not accept a close request. Close its window manually.'
                Require ($p.WaitForExit(20000)) 'The game is still closing. No force termination was attempted.'
            }
        }
    }
    if ($Action -notin @('Inspect','QueryAccounts')) { $snapshot = Get-Snapshot }
    Set-Stage 'Completed' "$Action completed."
    $result = @{ Ok=$true; Message=$(if ($Action -eq 'Inspect') { 'Environment checked.' } elseif ($Action -eq 'Register') { 'Local account created. Sign in inside the game, then create a character there.' } elseif ($Action -eq 'ManageAccount') { 'Account data updated.' } elseif ($Action -eq 'QueryAccounts') { 'Accounts refreshed.' } else { "$Action completed." }); Snapshot=$snapshot; AccountData=$script:accountData }
} catch {
    $result = @{ Ok=$false; Message=$_.Exception.Message; Snapshot=$null; FailedStage=$script:stage }
} finally {
    if ($transcribing) { Stop-Transcript | Out-Null }
    if ($hasLock -and $mutex) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
    if ($result) { Write-Result $result }
}
if (-not $result.Ok) { exit 1 }
