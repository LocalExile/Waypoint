# Public module/league files only. No server code is imported or executed.
function Invoke-LeagueData($environment, $request, [string]$action, [string]$phase) {
    $databases = @(Get-AccountDatabases)
    foreach ($database in $databases) { Assert-Physical $database }
    $python = Get-Python
    $OutputEncoding = [Text.UTF8Encoding]::new($false)
    $oldEncoding = $env:PYTHONIOENCODING
    try {
        $env:PYTHONIOENCODING = 'utf-8'
        $history = Join-Path (Get-WaypointData) 'config-history'
        $raw = ($request | ConvertTo-Json -Depth 30 -Compress) | & $python (Join-Path $PSScriptRoot 'LeagueData.py') $environment.Modules $environment.Leagues $environment.Choices $databases[0] $history $action $phase @($databases | Select-Object -Skip 1)
        $code = $LASTEXITCODE
        try { $reply = ($raw -join "`n") | ConvertFrom-Json } catch { throw 'The league helper did not return a valid result. Nothing can be confirmed.' }
        Require ($code -eq 0 -and $reply.Ok) $(if ($reply.Message) { [string]$reply.Message } else { 'League validation failed.' })
        return $reply
    } finally { $env:PYTHONIOENCODING = $oldEncoding }
}
function Save-ContentFile($snapshot) {
    # A release older than 0.2.0-beta does not read these files (Get-ContentEnvironment); what a newer one takes is checked
    # below, by what the environment allows.
    Require (-not (Test-LegacyReleaseMods)) 'This release is older than module and league editing. Update LocalExile to edit them.'
    # A port held by a program that is no LocalExile server (PortsInUse) blocks a start, not this change of files.
    $blocking = @($snapshot.Conflicts | Where-Object { $_ -notin @($snapshot.PortsInUse) })
    Require (-not $blocking.Count) ($blocking -join "`n")
    $environment = Get-ContentEnvironment
    $staged = Get-StagedModPath $ModStaged -File
    Require ((Get-Item -LiteralPath $staged).Length -le 262144) 'Settings exceed 256 KB.'
    $json = [IO.File]::ReadAllText($staged)
    $parsed = $json | ConvertFrom-Json
    Require ($parsed -is [Management.Automation.PSCustomObject]) 'Settings must be a JSON object.'
    if ($Action -in @('CreateLeague','SaveLeagueModules','SetLeagueVisibility')) {
        # Nothing here waits for the servers to stop: LocalExile reads its leagues and their module lists once, when a
        # server starts, so a change made while one runs is what its next start reads, as for module choices. The save
        # database is only read (for a new league's name), and backed up as before every change.
        if ($Action -in @('CreateLeague','SetLeagueVisibility')) {
            Require $environment.CanCreate 'This checkout has no supported league definition editing contract.'
            if ($Action -eq 'SetLeagueVisibility' -and $environment.ShippedLeaguesFixed) {
                # A release's own leagues are its files, which an update writes over. One made in Waypoint is its user's.
                $name = [string]$parsed.league
                Require ($name -and $name.Length -le 128 -and $name -notin @('.','..') -and $name -notmatch '[\\/:*?"<>|\x00-\x1F]') 'Select a league with a safe name.'
                Require (Test-Path -LiteralPath (Join-Path (Join-Path $environment.Leagues $name) '.waypoint-league.json') -PathType Leaf) 'A league that came with this release cannot be hidden or shown here: an update of LocalExile would undo it.'
            }
        } else { Require $environment.CanChoose 'This server has no supported league system overrides.' }
        $null = Invoke-LeagueData $environment $parsed $Action 'check'
        Backup-Saves $snapshot
        Set-Stage 'Saving league configuration' 'Validating compatibility and preserving existing characters and league definitions.'
        $null = Invoke-LeagueData $environment $parsed $Action 'apply'
        Remove-Item -LiteralPath $staged -Force
        return
    }
    Require $environment.CanEditSettings 'This server does not expose the shared module settings contract.'
    Require ($ModTarget -in @('Modules','Leagues')) 'Choose module or league settings.'
    if ($ModTarget -eq 'Modules') { Assert-ModId $ModId }
    else { Require ($ModId -and $ModId.Length -le 128 -and $ModId -notin @('.','..') -and $ModId -notmatch '[\\/:*?"<>|\x00-\x1F]') 'Select a league with a safe name.' }
    $source = if ($ModTarget -eq 'Modules') { $environment.Modules } else { $environment.Leagues }
    Assert-Physical $source
    $manifest = if ($ModTarget -eq 'Modules') { 'module.json' } else { 'league.json' }
    $found = @(Get-ChildItem -LiteralPath $source -Filter $manifest -File -Recurse | Where-Object { $_.Directory.Name -ceq $ModId })
    Require ($found.Count -eq 1) 'The selected system or league is missing or ambiguous. Refresh first.'
    Assert-Physical $found[0].FullName
    Assert-Physical (Join-Path $found[0].Directory.FullName 'settings.json')
    Require (Test-Path -LiteralPath (Join-Path $found[0].Directory.FullName 'settings.json') -PathType Leaf) 'This system declares no settings.'
    $destination = At ('data/' + $ModTarget.ToLowerInvariant() + '/' + $ModId + '.json')
    Assert-Physical $destination
    $hash = if (Test-Path -LiteralPath $destination -PathType Leaf) { Get-ModFileHash $destination } else { 'missing' }
    Require ($ModExpectedHash -and $hash -ceq $ModExpectedHash) 'Settings changed since they were opened. Refresh and review them before saving.'
    Backup-Saves $snapshot
    # Recheck after the backup, before either configuration file is replaced.
    Assert-Physical $destination
    $hash = if (Test-Path -LiteralPath $destination -PathType Leaf) { Get-ModFileHash $destination } else { 'missing' }
    Require ($hash -ceq $ModExpectedHash) 'Settings changed during backup. Refresh before saving.'
    if ($hash -ne 'missing') {
        $history = Join-Path (Get-WaypointData) ('config-history/' + [guid]::NewGuid().ToString('N'))
        Assert-Physical $history
        New-Item -ItemType Directory -Path $history -Force | Out-Null
        Copy-Item -LiteralPath $destination -Destination (Join-Path $history (Split-Path $destination -Leaf))
    }
    Set-Stage 'Saving game configuration' 'Writing player choices; shipped manifests are unchanged.'
    $folder = Split-Path $destination
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $temporary = Join-Path $folder ('waypoint-' + [guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $destination -Force
    Remove-Item -LiteralPath $staged -Force
}
