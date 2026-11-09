# Signs a local build of Signet (Signet.exe and our own DLLs) with the Certum certificate, so Smart
# App Control on this PC runs it. SimplySign Desktop must be connected.
#
#   ./scripts/sign-build.ps1                                   # the x64 Debug build
#   ./scripts/sign-build.ps1 -Configuration Release -Platform ARM64
#   ./scripts/sign-build.ps1 -Path <folder>
param(
    [string] $Configuration = 'Debug',
    [ValidateSet('x64', 'ARM64')] [string] $Platform = 'x64',
    [string] $Path
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'signing.ps1')

if (-not $Path) {
    $rid = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
    $Path = Get-ChildItem (Join-Path $root "src\Signet.App\bin\$Platform\$Configuration") -Directory -Filter 'net10.0-windows*' |
        ForEach-Object { Join-Path $_.FullName $rid } | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Path) { throw "No $Configuration $Platform build; dotnet build Signet.slnx -p:Platform=$Platform" }
}

$cert = Get-SigningCertificate
$signtool = Get-SignTool
$files = 'Signet.exe', 'Signet.dll', 'Signet.Core.dll' | ForEach-Object { Join-Path $Path $_ } | Where-Object { Test-Path $_ }
& $signtool sign ((Get-SignParams $cert) -split ' ') $files
if ($LASTEXITCODE -ne 0) { throw 'signtool failed' }
$files | ForEach-Object { Assert-Signed $_ $cert }
