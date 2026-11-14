# Kitesurf Browser 1.1.2 for Windows

Contrast fix.

The **Cloudflare Access service token** header and its `CF-Access-Client-Id` / `CF-Access-Client-Secret`
labels were rendered in the secondary grey palette, which is hard to read on the near-black connect
card. They now use the primary ink colour (`#E8EFF9`), and the explanatory line under the fields moved
up one step in the palette while staying visually secondary.

No behavioural changes: connection handling, screencast pacing, input mapping, Cloudflare Access
service-token support and the icon are identical to 1.1.1. `--selftest` passes its full suite.

Download `KitesurfBrowser.exe` below; it is self-contained (Windows 10 1809+ / 11, x64).
