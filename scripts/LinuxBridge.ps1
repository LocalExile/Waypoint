# The adapter on Linux. RuntimeBridge.ps1 loads this after its own functions, only when $IsLinux, so the functions here
# replace those that cannot work on Linux; Windows never reads this file. Everything else in the adapter is shared.
if (-not $env:LOCALAPPDATA) { $env:LOCALAPPDATA = [Environment]::GetFolderPath('LocalApplicationData') }
if (-not (Get-Command python -ErrorAction SilentlyContinue)) { Set-Alias python python3 -Scope Script }

# The Linux path of a Windows path a process under Wine shows. Proton picks the drive letters by where the game is, so the
# letter is read from that process's own prefix.
function ConvertFrom-WinePath([string]$path, [string]$folder) {
    if ($path -notmatch '^(?:\\\\\?\\)?([A-Za-z]):(\\.*)$') { return $path }
    $letter = $Matches[1].ToLowerInvariant(); $rest = $Matches[2].Replace('\', '/')
    try {
        $prefix = [IO.File]::ReadAllText("$folder/environ").Split([char]0) | Where-Object { $_ -like 'WINEPREFIX=*' } | Select-Object -First 1
        if (-not $prefix) { return $path }
        $drive = [IO.File]::ResolveLinkTarget("$($prefix.Substring(11).TrimEnd('/'))/dosdevices/${letter}:", $false)
        "$($drive.FullName.TrimEnd('/'))$rest"
    }
    catch { $path }
}

# Get-CimInstance Win32_Process, from /proc, with Windows' names so the adapter's checks read the same: a native program is
# "<file>.exe", and a process under Wine, which shows its Windows path, has that file's name and that exe's Linux path.
# Not when a caller gave its own (the checks' fixtures).
if (-not (Test-Path function:Get-CimInstance)) {
function Get-CimInstance {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string]$ClassName, [string]$Filter)
    $bootLine = [IO.File]::ReadAllLines('/proc/stat') | Where-Object { $_ -like 'btime *' }
    $boot = [DateTimeOffset]::FromUnixTimeSeconds([long]($bootLine -split ' ')[1])
    foreach ($folder in [IO.Directory]::GetDirectories('/proc')) {
        $id = [IO.Path]::GetFileName($folder)
        if ($id -notmatch '^\d+$') { continue }
        try {
            $stat = [IO.File]::ReadAllText("$folder/stat")
            $arguments = [IO.File]::ReadAllText("$folder/cmdline").Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)
        }
        catch { continue }
        if (-not $arguments.Count) { continue }
        $fields = $stat.Substring($stat.LastIndexOf(')') + 2).Split(' ')
        if ($arguments[0] -match '^[A-Za-z]:\\|\.exe$') {
            $name = ($arguments[0] -split '[\\/]')[-1]
            $path = ConvertFrom-WinePath $arguments[0] $folder
        }
        else {
            $path = try { [IO.File]::ResolveLinkTarget("$folder/exe", $false).FullName } catch { $arguments[0] }
            $name = "$([IO.Path]::GetFileName($path)).exe"
        }
        [pscustomobject]@{
            ProcessId = [int]$id; ParentProcessId = [int]$fields[1]; Name = $name; ExecutablePath = $path
            CommandLine = $arguments -join ' '; CreationDate = $boot.AddSeconds([long]$fields[19] / 100).LocalDateTime
        }
    }
}
}

# Get-NetTCPConnection -State Listen, from ss.
if (-not (Test-Path function:Get-NetTCPConnection)) {
function Get-NetTCPConnection {
    [CmdletBinding()]
    param([int]$LocalPort, [string]$State)
    foreach ($line in @(& ss -Hltnp)) {
        $columns = "$line" -split '\s+'
        if ($columns.Count -lt 4 -or $columns[3] -notmatch ':(\d+)$') { continue }
        $port = [int]$Matches[1]
        if ($PSBoundParameters.ContainsKey('LocalPort') -and $port -ne $LocalPort) { continue }
        $owner = if ($line -match 'pid=(\d+)') { [int]$Matches[1] } else { 0 }
        [pscustomobject]@{ LocalPort = $port; OwningProcess = $owner; State = 'Listen' }
    }
}
}

