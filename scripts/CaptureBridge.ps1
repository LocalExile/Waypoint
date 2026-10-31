# Optional tooling, deliberately separate from the four required runtime services.
function Get-CaptureHome {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $key = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($script:root.ToLowerInvariant())))).Replace('-','') }
    finally { $sha.Dispose() }
    $path = Join-Path $env:LOCALAPPDATA ('EmulatorWorkspaceLauncher/captures/' + $key + '-' + [Diagnostics.Process]::GetCurrentProcess().SessionId)
    Assert-Physical $path
    return $path
}
function Read-CaptureJson([string]$path) {
    Assert-Physical $path
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    Require ((Get-Item -LiteralPath $path).Length -lt 65536) 'Capture metadata is too large. Review its folder.'
    # Share delete so the recorder can replace status atomically while inspection reads it.
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    $reader = [IO.StreamReader]::new($stream)
    try { return ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
}
function Get-CaptureFileHash([string]$path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($path)
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)) }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
function Write-CaptureJson([string]$path, $value) {
    Assert-Physical $path; Assert-Physical ($path + '.tmp')
    New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
    $value | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath ($path + '.tmp') -Encoding UTF8
    Move-Item -LiteralPath ($path + '.tmp') -Destination $path -Force
}
function Test-CaptureSupport {
    foreach ($relative in @('scripts/walkmaps/walkmaps.py','scripts/walkmaps/walkmaps.js','src/GameServer.Gateway/AreaLayouts.cs')) {
        if (-not (Test-Path -LiteralPath (At $relative) -PathType Leaf)) { return $false }
    }
    return (Get-Content -LiteralPath (At 'scripts/walkmaps/walkmaps.py') -Raw).Contains('def on_message(') -and
        (Get-Content -LiteralPath (At 'src/GameServer.Gateway/AreaLayouts.cs') -Raw).Contains('Game_WALKMAPS')
}
function Get-CaptureGateway($services) {
    $gateway = @($services | Where-Object Name -eq 'Gateway') | Select-Object -First 1
    if (-not $gateway) { return $null }
    $targetId = if ($gateway.WatchProcessId) { $gateway.WatchProcessId } else { $gateway.ProcessId }
    if (-not $targetId) { return $null }
    $process = Get-Process -Id $targetId -ErrorAction SilentlyContinue
    if (-not $process) { return $null }
    $inventory = $script:byId[[int]$targetId]
    Require ($inventory -and [math]::Abs(($process.StartTime.ToUniversalTime() - $inventory.CreationDate.ToUniversalTime()).TotalMilliseconds) -lt 1) 'Gateway identity changed. Refresh before using capture.'
    return @{ ProcessId=[int]$targetId; Ticks=[string]$process.StartTime.ToUniversalTime().ToFileTimeUtc() }
}
function Test-CaptureGatewayEnabled($services) {
    $gateway = Get-CaptureGateway $services
    $marker = Read-CaptureJson (Join-Path (Get-CaptureHome) 'gateway.json')
    return $gateway -and $marker -and $marker.Enabled -eq $true -and $marker.ProcessId -eq $gateway.ProcessId -and $marker.Ticks -eq $gateway.Ticks
}
function Record-CaptureGateway($services) {
    $gateway = Get-CaptureGateway $services
    if ($gateway) { Write-CaptureJson (Join-Path (Get-CaptureHome) 'gateway.json') @{ ProcessId=$gateway.ProcessId; Ticks=$gateway.Ticks; Enabled=[bool]$CaptureAreaLayouts } }
}
function Get-ActiveCapture {
    $captureHome = Get-CaptureHome
    $active = Read-CaptureJson (Join-Path $captureHome 'active.json')
    if (-not $active) { return $null }
    Require ($active.Session -match '^[0-9a-f]{32}$') 'Invalid capture session metadata.'
    $active | Add-Member NoteProperty Directory (Join-Path $captureHome ('sessions/' + $active.Session)) -Force
    Assert-Physical $active.Directory
    $process = Get-Process -Id $active.ProcessId -ErrorAction SilentlyContinue
    if (-not $process) { return $active }
    # A reused PID is not ours. Never signal it or infer capture health from it.
    if ([math]::Abs(($process.StartTime.ToUniversalTime() - [datetime]::Parse($active.StartedUtc).ToUniversalTime()).TotalMilliseconds) -ge 1) { return $active }
    Get-Inventory
    $entry = $script:byId[[int]$active.ProcessId]
    $process.Refresh()
    if ($process.HasExited) { return $active }
    Require ([bool]$entry) 'Capture process disappeared during inspection. Refresh before retrying.'
    $args = @(Split-Arguments $entry.CommandLine)
    Require (Test-Path -LiteralPath $active.Python -PathType Leaf) 'Capture process ownership could not be verified.'
    Require ((Same-Path ([NativeRuntime]::FinalPath($entry.ExecutablePath)) ([NativeRuntime]::FinalPath($active.Python))) -and $args.Count -eq 10) 'Capture process ownership could not be verified.'
    # An identical portable/development build may have started this helper elsewhere.
    # Adopt only the same adapter bytes, then verify every checkout/session argument.
    Assert-Physical $args[1]
    Require ((Test-Path -LiteralPath $args[1] -PathType Leaf) -and
        (Get-CaptureFileHash $args[1]) -eq (Get-CaptureFileHash (Join-Path $PSScriptRoot 'WalkmapCapture.py'))) 'Capture helper differs from this build. Stop it from its original launcher or close the game.'
    $expected = @($args[1],'--repo',$script:root,'--session',$active.Directory,'--gateway-pid',[string]$active.GatewayPid,'--gateway-ticks',[string]$active.GatewayTicks)
    for ($i=0; $i -lt $expected.Count; $i++) { Require ($args[$i+1] -ceq $expected[$i]) 'Capture process arguments changed. No process was stopped.' }
    $active | Add-Member NoteProperty Running $true -Force
    return $active
}
function Get-CaptureSnapshot($services) {
    $captureHome = Get-CaptureHome
    $value = @{ Supported=(Test-CaptureSupport); DependenciesReady=$false; GatewayEnabled=$false; State='Off'; Detail='Capture is optional. Existing files are kept.'; ProcessId=0; LastArea=''; LastCaptureUtc=''; OutputDirectory=(At 'data/walkability'); LogPath='' }
    try {
        $dependency = Read-CaptureJson (Join-Path $captureHome 'dependencies.json')
        $value.DependenciesReady = [bool]($dependency -and $dependency.Ready -and (Test-Path -LiteralPath $dependency.Python -PathType Leaf) -and (Get-Item -LiteralPath $dependency.Python).LastWriteTimeUtc.Ticks -eq $dependency.PythonStamp)
        $value.GatewayEnabled = [bool](Test-CaptureGatewayEnabled $services)
        $active = Get-ActiveCapture
        if ($active) {
            $value.LogPath = Join-Path $active.Directory 'capture.log'
            if ($active.Running) { $value.ProcessId = [int]$active.ProcessId; $value.State='Starting'; $value.Detail='Starting the capture helper.' }
            $status = Read-CaptureJson (Join-Path $active.Directory 'status.json')
            # Windows virtual environments can run a redirector parent and a Python child.
            $statusOwned = $status -and (-not $active.Running -or $status.ProcessId -eq $active.ProcessId -or
                (Descends-From $script:byId[[int]$status.ProcessId] $script:byId[[int]$active.ProcessId]))
            if ($statusOwned) {
                $value.LastArea = [string]$status.LastArea; $value.LastCaptureUtc = [string]$status.LastCaptureUtc
                if ($active.Running) { $value.State = [string]$status.State; $value.Detail = [string]$status.Detail }
                elseif ($status.State -eq 'Error') { $value.State='Error'; $value.Detail=[string]$status.Detail }
            }
        }
        if (-not $value.Supported) { $value.State='Unsupported'; $value.Detail='This checkout does not provide a supported walk-map recorder.' }
        elseif (-not $value.ProcessId -and $value.State -ne 'Error') {
            if ($CaptureAreaLayouts -and -not $value.DependenciesReady) { $value.State='NeedsSetup'; $value.Detail='Check Python and Frida before enabling capture.' }
            elseif ($CaptureAreaLayouts -and @($services | Where-Object { $_.Name -eq 'Gateway' -and ($_.ProcessId -or $_.WatchProcessId) }).Count -and -not $value.GatewayEnabled) { $value.State='RestartRequired'; $value.Detail='Close the game, then restart Gateway to enable capture.' }
            elseif ($CaptureAreaLayouts) { $value.State='Stopped'; $value.Detail='Capture will start with Play or Start servers.' }
        }
    } catch { $value.State='Error'; $value.Detail=$_.Exception.Message }
    return $value
}
function Check-CaptureDependencies {
    Require (Test-CaptureSupport) 'This checkout does not provide a supported walk-map recorder.'
    Set-Stage 'Checking capture dependencies' 'Checking Python 3.9+ and Frida without installing packages.'
    $captureHome = Get-CaptureHome
    $candidates = @((Join-Path $captureHome 'python/Scripts/python.exe'), (At '.venv/Scripts/python.exe'))
    $systemPython = Get-Command python.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($systemPython) { $candidates += $systemPython.Source }
    foreach ($python in $candidates) {
        if (-not (Test-Path -LiteralPath $python -PathType Leaf)) { continue }
        Assert-Physical $python
        $oldPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $probe = & $python -c 'import sys; assert sys.version_info >= (3,9); import frida; assert callable(frida.attach)' 2>&1
            $probeCode = $LASTEXITCODE
        } finally { $ErrorActionPreference = $oldPreference }
        if ($probeCode -eq 0) {
            Write-CaptureJson (Join-Path $captureHome 'dependencies.json') @{ Ready=$true; Python=$python; PythonStamp=(Get-Item -LiteralPath $python).LastWriteTimeUtc.Ticks }
            return $python
        }
    }
    Write-CaptureJson (Join-Path $captureHome 'dependencies.json') @{ Ready=$false; Python=''; PythonStamp=0 }
    throw 'Python 3.9+ with Frida is required. Choose Set up capture to install Frida in a private Waypoint environment.'
}
function Setup-CaptureDependencies {
    Require (Test-CaptureSupport) 'This checkout does not provide a supported walk-map recorder.'
    $active = Get-ActiveCapture
    Require (-not $active -or -not $active.Running) 'Stop capture before changing its Python environment.'
    $command = Get-Command python.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    Require ([bool]$command) 'Install Python 3.9+ first, then reopen Waypoint and choose Set up capture.'
    $environment = Join-Path (Get-CaptureHome) 'python'
    Assert-Physical $environment
    $python = Join-Path $environment 'Scripts/python.exe'
    if (-not (Test-Path -LiteralPath $python)) {
        Set-Stage 'Setting up capture' 'Creating a private Python environment for this checkout.'
        & $command.Source -m venv $environment
        Require ($LASTEXITCODE -eq 0) 'Could not create the private capture environment. Check your Python installation.'
    }
    Assert-Physical $python
    Set-Stage 'Setting up capture' 'Installing Frida in the private environment. No global packages are changed.'
    & $python -m pip install --disable-pip-version-check frida
    Require ($LASTEXITCODE -eq 0) 'Frida installation failed. Check the operation log and your connection, then retry.'
    $null = Check-CaptureDependencies
}
function Start-Capture($snapshot) {
    Require (-not $snapshot.Conflicts.Count) ($snapshot.Conflicts -join "`n")
    Require (Test-CaptureGatewayEnabled $snapshot.Services) 'Close the game, then restart Gateway to enable capture.'
    $active = Get-ActiveCapture
    if ($active -and $active.Running) { return }
    $python = Check-CaptureDependencies
    # Refuse a second recorder rather than attaching beside a tool we do not own.
    Get-Inventory
    $other = @($script:processes | Where-Object { $_.Name -like 'python*' -and $_.CommandLine -match 'walkmaps\.py|WalkmapCapture\.py' })
    Require (-not $other.Count) 'Another walk-map recorder is running. Stop it from its own launcher before starting capture here.'
    $gateway = Get-CaptureGateway $snapshot.Services
    $captureHome = Get-CaptureHome
    $session = [guid]::NewGuid().ToString('N')
    $directory = Join-Path $captureHome ('sessions/' + $session)
    Assert-Physical $directory; Assert-Physical (At 'data/walkability')
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $arguments = @(('"' + (Join-Path $PSScriptRoot 'WalkmapCapture.py') + '"'),'--repo',('"' + $script:root + '"'),'--session',('"' + $directory + '"'),'--gateway-pid',[string]$gateway.ProcessId,'--gateway-ticks',[string]$gateway.Ticks)
    Set-Stage 'Starting area capture' 'Waiting for the verified client; existing capture files are preserved.'
    $process = Start-Process $python -ArgumentList $arguments -WorkingDirectory $script:root -WindowStyle Hidden -RedirectStandardOutput (Join-Path $directory 'capture.log') -RedirectStandardError (Join-Path $directory 'capture.err') -PassThru
    Write-CaptureJson (Join-Path $captureHome 'active.json') @{ Session=$session; Python=$python; ProcessId=$process.Id; StartedUtc=$process.StartTime.ToUniversalTime().ToString('o'); GatewayPid=$gateway.ProcessId; GatewayTicks=$gateway.Ticks }
    Start-Sleep -Milliseconds 300
    $process.Refresh()
    Require (-not $process.HasExited) 'Capture could not start. Open the capture log for details.'
}
function Stop-Capture {
    $active = Get-ActiveCapture
    if (-not $active -or -not $active.Running) { return }
    Set-Stage 'Stopping area capture' 'Detaching the recorder without closing the game or removing captured files.'
    $stop = Join-Path $active.Directory 'stop'
    Assert-Physical $stop
    [IO.File]::WriteAllText($stop, 'stop')
    $deadline = (Get-Date).AddSeconds(12)
    do {
        Start-Sleep -Milliseconds 200
        $active = Get-ActiveCapture
        if (-not $active -or -not $active.Running) { return }
    } while ((Get-Date) -lt $deadline)
    throw 'Capture did not stop in time. No game or recorder process was force-terminated. Review the capture log.'
}
