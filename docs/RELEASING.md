# Releasing Signet for Windows

A release is a GitHub release with two zips, one per architecture (`Signet-0.8.0-win-x64.zip`,
`Signet-0.8.0-win-arm64.zip`), each with a `.sha256` file. Each zip holds a folder `Signet`;
users unzip it anywhere and run `Signet.exe`. The .NET runtime travels inside, so nothing else
needs installing.

The build is done by GitHub Actions (`.github/workflows/release.yml`), never on a laptop: pushing a
`v…` tag builds both zips and makes a **draft** release. Nothing is public until the draft is
published.

The version is `<Version>` in `src/Signet.App/Signet.App.csproj`. A tag build overrides it with
the tag (`v0.8.0` → `0.8.0`), so keep the two in step.

## Releasing a version

In PowerShell, in the repository folder (`C:\Users\chris\signet-windows`):

1. Make sure everything is committed and pushed: `git status` should say
   `nothing to commit, working tree clean` and `Your branch is up to date with 'origin/main'`.
2. If the version changed, edit `<Version>` in `src/Signet.App/Signet.App.csproj`, then commit
   and push it.
3. Tag the release:
   ```powershell
   git tag -a v0.8.0 -m "Signet for Windows 0.8.0"
   git push origin v0.8.0
   ```
4. Open https://github.com/drchrispinnock/signet-windows/actions and wait for the **Release** run
   for `v0.8.0` to turn green (about ten minutes). If it goes red, open it and read the step that
   failed.
5. Open https://github.com/drchrispinnock/signet-windows/releases. There is a draft called
   **Signet for Windows 0.8.0** with four files. Click the pencil (Edit), check the notes, and
   press **Publish release**.

To try a build without releasing: Actions → **Release** → **Run workflow** → branch `main` →
**Run workflow**. The zips appear under **Artifacts** at the bottom of the run; no release is made.

To undo a tag pushed by mistake (before publishing):
```powershell
git push origin --delete v0.8.0
git tag -d v0.8.0
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
   ours), and hands the folder back to be zipped.
