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
- Models, except the Etherlink and shielded parts of `Network` (`l2…`, `shielded…`) and
  `Multisig` (only `MultisigContract`). `AppState` carries the Layer 2 and shielded maps through unread.
- The `TaquitoBridge` host (`JSONValue` with `IntValue`, `BridgeError.Typed`). Not yet: the
  call policy (S30: signing calls, read deadlines, context discard), `OperationJournal` (the
  bundle falls back to its in-memory journal), `LedgerSessions`, log redaction and rotation (S11).
- `KeyGenerator`, `KeyImporter`, `SessionKeys`, `TezosClientStore` (journal, `contracts`, removal
  snapshots, address-checked secrets), `WalletStore`, `AppStateStore` (nodes and service links),
  `WalletDirectorySettings`, `EndpointPolicy` (host classification only), `QRCode`.
- The chain layer with approved limits (S14), `SentOperation`, `Portfolio` (S26) and chain pinning
  (S12): `ChainService`, `MockChainService`, `TaquitoChainService`, `TzKTService`, `NodeMonitor`
  and the rest as before. Not yet: `BoundedHTTP`/`JSONBudget` (S19), `PublicHTTPClient`, `ArtworkCache`.
- `WalletViewModel`: accounts (create, import, session keys, remember, rename, forget, export,
  address book, octez-client import), refresh, networks and nodes, backups, faucet, staking and
  governance pass-throughs. Not yet: TzEL, Etherlink, multisig, dApps, Ledger, Buy.
  `SendViewModel` exists only for its `SendError`.
- Views: `WalletHomeView` (with `NoWalletsView` and the disclaimer), the header
  (`WalletIdentityView`, `OperationsMenuItems`), `NetworkBadgeMenu`, `ActionButtonsView`,
  `AssetListView`, `DelegateRowView`, `AccountNotOnChainView`, `ActivityTabsView` (transactions,
  tokens, NFTs), `NodeStatusBar`, `AccountAvatarView`, and the sheets Create, Import, Rename,
  Add address, Export, Forget, Remember key, Receive (Tezos side only) and Faucet.
- `NetworksSheet` (node per network with the chain check, `NodeIdentity`; TzKT, explorer and
  faucet links; no Layer 2 fields) and `SettingsView` (appearance, launch disclaimer, wallet
  directory, backups; no Updates or Developer sections yet). Settings is a dialog opened from the
  burger menu or Ctrl+, rather than a separate window; `Appearance` is applied by `AppearanceApplier`.
- Send: `SendViewModel` and `SendSheet` (compose, confirm, result; Tezos side only, no
  `ChainSidePicker`), with `OperationJournal` behind the bridge's `operationRead`/`operationSave`/
  `operationRemove` natives, stored in `%USERPROFILE%\.signet\pending_operations.json` (S15).
  `QuantumnetSendTests` is a Windows-only live round trip through the journal.
- `WalletBackup` is still the `3b33fe1` version (no manifest or locked snapshot, S05/S28).

Not ported, shown disabled in the menus with "Not available on Windows yet": Buy, Stake,
Baking, Governance, Connect Ledger (and `LedgerHID`, still a placeholder), dApps, multisig.
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
  0xC000027B. A runnable folder is the Release build output (`bin\x64\Release\…\win-x64`).
- The Mac's chmod 600 assertions are dropped (see `TestSupport.cs`).

This PC has Smart App Control enforced. It can block a freshly built, unsigned `Signet.exe` with
"An Application Control policy has blocked this file" (Code Integrity event 3077). Starting it as
`dotnet Signet.dll` doesn't help: WinUI resolves its XAML resources relative to `dotnet.exe` and
crashes with 0xC000027B. The tests are unaffected. Each build produces a different `Signet.exe`
hash, so some builds are allowed and some are not, but `Signet.exe` is only .NET's launcher for
`Signet.dll` and its code does not change: copying a `Signet.exe` that was already allowed over a
new build's (as `scratch\Signet-x64` keeps) runs the new build.
