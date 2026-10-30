# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Windows port of **Signet**, the macOS Tezos wallet in SwiftUI. The Mac app is the source of
truth and keeps evolving; this repo follows it. It is checked out as the git submodule `upstream/`
(origin `drchrispinnock/signet`; a working copy usually also sits at `..\signet`). Before porting
or changing a feature, read `upstream/CLAUDE.md` (detailed architecture), `upstream/docs/spec/SPEC.md`
and `upstream/docs/spec/EXAMPLE.png`. Behaviour, wording, file formats and edge cases should match
the Mac app unless Windows forces a difference. When it does, note the difference here.

Stack: C# / .NET 10, WinUI 3 (Windows App SDK, unpackaged), CommunityToolkit.Mvvm, xUnit v3.

## Commands

```powershell
git submodule update --init                     # after cloning
dotnet build Signet.slnx -p:Platform=x64
dotnet test --project tests/Signet.Core.Tests   # no UI needed
dotnet test --project tests/Signet.Core.Tests --filter-method "*.AddressTests.ShortensLongAddresses"
dotnet test --project tests/Signet.Core.Tests --filter-not-trait "Category=Network"   # offline
dotnet run --project src/Signet.App -p:Platform=x64
./scripts/make-icon.ps1                         # regenerate Signet.ico from upstream's 1024 px master
```

The app project defaults to x64 (pass `-p:Platform=ARM64` for Arm). Package versions are pinned
centrally in `Directory.Packages.props`, and warnings are errors. Tests run on Microsoft.Testing.Platform
(opted in by `global.json`), so use the `--filter-*` options above, not VSTest's `--filter`. A
freshly installed SDK may need the shell's PATH refreshed before `dotnet` is found.

## Layout

- `src/Signet.Core`: everything except views (Models, Services, ViewModels, the bridge host).
  It targets plain `net10.0` with no WinUI reference, so it can be tested headless.
- `src/Signet.App`: WinUI 3 views and Windows-only glue (window, URL-scheme activation, updater).
- `tests/Signet.Core.Tests`: ports of `upstream/SignetTests`.

## Porting conventions (keep the two codebases diffable)

- **Mirror names one-to-one.** `upstream/Signet/Models/Foo.swift` becomes
  `src/Signet.Core/Models/Foo.cs`, with the same type and member names (PascalCased). Services
  and ViewModels map the same way. A SwiftUI view `FooSheet.swift` becomes
  `src/Signet.App/Views/FooSheet.xaml(.cs)`. Test suite `FooTests` and test `doesBar` become
  `FooTests.DoesBar`. Keep doc comments, adapted to C#. Whoever syncs later should be able to
  open the Swift and C# files side by side.
- Swift value types (`struct`, `Sendable`) become `record`/`record struct`. `enum` with payloads
  becomes an abstract record hierarchy with nested cases (`StakingOperation.Stake`). Raw-value
  enums become C# enums plus a `…Extensions` class carrying `RawValue()`, `FromRawValue()` and the
  computed properties. Failable `init?` becomes a static `From…` factory returning null. Records
  compare lists by reference, so a type whose identity includes a list (`LedgerKey`) overrides
  equality. `@MainActor @Observable` view models become `ObservableObject` (CommunityToolkit.Mvvm)
  and are only touched on the UI thread. `async throws` becomes `async Task<T>` that throws.
- Swift `x(for: address)` becomes `XFor(address)` (`TezBalanceFor`, `DelegateInfoFor`, …), because
  C# can't have a member share its name with the type it returns. Other Swift argument labels
  are dropped (`estimateTransfer(from:to:amount:)` becomes `EstimateTransfer(wallet, destination,
  amount)`). A nested type that would clash with a property gets an `Info` suffix
  (`TokenInfo.MetadataInfo`), and a record parameter that would clash gets a more specific name
  (`NodeStatus.Healthy.BlockLevel`).
- `URLSession` becomes an injectable `HttpClient`, defaulting to `Http.Shared`. Requests go
  through `Http.Get`/`Http.PostJSON`, which apply a per-request timeout like `timeoutInterval`.
  `URLComponents` becomes `Uri.AppendingPathComponent(…).WithQuery(…)`. Upstream's bare `catch`
  around indexer lookups becomes a filter for network, timeout and parse exceptions.
