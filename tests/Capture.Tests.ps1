# Production process lifecycle with an inert Frida module. No real game/hook is used.
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('Waypoint capture ' + [guid]::NewGuid().ToString('N'))
$oldData = $env:LOCALAPPDATA; $oldPythonPath = $env:PYTHONPATH
$env:LOCALAPPDATA = Join-Path $fixture 'launcher-data'
$env:PYTHONPATH = Join-Path $fixture 'python-fixture'
$script:root = Join-Path $fixture 'Checkout with spaces'
$CaptureAreaLayouts = $true
$script:passed = 0
function Check($value, $message) { if (-not $value) { throw "FAIL: $message" }; $script:passed++; Write-Host "PASS: $message" }
$bridge = Join-Path $PSScriptRoot '../scripts/RuntimeBridge.ps1'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($bridge,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($function in $ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$true)) { . ([scriptblock]::Create($function.Extent.Text)) }
$native = $ast.Find({param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Add-Type'}, $true)
Add-Type -TypeDefinition $native.CommandElements[2].Value
. (Join-Path $PSScriptRoot '../scripts/CaptureBridge.ps1')
function Set-Stage([string]$stage, [string]$detail='') { }
try {
    New-Item -ItemType Directory -Force "$script:root/scripts/walkmaps", "$script:root/src/GameServer.Gateway", $env:PYTHONPATH | Out-Null
    Set-Content "$script:root/scripts/walkmaps/walkmaps.py" "def log(text): pass`ndef on_message(message, data, out): pass"
    Set-Content "$script:root/scripts/walkmaps/walkmaps.js" 'inert fixture'
    Set-Content "$script:root/src/GameServer.Gateway/AreaLayouts.cs" '// Game_WALKMAPS'
    Set-Content (Join-Path $env:PYTHONPATH 'frida.py') "def attach(pid): raise Exception('TEST MUST NEVER ATTACH')`nclass Device:`n    def enumerate_processes(self): return []`ndef get_local_device(): return Device()"
    & python -m venv --without-pip (Join-Path $script:root '.venv')
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the private redirector fixture.' }
    Get-Inventory
    $process = Get-Process -Id $PID
    $services = @([pscustomobject]@{ Name='Gateway'; Status='Ready'; ProcessId=$PID; WatchProcessId=0 })
    $snapshot = [pscustomobject]@{ Services=$services; Conflicts=@() }
    Check (Test-CaptureSupport) 'capability detection reads upstream files without executing them'
    Check (-not (Test-CaptureGatewayEnabled $services)) 'a running Gateway without verified capture metadata is not assumed enabled'
    Record-CaptureGateway $services
    Check (Test-CaptureGatewayEnabled $services) 'capture mode is tied to the current Gateway PID and exact start time'
    $captureHome = Get-CaptureHome
    $marker = Read-CaptureJson (Join-Path $captureHome 'gateway.json')
    $marker.Ticks = '1'; Write-CaptureJson (Join-Path $captureHome 'gateway.json') $marker
    Check (-not (Test-CaptureGatewayEnabled $services)) 'stale Gateway metadata cannot authorize attachment'
    Record-CaptureGateway $services
    $python = Check-CaptureDependencies
    Check ([bool]$python) 'dependency checks select a working Python without package installation'
    $state = Get-CaptureSnapshot $services
    Check ($state.DependenciesReady -and $state.GatewayEnabled -and $state.ProcessId -eq 0) 'optional capture state is independent of required service readiness'
    Start-Capture $snapshot
    Start-Sleep -Milliseconds 800
    $active = Get-ActiveCapture
    Check ($active.Running -and $active.ProcessId -ne $PID) 'capture starts a hidden, checkout-scoped helper with spaces in paths'
    $status = Get-CaptureSnapshot $services
    Check ($status.State -eq 'Waiting' -and $status.ProcessId -eq $active.ProcessId) 'a Python helper waiting for a client is not shown ready'
    $portable = Join-Path $fixture 'another portable folder'
    New-Item -ItemType Directory -Path $portable | Out-Null
    Copy-Item (Join-Path $PSScriptRoot '../scripts/CaptureBridge.ps1'), (Join-Path $PSScriptRoot '../scripts/WalkmapCapture.py') $portable
    . (Join-Path $portable 'CaptureBridge.ps1')
    Check ((Get-ActiveCapture).Running) 'identical helper bytes in a different portable folder retain verified session ownership'
    $firstId = $active.ProcessId
    Start-Capture $snapshot
    Check ((Get-ActiveCapture).ProcessId -eq $firstId) 'repeated starts reuse the owned session instead of creating a second recorder'
    $activePath = Join-Path $captureHome 'active.json'
    $originalActive = Read-CaptureJson $activePath
    $changed = Read-CaptureJson $activePath; $changed.Python = 'C:\foreign\python.exe'; Write-CaptureJson $activePath $changed
    $refused = $false
    try { Stop-Capture } catch { $refused = $_.Exception.Message -like '*ownership*' }
    Check ($refused -and (Get-Process -Id $firstId -ErrorAction SilentlyContinue)) 'changed recorder ownership refuses stop and preserves the process'
    Write-CaptureJson $activePath $originalActive
    Stop-Capture
    Check (-not (Get-ActiveCapture).Running) 'stop request exits the recorder without terminating the game or Gateway'
    Check ([bool](Get-Process -Id $PID)) 'Gateway process used by the fixture remains alive'
    $CaptureAreaLayouts = $false
    $state = Get-CaptureSnapshot $services
    Check ($state.State -eq 'Off' -and $state.GatewayEnabled) 'stopping capture does not misrepresent the existing Gateway startup mode'
    $CaptureAreaLayouts = $true
    $marker = Read-CaptureJson (Join-Path $captureHome 'gateway.json'); $marker.Enabled = $false
    Write-CaptureJson (Join-Path $captureHome 'gateway.json') $marker
    $state = Get-CaptureSnapshot $services
    Check ($state.State -eq 'RestartRequired') 'an existing Gateway needs an explicit restart after enabling capture'
    $blocked = $false
    try { Start-Capture $snapshot } catch { $blocked = $_.Exception.Message -like '*restart Gateway*' }
    Check $blocked 'capture cannot silently start against a Gateway without recording enabled'
    Remove-Item -LiteralPath "$script:root/scripts/walkmaps/walkmaps.js"
    Check ((Get-CaptureSnapshot $services).State -eq 'Unsupported') 'switching to a revision without a recorder reports unsupported'
    Write-Host "$script:passed capture bridge checks passed."
} finally {
    try { Stop-Capture } catch { Write-Warning $_ }
    $env:LOCALAPPDATA = $oldData; $env:PYTHONPATH = $oldPythonPath
    # Leave the small private fixture and logs for diagnosis; never remove source or captured game data.
}
