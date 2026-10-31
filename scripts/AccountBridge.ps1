# Offline account operations. RuntimeBridge owns the global writer lock and runtime/profile inventory.
function Assert-AccountsStopped($snapshot) {
    Require (-not $snapshot.Conflicts.Count) ($snapshot.Conflicts -join "`n")
    Require (-not @($snapshot.Services | Where-Object { $_.ProcessId -or $_.WatchProcessId }).Count) 'Close the game and stop LocalExile services before changing player accounts.'
    Require (-not @($script:listeners | Where-Object LocalPort -eq 7000).Count) 'Stop the local account service before changing player accounts.'
    $writers = @($script:processes | Where-Object {
        $_.Name -match '^GameServer\.(Gateway|Auth|Host)(\.exe)?$' -or
        $_.CommandLine -match '(?i)GameServer\.(Gateway|Auth|Host)\.(dll|csproj)(?:["\s]|$)' -or
        ($_.CommandLine -match '(?i)dotnet.+watch' -and $_.CommandLine -match '(?i)GameServer\.(Gateway|Auth|Host)(?:["\s/\\]|$)')
    })
    Require (-not $writers.Count) 'Account changes are read-only while any LocalExile login, Gateway or account-service process is running. Stop those services and close the game, then refresh. Nothing was changed.'
}

function Invoke-AccountData($request) {
    Assert-Physical $script:savePath
    $python = Get-Python
    $oldEncoding = $env:PYTHONIOENCODING
    $OutputEncoding = [Text.UTF8Encoding]::new($false)
    try {
        $env:PYTHONIOENCODING = 'utf-8'
        # The process pipe carries the request; credentials are never arguments or temporary files.
        $raw = ($request | ConvertTo-Json -Depth 6 -Compress) | & $python (Join-Path $PSScriptRoot 'AccountData.py') $script:savePath $script:root
        $code = $LASTEXITCODE
        try { $reply = ($raw -join "`n") | ConvertFrom-Json } catch { throw 'The account-data helper did not return a valid result. No account result can be confirmed.' }
        Require ($code -eq 0 -and $reply.Ok) $(if ($reply.Message) { [string]$reply.Message } else { 'Account data could not be read or changed.' })
        return $reply.Catalog
    } finally { $env:PYTHONIOENCODING = $oldEncoding }
}

function Manage-Account($snapshot) {
    Assert-AccountsStopped $snapshot
    [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
    try { $request = [Console]::In.ReadLine() | ConvertFrom-Json } catch { throw 'Could not read the local account change request.' }
    Apply-AccountChange $snapshot $request
}

function Apply-AccountChange($snapshot, $request) {
    Assert-AccountsStopped $snapshot
    Require ($request.Action -in @('Rename','Password','Duplicate','Delete')) 'Unknown account change.'
    Backup-Saves $snapshot
    # A backup can take time. Recheck all known writers immediately before beginning the database transaction.
    $checked = Get-Snapshot
    Assert-AccountsStopped $checked
    Set-Stage 'Changing account data' 'Applying the reviewed change in one SQLite transaction. Credentials are not logged.'
    $script:accountData = Invoke-AccountData $request
}
