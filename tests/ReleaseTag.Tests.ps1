# Which tags may become a release (tools/check-release-tag.ps1).
$ErrorActionPreference = 'Stop'
$check = Join-Path $PSScriptRoot '../tools/check-release-tag.ps1'
$script:passed = 0
function Check($value, $message) { if (-not $value) { throw "FAIL: $message" }; $script:passed++; Write-Host "PASS: $message" }
function Refusal([hashtable]$arguments) {
    try { & $check @arguments; return $null } catch { return $_.Exception.Message }
}

Check ($null -eq (Refusal @{ Tag='v0.9.2'; Latest='v0.9.1'; OnMain=$true })) 'the next version on main is released'
Check ($null -eq (Refusal @{ Tag='v0.10.0'; Latest='v0.9.1'; OnMain=$true })) 'versions compare as numbers, not as text'
Check ($null -eq (Refusal @{ Tag='v0.1.0'; Latest=''; OnMain=$true })) 'the first release has nothing to be above'
Check ($null -eq (Refusal @{ Tag='v0.1.0'; Latest='{"message":"Not Found"}'; OnMain=$true })) 'an answer that is no version counts as no release'
Check ((Refusal @{ Tag='v0.9.2'; Latest='v0.9.1' }) -like '*not on main*') 'a tag on a commit that is not on main is refused'
Check ((Refusal @{ Tag='v0.9.1'; Latest='v0.9.1'; OnMain=$true }) -like '*not above*') 'the latest version again is refused'
Check ((Refusal @{ Tag='v0.8.6'; Latest='v0.9.1'; OnMain=$true }) -like '*not above*') 'an older version is refused'
Check ((Refusal @{ Tag='v.0.3.1'; Latest='v0.9.1'; OnMain=$true }) -like '*not v<major>*') 'a tag that is no version is refused'
Check ((Refusal @{ Tag='v1.0'; Latest='v0.9.1'; OnMain=$true }) -like '*not v<major>*') 'a version needs all three numbers'
Write-Host "$script:passed release tag checks passed."
