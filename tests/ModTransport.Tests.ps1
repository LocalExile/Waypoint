# Exercise the production native-helper transport with private Python fixtures; no Frida/game/server is used.
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('Launcher-mod-transport-' + [guid]::NewGuid().ToString('N'))
$oldData = $env:LOCALAPPDATA
$env:LOCALAPPDATA = Join-Path $fixture 'launcher-data'
# As on a machine whose console is UTF-8, where a request must still reach the helper without a byte-order mark.
try { [Console]::InputEncoding = [Text.UTF8Encoding]::new($true) } catch [IO.IOException] { }
$script:root = Join-Path $fixture 'Checkout with spaces'
$Action = 'QueryMods'
$ResultPath = Join-Path $fixture 'result.json'
$script:passed = 0
function Check($value, $message) { if (-not $value) { throw "FAIL: $message" }; $script:passed++; Write-Host "PASS: $message" }
$bridge = Join-Path $PSScriptRoot '../scripts/RuntimeBridge.ps1'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($bridge,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($function in $ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$true)) { . ([scriptblock]::Create($function.Extent.Text)) }
. (Join-Path $PSScriptRoot '../scripts/ModBridge.ps1')
function Get-Python { return (Get-Command python.exe -CommandType Application | Select-Object -First 1).Source }
try {
    New-Item -ItemType Directory -Force "$script:root/scripts/dev", "$script:root/src/GameServer.Modding", "$script:root/src/GameServer.Gateway", "$script:root/Mods/Binaries/alpha" | Out-Null
    Set-Content "$script:root/src/GameServer.Modding/GameServer.Modding.csproj" '<Project />'
    Set-Content "$script:root/src/GameServer.Gateway/Program.Mods.cs" '// capability fixture'
    Set-Content "$script:root/Mods/Binaries/alpha/mod.json" '{"id":"alpha"}'
    Set-Content "$script:root/Mods/Binaries/alpha/alpha.dll" 'inert fixture; never loaded'
    $snapshot = [pscustomobject]@{ Services=@(
        [pscustomobject]@{Name='Gateway';Status='Ready';ProcessId=123;StartedUtc='2026-10-30T00:00:00Z'},
        [pscustomobject]@{Name='Game';Status='Running';ProcessId=456}
    ) }
    $helper = "$script:root/scripts/dev/client_control.py"
    @'
import json, os, pathlib, sys, time
pathlib.Path(__file__).with_name("helper-pid.txt").write_text(str(os.getpid()))
for line in sys.stdin:
    json.loads(line)
    print(json.dumps({"ok":True,"result":{"replies":["--- Mods in Mods/Binaries/ ---", "alpha 1.0 - feature, 1 hook(s)"]}}), flush=True)
time.sleep(60)
'@ | Set-Content -LiteralPath $helper
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $report=Invoke-ModClientCommands @('/mod') $snapshot
    $clock.Stop()
    $helperPid=[int][IO.File]::ReadAllText("$script:root/scripts/dev/helper-pid.txt")
    Check ($report.Contains('alpha 1.0') -and $clock.Elapsed.TotalSeconds -lt 12) 'confirmed replies do not wait indefinitely for Frida-style helper cleanup'
    Check (-not (Get-Process -Id $helperPid -ErrorAction SilentlyContinue)) 'only the disposable slow-cleanup helper is stopped after confirmation'
    Check ((Get-ModEnvironment $snapshot.Services).RuntimeReport.Contains('alpha 1.0')) 'confirmed runtime status is cached for the exact Gateway process'
    $saved=Get-Content -LiteralPath (Get-ModCachePath) -Raw | ConvertFrom-Json
    $saved.ProcessId=999
    $saved | ConvertTo-Json | Set-Content -LiteralPath (Get-ModCachePath)
    Check (-not (Get-ModEnvironment $snapshot.Services).RuntimeReport) 'a different Gateway PID cannot inherit old loaded status'
    $saved.ProcessId=123; $saved.CheckedUtc=[datetime]::UtcNow.AddMinutes(-3).ToString('o')
    $saved | ConvertTo-Json | Set-Content -LiteralPath (Get-ModCachePath)
    Check (-not (Get-ModEnvironment $snapshot.Services).RuntimeReport) 'runtime status expires after two minutes'
    $saved.CheckedUtc=[datetime]::UtcNow.ToString('o')
    $saved | ConvertTo-Json | Set-Content -LiteralPath (Get-ModCachePath)
    [IO.File]::AppendAllText("$script:root/Mods/Binaries/alpha/alpha.dll", 'changed')
    Check (-not (Get-ModEnvironment $snapshot.Services).RuntimeReport) 'changed mod outputs invalidate the last runtime report'
    @'
import json, sys
for line in sys.stdin:
    print(json.dumps({"ok":False,"error":"synthetic native failure"}), flush=True)
'@ | Set-Content -LiteralPath $helper
    $failure=''
    try { $null=Invoke-ModClientCommands @('/mod') $snapshot } catch { $failure=$_.Exception.Message }
    Check ($failure.Contains('synthetic native failure')) 'native request failures retain an actionable helper explanation'
    @'
import json, sys
for line in sys.stdin:
    print(json.dumps({"ok":True,"result":{"replies":["Mods reloaded (/mod for the list)."]}}), flush=True)
'@ | Set-Content -LiteralPath $helper
    $failure=''
    try { $null=Invoke-ModClientCommands @('/mod') $snapshot } catch { $failure=$_.Exception.Message }
    Check ($failure.Contains('did not return a mod list')) 'an acknowledgement alone cannot establish a loaded mod list'
    Set-Content -LiteralPath $helper -Value 'import sys; sys.stdin.read()'
    $failure=''
    try { $null=Invoke-ModClientCommands @('/mod') $snapshot } catch { $failure=$_.Exception.Message }
    Check ($failure.Contains('without a reply')) 'helper EOF without confirmation reports an uncertain outcome'
    Write-Host "$script:passed mod transport checks passed."
} finally {
    $env:LOCALAPPDATA=$oldData
    $resolved=[IO.Path]::GetFullPath($fixture)
    if ($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -match '^Launcher-mod-transport-[0-9a-f]{32}$') { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
