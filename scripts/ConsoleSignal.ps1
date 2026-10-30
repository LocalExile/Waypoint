# Disposable Ctrl+C helper. Never attach/detach the long-lived runtime adapter's own console.
param([Parameter(Mandatory=$true)][int]$TargetId,
      [Parameter(Mandatory=$true)][int]$ParentId,
      [Parameter(Mandatory=$true)][string]$StartedUtc)
$ErrorActionPreference = 'Stop'
try {
    $target = Get-CimInstance Win32_Process -Filter "ProcessId=$TargetId"
    if (-not $target) { exit 0 }
    if ($target.Name -ine 'dotnet.exe' -or $target.ParentProcessId -ne $ParentId -or $target.CreationDate.ToUniversalTime().ToString('o') -ne $StartedUtc) { exit 1 }
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'RuntimeBridge.ps1'), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { exit 1 }
    $command = $ast.Find({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Add-Type' }, $true)
    Add-Type -TypeDefinition $command.CommandElements[2].Value
    # Watch has its own dotnet hosts and a restarting app child. Every console peer must belong to
    # this verified process tree, never an unrelated terminal process. Unknown peers fail closed.
    $inventory = @(Get-CimInstance Win32_Process)
    $byId = @{}; foreach ($p in $inventory) { $byId[[int]$p.ProcessId] = $p }
    $allowed = [Collections.Generic.List[int]]::new()
    $allowed.Add($TargetId); $allowed.Add($ParentId)
    foreach ($p in $inventory) {
        if ($p.ProcessId -eq $PID) { continue }
        $current = $p
        for ($i=0; $i -lt 8; $i++) {
            if ($current.ProcessId -eq $TargetId -and $current.CreationDate -eq $target.CreationDate) { $allowed.Add([int]$p.ProcessId); break }
            $parent = $byId[[int]$current.ParentProcessId]
            if (-not $parent -or $parent.CreationDate -gt $current.CreationDate) { break }
            $current = $parent
        }
    }
    $sent = [NativeRuntime]::Interrupt($TargetId, $ParentId, $allowed.ToArray())
    # The console handles are now detached; avoid all PowerShell host output, including exit/error formatting.
    [Environment]::Exit($(if ($sent) { 0 } else { 1 }))
} catch { [Environment]::Exit(1) }
