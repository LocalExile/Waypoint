# LocalExile mod control uses its files and chat commands. No mod assembly is loaded into Waypoint.
function Get-ModFolder {
    if ($env:Game_MODS_FOLDER) { return [IO.Path]::GetFullPath((Join-Relative $env:Game_MODS_FOLDER)) }
    return At 'Mods/Binaries'
}
function Get-ModCachePath { return (Get-SourceSelectionPath) + '.mods.json' }
function Get-ModFileHash([string]$path) {
    # The desktop's Windows PowerShell child may not inherit a usable Get-FileHash module search path.
    Assert-Physical $path
    $stream = [IO.File]::OpenRead($path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose(); $stream.Dispose() }
}
function Read-ManagedMods {
    $path = (Get-SourceSelectionPath) + '.managed-mods.json'
    Assert-Physical $path
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    Require ((Get-Item -LiteralPath $path).Length -lt 262144) 'Managed mod history exceeds its limit; review it before continuing.'
    $record = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    Require ($record.Version -eq 1 -and (Same-Path $record.Root $script:root)) 'Managed mod history does not match this checkout.'
    return @($record.Entries)
}
function Write-ManagedMods([object[]]$entries) {
    $path = (Get-SourceSelectionPath) + '.managed-mods.json'
    Assert-Physical $path
    New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
    $temporary = $path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    @{ Version=1; Root=$script:root; Entries=@($entries) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $path -Force
}
function Record-ModBuild([string]$project, [string]$commit = '') {
    # Own only the standard first-party output files this build produced. Never claim a third-party folder wholesale.
    $id = [IO.Path]::GetFileNameWithoutExtension($project).ToLowerInvariant()
    Assert-ModId $id
    $folder = At "Mods/Binaries/$id"
    if (-not (Test-Path -LiteralPath (Join-Path $folder "$id.dll")) -or -not (Test-Path -LiteralPath (Join-Path $folder 'mod.json'))) { return }
    Assert-Physical $folder
    $files = @{}
    foreach ($name in @('mod.json', "$id.dll", "$id.pdb")) {
        $path = Join-Path $folder $name
        if (Test-Path -LiteralPath $path) { $files[$name] = Get-ModFileHash $path }
    }
    $relative = $project.Substring($script:root.Length).TrimStart('\','/')
    Require ($relative.StartsWith('Mods\Source\', [StringComparison]::OrdinalIgnoreCase) -or $relative.StartsWith('Mods/Source/', [StringComparison]::OrdinalIgnoreCase)) 'Mod build history requires a project under Mods/Source.'
    $entries = @(Read-ManagedMods | Where-Object { $_.Id -cne $id })
    $entries += @{ Id=$id; Project=$relative; SourceCommit=$commit; BuiltUtc=[datetime]::UtcNow.ToString('o'); Files=$files }
    Write-ManagedMods $entries
}
function Retire-RemovedManagedMods {
    $entries = @(Read-ManagedMods)
    $remaining = @()
    foreach ($entry in $entries) {
        Assert-ModId $entry.Id
        $project = [IO.Path]::GetFullPath((At $entry.Project))
        Require ($project.StartsWith([IO.Path]::GetFullPath((At 'Mods/Source')).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) 'Managed mod source path is outside Mods/Source.'
        if (Test-Path -LiteralPath $project -PathType Leaf) { $remaining += $entry; continue }
        $folder = At ("Mods/Binaries/" + $entry.Id)
        Assert-Physical $folder
        $files = @($entry.Files.PSObject.Properties)
        foreach ($file in $files) {
            Require ($file.Name -cin @('mod.json', ($entry.Id + '.dll'), ($entry.Id + '.pdb')) -and $file.Value -cmatch '^[a-f0-9]{64}$') 'Invalid managed mod file history.'
            $path = Join-Path $folder $file.Name
            Assert-Physical $path
            if (Test-Path -LiteralPath $path) { Require ((Get-ModFileHash $path) -ieq $file.Value) "The source for managed mod $($entry.Id) was removed, but its output was changed outside the recorded build. Review Mods/Binaries/$($entry.Id) before starting; no output was discarded." }
        }
        $recovery = Join-Path (Join-Path $env:LOCALAPPDATA 'EmulatorWorkspaceLauncher/retired-mods') ([guid]::NewGuid().ToString('N') + '/' + $entry.Id)
        Assert-Physical $recovery
        Set-Stage 'Retiring removed source mods' $entry.Id
        foreach ($file in $files) {
            $path = Join-Path $folder $file.Name
            if (Test-Path -LiteralPath $path) {
                New-Item -ItemType Directory -Path $recovery -Force | Out-Null
                Move-Item -LiteralPath $path -Destination (Join-Path $recovery $file.Name)
            }
        }
        Write-Host "Removed source mod $($entry.Id) will not load; its recorded outputs are retained at $recovery. Other files and mods were preserved."
    }
    if ($remaining.Count -ne $entries.Count) { Write-ManagedMods $remaining }
}
function Get-ModInventoryStamp {
    $folder = Get-ModFolder
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) { return '' }
    Assert-Physical $folder
    $files = @()
    foreach ($directory in @(Get-ChildItem -LiteralPath $folder -Directory | Select-Object -First 512)) {
        Assert-Physical $directory.FullName
        $files += @(Get-ChildItem -LiteralPath $directory.FullName -File | Where-Object { $_.Extension -in @('.dll','.json','.pdb') })
    }
    $state = Join-Path $folder 'mod-state.txt'
    if (Test-Path -LiteralPath $state) { $files += Get-Item -LiteralPath $state }
    $lines = @($files | Sort-Object FullName | ForEach-Object { Assert-Physical $_.FullName; $_.FullName + '|' + $_.Length + '|' + $_.LastWriteTimeUtc.Ticks })
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($lines -join "`n")))).Replace('-','') }
    finally { $sha.Dispose() }
}
function Get-ModEnvironment($services) {
    $supported = (Test-Path -LiteralPath (At 'src/GameServer.Modding/GameServer.Modding.csproj')) -and (Test-Path -LiteralPath (At 'src/GameServer.Gateway/Program.Mods.cs'))
    $result = @{ Supported=[bool]$supported; Folder=(Get-ModFolder); Enabled=($env:Game_MODS -ne '0'); RuntimeReport=''; CheckedUtc='' }
    if (-not $supported) { return $result }
    $gateway = $services | Where-Object { $_.Name -eq 'Gateway' -and $_.Status -eq 'Ready' }
    $cache = Get-ModCachePath
    if ($gateway -and (Test-Path -LiteralPath $cache)) {
        try {
            Assert-Physical $cache
            Require ((Get-Item -LiteralPath $cache).Length -lt 262144) 'Mod status cache exceeds its limit.'
            $saved = Get-Content -LiteralPath $cache -Raw | ConvertFrom-Json
            if ($saved.ProcessId -eq $gateway.ProcessId -and $saved.StartedUtc -eq $gateway.StartedUtc -and $saved.Stamp -eq (Get-ModInventoryStamp) -and ([datetime]::UtcNow - [datetime]::Parse($saved.CheckedUtc).ToUniversalTime()).TotalSeconds -lt 120) {
                $result.RuntimeReport = [string]$saved.Report; $result.CheckedUtc = [string]$saved.CheckedUtc
            }
        } catch { Write-Verbose 'Mod status cache was not usable; runtime status remains unknown.' }
    }
    return $result
}
function Assert-ModId([string]$id) {
    Require ($id -cmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$' -and $id -notin @('.','..')) 'Select a mod with a supported ID.'
}
function Get-ModBinaryFolder([string]$id) {
    Assert-ModId $id
    $path = Join-Path (Get-ModFolder) $id
    Assert-Physical $path
    Require (Test-Path -LiteralPath (Join-Path $path 'mod.json') -PathType Leaf) 'Build or install this mod first; its binary manifest is missing.'
    Assert-Physical (Join-Path $path 'mod.json')
    return $path
}
function Write-ModChoices([string[]]$lines) {
    if ($null -eq $lines) { $lines = @() }
    $folder = Get-ModFolder
    Assert-Physical $folder
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $path = Join-Path $folder 'mod-state.txt'
    Assert-Physical $path
    $temporary = Join-Path $folder ('waypoint-choices-' + [guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllLines($temporary, $lines, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $path -Force
}
function Invoke-ModClientCommands([string[]]$commands, $snapshot) {
    Require ([bool]@($snapshot.Services | Where-Object { $_.Name -eq 'Game' -and $_.ProcessId }).Count) 'Enter the game to use live mod controls. Otherwise stop Gateway and change its saved choices, or use the shown command in game.'
    Require ([bool]@($snapshot.Services | Where-Object { $_.Name -eq 'Gateway' -and $_.Status -eq 'Ready' }).Count) 'Gateway must be ready for live mod controls.'
    Require ($env:Game_MODS -ne '0') 'Mods are disabled by Game_MODS=0. Stop Gateway and change the launch profile first.'
    $helper = At 'scripts/dev/client_control.py'
    Require (Test-Path -LiteralPath $helper) 'This revision has no native client helper. Use the shown mod command in game.'
    Set-Stage 'Checking live mods' ($commands -join '; ')
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = Get-Python
    $info.Arguments = '"' + $helper + '" --stdio'
    $info.WorkingDirectory = $script:root; $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    try {
        $errors = $process.StandardError.ReadToEndAsync()
        foreach ($command in $commands) { $process.StandardInput.WriteLine((@{ command='command'; text=$command; wait=1.5 } | ConvertTo-Json -Compress)) }
        $process.StandardInput.Close()
        $replies = @()
        $deadline = [datetime]::UtcNow.AddSeconds(45)
        foreach ($command in $commands) {
            $line = $process.StandardOutput.ReadLineAsync()
            $remaining = [math]::Max(1, [int]($deadline - [datetime]::UtcNow).TotalMilliseconds)
            if (-not $line.Wait($remaining)) { throw 'Native mod helper timed out. The command may have applied. Check /mod in game before retrying.' }
            Require ($null -ne $line.Result) 'Native mod control ended without a reply. Check Python/Frida setup and enter the game, or use the shown command in game. A submitted change may already have applied.'
            $replies += $line.Result | ConvertFrom-Json
        }
        $failed = @($replies | Where-Object { $_.ok -ne $true })
        $reason = if ($failed.Count) { [string]$failed[0].error } else { '' }
        if ($reason.Length -gt 512) { $reason = $reason.Substring(0,512) }
        Require ($replies.Count -eq $commands.Count -and -not $failed.Count) ('Native helper did not confirm every request. ' + $reason + ' Check /mod in game; a submitted change may already have applied.')
        $report = (@($replies[-1].result.replies) -join "`n")
        Require ($report -match '(?m)^(--- Mods in .+/ ---|No mods in .+/ \(Agents/docs/dev/modding.md\)\.)$') 'Gateway did not return a mod list. Enter a playable area and try Check live status, or enter /mod in game.'
        $gateway = $snapshot.Services | Where-Object Name -eq 'Gateway'
        $cache = Get-ModCachePath
        Assert-Physical $cache
        New-Item -ItemType Directory -Path (Split-Path $cache) -Force | Out-Null
        @{ ProcessId=$gateway.ProcessId; StartedUtc=$gateway.StartedUtc; Stamp=(Get-ModInventoryStamp); CheckedUtc=[datetime]::UtcNow.ToString('o'); Report=$report } | ConvertTo-Json | Set-Content -LiteralPath $cache -Encoding UTF8
        Write-Host $report
        return $report
    } finally {
        # Frida can delay detach after flushing confirmed replies. Bound cleanup of this disposable helper only.
        if (-not $process.HasExited -and -not $process.WaitForExit(2000)) { $process.Kill(); Write-Host 'Native helper cleanup exceeded two seconds; the disposable helper was stopped.' }
        $process.Dispose()
    }
}
function Get-ModBackupMetadata($snapshot) {
    $environment = Get-ModEnvironment $snapshot.Services
    if (-not $environment.Supported) { return $null }
    $folder = Get-ModFolder
    $choices = ''
    $path = Join-Path $folder 'mod-state.txt'
    if (Test-Path -LiteralPath $path) {
        Assert-Physical $path
        Require ((Get-Item -LiteralPath $path).Length -lt 131072) 'Mod choices exceed the backup limit.'
        $choices = [IO.File]::ReadAllText($path)
    }
    $inventory = @()
    if (Test-Path -LiteralPath $folder -PathType Container) {
        Assert-Physical $folder
        foreach ($directory in @(Get-ChildItem -LiteralPath $folder -Directory | Select-Object -First 512)) {
            $manifest = Join-Path $directory.FullName 'mod.json'
            if (-not (Test-Path -LiteralPath $manifest)) { continue }
            Assert-Physical $manifest
            Require ((Get-Item -LiteralPath $manifest).Length -lt 131072) 'Mod manifest exceeds the backup limit.'
            $dlls = @{}
            foreach ($dll in @(Get-ChildItem -LiteralPath $directory.FullName -Filter *.dll -File)) { $dlls[$dll.Name] = Get-ModFileHash $dll.FullName }
            $inventory += @{ Id=$directory.Name; Manifest=[IO.File]::ReadAllText($manifest); Assemblies=$dlls }
        }
    }
    return @{ Folder=$folder; Enabled=$environment.Enabled; Choices=$choices; Inventory=$inventory; RuntimeReport=$environment.RuntimeReport; CheckedUtc=$environment.CheckedUtc }
}
function Invoke-ModOperation($snapshot) {
    Require (-not $snapshot.Conflicts.Count) ($snapshot.Conflicts -join "`n")
    $environment = Get-ModEnvironment $snapshot.Services
    Require $environment.Supported 'This LocalExile revision has no supported mod host.'
    $gateway = $snapshot.Services | Where-Object Name -eq 'Gateway'
    $gatewayRunning = $gateway.ProcessId -or $gateway.WatchProcessId
    if ($Action -eq 'QueryMods') { $null = Invoke-ModClientCommands @('/mod') $snapshot; return }
    if ($Action -eq 'RestoreModChoices') {
        Require (-not $gatewayRunning) 'Stop Gateway before restoring saved mod choices. No mod DLLs or saves will be replaced.'
        $backup = Get-SelectedBackup
        $metadata = Get-Content -LiteralPath (Join-Path $backup 'backup.json') -Raw | ConvertFrom-Json
        Require ($null -ne $metadata.Mods) 'This backup has no recorded mod choices.'
        Require (Same-Path $metadata.Mods.Folder (Get-ModFolder)) 'This backup recorded a different mod folder. Review its choices manually; none were changed.'
        $lines = @(([string]$metadata.Mods.Choices) -split "`n" | Where-Object { $_.Trim() })
        foreach ($line in $lines) { Require ($line.TrimEnd("`r") -cmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,127} (on|off)$') 'The saved mod choices have an unsupported format; no choices were changed.' }
        Backup-Saves $snapshot
        Set-Stage 'Restoring mod choices' 'Replacing saved enable/disable overrides only; no mod binaries or save restore.'
        Write-ModChoices $lines
        return
    }
    if ($Action -in @('EnableMod','DisableMod','ReloadMod')) { $null = Get-ModBinaryFolder $ModId }
    if ($Action -eq 'ReloadMod') {
        Backup-Saves $snapshot
        $report = Invoke-ModClientCommands @("/mod_reload $ModId", '/mod') $snapshot
        Require ($report -match ('(?m)^' + [regex]::Escape($ModId) + ' .+ - (feature|league|script), [0-9]+ hook\(s\)$')) 'Gateway did not confirm this mod loaded. Its error is visible on Mods; a reload can reset dependent mods too.'
        return
    }
    if ($Action -in @('EnableMod','DisableMod')) {
        if ($gatewayRunning) {
            Backup-Saves $snapshot
            $verb = if ($Action -eq 'EnableMod') { 'enable' } else { 'disable' }
            $report = Invoke-ModClientCommands @("/mod_$verb $ModId", '/mod') $snapshot
            if ($Action -eq 'EnableMod') { Require ($report -match ('(?m)^' + [regex]::Escape($ModId) + ' .+ - (feature|league|script), [0-9]+ hook\(s\)$')) 'The command was sent, but Gateway did not confirm this mod loaded. See Mods for the reason.' }
            else { Require ($report -match ('(?m)^' + [regex]::Escape($ModId) + ' - not loaded, disabled$')) 'Gateway did not confirm this mod disabled. Check /mod before retrying.' }
        } else {
            Backup-Saves $snapshot
            Set-Stage 'Saving mod choice' 'Gateway is stopped; the choice takes effect on its next start.'
            $path = Join-Path (Get-ModFolder) 'mod-state.txt'
            $lines = @()
            if (Test-Path -LiteralPath $path) {
                Assert-Physical $path
                Require ((Get-Item -LiteralPath $path).Length -lt 131072) 'Mod choices exceed their size limit.'
                $lines = @([IO.File]::ReadAllLines($path) | Where-Object { $_ -cnotmatch ('^' + [regex]::Escape($ModId) + ' +(on|off) *$') })
            }
            $lines += $ModId + $(if ($Action -eq 'EnableMod') { ' on' } else { ' off' })
            Write-ModChoices $lines
        }
        return
    }
    Require (Test-Sdk) 'Install the .NET 10 SDK to build mods.'
    Require (Same-Path (Get-ModFolder) (At 'Mods/Binaries')) 'Source mods build into Mods/Binaries, but this profile selects another mod folder. Select the normal mod folder before using launcher builds; no files were copied between folders.'
    $running = @($snapshot.Services | Where-Object { $_.Name -ne 'Game' -and ($_.ProcessId -or $_.WatchProcessId) })
    if ($gatewayRunning) { Require ($gateway.Status -eq 'Ready') 'Wait for Gateway to finish starting/restarting before building mods.' }
    if ($Action -eq 'BuildMod') {
        Require ($ModProject -and -not [IO.Path]::IsPathRooted($ModProject)) 'Select a source mod project from this checkout.'
        $project = [IO.Path]::GetFullPath((At $ModProject))
        Require ($project.StartsWith([IO.Path]::GetFullPath((At 'Mods/Source')).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -and $project.EndsWith('.csproj', [StringComparison]::OrdinalIgnoreCase)) 'The mod project must be inside this checkout''s Mods/Source folder.'
        Assert-Physical $project
        Require (Test-Path -LiteralPath $project -PathType Leaf) 'The selected mod project is missing.'
        Backup-Saves $snapshot
        Set-Stage 'Building mod' $ModProject
        $arguments = @('build', $project, '--nologo', '-v', 'minimal')
        if ($running.Count) { $arguments += '--no-dependencies' }
        & dotnet @arguments
        Require ($LASTEXITCODE -eq 0) 'Mod build failed. A successful previous build does not prove the new mod applied. See compiler output; stop servers and Build checkout if dependencies need rebuilding.'
        Record-ModBuild $project $snapshot.SourceCommit
    } else {
        Backup-Saves $snapshot
        Build-GatewayMods -NoDependencies:([bool]$running.Count) -AllowRetire:(-not $gatewayRunning) -SourceCommit $snapshot.SourceCommit
    }
    if ($gateway.Status -eq 'Ready' -and @($snapshot.Services | Where-Object { $_.Name -eq 'Game' -and $_.ProcessId }).Count) {
        Start-Sleep -Milliseconds 600
        $null = Invoke-ModClientCommands @('/mod') (Get-Snapshot)
    } else { Write-Host 'Mod build completed. Live load status is unconfirmed; enter the game and use Check live status or /mod.' }
}
