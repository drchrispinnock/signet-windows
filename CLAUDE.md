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
./scripts/sign-build.ps1                        # sign the Debug x64 build (SimplySign connected)
./scripts/release.ps1 -Tag v0.9.15              # sign, pack and draft a tag's CI build (docs/RELEASING.md)
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

The pin is upstream `1fc7dc4` (main, 2026-11-05), and everything up to it is ported: every
feature (TzEL included), the infrastructure behind them, and every upstream test suite (the
Linux/FreeBSD build commits `698121e`, `7dc82dc`, `461d097`, `436631a`, `78a6098` need nothing
here). Ported:
- Models, `Network` and `AppState` in full, the shielded members included.
- The `TaquitoBridge` host (`JSONValue` with `IntValue`, `BridgeError.Typed`, the journal and
  Ledger session natives) with upstream's call policy (S30): `SigningCalls`/`CallKind`, `Sign`
  refuses a read, a read has a 60 s deadline (`CallWithin` for another) after which its HTTP work
  is cancelled and the context discarded (a generation counter drops late timers, fetches and
  Ledger replies), and string arguments are capped at 2 MB. Foreground calls run through one FIFO gate per bridge,
  held until the asynchronous outcome settles, with at most 64 waiters. A read's deadline starts
  after admission; its timeout resets the context before the next call starts. Timer, fetch and
  Ledger callbacks continue on the bridge thread while a foreground call waits. `bridge.log` rotates to `bridge.log.1` past 5 MB
  (S11), and every line also goes to a `systemLog` sink (the debugger's output by default).
- `KeyGenerator`, `KeyImporter`, `SessionKeys`, `TezosClientStore` (journal, `contracts`, removal
  snapshots, address-checked secrets), `WalletStore`, `AppStateStore` (nodes, Layer 2 and service links),
  `WalletDirectorySettings`, `EndpointPolicy`, `QRCode`, `DeveloperSettings`. `ImportWallets`
  follows a link or junction before refusing the directory itself (upstream compares device and inode).
- `BoundedHTTP` (S19): `Http.Get`/`PostJSON`/`Send` stream every native reply under a byte cap and a
  JSON budget, with upstream's caps per reader (TzKT pages 4 MB and records 256 KB, node replies and
  Tezos Domains 16 KB, the faucet 64 KB, the node probe 16 KB unchecked).
- `WalletBackup` at HEAD (S05, S09, S28): a locked, recovered snapshot (`SnapshotFiles`), a
  `.signet-backup` manifest naming the source wallet, a uniquely named staging folder, and pruning of
  complete generations of this wallet only. The manifest's `source` is a Windows path, so a backup
  folder is not shared with a Mac.
- Artwork: Core's `ImageLoader` (upstream's actor: candidates in order with the disk consulted
  candidate by candidate, `6a4a2b0`; public HTTPS through the pinned client; the 8 MB cut-off and
  too-large memory; six loads at a time) and `ArtworkCache` in `%LOCALAPPDATA%\Signet\artwork`.
  Core has no codec, so the loader deals in encoded thumbnails (`ImageLoader.Thumbnail`: a PNG, or
  an SVG document kept as it came) and the app installs Windows Imaging Component as its `Decoder`.
- The chain layer with approved limits (S14), `SentOperation`, `Portfolio` (S26) and chain pinning
  (S12): `ChainService`, `MockChainService`, `TaquitoChainService`, `TzKTService`, `NodeMonitor`
  and the rest as before; `TokenMetadataResolver` (metadata the indexer lacks, read from the
  `token_metadata` big map and the TZIP-21 document) with `JSONBudget`; `IPFS` at HEAD (validated
  content paths, public HTTPS candidates only, S13); `PublicHTTPClient.Data`, the bounded GET
  through the pinned client with checked redirects (upstream `data(for:limit:followRedirects:)`).
