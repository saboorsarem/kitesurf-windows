# Kitesurf Browser 1.2.0 for Windows

Resilience, telemetry, and debugging tools.

## Connection resilience

- **Automatic reconnect.** A dropped CDP socket (Worker idle timeout, CPU budget, laptop sleep) no
  longer leaves a dead tab. The client keeps the tab's URL, rebuilds the session, and walks
  1 s → 2 s → 4 s → 8 s → 15 s, reporting each attempt in the page overlay. After six failures it stops
  and says so instead of spinning forever. History within the new session restarts, since the engine
  assigns history per session.
- **Endpoint discovery.** The client no longer assumes a fixed socket path: it reads
  `/json/version` for the browser socket and `/json/list` for the first page target, then normalises
  `ws(s)://`, `http(s)://` and relative URLs, and forces `wss://` when the engine is behind TLS. The
  documented `/devtools/page/kitesurf` path remains the fallback.
- **Subset-safe CDP.** Optional domains degrade instead of throwing: `Page.getNavigationHistory`,
  `Page.captureScreenshot` and the `Runtime` helpers return a capability message when the engine
  answers `-32601 Method not found`, and the shell shows it in the console.

## Debugging and telemetry

- **Frame telemetry** in the status bar: resolution, frames per second over a 2 s window, and the
  ack-to-next-frame interval measured per session.
- **Raw CDP inspector**: the `CDP` toggle in the console echoes every JSON-RPC request (violet) and
  event (mint), truncated at 4 000 characters. Useful when you are testing the engine itself.
- **Clipboard frames**: `Ctrl+Shift+S`, or the copy button, puts the engine's `Page.captureScreenshot`
  render on the Windows clipboard.

## Repository

- `CONTRIBUTING.md`, issue forms for bugs and features, and repository topics for discovery.
- `global.json` asks for SDK `8.0.100` with `rollForward: latestMinor`, so any 8.x SDK builds locally.

`--selftest` now covers endpoint discovery and telemetry as well: 19 checks, run in CI on every push
and on every release.

## Verify your download

```
SHA-256 (KitesurfBrowser.exe) = 021b48f0392abf573783a53d96ed0b09ee33871cf70176dc75838ba078401f87
```

From PowerShell, in the folder that holds the download:

```powershell
cd $env:USERPROFILE\Downloads
$expected = (Get-Content .\SHA256SUMS.txt) -split '\s+' | Select-Object -First 1
(Get-FileHash .\KitesurfBrowser.exe -Algorithm SHA256).Hash -eq $expected
```

`SHA256SUMS.txt` is attached to this release. A `Resolve-Path` error means the shell is in a different
folder than the download, not that the file is corrupt.
