# Kitesurf Browser 1.1.0 for Windows

A native WPF client for the [Kitesurf](https://github.com/cloudflare/kitesurf) browser engine. Kitesurf
renders pages on Cloudflare Workers and speaks the Chrome DevTools Protocol; this is the Windows shell
for that engine, with no Chromium inside the window.

## Install

Download `KitesurfBrowser.exe` and run it. It is self-contained: no .NET install, no runtime, no
installer. Windows 10 1809+ or Windows 11, x64.

## New in 1.1.0 — Cloudflare Access service tokens

Deployed Workers are usually protected by Zero Trust, which answers an unauthenticated request with a
redirect or a 403. The connect panel now has a **Cloudflare Access service token** section:

- `CF-Access-Client-Id` and `CF-Access-Client-Secret` are sent on `/json/version` **and** on the CDP
  WebSocket upgrade, since Access checks both.
- The secret is encrypted with Windows DPAPI (current user) before it is written to `settings.json`,
  masked in the UI, and never logged.
- A blocked deployment now reports *"Blocked by Cloudflare Access — add a valid CF-Access-Client-Id and
  CF-Access-Client-Secret"* instead of a generic parse error, and an authenticated engine shows an
  `ACCESS` badge in the address bar.

## Connect an engine

- **Local engine** — in the Kitesurf repository run `pnpm install --frozen-lockfile` and `pnpm run dev`,
  then connect to `127.0.0.1:8787`.
- **Cloudflare engine** — deploy the Worker as the engine README describes and paste its URL. A Workers
  Paid plan is required because each page runs in its own Dynamic Worker; put Cloudflare Access in front
  of the deployment.
- **No engine** — `KitesurfBrowser.exe --demo` starts a loopback-only synthetic page so the shell can be
  exercised end to end.

## What is in this build

- Tabs backed by one CDP WebSocket each, with per-tab viewport, history and reconnect state.
- Screencast rendering paced by frame acks, painted at native DPI.
- Mouse, wheel, text and editing-key input forwarded in engine viewport coordinates.
- Omnibox with search fallback, back/forward driven by `Page.getNavigationHistory`.
- Console drawer with `Runtime.consoleAPICalled` output and `Runtime.evaluate`.
- Engine manager with `/json/version` probing, saved engines, service tokens and reconnect on launch.
- Screenshot capture to PNG, DevTools hand-off to the engine's own inspector.
- `--selftest`: a non-interactive protocol check covering connect, screencast, navigation, input,
  evaluation, screenshots, history, pointer mapping, Cloudflare Access headers and DPAPI storage.
  This build passes 17/17 against the demo engine.

## Known limits

Kitesurf is an experimental engine implementing a subset of CDP. Rendering fidelity, form behaviour and
media support are whatever the engine produces. This client never falls back to WebView2 and does not
patch pages.

Apache 2.0, matching the upstream repository.
