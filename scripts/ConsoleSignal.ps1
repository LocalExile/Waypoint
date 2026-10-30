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
    $sent = [NativeRuntime]::Interrupt($TargetId, $ParentId)
    # The console handles are now detached; avoid all PowerShell host output, including exit/error formatting.
    [Environment]::Exit($(if ($sent) { 0 } else { 1 }))
} catch { [Environment]::Exit(1) }
