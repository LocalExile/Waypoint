# Whether a version tag may become a release: a new version, from main. The Release workflow publishes whatever tag reaches
# it, so a leftover tag would otherwise go out as a public release.
#   -Tag v1.2.3   the tag
#   -Latest v1.2.2   GitHub's latest release; anything that is no version counts as none
#   -OnMain       the tag's commit is on main
param([Parameter(Mandatory)][string]$Tag, [string]$Latest = '', [switch]$OnMain)

function Get-Version([string]$name) { if ($name -match '^v(\d+\.\d+\.\d+)$') { [version]$Matches[1] } }

$number = Get-Version $Tag
if (-not $number) { throw "$Tag is not v<major>.<minor>.<patch>." }
if (-not $OnMain) { throw "$Tag is not on main. Release only from main." }
$newest = Get-Version $Latest
if ($newest -and $number -le $newest) { throw "$Tag is not above the latest release, $Latest. Use a higher version." }
