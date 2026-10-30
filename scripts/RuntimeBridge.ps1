# Adapter v1: Windows PowerShell 5.1, upstream LocalExile 3.29.3b / .NET 10 Debug layout.
# All operations use result files; live.ps1 never runs in a pipeline or with captured stdout.
param(
    [Parameter(Mandatory=$true)][ValidateSet('Inspect','Start','Play','Stop','CloseGame','Restart','Build')][string]$Action,
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
$names = @('Patch','Gateway','Auth')
$portMap = @{ Patch = @(12995,8080); Gateway = @(6112); Auth = @(20493,20495) }

function Write-Result($value) {
    $value | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
}
function Require($condition, [string]$message) { if (-not $condition) { throw $message } }
function At([string]$relative) { Join-Path $script:root $relative }
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
        $argsForLive = @{ NoClient=$true; NoBuild=$true; Only=$missing }
        foreach ($n in $missing) {
            $dll = At "src/GameServer.$n/bin/Debug/net10.0/GameServer.$n.dll"
            Require (Test-Path -LiteralPath $dll) "Build $n first: stop servers, then click Build checkout."
            Require ($snapshot.SourceCommit -and (Get-Item -LiteralPath $dll).VersionInfo.ProductVersion.Contains($snapshot.SourceCommit)) 'Existing binaries do not match the selected commit. Stop servers, then Build checkout.'
        }
        & git -C $script:root diff --quiet HEAD --
        Require ($LASTEXITCODE -eq 0) 'The checkout has uncommitted changes. Stop servers and build before starting additional services.'
    } else { $argsForLive = @{ NoClient=$true; Only=$missing } }
    $entry = if ($script:profile -eq 'Local runtime wrapper') { At 'scripts/dev/live.local.ps1' } else { At 'scripts/dev/live.ps1' }
    & $entry @argsForLive
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
        Start-Transcript -LiteralPath (Join-Path (Split-Path $ResultPath) 'operation.log') -Force | Out-Null
        $transcribing = $true
    }
    $snapshot = Get-Snapshot
    $selected = if ($Service -eq 'All') { $names } else { @($Service) }
    switch ($Action) {
        'Build' {
            Require (-not @($snapshot.Services | Where-Object { $_.Name -ne 'Game' -and $_.ProcessId }).Count) 'Stop this checkout''s services before rebuilding their assemblies.'
            Require (Test-Sdk) 'Install the .NET 10 SDK first.'
            & dotnet build (At 'GameServer.slnx') --nologo
            Require ($LASTEXITCODE -eq 0) 'LocalExile build failed. Read the operation output.'
        }
        'Start' { Start-Services $snapshot $selected }
        'Play' {
            Assert-CanStart $snapshot
            if (-not @($snapshot.Services | Where-Object { $_.Name -eq 'Game' -and $_.ProcessId }).Count) {
                Start-Services $snapshot $names
                $entry = if ($script:profile -eq 'Local runtime wrapper') { At 'scripts/dev/live.local.ps1' } else { At 'scripts/dev/live.ps1' }
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
    $result = @{ Ok=$true; Message=$(if ($Action -eq 'Inspect') { 'Environment checked.' } else { "$Action completed." }); Snapshot=$snapshot }
} catch {
    $result = @{ Ok=$false; Message=$_.Exception.Message; Snapshot=$null }
} finally {
    if ($transcribing) { Stop-Transcript | Out-Null }
    if ($hasLock -and $mutex) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
    if ($result) { Write-Result $result }
}
if (-not $result.Ok) { exit 1 }
