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

The build is done by GitHub Actions (`.github/workflows/release.yml`), never on a laptop: pushing a
`v…` tag builds both zips and makes a **draft** release. Nothing is public until the draft is
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
5. Open https://github.com/drchrispinnock/signet-windows/releases. There is a draft called
   **Signet for Windows 0.8.1** with the files above. Click the pencil (Edit), check the notes, and
   press **Publish release**. From that moment installed copies are offered the update.

To try a build without releasing: Actions → **Release** → **Run workflow** → branch `main` →
**Run workflow**. The installers appear under **Artifacts** at the bottom of the run (versioned
like `0.8.1-ci.12`); no release is made and no one is offered anything.

To test the updater on a PC without publishing anything, pack two versions into a folder with
`vpk pack` (as the workflow does, `-o` the same folder for both), install the older one with its
Setup, and start it with the environment variable `SIGNET_UPDATE_SOURCE` set to that folder: it
then updates from the folder instead of GitHub.

To undo a tag pushed by mistake (before publishing):
```powershell
git push origin --delete v0.8.1
git tag -d v0.8.1
```
and delete the draft on the Releases page.

## Unsigned releases

Until signing is set up, `Signet.exe` is unsigned. People who run it see Microsoft Defender
SmartScreen's **Windows protected your PC**; they click **More info**, then **Run anyway**. On a PC
with **Smart App Control** turned on, Windows may refuse to run it at all. Say so in the release
notes.

## Code signing with the SignPath Foundation

SignPath signs open-source projects for free. The project qualifies: it is public and MIT licensed.

1. Apply at https://signpath.org (the open-source / SignPath Foundation programme). The form asks
   about the project, its repository, licence and maintainers; their instructions are the
   authority on what is needed.
2. Make sure your GitHub account has two-factor authentication turned on.
3. SignPath usually asks for a short **code signing policy** on the project's page (who may sign,
   how builds are made: here, only by GitHub Actions from tagged commits). Ask Claude to draft it
   for the README when you get there.
4. When they accept, they give you an organisation id, a project slug, a signing policy slug and an
   API token. Add the token to the repository: Settings → Secrets and variables → Actions →
   **New repository secret**, name `SIGNPATH_API_TOKEN`. The ids are not secret.
5. Then a signing step goes into the workflow between **Build** and **Package**: SignPath's GitHub
   action uploads the unsigned build, signs `Signet.exe` and `Signet.dll` (and the other files of
   ours), and hands the folder back to Velopack. The Setup and Update executables Velopack makes
   need signing too (its `--signTemplate` option can call the same signing step).

Until then, updates are trusted on HTTPS from GitHub alone (Velopack has no signature check like
Sparkle's EdDSA one on the Mac), which is why automatic download is off by default: users see each
update offered and choose to install it.
