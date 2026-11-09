# Packs a Release build of Signet with Velopack into dist\: the Setup, the portable zip, the
# update packages and the update feed for one architecture. Used by the workflow (unsigned, for a
# manual test run) and by scripts\release.ps1 (signed, for a release).
#
#   ./scripts/pack.ps1 -BuildDir <Release output folder> -Rid win-x64 -Version 0.9.15 `
#       [-SignParams '<signtool arguments>'] [-Delta] [-Token <GitHub token>] [-OutDir dist]
#
# -SignParams makes vpk sign every unsigned binary in the package (Signet.exe, Signet.dll, the
# bundled libraries) and the Setup.exe and Update.exe it makes, with its own copy of signtool.
# -Delta downloads the latest published release first, so this one carries a delta update for it.
param(
    [Parameter(Mandatory)] [string] $BuildDir,
    [Parameter(Mandatory)] [ValidateSet('win-x64', 'win-arm64')] [string] $Rid,
    [Parameter(Mandatory)] [string] $Version,
    [string] $SignParams,
    [switch] $Delta,
    [string] $Token,
    [string] $Repository = 'drchrispinnock/signet-windows',
    [string] $OutDir = 'dist',
    [string] $WorkDir = 'releases'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$vpkVersion = '1.2.161'

if (-not (Test-Path (Join-Path $BuildDir 'Signet.exe'))) { throw "No Signet.exe in $BuildDir" }

$tools = Join-Path $env:USERPROFILE '.dotnet\tools'
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) { $env:PATH += ";$tools" }
$installed = (& dotnet tool list -g) -match "^vpk\s+$([regex]::Escape($vpkVersion))\s"
if (-not $installed) {
    & dotnet tool update -g vpk --version $vpkVersion
    if ($LASTEXITCODE -ne 0) { throw 'Could not install vpk' }
}

New-Item -ItemType Directory -Force $WorkDir | Out-Null
if ($Delta) {
    $download = @('download', 'github', '--repoUrl', "https://github.com/$Repository", '--channel', $Rid, '-o', $WorkDir)
    if ($Token) { $download += @('--token', $Token) }
    & vpk @download
    # The first Velopack release had nothing to download from; that is fine.
    if ($LASTEXITCODE -ne 0) { Write-Host 'No earlier release to make a delta from.'; $global:LASTEXITCODE = 0 }
}

$pack = @('pack', '-u', 'SignetWallet', '-v', $Version, '-p', $BuildDir, '-e', 'Signet.exe',
    '--packTitle', 'Signet', '--packAuthors', 'Chris Pinnock',
    '-i', (Join-Path $root 'src\Signet.App\Assets\Signet.ico'),
    '-c', $Rid, '-r', $Rid, '-o', $WorkDir)
if ($SignParams) {
    # One file per signtool call: SimplySign handles a long batch badly, and a failure then names
    # the file.
    $pack += @('--signParams', $SignParams, '--signParallel', '1')
}
& vpk @pack
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }

# Only this version's files (not the earlier package fetched for the delta).
New-Item -ItemType Directory -Force $OutDir | Out-Null
Get-ChildItem $WorkDir | Where-Object {
    $_.Name -like "*-$Version-*" -or $_.Name -like "*-$Rid-Setup.exe" -or $_.Name -like "*-$Rid-Portable.zip" -or
    $_.Name -eq "releases.$Rid.json" -or $_.Name -eq "assets.$Rid.json" -or $_.Name -eq "RELEASES-$Rid"
} | Copy-Item -Destination $OutDir
Get-ChildItem $OutDir | Format-Table Name, Length