- `URL` becomes `Uri`. Use `AbsoluteString()` (`Models/UriExtensions.cs`) wherever Swift uses
  `absoluteString`: `Uri.AbsoluteUri` adds a trailing `/` to a bare host. `Decimal` becomes
  `decimal`. Parse and format numbers on the wire with `CultureInfo.InvariantCulture`; use the
  current culture only for display.
- Tests: each upstream suite becomes a class of the same name in a file of the same name.
  Arguments become `[Theory]`/`[InlineData]`, `.tags(.network)` becomes
  `[Trait(Category.Name, Category.Network)]`, and `@Test("label")` keeps its label as `DisplayName`.
  An upstream test whose code isn't ported yet is an empty `[Fact(Skip = Pending.X)]` stub with
  the same name, so the skipped list in `dotnet test` output is the porting backlog.
- Platform substitutions:

  | Mac | Windows |
  |---|---|
  | JavaScriptCore | ClearScript V8 (`TaquitoBridge`) |
  | CryptoKit | `System.Security.Cryptography` (P-256, SHA, PBKDF2) and NSec (Ed25519) |
  | IOKit HID (`LedgerHID`) | HidSharp (same vendor 0x2c97, usage page 0xFFA0, same framing) |
  | WKWebView (`BuyWebView`) | WebView2 |
  | Core Image QR | QRCoder |
  | Sparkle | Velopack (GitHub Releases) |
  | UserDefaults | JSON settings file under `%LOCALAPPDATA%\Signet` |
  | `~/.signet`, `~/.signet_backups`, `~/.tezos-client` | the same names under `%USERPROFILE%` |
  | `signet://` in Info.plist, `LSMultipleInstancesProhibited` | `HKCU\Software\Classes\signet` and single-instance redirection (`AppInstance`) |

- **Wallet files must stay byte-compatible with octez-client** (UTF-8 without BOM, same JSON
  shape, atomic replace, `wallet_lock`). Don't let Windows defaults such as BOMs or CRLF leak in.

## Taquito bridge: shared, never forked

All chain access, signing, Ledger APDUs, Octez Connect and key encryption run in the Mac app's
JavaScript bundle. The Windows app embeds `upstream/Signet/Resources/taquito-bridge.js` unchanged
(see `Signet.Core.csproj`). Don't copy or edit it here. If the bridge needs a change, make it
upstream in `TaquitoBridge/src` and rebuild there (`npm install && npm run build`, which needs
Node.js). Then advance the submodule.

