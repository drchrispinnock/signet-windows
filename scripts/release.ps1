# Signs and drafts a release from the build GitHub Actions made for a tag (docs\RELEASING.md).
#
#   ./scripts/release.ps1 -Tag v0.9.15
#
# The Certum code-signing key lives in SimplySign, so it can only be used on this PC while
# SimplySign Desktop is connected. The workflow builds and tests the tagged commit and uploads the
# unsigned build of each architecture; this script downloads them, checks they were built from the
# tag, has Velopack sign and pack them, checks the signatures, and makes a DRAFT GitHub release.
# Nothing is public until the draft is published.
param(
    [Parameter(Mandatory)] [string] $Tag,
    [string] $Repository = 'drchrispinnock/signet-windows',
    [string] $WorkRoot = (Join-Path $env:TEMP 'signet-release')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'signing.ps1')

if ($Tag -notmatch '^v(\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?)$') { throw "Tag $Tag is not v<major>.<minor>.<patch>" }
$version = $Matches[1]

# 1. The key: SimplySign Desktop must be connected.
$cert = Get-SigningCertificate

# 2. The workflow run for the tag, built from the tag's commit.
$commit = (& git -C $root rev-parse "$Tag^{commit}").Trim()
if ($LASTEXITCODE -ne 0) { throw "No tag $Tag here; git fetch --tags" }
$runs = & gh run list --repo $Repository --workflow release.yml --branch $Tag --event push `
    --json databaseId,headSha,status,conclusion --limit 5 | ConvertFrom-Json
$run = $runs | Where-Object { $_.headSha -eq $commit } | Select-Object -First 1
if (-not $run) { throw "No Release run for $Tag ($commit) on GitHub yet" }
if ($run.status -ne 'completed') { throw "The Release run for $Tag is still $($run.status); wait for it" }
if ($run.conclusion -ne 'success') { throw "The Release run for $Tag ended in $($run.conclusion)" }

$work = Join-Path $WorkRoot $Tag
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
$dist = Join-Path $work 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$token = (& gh auth token).Trim()

foreach ($platform in 'x64', 'ARM64') {
    $rid = if ($platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
    $build = Join-Path $work "build-$rid"
    Write-Host "== ${rid}: downloading the build of $commit"
    & gh run download $run.databaseId --repo $Repository --name "Signet-build-$platform" --dir $build
    if ($LASTEXITCODE -ne 0) { throw "Could not download Signet-build-$platform" }
    $stamp = (Get-Content (Join-Path $build 'BUILD_COMMIT.txt') -Raw).Trim()
    if ($stamp -ne $commit) { throw "The $rid build is of $stamp, not $commit" }
    Remove-Item (Join-Path $build 'BUILD_COMMIT.txt')   # not part of the app

    Write-Host "== ${rid}: signing and packing $version"
    & (Join-Path $PSScriptRoot 'pack.ps1') -BuildDir $build -Rid $rid -Version $version `
        -SignParams (Get-SignParams $cert) -Delta -Token $token -Repository $Repository `
        -OutDir $dist -WorkDir (Join-Path $work "releases-$rid")

    # 3. Check what users run: the Setup, and Signet.exe in the portable zip.
    Assert-Signed (Join-Path $dist "SignetWallet-$rid-Setup.exe") $cert
    $portable = Join-Path $work "portable-$rid"
    Expand-Archive (Join-Path $dist "SignetWallet-$rid-Portable.zip") $portable
    $inner = Get-ChildItem $portable -Recurse -Filter Signet.exe | Select-Object -First 1
    if (-not $inner) { throw "No Signet.exe in the $rid portable zip" }
    Assert-Signed $inner.FullName $cert
}

# 4. Checksums in sha256sum's format, then the draft.
$sums = Get-ChildItem $dist -File | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
[IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'), (($sums -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))

Write-Host "== Drafting the release $Tag"
& gh release create $Tag (Get-ChildItem $dist -File).FullName --repo $Repository --verify-tag `
    --draft --title "Signet for Windows $version" --generate-notes
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
Write-Host "Done. Check the draft at https://github.com/$Repository/releases and publish it."
