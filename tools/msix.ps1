# Builds the Microsoft Store package (`make msix`): Pulse published self-contained (the Store can't install the
# .NET runtime first), laid out with packaging/msix/AppxManifest.xml and its logos, and packed with makeappx.
# The Store signs it on publishing, so it's left unsigned here.
#
#   make msix                     package for the latest release tag (v0.2.0 → 0.2.0.0)
#   make msix MSIX_VERSION=0.3.0  a given version (the Store needs four numbers, the last one 0)
#
# To try it on this PC before submitting (unsigned packages need Windows' Developer Mode, which you turn on in
# Settings → System → For developers):
#   Add-AppxPackage -Register artifacts\msix\layout\AppxManifest.xml
# and remove it again with: Get-AppxPackage *Pulse* | Remove-AppxPackage
param([string] $Version = '')

$ErrorActionPreference = 'Continue'
Set-Location (Split-Path -Parent $PSScriptRoot)

function Fail([string] $message) {
    Write-Host "msix: $message" -ForegroundColor Red
    exit 1
}

# ---- Version: from the latest release tag unless given; the Store wants a.b.c.0 -------------------------------

if (-not $Version) {
    $tag = (& git.exe describe --tags --abbrev=0 --match 'v*' 2>$null)
    if (-not $tag) { Fail 'no release tag yet; give MSIX_VERSION=0.2.0' }
    $Version = $tag.TrimStart('v')
}
$numbers = ($Version -split '-')[0]
if ($numbers -notmatch '^\d+\.\d+\.\d+$') { Fail "'$Version' isn't a version like 0.2.0" }
$packageVersion = "$numbers.0"

# ---- Tools ---------------------------------------------------------------------------------------------------

$makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
    Sort-Object { [version]($_.Directory.Parent.Name) } | Select-Object -Last 1
if (-not $makeappx) { Fail 'makeappx.exe not found: install the Windows SDK (it comes with Visual Studio, or on its own).' }

$identity = Get-Content packaging/msix/identity.json -Raw | ConvertFrom-Json

# ---- Publish and lay out the package ---------------------------------------------------------------------------

$root = 'artifacts/msix'
$layout = "$root/layout"
if (Test-Path $root) { Remove-Item -Recurse -Force $root }
New-Item -ItemType Directory -Force $layout | Out-Null

Write-Host "Publishing Pulse $Version, self-contained..."
& dotnet publish src/Pulse/Pulse.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true `
    "-p:MinVerVersionOverride=$Version" -o $layout --artifacts-path "$root/build" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish failed.' }

Copy-Item -Recurse packaging/msix/Assets "$layout/Assets"
$manifest = (Get-Content packaging/msix/AppxManifest.xml -Raw).
    Replace('$IdentityName$', $identity.IdentityName).
    Replace('$Publisher$', [Security.SecurityElement]::Escape($identity.Publisher)).
    Replace('$PublisherDisplayName$', [Security.SecurityElement]::Escape($identity.PublisherDisplayName)).
    Replace('$DisplayName$', [Security.SecurityElement]::Escape($identity.DisplayName)).
    Replace('$Version$', $packageVersion)
[IO.File]::WriteAllText((Resolve-Path $layout).Path + '\AppxManifest.xml', $manifest, (New-Object Text.UTF8Encoding $false))

# ---- Pack ----------------------------------------------------------------------------------------------------

$package = "$root/Pulse-$packageVersion-x64.msix"
& $makeappx.FullName pack /d $layout /p $package /o | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "makeappx failed (run it again with output: `"$($makeappx.FullName)`" pack /d $layout /p $package /o)." }

$size = [math]::Round((Get-Item $package).Length / 1MB, 1)
Write-Host ''
Write-Host "Built $package ($size MB), version $packageVersion, for '$($identity.IdentityName)'." -ForegroundColor Green
if ($identity.IdentityName -like '*LocalTest*') {
    Write-Host 'This is a local test package: fill packaging/msix/identity.json from Partner Center before submitting.'
}
