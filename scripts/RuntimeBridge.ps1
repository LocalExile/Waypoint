# Adapter v1: Windows PowerShell 5.1, upstream LocalExile 3.29.3b / .NET 10 Debug layout.
# All operations use result files; live.ps1 never runs in a pipeline or with captured stdout.
param(
    [Parameter(Mandatory=$true)][ValidateSet('Inspect','Start','Play','Stop','CloseGame','Restart','Build','PrepareClient','Dependencies','Register')][string]$Action,
    [Parameter(Mandatory=$true)][string]$RepoRoot,
    [ValidateSet('All','Auth','Patch','Gateway')][string]$Service = 'All',
    [Parameter(Mandatory=$true)][string]$ResultPath,
    [switch]$UseLocalProfile
)
$ErrorActionPreference = 'Stop'
$result = $null
$transcribing = $false
$mutex = $null
$hasLock = $false
$script:stage = 'Checking setup'
$names = @('Patch','Gateway','Auth')
$portMap = @{ Patch = @(12995,8080); Gateway = @(6112); Auth = @(20493,20495) }

function Write-Result($value) {
    $value | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
}
function Require($condition, [string]$message) { if (-not $condition) { throw $message } }
function At([string]$relative) { Join-Path $script:root $relative }
function Set-Stage([string]$stage, [string]$detail = '') {
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
function Get-Snapshot {
    Get-Inventory
    $conflicts = [Collections.Generic.List[string]]::new()
    $services = @()
    foreach ($name in $names) {
        $owned = @($script:processes | Where-Object { Owned-Server $_ $name })
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
        $status = if ($ready) { 'Ready' } elseif ($process) { 'Starting' } else { 'Stopped' }
        $detail = if ($ready) { 'Owned process; all required ports listening' } elseif ($process) { 'Process exists; required listeners are missing' } else { 'No verified process for this checkout' }
        $services += New-Service $name $status $process $detail $portMap[$name]
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
        @{ Name='Original client'; Ready=(Test-Path -LiteralPath (At 'Client/Game.exe')); Detail='Standalone 3.29.3b executable at Client/Game.exe' },
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
    @{ Root=$script:root; Profile=$script:profile; SaveDatabase=$script:savePath; SourceCommit=$commit; SourceBranch=$branch;
       BuildCommit=($builds -join '; '); Requirements=@($requirements); Services=@($services); Conflicts=@($conflicts.ToArray()) }
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
    $missing = @($selected | Where-Object { $n = $_; -not ($snapshot.Services | Where-Object { $_.Name -eq $n -and $_.Status -eq 'Ready' }) })
    if (-not $missing.Count) { return }
    # live.ps1 rebuilds dependencies. Do not overwrite assemblies while any owned server is running.
    $running = @($snapshot.Services | Where-Object { $_.Name -ne 'Game' -and $_.ProcessId -ne 0 })
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
    $latest = Get-Snapshot
    Require (-not $latest.Conflicts.Count) ($latest.Conflicts -join "`n")
    Require (-not @($latest.Services | Where-Object { $_.Name -in $missing -and $_.ProcessId }).Count) 'A selected service started during preparation. Refresh and retry without restarting it.'
    Set-Stage 'Starting services' ($missing -join ', ')
    $argsForLive = @{ NoClient=$true; NoBuild=$true; NoExtract=$true; Only=$missing }
    $entry = if ($script:profile -eq 'Local runtime wrapper') { At 'scripts/dev/live.local.ps1' } else { At 'scripts/dev/live.ps1' }
    & $entry @argsForLive
    Set-Stage 'Waiting for services' 'Verifying ownership of every required listening port.'
    $deadline = (Get-Date).AddSeconds(60)
    do {
        $latest = Get-Snapshot
        Require (-not $latest.Conflicts.Count) ($latest.Conflicts -join "`n")
        $pending = @($latest.Services | Where-Object { $_.Name -in $selected -and $_.Status -ne 'Ready' })
        if (-not $pending.Count) { return }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    throw 'Servers did not become ready within 60 seconds. Review logs/live-*.log and logs/live-*.err. Partial starts remain visible; use Stop servers to clean up.'
}
function Stop-Services($snapshot, [string[]]$selected) {
    Require (-not @($snapshot.Services | Where-Object { $_.Name -eq 'Game' -and $_.ProcessId }).Count) 'Close the game first using Close game, then stop or restart services.'
    foreach ($name in @('Gateway','Auth','Patch')) {
        if ($name -notin $selected) { continue }
        Get-Inventory
        $targets = @($script:processes | Where-Object { Owned-Server $_ $name })
        foreach ($target in $targets) {
            Set-Stage 'Stopping services' $name
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
            while ((Get-Process -Id $target.ProcessId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
            Require (-not (Get-Process -Id $target.ProcessId -ErrorAction SilentlyContinue)) "$name did not stop in 20 seconds. No force termination was attempted."
            Write-Host "$name stopped."
        }
    }
}

function Assert-SetupStopped($snapshot) {
    Require (-not $snapshot.Conflicts.Count) ($snapshot.Conflicts -join "`n")
    Require (-not @($snapshot.Services | Where-Object ProcessId -gt 0).Count) 'Close the game and stop services before preparing the installation or creating an account.'
}
function Prepare-Client($snapshot) {
    Assert-SetupStopped $snapshot
    Require (Test-Sdk) 'Install .NET 10 SDK first.'
    $client = Get-Item -LiteralPath (At 'Client') -ErrorAction SilentlyContinue
    Require ($client -and -not ($client.Attributes -band [IO.FileAttributes]::ReparsePoint)) 'Client must be a separate physical copy in this checkout; preparation refuses a redirected Client folder.'
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
    Assert-SetupStopped $snapshot
    Require (Test-Sdk) 'Install .NET 10 SDK first.'
    Require (-not ($ResultPath -match '[&|<>^%!"\r\n]')) 'Account setup needs a launcher data path without command-shell metacharacters.'
    Require ((-not $env:Game_DEV_PORT -or $env:Game_DEV_PORT -eq '7000') -and (-not $env:Game_DEV_BIND -or $env:Game_DEV_BIND -eq '127.0.0.1')) 'Account setup requires the default localhost account-service address.'
    Require (-not @($script:listeners | Where-Object LocalPort -eq 7000).Count) 'Port 7000 is in use. Stop the existing account service first; it will not be adopted.'
    # This request arrives through stdin, never a command argument or file. Do not transcribe this action.
    try { $request = [Console]::In.ReadLine() | ConvertFrom-Json } catch { throw 'Could not read local account request.' }
    Require ($request.Name -is [string] -and $request.Name -match '^[A-Za-z0-9_-]{3,32}$') 'Account name must be 3-32 letters, digits, underscores or hyphens.'
    Require ($request.Password -is [string] -and $request.Password.Length -ge 4 -and $request.Password.Length -le 1024) 'Password must contain 4-1024 characters.'
    Set-Stage 'Building account service' 'Preparing the local account service for this checkout.'
    & dotnet build (At 'src/GameServer.Host') --nologo -v minimal | Out-Null
    Require ($LASTEXITCODE -eq 0) 'Account service build failed. Run Build checkout to inspect compiler output.'
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

try {
    Require ([IO.Path]::IsPathRooted($ResultPath)) 'ResultPath must be absolute.'
    $script:root = (Resolve-Path -LiteralPath $RepoRoot).Path.TrimEnd('\')
    Require (-not ($script:root -match '[&|<>^%!"\r\n]')) 'This checkout path contains characters unsupported by upstream live.ps1 command handling. Use a plain path (spaces are supported).'
    Require (Test-Path -LiteralPath (At 'GameServer.slnx')) 'Choose the LocalExile repository root containing GameServer.slnx.'
    Require (Test-Path -LiteralPath (At 'scripts/dev/live.ps1')) 'The checkout has no scripts/dev/live.ps1.'
    Set-Location -LiteralPath $script:root
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
public static class NativeRuntime {
  [DllImport("shell32.dll", SetLastError=true)] static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string line, out int count);
  [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
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
  public static bool Interrupt(int pid, int parent) {
    FreeConsole(); if (!AttachConsole((uint)pid)) return false;
    try {
      // A dedicated upstream console contains cmd + dotnet + this helper. Do not broadcast into a shared terminal.
      var ids = new uint[8]; uint count = GetConsoleProcessList(ids, (uint)ids.Length);
      if (count != 3 || Array.IndexOf(ids,(uint)pid)<0 || Array.IndexOf(ids,(uint)parent)<0 || Array.IndexOf(ids,(uint)System.Diagnostics.Process.GetCurrentProcess().Id)<0) return false;
      SetConsoleCtrlHandler(IntPtr.Zero, true);
      bool sent = GenerateConsoleCtrlEvent(0,0); Thread.Sleep(300); return sent;
    } finally { FreeConsole(); }
  }
}
'@
    Import-RuntimeProfile
    if ($Action -ne 'Inspect') {
        # One writer across all launcher instances/checkouts: upstream has fixed shared ports.
        $mutex = [Threading.Mutex]::new($false, 'Local\EmulatorWorkspaceLauncher.Runtime.v1')
        try { $hasLock = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $hasLock = $true }
        Require $hasLock 'Another launcher operation is in progress. Wait for it to finish.'
        if ($Action -ne 'Register') {
            Start-Transcript -LiteralPath (Join-Path (Split-Path $ResultPath) 'operation.log') -Force | Out-Null
            $transcribing = $true
        }
    }
    Set-Stage 'Checking setup' 'Checking prerequisites, process ownership and selected installation.'
    $snapshot = Get-Snapshot
    $selected = if ($Service -eq 'All') { $names } else { @($Service) }
    switch ($Action) {
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
            Require (-not @($snapshot.Services | Where-Object { $_.Name -ne 'Game' -and $_.ProcessId }).Count) 'Stop this checkout''s services before rebuilding their assemblies.'
            Require (Test-Sdk) 'Install the .NET 10 SDK first.'
            Set-Stage 'Building checkout' 'Building the selected LocalExile solution.'
            & dotnet build (At 'GameServer.slnx') --nologo
            Require ($LASTEXITCODE -eq 0) 'LocalExile build failed. Read the operation output.'
        }
        'Start' { Start-Services $snapshot $selected }
        'Play' {
            Assert-CanStart $snapshot
            if (-not @($snapshot.Services | Where-Object { $_.Name -eq 'Game' -and $_.ProcessId }).Count) {
                Start-Services $snapshot $names
                $entry = if ($script:profile -eq 'Local runtime wrapper') { At 'scripts/dev/live.local.ps1' } else { At 'scripts/dev/live.ps1' }
                Set-Stage 'Opening game' 'Launching the prepared client through live.ps1 -ClientOnly.'
                & $entry -ClientOnly
                Start-Sleep -Milliseconds 700
                $after = Get-Snapshot
                Require ([bool]@($after.Services | Where-Object { $_.Name -eq 'Game' -and $_.ProcessId }).Count) 'The game exited during startup. Review Client/logs/Client.txt.'
            }
        }
        'Stop' { Stop-Services $snapshot $selected }
        'Restart' { Stop-Services $snapshot $selected; Start-Services (Get-Snapshot) $selected }
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
    if ($Action -ne 'Inspect') { $snapshot = Get-Snapshot }
    Set-Stage 'Completed' "$Action completed."
    $result = @{ Ok=$true; Message=$(if ($Action -eq 'Inspect') { 'Environment checked.' } elseif ($Action -eq 'Register') { 'Local account created. Sign in inside the game, then create a character there.' } else { "$Action completed." }); Snapshot=$snapshot }
} catch {
    $result = @{ Ok=$false; Message=$_.Exception.Message; Snapshot=$null; FailedStage=$script:stage }
} finally {
    if ($transcribing) { Stop-Transcript | Out-Null }
    if ($hasLock -and $mutex) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
    if ($result) { Write-Result $result }
}
if (-not $result.Ok) { exit 1 }
