# Contributing

Thanks for looking. This repository is a Windows client for the
[Kitesurf](https://github.com/cloudflare/kitesurf) engine; the engine itself lives upstream.

## Prerequisites

- Windows 10 (1809+) or Windows 11, x64
- .NET 8 SDK (`global.json` accepts any 8.x feature band)
- Optional: a Kitesurf engine on `127.0.0.1:8787` (`pnpm install --frozen-lockfile && pnpm run dev`
  in the upstream repository). No engine is needed for the demo or the self-test.

## Build, run, verify

```powershell
dotnet build -c Release
dotnet run -- --demo        # synthetic engine + window
dotnet run -- --selftest    # headless protocol check, writes %TEMP%\kitesurf-selftest.json
```

`--selftest` must pass before a pull request is reviewed. It covers connect, screencast pacing,
navigation, input mapping, evaluation, screenshots, session history, Cloudflare Access headers and the
DPAPI secret store. CI runs the same command.

## Where things live

| Path | Responsibility |
| --- | --- |
| `Core/Cdp/CdpClient.cs` | JSON-RPC over WebSocket, request ids, traffic echo |
| `Core/PageSession.cs` | Per-tab CDP lifecycle: viewport, screencast, ack pacing, telemetry |
| `Core/EngineEndpoint.cs` | Endpoint parsing, `/json/version` + `/json/list` discovery, Access headers |
| `Controls/PageSurface.cs` | Frame painting and pointer mapping |
| `ViewModels/` | Shell and tab state, reconnect policy |
| `DemoEngine/` | Loopback CDP server used by `--demo` and `--selftest` |

## Conventions

- File-scoped namespaces, nullable reference types enabled, no new NuGet dependencies without a reason.
- Comments explain *why* something is done, not what the line does; keep them short.
- Anything that talks to the engine belongs in `Core/`, not in a code-behind.
- If you change the protocol layer, extend `SelfTest` with the check that would have caught the bug.

## Commits

Sign off your commits (`git commit -s`), matching the upstream DCO. Keep the message imperative and
explain the failure mode that motivated the change.