`TaquitoBridge` (C#) must provide the same native contract that `upstream/Signet/Services/TaquitoBridge.swift`
installs as `globalThis.__signet`: `setTimer`/`clearTimer` (calls back `__signet_timerFired`),
`randomBytes`, `fetch`/`cancelFetch` (calls back `__signet_fetchDone`), `ledgerDevices`/
`ledgerExchange` (calls back `__signet_ledgerDone`), `storageGet`/`storageSet`/`storageDelete`,
and `octezConnectEvent`. V8, like JavaScriptCore, is single-threaded: every touch of the engine
happens on one dedicated thread. Promises are unwrapped into a JSON value for callers.

## Syncing with upstream

The submodule pin marks the last Mac commit that has been ported.

```powershell
./scripts/upstream-changes.ps1        # fetches, lists unported commits oldest-first with files touched
```

Port each commit in order. Bridge-only and bundle changes need no C# work beyond the native
contract. Then pin it with `git -C upstream checkout <sha>; git add upstream` and commit as
`Port upstream <sha>: <subject>`. If a Mac change makes no sense on Windows (Sparkle, notarisation,
`project.yml`), still advance the pin past it.

## Status

The pin is upstream `9b9fa4e`, but that commit was **not ported in full**: the jump from `3b33fe1`
brought the account-management UI up to date and skipped whole subsystems. Ported as of `9b9fa4e`:
- Models, except the Etherlink and shielded parts of `Network` (`l2…`, `shielded…`).
  `AppState` carries the Layer 2 and shielded maps through unread.
- The `TaquitoBridge` host (`JSONValue` with `IntValue`, `BridgeError.Typed`, the journal and
  Ledger session natives). Not yet: the call policy (S30: signing calls, read deadlines, context
  discard) and log rotation during writes (S11).
- `KeyGenerator`, `KeyImporter`, `SessionKeys`, `TezosClientStore` (journal, `contracts`, removal
  snapshots, address-checked secrets), `WalletStore`, `AppStateStore` (nodes and service links),
  `WalletDirectorySettings`, `EndpointPolicy` (host classification only), `QRCode`.
- The chain layer with approved limits (S14), `SentOperation`, `Portfolio` (S26) and chain pinning
  (S12): `ChainService`, `MockChainService`, `TaquitoChainService`, `TzKTService`, `NodeMonitor`
  and the rest as before. Not yet: `BoundedHTTP`/`JSONBudget` (S19), `ArtworkCache`.
- `WalletViewModel`: accounts (create, import, session keys, remember, rename, forget, export,
  address book, octez-client import), refresh, networks and nodes, backups, faucet, staking and
  governance pass-throughs, dApps, multisig, Buy, Ledger. Not yet: TzEL, Etherlink.
  `SendViewModel` exists only for its `SendError`.
- Views: `WalletHomeView` (with `NoWalletsView` and the disclaimer), the header
  (`WalletIdentityView`, `OperationsMenuItems`), `NetworkBadgeMenu`, `ActionButtonsView`,
  `AssetListView`, `DelegateRowView`, `AccountNotOnChainView`, `ActivityTabsView` (transactions,
  tokens, NFTs), `NodeStatusBar`, `AccountAvatarView`, and the sheets Create, Import, Rename,
  Add address, Export, Forget, Remember key, Receive (Tezos side only) and Faucet.
- `NetworksSheet` (node per network with the chain check, `NodeIdentity`; TzKT, explorer and
  faucet links; no Layer 2 fields) and `SettingsView` (appearance, launch disclaimer, wallet
  directory, backups, updates; no Developer section yet). Settings is a dialog opened from the
  burger menu or Ctrl+, rather than a separate window; `Appearance` is applied by `AppearanceApplier`.
- Send: `SendViewModel` and `SendSheet` (compose, confirm, result; Tezos side only, no
  `ChainSidePicker`), with `OperationJournal` behind the bridge's `operationRead`/`operationSave`/
  `operationRemove` natives, stored in `%USERPROFILE%\.signet\pending_operations.json` (S15).
  `QuantumnetSendTests` is a Windows-only live round trip through the journal.
- Staking: `StakingSheet` (delegate, change or remove the delegate, stake, unstake, finalize),
  opened from the Stake tile and the delegate row (a contract's delegate row opens Sign multisig).
- Buy: `BuyProvider` (Mt Pelerin's widget; Coinbase, Kraken and Transak as browser hand-offs with
  the address and a QR code), `MtPelerin` (URL, packed message, armored proof), the view model's
  `BuyURL` and `BuySheet`, from the Buy tile on mainnet and "Buy tez…" on an empty account. The
  widget runs in WebView2 (`BuyWebView`, browsing data in `%LOCALAPPDATA%\Signet\WebView2`) under
  upstream's S23 policy. That policy is pure code in Core as `BuyWebViewPolicy` (upstream keeps it
  on the view), so the headless tests reach it. WebView2 has no `WKNavigationType`: a
  user-initiated navigation that is not a redirect counts as `LinkActivated`, anything else as
  `Other`. Permissions other than camera and microphone are refused rather than prompted, and a
  frame's own permission requests are refused. The provider choice is saved only when the user
  picks one in the open drop-down.
- Baking and governance: `BakingSheet` (register, consensus and companion keys with BLS proof of
  possession, staking parameters; no companion section on Quantumnet) and `GovernanceSheet`
  (upvotes in a proposal period, ballots in the votes), from the burger menu ("Governance…"
  only when `CanGovern`). Their text fields are made fresh for each step and bound to strings
  (`Sheet.BoundTextBox`); never move one control between parents, which is what crashed Sign
  multisig (0x800F1000 from `UIElementCollection.Add`).
- dApps (Octez Connect / TZIP-10): `DAppRequest` at HEAD (sender id from the message only,
  `SameEndpoint`, `EndpointDescription`, `DAppPreparedBatch`), `DAppConnectionManager` (grants
  rechecked before signing, S08; undelivered replies, S17; shared start/stop, S18; 16 pending,
  S19), `ConnectDAppSheet` (with `DAppIconView`) and `DAppRequestSheet`; "dApps…" and
  Ctrl+Shift+D. `WalletViewModel` builds one manager per wallet directory on
  `<wallet dir>\octez-connect.json` and replaces it on a directory change. Bridge events arrive
  on the bridge thread and are posted to the UI thread through the `SynchronizationContext` the
  manager was built on. The manager's bridge is `new TaquitoBridge(requirePublicEndpoints: true)`:
  every fetch goes through `PublicHTTPClient`, which on Windows is a `SocketsHttpHandler` whose
  `ConnectCallback` resolves the host itself, refuses any local or private answer and connects to
  the checked address (upstream runs curl with `--resolve`); no proxy, no redirects. Every bridge
  fetch is capped at 32 MB and 60 s, and the log is redacted (`Redacted`, `RedactedLine`, S11).
  Upstream's read deadlines are approximated by `WaitAsync` on the manager's own calls. The pairing
  code field is a `PasswordBox`, from upstream `6c1b7d3` (after the pin, ported early). `Program.cs`
  replaces the generated `Main` (`DISABLE_XAML_GENERATED_MAIN`): it registers `signet://` with
  `ActivationRegistrationManager.RegisterForProtocolActivation` at every launch (so the scheme
  points at whichever copy ran last), and a second launch redirects its activation to the running
  instance through `AppInstance` and exits, as `LSMultipleInstancesProhibited` does on the Mac.
  Not yet: the live `ShadownetDAppPrepareTests` (the Shadownet faucet needs proof of work).
- Multisig: `Multisig` models, `MultisigService` (`BridgeMultisigService`, `MockMultisigService`,
  the proposal stores), the `WalletViewModel` multisig section (S16 context checks, S27 untrusted
  proposals, S29 revealed keys) and the sheets Create, Add, Sign and Submit multisig, enabled in
  the burger menu. `multisig-proposals.json` is written in the Mac's `JSONEncoder` form (sorted
  keys, uppercase UUIDs, ISO 8601 dates, LF), so a wallet folder moves between the two.
  `WalletBackup.Files` is HEAD's list (contracts, proposals, journal; no shielded files), though
  the rest of `WalletBackup` is still old. Upstream's live `ShadownetMultisigTests` is
  `QuantumnetMultisigTests` here (the Shadownet faucet needs proof of work). Quantumnet refuses
  classic keys inside a contract (`non_quantum_resistant_signature_algorithm`), so that test's
  signers are tz5 keys and a tz1 pays; a multisig there needs tz5 signers.
