# Public module/league files only. No server code is imported or executed.
function Invoke-LeagueData($environment, $request, [string]$action, [string]$phase) {
    Assert-Physical $script:savePath
    $python = Get-Python
    $OutputEncoding = [Text.UTF8Encoding]::new($false)
    $oldEncoding = $env:PYTHONIOENCODING
    try {
        $env:PYTHONIOENCODING = 'utf-8'
        $history = Join-Path (Get-WaypointData) 'config-history'
        $raw = ($request | ConvertTo-Json -Depth 30 -Compress) | & $python (Join-Path $PSScriptRoot 'LeagueData.py') $environment.Modules $environment.Leagues $environment.Choices $script:savePath $history $action $phase
        $code = $LASTEXITCODE
        try { $reply = ($raw -join "`n") | ConvertFrom-Json } catch { throw 'The league helper did not return a valid result. Nothing can be confirmed.' }
        Require ($code -eq 0 -and $reply.Ok) $(if ($reply.Message) { [string]$reply.Message } else { 'League validation failed.' })
        return $reply
    } finally { $env:PYTHONIOENCODING = $oldEncoding }
}
function Save-ContentFile($snapshot) {
    Require (-not $script:isRelease) 'This release does not declare support for module and league editing. Use a current source checkout.'
    Require (-not $snapshot.Conflicts.Count) ($snapshot.Conflicts -join "`n")
    $environment = Get-ContentEnvironment
    $staged = Get-StagedModPath $ModStaged -File
    Require ((Get-Item -LiteralPath $staged).Length -le 262144) 'Settings exceed 256 KB.'
    $json = [IO.File]::ReadAllText($staged)
    $parsed = $json | ConvertFrom-Json
    Require ($parsed -is [Management.Automation.PSCustomObject]) 'Settings must be a JSON object.'
    if ($Action -in @('CreateLeague','SaveLeagueModules','SetLeagueVisibility')) {
        if ($Action -in @('CreateLeague','SetLeagueVisibility')) {
            Require $environment.CanCreate 'This checkout has no supported league definition editing contract.'
            Assert-AccountsStopped $snapshot
        } else { Require $environment.CanChoose 'This server has no supported league system overrides.' }
        $null = Invoke-LeagueData $environment $parsed $Action 'check'
        Backup-Saves $snapshot
        # Inventory and definitions are checked again after backup; creation never stops any process itself.
        if ($Action -in @('CreateLeague','SetLeagueVisibility')) { Assert-AccountsStopped (Get-Snapshot) }
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
