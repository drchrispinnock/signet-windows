# Code signing with the Certum Open Source code-signing certificate, whose key is in SimplySign
# (Certum's cloud card). Dot-sourced by release.ps1 and sign-build.ps1.
#
# While SimplySign Desktop is connected (its tray icon, then the code from the SimplySign phone
# app), the certificate is in the user's certificate store with its key on the virtual card, and
# signtool signs with it by thumbprint.

# "Open Source Developer Chris Pinnock", Certum Code Signing 2021 CA, valid until 9 November 2027.
# After a renewal, put the new certificate's thumbprint here.
$SigningThumbprint = '61EA6BA1190E30EFEAE84EA9519E7BC6E95564AB'
$TimestampURL = 'http://time.certum.pl'

function Get-SigningCertificate {
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $SigningThumbprint }
    if (-not $cert -or -not $cert.HasPrivateKey) {
        throw ('The signing certificate {0} is not available. Start SimplySign Desktop and connect ' +
            '(tray icon, then the code from the SimplySign app); if it still does not appear, use ' +
            'SimplySign Desktop''s certificate manager to register it.') -f $SigningThumbprint
    }
    if ($cert.NotAfter -lt (Get-Date).AddDays(1)) { throw "The signing certificate expires on $($cert.NotAfter); renew it" }
    $cert
}

# signtool sign arguments: SHA-256 digest, RFC 3161 timestamp from Certum so the signature stays
# valid after the certificate expires.
function Get-SignParams($cert) {
    "/sha1 $($cert.Thumbprint) /fd sha256 /tr $TimestampURL /td sha256"
}

function Assert-Signed([string] $path, $cert) {
    $sig = Get-AuthenticodeSignature $path
    if ($sig.Status -ne 'Valid') { throw "$path is not validly signed: $($sig.Status) $($sig.StatusMessage)" }
    if ($sig.SignerCertificate.Thumbprint -ne $cert.Thumbprint) { throw "$path is signed by $($sig.SignerCertificate.Subject)" }
    if (-not $sig.TimeStamperCertificate) { throw "$path has no timestamp" }
    Write-Host "Signed: $path"
}

# signtool: Velopack's copy, or the Windows SDK build tools NuGet package's.
function Get-SignTool {
    $candidates = @(
        Get-ChildItem (Join-Path $env:USERPROFILE '.dotnet\tools\.store\vpk') -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue
        Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools') -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq 'x64' }
    )
    $tool = $candidates | Select-Object -First 1
    if (-not $tool) { throw 'No signtool.exe found; build the app once, or install vpk (dotnet tool install -g vpk)' }
    $tool.FullName
}
