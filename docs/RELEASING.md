# Releasing Signet for Windows

From 0.8.1 a release is made with Velopack. For each architecture (`win-x64`, `win-arm64`) the
GitHub release holds:

- `SignetWallet-win-x64-Setup.exe`: the installer most people want. It installs for the current
  user (no administrator rights) into `%LOCALAPPDATA%\SignetWallet`, adds a Start-menu entry and
  an entry in Settings → Apps for uninstalling.
- `SignetWallet-win-x64-Portable.zip`: the same app to unzip anywhere; it updates itself too.
- `SignetWallet-0.8.1-win-x64-full.nupkg` and, from the second Velopack release on,
  `…-delta.nupkg`: the update packages.
- `releases.win-x64.json`, `assets.win-x64.json`, `RELEASES-win-x64`: the update feed. Installed
  copies of Signet read it from the **latest published** release once a day (Settings →
  Updates), so publishing the draft is what offers the update to everyone.

plus `SHA256SUMS.txt`. The .NET runtime travels inside, so nothing else needs installing. The app
id `SignetWallet` keeps the install apart from Signet's own settings in `%LOCALAPPDATA%\Signet`,
so uninstalling never removes them (and the wallet in `%USERPROFILE%\.signet` is never touched).

0.8.0 was a plain zip without the updater: its users install 0.8.1 once by hand, and are updated
automatically from then on.

The app is built and tested by GitHub Actions (`.github/workflows/release.yml`), never on a
laptop. It is signed on this PC, because the signing key is in Certum's SimplySign and needs the
SimplySign phone app to unlock (see **Code signing** below). So a release is two halves: pushing
a `v…` tag builds the tagged commit on GitHub, then `scripts/release.ps1` here downloads that
build, signs it, packs it and makes a **draft** release. Nothing is public until the draft is
published.

The version is `<Version>` in `src/Signet.App/Signet.App.csproj`. A tag build overrides it with
the tag (`v0.8.1` → `0.8.1`), so keep the two in step.

## Releasing a version

In PowerShell, in the repository folder (`C:\Users\chris\signet-windows`):

1. Make sure everything is committed and pushed: `git status` should say
   `nothing to commit, working tree clean` and `Your branch is up to date with 'origin/main'`.
2. If the version changed, edit `<Version>` in `src/Signet.App/Signet.App.csproj`, then commit
   and push it.
3. Tag the release:
   ```powershell
   git tag -a v0.8.1 -m "Signet for Windows 0.8.1"
   git push origin v0.8.1
   ```
4. Open https://github.com/drchrispinnock/signet-windows/actions and wait for the **Release** run
   for `v0.8.1` to turn green (about ten minutes). If it goes red, open it and read the step that
   failed.
5. Connect SimplySign Desktop (its tray icon → connect, then the code from the SimplySign phone
   app).
6. Sign and draft:
   ```powershell
   ./scripts/release.ps1 -Tag v0.8.1
   ```
   It checks that the build on GitHub is of the tagged commit, has Velopack sign every unsigned
   binary (`Signet.exe`, our DLLs and the bundled libraries without a signature, plus the
   `Setup.exe` and `Update.exe` Velopack makes), checks the signatures on the Setup and on
   `Signet.exe` in the portable zip, and uploads everything to a draft release. Its working
   folder is `%TEMP%\signet-release\v0.8.1`.
7. Open https://github.com/drchrispinnock/signet-windows/releases. There is a draft called
   **Signet for Windows 0.8.1** with the files above. Click the pencil (Edit), check the notes, and
   press **Publish release**. From that moment installed copies are offered the update.

If step 6 fails after the draft was made, delete the draft on the Releases page and run it again.

To try a build without releasing: Actions → **Release** → **Run workflow** → branch `main` →
**Run workflow**. The unsigned installers appear under **Artifacts** at the bottom of the run
(versioned like `0.8.1-ci.12`); no release is made and no one is offered anything.

To test the updater on a PC without publishing anything, pack two versions into a folder with
`scripts/pack.ps1` (as the workflow does, `-WorkDir` the same folder for both), install the older
one with its Setup, and start it with the environment variable `SIGNET_UPDATE_SOURCE` set to that
folder: it then updates from the folder instead of GitHub.

To undo a tag pushed by mistake (before publishing):
```powershell
git push origin --delete v0.8.1
git tag -d v0.8.1
```
and delete the draft on the Releases page.

## Code signing

The certificate is Certum's **Open Source Code Signing** certificate, issued to
"Open Source Developer Chris Pinnock" by Certum Code Signing 2021 CA, valid from 9 November 2026
to 9 November 2027. Its public half and Certum's chain are in `C:\Users\chris\WinCert`; the
private key never leaves SimplySign, Certum's cloud card.

One-time setup on this PC:

1. Install **SimplySign Desktop** from Certum's support site, and activate the **SimplySign**
   phone app with the details from the certificate order.
2. Connect (tray icon → connect, the code from the phone app). The certificate should appear in
   the user's certificate store; if not, register it in SimplySign Desktop's certificate manager.
3. Check:
   ```powershell
   Get-ChildItem Cert:\CurrentUser\My | Where-Object Thumbprint -eq 61EA6BA1190E30EFEAE84EA9519E7BC6E95564AB | Format-List Subject, HasPrivateKey
   ```
   `HasPrivateKey` should be `True`.

The scripts (`scripts/signing.ps1`) sign with signtool by thumbprint, SHA-256, and an RFC 3161
timestamp from `http://time.certum.pl`, so signatures stay valid after the certificate expires.
**When the certificate is renewed**, put the new thumbprint in `$SigningThumbprint` in
`scripts/signing.ps1` and update this section.

`./scripts/sign-build.ps1` signs a local build (`Signet.exe`, `Signet.dll`, `Signet.Core.dll`;
Debug x64 by default, or `-Configuration Release`, `-Platform ARM64`, `-Path <folder>`), so
Smart App Control on this PC runs it.

What to expect:
- **Unknown publisher** becomes "Open Source Developer Chris Pinnock", and Smart App Control
  accepts the signed app.
- SmartScreen can still warn for the first releases, until the certificate builds up a download
  reputation. Until it stops, keep the **Run anyway** instructions in the release notes.
- Updates are trusted on HTTPS from GitHub alone (Velopack has no signature check like Sparkle's
  EdDSA one on the Mac), which is why automatic download is off by default: users see each update
  offered and choose to install it.

Releases before 0.9.15 are unsigned: SmartScreen shows **Windows protected your PC** (**More
info**, then **Run anyway**), and Smart App Control may refuse them.
