# LocalExile mod control uses its files and chat commands. No mod assembly is loaded into Waypoint.
function Test-ModChildPath([string]$path, [string]$parent) {
    $separator = [IO.Path]::DirectorySeparatorChar
    $comparison = if ($separator -eq '/') { [StringComparison]::Ordinal } else { [StringComparison]::OrdinalIgnoreCase }
    $prefix = [IO.Path]::GetFullPath($parent).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + $separator
    return [IO.Path]::GetFullPath($path).StartsWith($prefix, $comparison)
}
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
        Require (Test-ModChildPath $project (At 'Mods/Source')) 'Managed mod source path is outside Mods/Source.'
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
        $recovery = Join-Path (Join-Path (Get-WaypointData) 'retired-mods') ([guid]::NewGuid().ToString('N') + '/' + $entry.Id)
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
    # The server moved Program.Mods.cs into Modding/ on 2026-10-31; older revisions keep it in the Gateway's root.
    $modHost = @('src/GameServer.Gateway/Modding/Program.Mods.cs','src/GameServer.Gateway/Program.Mods.cs') | Where-Object { Test-Path -LiteralPath (At $_) }
    $supported = ($script:isRelease -and (Test-Path -LiteralPath (Get-ModFolder))) -or ((Test-Path -LiteralPath (At 'src/GameServer.Modding/GameServer.Modding.csproj')) -and $modHost)
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
    # Under a UTF-8 console a started process's stdin begins with a byte-order mark, which the helper cannot read as JSON.
    [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
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
function Get-StagedModPath([string]$path, [switch]$File) {
    # Only what Waypoint checked into its own downloads folder; never a path elsewhere on the disk.
    Require ($path -and [IO.Path]::IsPathRooted($path)) 'The mod was not prepared by Waypoint.'
    $downloads = Join-Path (Get-WaypointData) 'mod-downloads'
    $full = [IO.Path]::GetFullPath($path)
    Require (Test-ModChildPath $full $downloads) 'The mod was not prepared by Waypoint.'
    Assert-Physical $full
    if ($File) { Require (Test-Path -LiteralPath $full -PathType Leaf) 'The prepared mod file is missing.' }
    else { Require (Test-Path -LiteralPath $full -PathType Container) 'The prepared mod folder is missing.' }
    return $full
}
function Move-ModAside([string]$folder, [string]$target) {
    # Never deleted: the previous copy of a mod moves to Mods/.replaced/, where the Gateway does not look.
    if (-not (Test-Path -LiteralPath $folder)) { return }
    Assert-Physical $folder
    $aside = At ('Mods/.replaced/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8) + '/' + $target + '/' + $ModId)
    Assert-Physical $aside
    New-Item -ItemType Directory -Path (Split-Path $aside) -Force | Out-Null
    Move-Item -LiteralPath $folder -Destination $aside
    Write-Host "The previous Mods/$target/$ModId is kept at $aside."
}
function Install-StagedMod($snapshot) {
    # -ModIds, -ModStageds and -ModTargets (';'-separated, in the same order) install several with one backup; every one is
    # checked before any is copied.
    $ids = @(if ($ModIds) { $ModIds -split ';' } else { $ModId })
    $stageds = @(if ($ModIds) { $ModStageds -split ';' } else { $ModStaged })
    $targets = @(if ($ModIds) { $ModTargets -split ';' } else { $ModTarget })
    Require ($ids.Count -eq $stageds.Count -and $ids.Count -eq $targets.Count) 'Each mod to install needs its prepared folder and target.'
    $mods = @(for ($i = 0; $i -lt $ids.Count; $i++) {
        $id = $ids[$i]; $target = $targets[$i]
        Assert-ModId $id
        Require ($target -in @('Binaries','Source')) 'Choose where the mod goes.'
        Require (-not $script:isRelease -or $target -eq 'Binaries') 'A release install cannot build a mod from source. Install a release of this mod instead.'
        $staged = Get-StagedModPath $stageds[$i]
        Require ((Split-Path $staged -Leaf) -ceq $id) 'The prepared mod folder does not match its id.'
        if ($target -eq 'Source') {
            # Installed only: the player builds it (BuildMod), or live.ps1 does before it starts a Gateway.
            Require ([bool]@(Get-ChildItem -LiteralPath $staged -Filter *.csproj -File).Count) 'Only a project mod is installed as source.'
        } else {
            Require ((Test-Path -LiteralPath (Join-Path $staged 'mod.json') -PathType Leaf) -or [bool]@(Get-ChildItem -LiteralPath $staged -Filter *.cs -File).Count) "The prepared mod $id has no mod.json and no scripts."
        }
        [pscustomobject]@{ Id = $id; Target = $target; Staged = $staged }
    })
    Backup-Saves $snapshot
    Set-Stage $(if ($mods.Count -eq 1) { 'Installing mod' } else { 'Installing mods' }) (($mods | ForEach-Object { "$($_.Id) into Mods/$($_.Target)" }) -join ', ')
    foreach ($mod in $mods) {
        $script:ModId = $mod.Id
        $root = if ($mod.Target -eq 'Source') { At 'Mods/Source' } else { Get-ModFolder }
        Assert-Physical $root
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        $destination = Join-Path $root $mod.Id
        Move-ModAside $destination $mod.Target
        # A release replaces a source install of the same mod, which live.ps1 would otherwise build over it.
        if ($mod.Target -eq 'Binaries' -and -not $script:isRelease) { Move-ModAside (At "Mods/Source/$($mod.Id)") 'Source' }
        Copy-Item -LiteralPath $mod.Staged -Destination $destination -Recurse
        Remove-Item -LiteralPath $mod.Staged -Recurse -Force
        Write-Host "Installed $($mod.Id) into Mods/$($mod.Target)."
    }
    # The launcher's record of what it built for these ids no longer describes the new files.
    if (-not $script:isRelease) { Write-ManagedMods @(Read-ManagedMods | Where-Object { $_.Id -cnotin $ids }) }
}
function Save-ModSettings($snapshot) {
    Assert-ModId $ModId
    $staged = Get-StagedModPath $ModStaged -File
    Require ((Get-Item -LiteralPath $staged).Length -lt 262144) 'Mod settings exceed their size limit.'
    $json = [IO.File]::ReadAllText($staged)
    $parsed = $json | ConvertFrom-Json
    Require ($parsed -is [Management.Automation.PSCustomObject]) 'Mod settings must be a JSON object.'
    Backup-Saves $snapshot
    Set-Stage 'Saving mod settings' $ModId
    $folder = At 'data/mods'
    Assert-Physical $folder
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $path = Join-Path $folder "$ModId.json"
    Assert-Physical $path
    if ($ModExpectedHash) {
        $hash = if (Test-Path -LiteralPath $path -PathType Leaf) { Get-ModFileHash $path } else { 'missing' }
        Require ($hash -ceq $ModExpectedHash) 'Settings changed since they were opened. Refresh before saving.'
    }
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $history = Join-Path (Get-WaypointData) ('config-history/' + [guid]::NewGuid().ToString('N'))
        Assert-Physical $history
        New-Item -ItemType Directory -Path $history -Force | Out-Null
        Copy-Item -LiteralPath $path -Destination (Join-Path $history ($ModId + '.json'))
    }
    $temporary = Join-Path $folder ('waypoint-' + [guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $path -Force
    Remove-Item -LiteralPath $staged -Force
    Write-Host "Saved the settings of $ModId; a running Gateway reloads the mod."
}
function Uninstall-Mod($snapshot) {
    # Like a replaced copy, an uninstalled mod moves to Mods/.replaced/; a running Gateway unloads a folder that disappears.
    # -ModIds (';'-separated) uninstalls several with one backup; every one is checked before any moves.
    $ids = @(if ($ModIds) { $ModIds -split ';' | Where-Object { $_ } } else { $ModId })
    $mods = @(foreach ($id in $ids) {
        Assert-ModId $id
        $binary = Join-Path (Get-ModFolder) $id
        $source = At "Mods/Source/$id"
        $hasSource = (-not $script:isRelease) -and (Test-Path -LiteralPath $source)
        Require ((Test-Path -LiteralPath $binary) -or $hasSource) "No mod $id is installed."
        [pscustomobject]@{ Id = $id; Binary = $binary; Source = $source; HasSource = $hasSource }
    })
    Backup-Saves $snapshot
    Set-Stage $(if ($mods.Count -eq 1) { 'Uninstalling mod' } else { 'Uninstalling mods' }) (($ids -join ', ') + ' to Mods/.replaced')
    foreach ($mod in $mods) {
        $script:ModId = $mod.Id
        if ($mod.HasSource) { Move-ModAside $mod.Source 'Source' }
        Move-ModAside $mod.Binary 'Binaries'
    }
    if (-not $script:isRelease) { Write-ManagedMods @(Read-ManagedMods | Where-Object { $_.Id -cnotin $ids }) }
    $state = Join-Path (Get-ModFolder) 'mod-state.txt'
    if (Test-Path -LiteralPath $state) {
        Assert-Physical $state
        Require ((Get-Item -LiteralPath $state).Length -lt 131072) 'Mod choices exceed their size limit.'
        Write-ModChoices @([IO.File]::ReadAllLines($state) | Where-Object { $line = $_; -not ($ids | Where-Object { $line -cmatch ('^' + [regex]::Escape($_) + ' +(on|off) *$') }) })
    }
    Write-Host ("Uninstalled " + ($ids -join ', ') + '.')
}
# A release before 0.2.0-beta keeps the mods it shipped with, built for an older mod API; 0.2.0-beta was the first to ship
# an empty Mods\Binaries for downloaded mods. A release.json without a readable version counts as the older kind.
function Test-LegacyReleaseMods {
    if (-not $script:isRelease) { return $false }
    try { $version = [string](Get-Content -LiteralPath (At 'release.json') -Raw | ConvertFrom-Json).version } catch { return $true }
    if ($version -notmatch '^[vV]?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+.*)?$') { return $true }
    $core = [version]"$($Matches[1]).$($Matches[2]).$($Matches[3])"
    if ($core -ne [version]'0.2.0') { return $core -lt [version]'0.2.0' }
    # 0.2.0 itself: an alpha came before the beta.
    return $Matches[4] -and $Matches[4] -like 'alpha*'
}
function Invoke-ModOperation($snapshot) {
    if ($Action -in @('InstallMod','SaveModSettings','UninstallMod')) {
        Require (-not (Test-LegacyReleaseMods)) 'This release does not declare compatibility with downloadable mods. Use a current source checkout to install or update mods.'
    }
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
    if ($Action -eq 'SaveModSettings') { Save-ModSettings $snapshot; return }
    if ($Action -eq 'UninstallMod') { Uninstall-Mod $snapshot; return }
    if ($Action -eq 'InstallMod') { Install-StagedMod $snapshot; return }
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
        # -ModProjects (';'-separated) builds several at once: one backup, and one MSBuild run that builds them in parallel
        # and their shared references once (two dotnet builds side by side would race on those references).
        $names = @(if ($ModProjects) { $ModProjects -split ';' | Where-Object { $_ } } else { $ModProject })
        $projects = @(foreach ($name in $names) {
            Require ($name -and -not [IO.Path]::IsPathRooted($name)) 'Select a source mod project from this checkout.'
            $project = [IO.Path]::GetFullPath((At $name))
            Require ((Test-ModChildPath $project (At 'Mods/Source')) -and $project.EndsWith('.csproj', [StringComparison]::OrdinalIgnoreCase)) 'The mod project must be inside this checkout''s Mods/Source folder.'
            Assert-Physical $project
            Require (Test-Path -LiteralPath $project -PathType Leaf) "The mod project $name is missing."
            $project
        })
        Backup-Saves $snapshot
        Set-Stage $(if ($projects.Count -eq 1) { 'Building mod' } else { 'Building mods' }) ($names -join ', ')
        if ($projects.Count -eq 1) {
            $arguments = @('build', $projects[0], '--nologo', '-v', 'minimal')
            if ($running.Count) { $arguments += '--no-dependencies' }
            & dotnet @arguments
        } else {
            $list = [Security.SecurityElement]::Escape(($projects -join ';'))
            $references = if ($running.Count) { 'false' } else { 'true' }
            $traversal = Join-Path ([IO.Path]::GetTempPath()) ('waypoint-mods-' + [guid]::NewGuid().ToString('N') + '.proj')
            [IO.File]::WriteAllText($traversal, @"
<Project>
  <Target Name="Build">
    <MSBuild Projects="$list" Targets="Restore" Properties="MSBuildRestoreSessionId=`$([System.Guid]::NewGuid())" BuildInParallel="true" />
    <MSBuild Projects="$list" Targets="Build" Properties="BuildProjectReferences=$references" BuildInParallel="true" />
  </Target>
</Project>
"@)
            try { & dotnet msbuild $traversal -m -nologo -v:minimal }
            finally { Remove-Item -LiteralPath $traversal -Force -ErrorAction SilentlyContinue }
        }
        Require ($LASTEXITCODE -eq 0) 'Mod build failed. A successful previous build does not prove the new mod applied. See compiler output; stop servers and Build checkout if dependencies need rebuilding.'
        foreach ($project in $projects) { Record-ModBuild $project $snapshot.SourceCommit }
    } else {
        Backup-Saves $snapshot
        Build-GatewayMods -NoDependencies:([bool]$running.Count) -AllowRetire:(-not $gatewayRunning) -SourceCommit $snapshot.SourceCommit
    }
    if ($gateway.Status -eq 'Ready' -and @($snapshot.Services | Where-Object { $_.Name -eq 'Game' -and $_.ProcessId }).Count) {
        Start-Sleep -Milliseconds 600
        $null = Invoke-ModClientCommands @('/mod') (Get-Snapshot)
    } else { Write-Host 'Mod build completed. Live load status is unconfirmed; enter the game and use Check live status or /mod.' }
}
