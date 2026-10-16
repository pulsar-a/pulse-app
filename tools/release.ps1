# Cuts a release (`make release`): works out the next version from the conventional commits since the last
# release, writes it into CHANGELOG.md, commits that with a tag, and pushes both. The tag starts the release
# workflow, which builds and publishes the installer and updates.
#
#   make release                  next stable version (feat → minor, fix → patch, BREAKING CHANGE → major)
#   make release PRE=beta         next beta (0.3.0-beta.1, then 0.3.0-beta.2…)
#   make release VERSION=1.0.0    exactly this version
#   make changelog                show what the next release would be, change nothing
#
# Versioning, changelog, release commit and tag come from Versionize (pinned in dotnet-tools.json); this script
# adds the checks around it, the confirmation, and the push.
param(
    [string] $Pre = '',
    [string] $Version = '',
    [switch] $DryRun,
    [switch] $Yes
)

# Not 'Stop': Windows PowerShell turns anything a program writes to stderr into a fatal error, and git reports
# progress there. Every call's exit code is checked instead.
$ErrorActionPreference = 'Continue'
Set-Location (Split-Path -Parent $PSScriptRoot)

function Fail([string] $message) {
    Write-Host "release: $message" -ForegroundColor Red
    exit 1
}

# Not named Git: PowerShell names aren't case-sensitive, so it would shadow git itself.
function Invoke-Git([Parameter(ValueFromRemainingArguments)] [string[]] $arguments) {
    $output = & git.exe @arguments 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { Fail "git $($arguments -join ' ') failed: $output" }
    return $output
}

# ---- Checks: release only what's committed, from main, on top of what GitHub has --------------------------------

if (-not $DryRun) {
    if (Invoke-Git status --porcelain) { Fail 'there are uncommitted changes. Commit or stash them first.' }
    $branch = (Invoke-Git rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne 'main') { Fail "releases are made from main (this is $branch)." }
    Invoke-Git fetch --quiet --tags origin | Out-Null
    $behind = [int](Invoke-Git rev-list --count 'HEAD..origin/main')
    if ($behind -gt 0) { Fail "main is $behind commit(s) behind GitHub. Pull first." }
}
if ($Pre -and $Version) { Fail 'give either PRE or VERSION, not both.' }

& dotnet tool restore | Out-Null
if ($LASTEXITCODE -ne 0) { Fail 'dotnet tool restore failed.' }

# ---- The version: from the commits, unless given ------------------------------------------------------------

$previous = (& git.exe describe --tags --abbrev=0 --match 'v*' 2>$null)
if (-not $Version) {
    $probe = @('versionize', '--tag-only', '--dry-run', '--skip-dirty', '--exit-insignificant-commits')
    if ($Pre) { $probe += @('--pre-release', $Pre) }
    $preview = & dotnet @probe 2>&1
    if ($LASTEXITCODE -ne 0) {
        Fail "nothing to release since $previous (no feat, fix or breaking change). Docs and chores alone don't make a release."
    }
    $match = [regex]::Match(($preview -join "`n"), '<a name="([^"]+)"></a>')
    if (-not $match.Success) { Fail "couldn't read the next version from Versionize:`n$($preview -join "`n")" }
    $Version = $match.Groups[1].Value
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { Fail "'$Version' isn't a version like 1.2.3 or 1.2.3-beta.1." }
$tag = "v$Version"
if (& git.exe tag --list $tag) { Fail "$tag already exists." }
$isPre = $Version.Contains('-')

# A stable release after betas lists everything since the last stable release, betas included.
$run = @('versionize', '--tag-only', '--release-as', $Version)
if (-not $isPre) { $run += '--aggregate-pre-releases' }

# ---- Show what's coming --------------------------------------------------------------------------------------

$changes = & dotnet @($run + @('--dry-run', '--skip-dirty')) 2>&1 |
    Where-Object { $_ -match '^(## |### |\* )' } |
    ForEach-Object { $_ -replace ' \(\[[0-9a-f]{7}\]\([^)]*\)\)', '' -replace '\[([^\]]+)\]\([^)]*\)', '$1' }
Write-Host ''
Write-Host "Release $tag$(if ($isPre) { ' (pre-release)' })$(if ($previous) { ", after $previous" })" -ForegroundColor Cyan
$changes | ForEach-Object { Write-Host "  $_" }
Write-Host ''

if ($DryRun) {
    Write-Host 'Dry run: nothing was changed.'
    exit 0
}

if (-not $Yes) {
    $answer = Read-Host "Write CHANGELOG.md, commit, tag $tag and push to GitHub? This publishes a release. [y/N]"
    if ($answer -notmatch '^(y|yes)$') { Write-Host 'Stopped. Nothing was changed.'; exit 0 }
}

# ---- Release: changelog, commit, tag (Versionize), then push both at once --------------------------------------

& dotnet @run
if ($LASTEXITCODE -ne 0) { Fail 'Versionize failed; see above.' }
if (-not (& git.exe tag --list $tag)) { Fail "Versionize didn't create $tag." }

Invoke-Git push --atomic origin main $tag | Out-Null
Write-Host ''
Write-Host "Pushed $tag. The release workflow is building it:" -ForegroundColor Green
Write-Host '  https://github.com/pulsar-a/pulse-app/actions/workflows/release.yml'
Write-Host "  https://github.com/pulsar-a/pulse-app/releases/tag/$tag (in about 5 minutes)"