# live.ps1 starts a server on Linux with its dll's absolute path, and no cmd wrapper: the path says whose it is. A release's
# servers are Windows programs under Proton, from app/.
function Owned-Server($process, [string]$name) {
    if ($script:isRelease) { return $process -and (Same-Path $process.ExecutablePath (At "app/GameServer.$name.exe")) }
    if (-not $process -or $process.Name -ine 'dotnet.exe') { return $false }
    $arguments = @(-split "$($process.CommandLine)")
    if ($arguments.Count -ne $(if ($name -eq 'Auth') { 3 } else { 2 })) { return $false }
    if ($name -eq 'Auth' -and $arguments[2] -ine 'serve') { return $false }
    foreach ($configuration in 'Debug', 'Release') {
        if (Same-Path $arguments[1] (At "src/GameServer.$name/bin/$configuration/net10.0/GameServer.$name.dll")) { return $true }
    }
    $false
}
function Owned-Watcher($process) {
    if (-not $process -or $process.Name -ine 'dotnet.exe') { return $false }
    $arguments = @(-split "$($process.CommandLine)")
    # The first two: `dotnet watch`, with the project this checkout's; dotnet-watch's own process names no project.
    $arguments.Count -ge 5 -and $arguments[1] -ceq 'watch' -and $arguments[2] -ceq 'run' -and $arguments[3] -ceq '--project' -and
        (Same-Path $arguments[4] (At 'src/GameServer.Gateway'))
}
function Owned-WatchedGateway($process, $watcher) {
    $process -and (Same-Path $process.ExecutablePath (At 'src/GameServer.Gateway/bin/Debug/net10.0/GameServer.Gateway')) -and (Descends-From $process $watcher)
}

function Get-Python {
    $venv = At '.venv/bin/python'
    if (Test-Path -LiteralPath $venv) { return $venv }
    $command = Get-Command python3, python -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    Require ([bool]$command) 'Python is required to make a consistent save backup. Install Python or disable automatic backups explicitly.'
    $command.Source
}
function npm.cmd { & npm @args }

# LocalExile.exe of a release is the Windows program, under Proton in the install's prefix (Platform.WindowsRunner).
function Invoke-Release([string[]]$arguments, [string]$standardInput) {
    $saved = @{}
    $settings = @{ WINEPREFIX = (At 'data/wine-prefix'); PROTONPATH = 'GE-Proton'; GAMEID = 'umu-238960'; PROTON_VERB = 'runinprefix' }
    foreach ($key in $settings.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key)
        if (-not $saved[$key] -or $key -eq 'PROTON_VERB') { [Environment]::SetEnvironmentVariable($key, $settings[$key]) }
    }
    try { $standardInput | & umu-run (At 'LocalExile.exe') @arguments 2>$null }
    finally { foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) } }
}

