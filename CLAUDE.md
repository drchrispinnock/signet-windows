# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Windows port of **Signet**, the macOS Tezos wallet in SwiftUI. The Mac app is the source of
truth and keeps evolving; this repo follows it. It is checked out as the git submodule `upstream/`
(origin `drchrispinnock/signet`; a working copy usually also sits at `..\signet`). Before porting
or changing a feature, read `upstream/CLAUDE.md` (detailed architecture), `upstream/spec/SPEC.md`
and `upstream/spec/EXAMPLE.png`. Behaviour, wording, file formats and edge cases should match
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

Ported as of upstream `3b33fe1`:
- All of `Signet/Models`.
- The `TaquitoBridge` host, with `JSONValue`, `OctezConnectStorage`, and `AtomicFile` for
  Foundation's `.atomic` writes.
- `KeyGenerator`, `TezosClientStore`/`WalletStore`, `AppStateStore`, `WalletDirectorySettings`,
  `WalletBackup`/`BackupSettings`.
- The tests for all of the above.

`LedgerHID` is a placeholder that reports no devices. The app shows `Views/BridgeCheckView`, a
temporary Windows-only page (node head, balance breakdown, read-only wallet listing, throwaway key
generation, live bridge log) that goes away when `WalletHomeView` is ported. Next, in order: the
chain and indexer services (`ChainService`, `MockChainService`, `TaquitoChainService`,
`TzKTService`, `TezosDomainsService`, …), `KeyImporter`, `WalletViewModel`, then the views screen
by screen. Run `dotnet test` and read the skipped tests for the detailed backlog. The bridge log is
at `%LOCALAPPDATA%\Signet\Logs\bridge.log`.

Windows-specific porting notes:
- `UserDefaults` is a C# class of that name backed by `%LOCALAPPDATA%\Signet\preferences.json`.
  Tests use `UserDefaults.Temporary()` where upstream uses a fresh suite.
- Paths are `string`s, compared with `PathUtility.SamePath` (case-insensitive).
- `WalletStore.add(_:secretKey:)` is `AddWithSecretKey`, because both Swift overloads take a string.
- `wallet_lock` is an exclusive open with a 30-second wait instead of `lockf`.
- The Mac's chmod 600 assertions are dropped (see `TestSupport.cs`).

This PC has Smart App Control enforced. It can block a freshly built, unsigned `Signet.exe` with
"An Application Control policy has blocked this file" (Code Integrity event 3077). Starting it as
`dotnet Signet.dll` doesn't help: WinUI resolves its XAML resources relative to `dotnet.exe` and
crashes with 0xC000027B. The tests are unaffected.