- Etherlink governance (`EtherlinkGovernance`, the slow and fast kernel-upgrade contracts): on
  `Network` the map is `EtherlinkGovernanceContracts` (a value-equal `EtherlinkGovernance.Contracts`,
  since `Network` is a record compared by value; the name `EtherlinkGovernance` is the model's).
  The Governance sheet has Tezos/Etherlink tabs; the Etherlink half is `EtherlinkGovernanceView.cs`,
  a partial of `GovernanceSheet` (one dialog on Windows). The Networks sheet's governance fields use
  `SettingField` (`NetworkSettingsControls.cs`, upstream's shared text setting), as do the Shielded
  settings.
- Etherlink history and holdings on the activity tabs, the Tezos/Etherlink `LayerToggles`.
- NFTs: newest first across both layers, play badge, and `NFTGridView.cs` (selection with click,
  Ctrl-click, Shift-click; right-click Transfer, Burn, Download, explorer and objkt links, View
  Artwork, copies, Select All). `TransferNFTSheet` (`TokenTransferRequest`, `BatchLimits`,
  `WalletViewModel+Tokens.cs`), `DownloadNFTsSheet` and `NFTDownloader` (Show in File Explorer;
  Windows also turns `< > " | ? *` and a trailing dot in a token name into file-name-safe text).
- tz4 sends make the reveal's proof of possession (`Wallet.ProofOfPossession`,
  `ChainError.ProofOfPossessionRequired`, the compose-step password); "Create proof of
  possession…" (`ProofOfPossessionSheet`); the Baking key field takes a tz address
  (`WalletViewModel.KeyFieldContentOf` / `PublicKeyForKeyOrAddress`; the address case is
  `KeyFieldContent.AddressContent`, as a nested `Address` would clash) and Reactivate for a
  deactivated baker; dApps watch each operation into a block (`AwaitingHash`).
- TzEL (shielded tez), as upstream's CLAUDE.md describes it. Windows differences: `Blake2s.cs` is
  managed code (an unrolled compression; upstream's is `tzel_blake2s.c`), with the auth-tree leaf
  and node beside it; a whole tree takes a few seconds in Release, about 10 s in Debug.
  `ShieldedWallet` and its records are classes, deep-copied and compared through their JSON
  (`ShieldedJSON`: sorted keys, ISO 8601 dates, LF, as the Mac's `JSONEncoder` writes), so a
  wallet folder moves between the two. `PrivateFile` is an atomic write and a read that refuses
  links (the profile's ACL makes the files private). The view model takes its stores and rollup
  through `ConfigureShielded` (the app passes `FileShieldedStore`); `ShieldedRollupExtensions.
  DeliveryPollInterval` is shortened by the tests. `ShieldedTests` runs the whole shield, unshield
  and transfer flows through the bridge against `MockShieldedRollup`; the classes that build
  signing trees share a non-parallel collection (`CpuHeavy`) so timing tests are not starved.
- `WalletViewModel`: accounts (create, import, session keys, remember, rename, forget, export,
  address book, octez-client import), refresh, networks and nodes, backups, faucet, staking and
  governance pass-throughs, dApps, multisig, Buy, Ledger, Etherlink, Etherlink governance, tokens,
  shielded (`WalletViewModel+Shielded.cs`).
  `SendViewModel` exists only for its `SendError`.
- Views: `WalletHomeView` (with `NoWalletsView` and the disclaimer), the header
  (`WalletIdentityView`, `OperationsMenuItems`), `NetworkBadgeMenu`, `ActionButtonsView`,
  `AssetListView`, `DelegateRowView`, `AccountNotOnChainView`, `ActivityTabsView` (transactions,
  tokens, NFTs), `NodeStatusBar`, `AccountAvatarView`, and the sheets Create, Import, Rename,
  Add address, Export, Forget, Remember key, Receive and Faucet.
- `NetworksSheet` (node per network with the chain check, `NodeIdentity`; TzKT, explorer and
  faucet links; Enable Layer 2 and the Layer 2 Michelson node) and `SettingsView` (appearance, launch disclaimer, wallet
  directory, backups, updates, Developer). Settings is a dialog opened from the
  burger menu or Ctrl+, rather than a separate window; `Appearance` is applied by `AppearanceApplier`.
- Send: `SendViewModel` and `SendSheet` (compose, confirm, result; Tezos and Etherlink sides),
  with `OperationJournal` behind the bridge's `operationRead`/`operationSave`/
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
  The manager's calls carry upstream's read deadlines (`CallWithin`). The pairing
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
  Upstream's live `ShadownetMultisigTests` is
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
- Etherlink (0.9 to 0.9.3): `Network`'s Layer 2 members (`L2RPCURL`, `L2Enabled`, `Layer2`,
  `CanDepositToL2`, bridge helper and rollup, `EtherlinkGateway`), `AppState.L2Enabled`/`L2NodeURLs`,
  `Keccak` and `EtherlinkAlias`, the chain calls (`EtherlinkBalanceFor`, deposit, alias status and
  link; a Layer 2 service reads `balance` alone for tez), the Etherlink row (S31-bound
  `BridgeContextInfo`/`BridgeEstimate`), `L2Monitor` (the status bar's Layer 2 line), and the
  sheets: `BridgeSheet` ("Bridge…" when `CanBridge`), Receive's Etherlink side with alias linking,
  Send's Etherlink side, and the Networks sheet's Layer 2 switch and node. `ChainSidePicker`
  (with the `ChainSide` enum) is in the app. Naming differences: `NodeMonitor.LayerValue`
  (the property `Layer` takes the name, as with `Network.FaucetKindValue`),
  `WalletViewModel.BridgeContextInfo` (the property is `BridgeContext`) and
  `EtherlinkAliasStatusForSelected()` (upstream `etherlinkAliasStatus()`, which would clash with
  its return type). Upstream's `SendSheet.SideContext`/`capture` is `SendSideContext.Capture`
  in Core, so `SendSideContextTests` runs headless. The live tests (`ShadownetBridgeTests`,
  `EtherlinkLiveTests`) fund through the Shadownet faucet's proof of work and pass, about 40 s each.
Every menu item is ported, and every upstream test suite has a class of the same name. The skipped
tests each give their reason: Mac-only checks (Sparkle's Info.plist keys, the in-app test host,
POSIX modes), tests on the user's real wallet state, the lock benchmark, a curl configuration
Windows does not use, cancellation `WaitForConfirmation` does not take, and image decoding (Core
has no codec). The bridge log is at `%LOCALAPPDATA%\Signet\Logs\bridge.log`.

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
- `Services/ArtworkImages.cs` (app) puts Core's `ImageLoader` thumbnails into an `Image`
  (`BitmapImage`, or `SvgImageSource` for an SVG) and keeps the pictures in memory.
- The token, Etherlink and shield logos are linked from `upstream/Signet/Resources/Assets.xcassets`
  in `Signet.App.csproj`, never copied.
- `dotnet publish` of the app drops `Signet.pri` and the `.xbf` files and the result crashes with
  0xC000027B. A runnable folder is the Release build output (`bin\x64\Release\…\win-x64`), which
  carries the .NET runtime (`SelfContained` in Release).
- Releases and updates: `.github/workflows/release.yml` builds and tests a tag and uploads the
  unsigned output; `scripts/release.ps1`, run here with SimplySign Desktop connected, checks it is
  the tag's commit, signs and packs it with Velopack (`scripts/pack.ps1`: `vpk`, app id
  `SignetWallet`, one channel per architecture, `--signParams` through `scripts/signing.ps1`) into
  a Setup, a portable zip and the update feed on a draft GitHub release; `docs/RELEASING.md` is
  the procedure. The certificate is Certum Open Source Code Signing (key in SimplySign, so no CI
  signing); a manual workflow run still packs unsigned installers. `VelopackApp` runs first
  in `Program.Main` (its uninstall hook takes back `signet://`). `UpdaterService` (app) is
  upstream's Sparkle `UpdaterService`: a daily check (on by default), automatic download (off by
  default: Velopack has no EdDSA check like Sparkle's, so nothing is installed unseen), last
  checked, Check Now; "Check for Updates…" sits in the burger menu (Windows has no app menu) and
  opens `UpdateSheet`, which also offers what the daily check finds. Only an installed copy
  updates; `SIGNET_UPDATE_SOURCE` points it at a local `vpk pack` folder for testing.
- The Mac's chmod 600 assertions are dropped (see `TestSupport.cs`).
- The 5 November 2026 audit (`..\signet-audit`; upstream `audit/AUDIT_20261105.md`, commits
  `fc84e47`, `de18ad5`, `555311f`) is ported as upstream has it, with `AuditFixTests`:
  `FileShieldedWalletStore` holds `tzel-<chain>.lock` (an open with no sharing, upstream `lockf`)
  beneath its in-process lock keyed by the real path (`PathUtility.RealPath`, links and
  junctions followed), 5 s then `ShieldedError.WalletLocked`; one 128 MB bound for reading and
  writing the shielded file (`WalletTooLarge`), shared with the backup's snapshot and
  fingerprint through `PrivateFile.ReadLimit`; `RollupState.Level` bounds; backups after new
  shielded keys, pools and records; unshield and transfer records (`local:` id, `PayloadHex`,
  `Submitted`) on file before the operator is contacted, `WalletViewModel.SubmitShielded`,
  `ShieldedError.Rejected` / `RollupNodeService.IsRejection`, and Submit again
  (`ResubmitWithdrawal`/`ResubmitTransfer`); the console's object scrub and `RedactedLine`'s UUID
  and byte-run rules; the plaintext-key and plain-HTTP notes in the sheets. Windows differences:
  upstream's `ShieldSettingsSheet.plainHTTP` is `ShieldSettingsNotes.PlainHTTP` in Core;
  `NodeMonitor.probe(session:)` is `ProbeOver(HttpClient)`; the lock test holds the lock file
  itself rather than from a Python process, and the path-alias test makes a junction when it
  cannot make a symbolic link; `RollupHead.Level` is an `int`, so the level test's edges are
  int's. The shield flow also backs up after it spends a key index (Windows addition).
  Windows-only fixes from the same audit, ahead of upstream:
  - G03: `FaucetService` has upstream's S21 bounds (it had been ported without them):
    `Limits`, `Validate`, `Checked`, capped rounds and time, and a cancellable `Solve`. The view
    model's token reaches the requests and the hashing (`Http.Get`/`PostJSON`/`Send` take a
    `CancellationToken`). Tests in `FaucetServiceTests`.
  - G07: `AtomicFile` creates every file with a protected owner-only DACL (the user and SYSTEM)
    before writing, so a custom wallet or backup folder cannot hand its permissions down.
    Downloaded NFTs pass `ownerOnly: false`. Test in `StorageAuditRepairTests`.
  - K02: `CrashLog` (Core) writes `crash.log` through the bridge's redaction and rotates it at
    1 MB (`LoggingPolicyTests`).
  - `JSONValue.Array`/`Object` copy the collections they are given.
  - T04: foreground bridge calls now run one at a time through a bounded FIFO gate held
    until their asynchronous outcome settles. `BridgeCallGateTests` covers signing/read
    ordering, rejection, timeout recovery, abandoned caller waits, queue bounds and per-bridge
    independence.
  - Still open: G02 master-key encryption, separating scanned commitments from the key file,
    and E01 to E03 in the Octez Connect SDK.

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
new build's (as `scratch\Signet-x64` keeps) runs the new build. With SimplySign Desktop
connected, `./scripts/sign-build.ps1` signs a local build instead, which Smart App Control accepts.