# As on Windows, but for how the temporary account service is started and stopped: Ctrl+C is SIGINT, and needs no console.
function Register-Account($snapshot) {
    Assert-AccountsStopped $snapshot
    Require ($script:isRelease -or (Test-Sdk)) 'Install .NET 10 SDK first.'
    Require ((-not $env:Game_DEV_PORT -or $env:Game_DEV_PORT -eq '7000') -and (-not $env:Game_DEV_BIND -or $env:Game_DEV_BIND -eq '127.0.0.1')) 'Account setup requires the default localhost account-service address.'
    Require (-not @($script:listeners | Where-Object LocalPort -eq 7000).Count) 'Port 7000 is in use. Stop the existing account service first; it will not be adopted.'
    [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
    try { $request = [Console]::In.ReadLine() | ConvertFrom-Json } catch { throw 'Could not read local account request.' }
    Require ($request.Name -is [string] -and $request.Name -match '^[A-Za-z0-9_-]{3,32}$') 'Account name must be 3-32 letters, digits, underscores or hyphens.'
    Require ($request.Password -is [string] -and $request.Password.Length -ge 4 -and $request.Password.Length -le 1024) 'Password must contain 4-1024 characters.'
    Assert-AccountDatabase $request $false
    Backup-Saves $snapshot
    if ($script:isRelease) {
        Require ($request.Password -cmatch '^[\x20-\x7E]+$') 'The password can only use English letters, digits and common symbols.'
        Set-Stage 'Creating local account' 'Using this installation only. Credentials are not saved by the launcher.'
        $lines = @(Invoke-Release @('account', 'create', $request.Name, '--password-stdin', '--json') $request.Password)
        $code = $LASTEXITCODE
        $failure = @($lines | ForEach-Object { try { $_ | ConvertFrom-Json } catch { $null } } | Where-Object { $_.event -eq 'error' } | Select-Object -First 1)
        Require ($code -eq 0) $(if ($failure.Count) { [string]$failure[0].message } else { "LocalExile.exe could not create the account (exit $code)." })
        return
    }
    Set-Stage 'Building account service' 'Preparing the local account service for this checkout.'
    & dotnet build (At 'src/GameServer.Host') --nologo -v minimal | Out-Null
    Require ($LASTEXITCODE -eq 0) 'Account service build failed. Run Build checkout to inspect compiler output.'
    Assert-AccountsStopped (Get-Snapshot)
    $log = Join-Path (Split-Path $ResultPath) 'account-service.log'
    $err = Join-Path (Split-Path $ResultPath) 'account-service.err'
    $start = [Diagnostics.ProcessStartInfo]::new('setsid')
    $start.WorkingDirectory = $script:root
    $start.UseShellExecute = $false
    foreach ($argument in 'sh', '-c', 'log=$1; err=$2; shift 2; exec "$@" </dev/null >"$log" 2>"$err"', 'sh', $log, $err, 'dotnet', (At 'src/GameServer.Host/bin/Debug/net10.0/GameServer.Host.dll')) { $start.ArgumentList.Add($argument) }
    $server = [Diagnostics.Process]::Start($start)
    $client = $null; $registered = $false
    try {
        Set-Stage 'Creating local account' 'Using this installation only. Credentials are not saved by the launcher.'
        $deadline = (Get-Date).AddSeconds(20)
        do {
            Start-Sleep -Milliseconds 200
            Get-Inventory
            $listeners = @($script:listeners | Where-Object LocalPort -eq 7000)
            Require (-not @($listeners | Where-Object { $_.OwningProcess -ne $server.Id }).Count) 'Another process took account-service port 7000. No account request was sent.'
        } until ($listeners.Count -or (Get-Date) -gt $deadline -or $server.HasExited)
        Require ($listeners.Count) 'The temporary account service did not become ready.'
        $client = [Net.Sockets.TcpClient]::new()
        $client.ReceiveTimeout = 15000; $client.SendTimeout = 15000
        $client.Connect('127.0.0.1', 7000)
        $stream = $client.GetStream()
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false), 4096, $true)
        $writer.AutoFlush = $true
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $false, 4096, $true)
        try {
            $payload = @{op='register';name=$request.Name;password=$request.Password} | ConvertTo-Json -Compress
            $writer.WriteLine($payload)
            $reply = $reader.ReadLine() | ConvertFrom-Json
            Require ($reply.ok -eq $true) 'The account could not be created. The name may already exist, or it may not meet the server rules. No password was changed.'
            $registered = $true
        } finally { $reader.Dispose(); $writer.Dispose(); $payload = $null }
    }
    finally {
        $request = $null
        if ($client) { $client.Dispose() }
        if (-not $server.HasExited) {
            Set-Stage 'Closing account service' 'Allowing the temporary local service to shut down.'
            & kill -INT $server.Id
            Require ($server.WaitForExit(20000)) "The temporary account service (PID $($server.Id)) is still running."
        }
    }
    Require $registered 'Account registration did not finish.'
}