- Ledger: `LedgerHID` (`LedgerFraming`, its S22 `ExchangeTracker`, `LedgerDevice`) over HidSharp,
  `LedgerSessions` (the process-wide device lease, behind the bridge's `ledgerSessionBegin`/
  `ledgerSessionEnd` natives: the dApp manager's bridge is a second context on the same device),
  `LedgerService` (`BridgeLedgerService`, `MockLedgerService`), `WalletViewModel.ConnectLedger` and
  `ConnectLedgerSheet` (burger menu, Ctrl+Shift+L, the welcome screen). Windows differences: a
  device's id is its HID path (upstream: the IOKit registry id); reports carry a leading report-id
  byte, added and stripped in `LedgerHID`; the vendor interface is found by its report
  descriptor's usage page, or by `&mi_00` in the path when the descriptor cannot be read.
- `WalletBackup` is still the `3b33fe1` version (no manifest or locked snapshot, S05/S28).

Every menu item is ported except the TzEL and Etherlink ones, which are left out of the menus.
Shielded settings is left out of the menu altogether while TzEL is out of scope. Run `dotnet test` and read the skipped tests for the
detailed backlog. The bridge log is at `%LOCALAPPDATA%\Signet\Logs\bridge.log`.

Windows-specific porting notes:
- `UserDefaults` is a C# class of that name backed by `%LOCALAPPDATA%\Signet\preferences.json`.
  Tests use `UserDefaults.Temporary()` where upstream uses a fresh suite.
- Paths are `string`s, compared with `PathUtility.SamePath` (case-insensitive).
- Source files are UTF-8 without a BOM and contain characters such as `…` and `“”`. Windows
  PowerShell 5.1's `Get-Content` reads them as ANSI, so a scripted rewrite garbles them (`â€¦`).
  Edit them with the editor tools, or with explicit UTF-8 encodings.
- `WalletStore.add(_:secretKey:)` is `AddWithSecretKey`, because both Swift overloads take a string.
- `wallet_lock` is an exclusive open instead of `lockf`, tried for 5 seconds (`StoreError.Locked`)
  behind a per-directory in-process lock, as upstream. The removal snapshot's storage identity
  is the directory's path and creation time (upstream: device and inode).
- Sheets are ContentDialogs deriving from `Views/Sheet.cs`; the primary button runs the action and
  the dialog stays open with the error when it fails. Small dashboard controls and the sheets are
  built in code (`Views/Foo.cs`, no XAML); the window shell and `WalletHomeView` have XAML.
- There is no menu bar: upstream's Operations menu is the burger menu only, and its shortcuts
  (Ctrl+N, Ctrl+Shift+I, Ctrl+Shift+N, Ctrl+R) are installed on the window.
- `Services/ImageLoader.cs` (app) uses `BitmapImage`, trying each URL in turn; no disk cache yet.
- The token, Etherlink and shield logos are linked from `upstream/Signet/Resources/Assets.xcassets`
  in `Signet.App.csproj`, never copied.
- `dotnet publish` of the app drops `Signet.pri` and the `.xbf` files and the result crashes with
  0xC000027B. A runnable folder is the Release build output (`bin\x64\Release\…\win-x64`), which
  carries the .NET runtime (`SelfContained` in Release).
- Releases and updates: `.github/workflows/release.yml` packs that output with Velopack (`vpk`,
  app id `SignetWallet`, one channel per architecture) into a Setup, a portable zip and the update
  feed on a draft GitHub release; `docs/RELEASING.md` is the procedure. `VelopackApp` runs first
  in `Program.Main` (its uninstall hook takes back `signet://`). `UpdaterService` (app) is
  upstream's Sparkle `UpdaterService`: a daily check (on by default), automatic download (off by
  default: Velopack has no EdDSA check like Sparkle's, so nothing is installed unseen), last
  checked, Check Now; "Check for Updates…" sits in the burger menu (Windows has no app menu) and
  opens `UpdateSheet`, which also offers what the daily check finds. Only an installed copy
  updates; `SIGNET_UPDATE_SOURCE` points it at a local `vpk pack` folder for testing.
- The Mac's chmod 600 assertions are dropped (see `TestSupport.cs`).

- `Signet.exe --preview` opens a second, throwaway window on `App.MakePreviewModel()`: the mock
  chain, in-memory accounts (Captain Stake held with a placeholder key, so Baking and Governance
  show a baker) and mock multisig. It claims neither the single instance nor `signet://`, shows
  no disclaimer and touches no wallet folder (preferences only if a setting is changed in it),
  so UI checks need not repoint the user's preferences (upstream's `#Preview` and test-host model). An exception escaping a UI
  event is written to `%LOCALAPPDATA%\Signet\Logs\crash.log` (the app still exits).

This PC has Smart App Control enforced. It can block a freshly built, unsigned `Signet.exe` with
"An Application Control policy has blocked this file" (Code Integrity event 3077). Starting it as
`dotnet Signet.dll` doesn't help: WinUI resolves its XAML resources relative to `dotnet.exe` and
crashes with 0xC000027B. The tests are unaffected. Each build produces a different `Signet.exe`
hash, so some builds are allowed and some are not, but `Signet.exe` is only .NET's launcher for
`Signet.dll` and its code does not change: copying a `Signet.exe` that was already allowed over a
new build's (as `scratch\Signet-x64` keeps) runs the new build.
