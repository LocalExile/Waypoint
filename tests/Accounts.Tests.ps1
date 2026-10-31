# Production account write gates with disposable requests and fake runtime inventory; no real process is controlled.
$ErrorActionPreference = 'Stop'
$bridge = Join-Path $PSScriptRoot '../scripts/RuntimeBridge.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($bridge, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($name in @('Require','Assert-SetupStopped','Same-Path')) {
    $node = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
    . ([scriptblock]::Create($node.Extent.Text))
}
. (Join-Path $PSScriptRoot '../scripts/AccountBridge.ps1')
$script:passed = 0
function Check($value, $message) { if (-not $value) { throw "FAIL: $message" }; $script:passed++; Write-Host "PASS: $message" }
function Rejected([scriptblock]$work) { try { & $work; return $false } catch { return $true } }
function Clean {
    $script:processes = @(); $script:listeners = @()
    $script:snapshot = [pscustomobject]@{ Conflicts=@(); Services=@() }
}
Clean
$script:savePath = 'C:\Account fixture\saves.db'
Check (-not (Rejected { Assert-AccountDatabase @{ ExpectedDatabase='c:\account fixture\saves.db' } $true })) 'reviewed database identity uses Windows path comparison'
Check (Rejected { Assert-AccountDatabase @{ ExpectedDatabase='C:\Other\saves.db' } $true }) 'a changed database selection requires another review'
Check (Rejected { Assert-AccountDatabase @{} $true }) 'a management request must identify the reviewed save database'
Check (-not (Rejected { Assert-AccountsStopped $script:snapshot })) 'offline account changes pass only when runtime inventory is clear'
foreach ($service in @('Game','Auth','Gateway','Patch')) {
    Clean
    $script:snapshot.Services = @([pscustomobject]@{ Name=$service; ProcessId=12345; WatchProcessId=0 })
    Check (Rejected { Assert-AccountsStopped $script:snapshot }) "$service blocks account changes, including a logged-in character or login/selection session"
}
Clean
$script:snapshot.Services = @([pscustomobject]@{ Name='Gateway'; ProcessId=0; WatchProcessId=12345 })
Check (Rejected { Assert-AccountsStopped $script:snapshot }) 'a watcher between child-process restarts still blocks writes'
foreach ($command in @('dotnet C:\Other\GameServer.Gateway.dll','dotnet src\GameServer.Auth\bin\GameServer.Auth.dll serve',
    'dotnet src\GameServer.Host\bin\GameServer.Host.dll','dotnet watch run --project src\GameServer.Gateway --non-interactive --')) {
    Clean
    $script:processes = @([pscustomobject]@{ Name='dotnet.exe'; CommandLine=$command })
    Check (Rejected { Assert-AccountsStopped $script:snapshot }) "an unowned or not-yet-listening runtime blocks changes: $command"
}
Clean
$script:processes = @([pscustomobject]@{ Name='GameServer.Host.exe'; CommandLine='' })
Check (Rejected { Assert-AccountsStopped $script:snapshot }) 'a standalone account-service executable blocks changes without relying on its command line'
Clean
$script:listeners = @([pscustomobject]@{ LocalPort=7000 })
Check (Rejected { Assert-AccountsStopped $script:snapshot }) 'an unknown account-service listener is not adopted'
Clean
$script:snapshot.Conflicts = @('another game client is running')
Check (Rejected { Assert-AccountsStopped $script:snapshot }) 'ownership conflicts fail closed'
Clean
$script:calls = [Collections.Generic.List[string]]::new()
function Backup-Saves($state) { $script:calls.Add('backup') }
function Get-Snapshot { $script:calls.Add('recheck'); return $script:snapshot }
function Set-Stage($stage,$detail) { }
function Invoke-AccountData($request) { $script:calls.Add('write'); return @{ Accounts=@() } }
$request = @{ Action='Delete'; AccountId='fixture'; ExpectedRevision='fixture'; Name='Fixture' }
Apply-AccountChange $script:snapshot $request
Check (($script:calls -join ',') -eq 'backup,recheck,write') 'backup and a fresh runtime check precede every transactional write'
Clean; $script:calls.Clear()
function Backup-Saves($state) { throw 'fixture backup failure' }
Check (Rejected { Apply-AccountChange $script:snapshot $request }) 'a failed automatic backup blocks the change'
Check ($script:calls.Count -eq 0) 'a failed backup never reaches the data helper'
Clean; $script:calls.Clear()
function Backup-Saves($state) { $script:calls.Add('backup'); $script:snapshot.Conflicts = @('server started during backup') }
Check (Rejected { Apply-AccountChange $script:snapshot $request }) 'a runtime that appears during backup blocks the change'
Check (-not $script:calls.Contains('write')) 'a failed second inventory check never reaches the data helper'
Write-Host "$script:passed account bridge checks passed."
